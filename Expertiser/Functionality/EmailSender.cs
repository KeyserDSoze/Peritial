using Rystem.UnitTest;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser.Functionality
{
    public class EmailSenderTest : IUnitTest
    {
        public async Task DoWorkAsync(Action<object> action, UnitTestMetrics metrics, params string[] args)
        {
            await new Manager(null, null, null, null).ServiceManager.EmailSender.SendEmailAsync("a.rapiti@vetrya.com", "as good as you", "as good as you");
        }
    }
}
