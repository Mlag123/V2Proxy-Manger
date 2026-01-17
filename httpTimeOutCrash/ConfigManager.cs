using System;
using System.Collections.Generic;
using System.Text;

namespace httpTimeOutCrash
{



    internal class ConfigManager
    {

        public static String configFile = AppDomain.CurrentDomain.BaseDirectory + "\\" + "config.cfg";

        public static Dictionary<string, string> settings = new();


        public static void Init()
        {
            if (!File.Exists(configFile))
            {
                File.WriteAllLines(configFile, new[]
                {
                    "socksPort=10808",
                    "timeoutMs=2500",
                    "logLevel=Info"
                });
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Config file created: " + configFile);
                Console.ResetColor();

            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Config file exist: " + configFile);
                Console.ResetColor();

            }


            foreach(var line in File.ReadAllLines(configFile))
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains("="))
                    continue;

                var parts = line.Split('=',2);
                var key = parts[0].Trim();
                var value = parts[1].Trim();
                settings[key] = value;
            }

        }

        public static string Get(string key,string defaultValue = "")
        {
            return settings.ContainsKey(key) ? settings[key] : defaultValue;
        }
    }
}
