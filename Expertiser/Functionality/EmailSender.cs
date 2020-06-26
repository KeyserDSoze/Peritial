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
            new Manager(null, null, null, null).ServiceManager.EmailSender.Send("a.rapiti@vetrya.com", "as good as you", "as good as you");
        }
    }
}
