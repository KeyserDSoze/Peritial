using Rystem.UnitTest;
using System;
using System.Threading.Tasks;

namespace Expertiser
{
    class Program
    {
        static async Task Main()
        {
            //var testMachine = new ConsoleMachine<IImporter>("Expertiser");
            //testMachine.Start();
            await new EmailSender().SendEmailAsync("alessandro.rapiti44@gmail.com", "aloah", "dasdasdsad");
        }
    }
}
