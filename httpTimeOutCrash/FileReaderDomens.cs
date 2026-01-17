using System;
using System.Collections.Generic;
using System.Text;

namespace httpTimeOutCrash
{
    internal class FileReaderDomens
    {


        static string fileName = "badDomens.txt";
        static string fullPath = AppDomain.CurrentDomain.BaseDirectory + "\\" + "badDomens.txt";


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
        }


        public static string[] readBadDomens()
        {
            /*  if (!File.Exists(fullPath){

              }*/

            string[] badDomens = File.ReadAllLines(fullPath);
            for (int i = 0; i < badDomens.Length; i++)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Bad domen from file: " + badDomens[i]);
            }
            return badDomens;
            Console.ForegroundColor = ConsoleColor.Green;

        }

    }
}
