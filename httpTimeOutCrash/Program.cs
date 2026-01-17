using httpTimeOutCrash;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
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
    static readonly ConcurrentDictionary<string, bool> BadDomains = new();
    static string[] blocklist = null;
    static string[] whitelist = null;

    static readonly object LogLock = new();


    static async Task Main()
    {
        ConfigManager.Init();
        STALL_TIMEOUT_MS = int.Parse(ConfigManager.Get("timeoutMs"));
        VLESS_PORT = int.Parse(ConfigManager.Get("socksPort"));

        FileReaderDomens.init();
        blocklist = FileReaderDomens.readBadDomens();
        whitelist = FileReaderDomens.readWhiteList();

        foreach (var domain in blocklist)
        {
            BadDomains.TryAdd(domain, true);
        }
        //запускаем tcp слушатель
        var listener = new TcpListener(IPAddress.Loopback, 8888);
        listener.Start();
        Console.WriteLine("Proxy listening on 127.0.0.1:8888");

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
              var reader = new StreamReader(clientStream, Encoding.ASCII, false, 4096, true);


            //проверяем, если соеденение не connect - игнорируем
            string requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine) || !requestLine.StartsWith("CONNECT"))
                return;

            //парсим домен, получаем хост и порт
            string target = requestLine.Split(' ')[1];
            var parts = target.Split(':');
            string host = parts[0];
            int port = int.Parse(parts[1]);

            lock (LogLock)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"CONNECT {host}:{port}"); //выводим на экран

            }

           
            TcpClient server = null;


            bool isFromFile = blocklist.Any(d => host.EndsWith(d, StringComparison.OrdinalIgnoreCase));

            bool skipTunnel = host.EndsWith(".ru") || host.EndsWith(".рф") || host.EndsWith(".su")
                              || whitelist.Any(d => host.EndsWith(d, StringComparison.OrdinalIgnoreCase));

            bool useV2Ray =
       !skipTunnel &&
       BadDomains.Keys.Any(d => host.EndsWith(d, StringComparison.OrdinalIgnoreCase));

            // bool useV2Ray = !skipTunnel && BadDomains.ContainsKey(host); old



            try
            {


                if (useV2Ray) // если домен печальный, то гоним в VPN
                {
                    try
                    {
                        server = await ConnectViaSocks5(host, port, VLESS_PORT, 5000); // подключение через VPN
                        //aliveSocket
                        server.NoDelay = true;
                        server.Client.SetSocketOption(
                         SocketOptionLevel.Socket,
                         SocketOptionName.KeepAlive,
                         true
                         );

                        if (isFromFile)
                        {
                            Log(host, "→ via v2rayN (from badDomens.txt)", ConsoleColor.DarkCyan);
                        }
                        else
                        {
                            Log(host, "→ via v2rayN", ConsoleColor.Red);
                        }
                    }
                    catch
                    {
                        // fallback на прямое соединение
                        server = new TcpClient();
                        await server.ConnectAsync(host, port);
                        Log(host, "→ direct fallback", ConsoleColor.Yellow);
                    }
                }
                else // домен напрямую
                {
                    server = new TcpClient();
                    await server.ConnectAsync(host, port); // прямое соединение
                    server.NoDelay = true;
                     server.Client.SetSocketOption(
                     SocketOptionLevel.Socket,
                     SocketOptionName.KeepAlive,
                     true
                     );

                    if (isFromFile)
                    {
                        Log(host, "→ direct (was in badDomens.txt)", ConsoleColor.DarkCyan);
                    }
                    else
                    {
                        Log(host, "→ direct", ConsoleColor.Green);
                    }
                }

                // отвечаем браузеру
                byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"); //отвечаем браузеру, что  коннект есть
                await clientStream.WriteAsync(ok);

                var serverStream = server.GetStream(); //получаем поток байтов

                long bytesClientToServer = 0; //количество переданных байт на сервер
                long bytesServerToClient = 0; //количество переданных байт клиенту

                Task t1, t2;
                if (useV2Ray)
                {
                    // 🔥 VPN → вечный туннель
                    t1 = Pump(clientStream, serverStream, b => bytesClientToServer += b, CancellationToken.None);
                    t2 = Pump(serverStream, clientStream, b => bytesServerToClient += b, CancellationToken.None);

                    await Task.WhenAll(t1, t2); //было WhenAny();
                }
                else
                {
                    // direct → можно оставить таймаут
                    var cts = new CancellationTokenSource(STALL_TIMEOUT_MS);

                    t1 = Pump(clientStream, serverStream, b => bytesClientToServer += b, cts.Token);
                    t2 = Pump(serverStream, clientStream, b => bytesServerToClient += b, cts.Token);

                    await Task.WhenAny(
                        Task.WhenAll(t1, t2),
                        Task.Delay(STALL_TIMEOUT_MS)
                    );
                }

                /*
                                var cts = new CancellationTokenSource(STALL_TIMEOUT_MS); //проверяем соеденение с тайм-аутом

                                var t1 = Pump(clientStream, serverStream, b => bytesClientToServer += b, cts.Token); //передаем байты от клиента на сервер
                                var t2 = Pump(serverStream, clientStream, b => bytesServerToClient += b, cts.Token); //передаем байты от сервера клиенту

                                await Task.WhenAny(Task.WhenAll(t1, t2), Task.Delay(STALL_TIMEOUT_MS)); //не понятно что делает*/





                /////

                if (!useV2Ray && bytesServerToClient == 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Log(host, "STALL (no payload)");
                    BadDomains[host] = true; // добавляем в кэш плохих доменов
                    Console.ForegroundColor = ConsoleColor.Green;
                }
                else
                {
                    Log(host, $"OK ({bytesServerToClient} bytes)");
                    BadDomains.TryRemove(host, out _); // если раньше был плохой — удаляем
                }
            }
            catch (SocketException ex)
            {
                Log(host, $"TCP_FAIL {ex.SocketErrorCode}");
                BadDomains[host] = true;
            }
            catch (Exception ex)
            {
                Log(host, $"ERROR {ex.GetType().Name}");
                BadDomains[host] = true;
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    static async Task Pump(Stream input, Stream output, Action<int> onBytes, CancellationToken ct)
    {
        byte[] buffer = new byte[256*1024];

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

            if (read <= 0)
                break;

            onBytes(read);
            await output.WriteAsync(buffer, 0, read, ct);
        }
    }


    static void Log(string host, string result, ConsoleColor color = ConsoleColor.White)
    {

        lock (LogLock)
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.UtcNow:o} {host} {result}");
            Console.ForegroundColor = prevColor;

        }

      
    }


    static void Log(string host, string result)
    {
        lock (LogLock)
        {

            Console.WriteLine($"{DateTime.UtcNow:o} {host} {result}");

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
        await client.ConnectAsync(socksHost, socksPort);

        var stream = client.GetStream();

        // handshake no-auth
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, 0, 3, cts.Token);
        byte[] response = new byte[2];
        await stream.ReadAsync(response, 0, 2, cts.Token);

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
    static void DrawBottomLine(string text)
    {
        int bottomLine = Console.WindowHeight - 1; // последняя видимая строка
        Console.SetCursorPosition(0, bottomLine);  // ставим курсор на последнюю строку
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(text.PadRight(Console.WindowWidth)); // заполняем всю строку пробелами
        Console.ResetColor();
        Console.SetCursorPosition(0, bottomLine - 1); // возвращаем курсор чуть выше, чтобы следующий лог шёл нормально
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
