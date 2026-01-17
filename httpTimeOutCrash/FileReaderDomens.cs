using System;
using System.Collections.Generic;
using System.Text;

namespace httpTimeOutCrash
{
    internal class FileReaderDomens
    {

        static string fullPath = AppDomain.CurrentDomain.BaseDirectory + "\\" + "blocklist.txt";
        static string fullPathWhiteList = AppDomain.CurrentDomain.BaseDirectory + "\\" + "whitelist.txt";


        public static void init()
        {
            if (!File.Exists(fullPath))
            {
                File.Create(fullPath).Dispose();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("File created: " + fullPath);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGreen;

                Console.WriteLine("File exists: " + fullPath);
            }

            if (!File.Exists(fullPathWhiteList))
            {
                File.Create(fullPathWhiteList).Dispose();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine("WhiteList exist");
                Console.ResetColor();
            }

        }


        public static string[] readWhiteList()
        {
            string[] whiteList = File.ReadAllLines(fullPathWhiteList);

            for (int i = 0; i < whiteList.Length; i++)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Domain from whitelist: " + whiteList[i]);
            }
            return whiteList;
        }


        public static string[] readBadDomens()
        {
   

            string[] badDomens = File.ReadAllLines(fullPath);
            for (int i = 0; i < badDomens.Length; i++)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Bad domen from file: " + badDomens[i]);
            }
            return badDomens;

        }

    }
}
