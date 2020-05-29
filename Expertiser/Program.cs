using Rystem.UnitTest;
using System;

namespace Expertiser
{
    class Program
    {
        static void Main()
        {
            var testMachine = new ConsoleMachine<IImporter>("Expertiser");
            testMachine.Start();
        }
    }
}
