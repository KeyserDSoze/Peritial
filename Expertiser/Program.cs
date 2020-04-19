using Rystem.Interfaces.Utility.Tester;
using System;

namespace Expertiser
{
    class Program
    {
        static void Main(string[] args)
        {
            var testMachine = new TestMachine<IImporter>("Expertiser");
            testMachine.Start();
        }
    }
}
