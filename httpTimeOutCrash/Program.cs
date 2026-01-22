using httpTimeOutCrash;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class HttpsProxy
{


    static int STALL_TIMEOUT_MS = 0;
    static int VLESS_PORT;// сколько ждём данных
  //  static readonly ConcurrentDictionary<string, bool> BadDomains = new();
    static string[] blocklist = null;
    static string[] whitelist = null;
    static readonly ConcurrentQueue<(ConsoleColor color, string text)> LogQueue = new();
    static readonly AutoResetEvent LogSignal = new(false);


    static readonly HashSet<string> BadDomains =
    new(StringComparer.OrdinalIgnoreCase);

    static IEnumerable<string> GetSuffixes(string host)
    {
        while (true)
        {
            yield return host;
            int dot = host.IndexOf('.');
            if (dot < 0) yield break;
            host = host[(dot + 1)..];
        }
    }

    static readonly object LogLock = new();

    static void StartLogger()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                LogSignal.WaitOne();

                while (LogQueue.TryDequeue(out var item))
                {
                    Console.ForegroundColor = item.color;
                    Console.WriteLine(item.text);
                }
            }
        });

        thread.IsBackground = true;
        thread.Start();
    }
    static void LogAsync(string host, string msg, ConsoleColor color = ConsoleColor.White)
    {
        string line = $"{DateTime.UtcNow:o} {host} {msg}";
        LogQueue.Enqueue((color, line));
        LogSignal.Set();
    }
    static async Task Main()
    {
        StartLogger();
        ConfigManager.Init();
        STALL_TIMEOUT_MS = int.Parse(ConfigManager.Get("timeoutMs"));
        VLESS_PORT = int.Parse(ConfigManager.Get("socksPort"));

        FileReaderDomens.init();
        blocklist = FileReaderDomens.readBadDomens();
        whitelist = FileReaderDomens.readWhiteList();

        foreach (var domain in blocklist)
        {
            BadDomains.Add(domain);
        }
        //запускаем tcp слушатель
        var listener = new TcpListener(IPAddress.Loopback, 8888);
        listener.Start();
        LogAsync("","Proxy listening on 127.0.0.1:8888",ConsoleColor.Green);
      //  Console.WriteLine();

        while (true)
        {
            //получаем клиент и гоняем его в хукер клиента
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClient(client);
        }
    }



    static async Task HandleClient(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            client.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.KeepAlive,
            true
            );

            //читаем первую строку с http
            var clientStream = client.GetStream();
            string requestLine = await ReadLineAsync(clientStream);
            if (string.IsNullOrEmpty(requestLine) || !requestLine.StartsWith("CONNECT"))
                return;

            // 🔥 ОБЯЗАТЕЛЬНО съесть все заголовки
            while (true)
            {
                string header = await ReadLineAsync(clientStream);
                if (string.IsNullOrEmpty(header))
                    break; // \r\n — конец заголовков
            }

            if (string.IsNullOrEmpty(requestLine) || !requestLine.StartsWith("CONNECT"))
                return;

            //парсим домен, получаем хост и порт
            string target = requestLine.Split(' ')[1];
            var parts = target.Split(':');
            string host = parts[0];
            int port = int.Parse(parts[1]);

            lock (LogLock)
            {
                LogAsync(host,$"CONNECT {host}:{port}", ConsoleColor.Green);
        

            }

           
            TcpClient server = null;


            bool isFromFile = blocklist.Any(d => host.EndsWith(d, StringComparison.OrdinalIgnoreCase));

            bool skipTunnel = host.EndsWith(".ru") || host.EndsWith(".рф") || host.EndsWith(".su")
                              || whitelist.Any(d => host.EndsWith(d, StringComparison.OrdinalIgnoreCase));


            bool useV2Ray = !skipTunnel &&
    (GetSuffixes(host).Any(s => BadDomains.Contains(s)) || isFromFile);


            // bool useV2Ray = !skipTunnel && BadDomains.ContainsKey(host); old


            long bytesClientToServer = 0;
            long bytesServerToClient = 0;
          

            try
            {
                if (useV2Ray)
                {
                    try
                    {
                        server = await ConnectViaSocks5(host, port, VLESS_PORT, 10000);
                        server.NoDelay = true;
                        server.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                

                        LogAsync(host, isFromFile ? "→ via v2rayN (from badDomens.txt)" : "→ via v2rayN", ConsoleColor.Red);
                    }
                    catch
                    {
                        server = new TcpClient();
                        await server.ConnectAsync(host, port);
                        LogAsync(host, "> direct fallback", ConsoleColor.Yellow);
                    }
                }
                else
                {
                    server = new TcpClient();
                    await server.ConnectAsync(host, port);
                    server.NoDelay = true;
                    server.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    LogAsync(host, isFromFile ? "→ direct (was in badDomens.txt)" : "→ direct", ConsoleColor.Green);

                }
                byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await clientStream.WriteAsync(ok);
                var streamServer = server.GetStream();
                Task t1, t2;
                if (useV2Ray)
                {
                    t1 = Pump(clientStream, streamServer, b => bytesClientToServer += b, CancellationToken.None);
                    t2 = Pump(streamServer, clientStream, b => bytesServerToClient += b, CancellationToken.None);

                }
                else
                {
                    var cts = new CancellationTokenSource();
                    var stallTimer = new System.Timers.Timer(STALL_TIMEOUT_MS);
                    stallTimer.AutoReset = false;
                    stallTimer.Elapsed += (_, __) => cts.Cancel();
                    stallTimer.Start();

                    void Touch() { stallTimer.Stop(); stallTimer.Start(); }

                    t1 = Pump(clientStream, streamServer, b => { bytesClientToServer += b; Touch(); }, cts.Token);
                    t2 = Pump(streamServer, clientStream, b => { bytesServerToClient += b; Touch(); }, cts.Token);
                }
                await Task.WhenAll(t1, t2);
                if(!useV2Ray&& bytesClientToServer == 0)
                {
                    LogAsync(host, "STALL (no payload)", ConsoleColor.DarkRed);
                    BadDomains.Add(host);
                }
                else
                {
                    LogAsync(host, $"OK ({bytesServerToClient} bytes");
                    BadDomains.Remove(host);
                }
            }catch (SocketException ex)
        {
            LogAsync(host, $"TCP_FAIL {ex.SocketErrorCode}");
            BadDomains.Add(host);
        }
        catch (Exception ex)
        {
            LogAsync(host, $"ERROR {ex.GetType().Name}");
            BadDomains.Add(host);
        }
        finally
        {
            server?.Dispose();
        }


 

        }
    }

    static async Task Pump(Stream input, Stream output, Action<int> onBytes, CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024]; // увеличил буфер

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await input.ReadAsync(buffer, 0, buffer.Length, ct);
            }
            catch
            {
                break;
            }

            if (read <= 0) break;

            onBytes(read);
            await output.WriteAsync(buffer, 0, read, ct);
        }
    }





    //я боюсь пытатся понять как оно работает
    // SOCKS5 connect с таймаутом для Windows
    static async Task<TcpClient> ConnectViaSocks5(
    string host, int port,
    int socksPort = 10808,
    int timeoutMs = 5000,
    string socksHost = "127.0.0.1")
    {
        var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);

        // Подключаемся с таймаутом
        var connectTask = client.ConnectAsync(socksHost, socksPort);
        if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs, cts.Token)) != connectTask)
            throw new TimeoutException("SOCKS5 connect timed out");

        var stream = client.GetStream();

        // handshake no-auth
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, 0, 3, cts.Token);
        byte[] response = new byte[2];
        await stream.ReadAsync(response, 0, 2, cts.Token);

        // SOCKS5 CONNECT
        var hostBytes = Encoding.ASCII.GetBytes(host);
        byte[] request = new byte[7 + hostBytes.Length];
        request[0] = 0x05; // SOCKS5
        request[1] = 0x01; // CONNECT
        request[2] = 0x00; // reserved
        request[3] = 0x03; // domain
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xFF);

        await stream.WriteAsync(request, 0, request.Length, cts.Token);

        byte[] resp = new byte[10 + hostBytes.Length];
        await stream.ReadAsync(resp, 0, resp.Length, cts.Token);

        if (resp[1] != 0x00)
            throw new Exception("SOCKS5 connect failed");

        return client;
    }



    string ReadLine(NetworkStream stream)
    {
        var ms = new MemoryStream();
        while (true)
        {
            int b = stream.ReadByte();
            if (b == -1) break;
            ms.WriteByte((byte)b);
            if (ms.Length >= 2)
            {
                var buf = ms.GetBuffer();
                if (buf[ms.Length - 2] == '\r' && buf[ms.Length - 1] == '\n')
                    break;
            }
        }
        return Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length - 2);
    }
    static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var ms = new MemoryStream();
        byte[] buffer = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, 1);
            if (read == 0) break;

            ms.WriteByte(buffer[0]);

            if (ms.Length >= 2)
            {
                var buf = ms.GetBuffer();
                if (buf[ms.Length - 2] == '\r' && buf[ms.Length - 1] == '\n')
                    break;
            }
        }

        return Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length - 2);
    }
}
