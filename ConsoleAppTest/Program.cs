using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleAppTest
{
    class Program
    {
        static void Main(string[] args)s
        {
            doMyAsyncMethod();
        }

        private static async Task doMyAsyncMethod()
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}
