using Rystem.UnitTest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser.Functionality
{
    public class DefaultIndex : IUnitTest
    {
        public Task<bool> DoWorkAsync(Action<object> action, params string[] args)
        {
            var q = new DefaultIndex<Alo>(new Alo())
                .Include(x => x.Val)
                .Include(x => x.Qq)
                .Include(x => x.Flo)
                    .ThenInclude(x => x.Olzo)
                .Include(x => x.Flo)
                    .ThenInclude(x => x.Flo2)
                        .ThenInclude(x => x.De);
            return Task.FromResult(true);
        }
        public class Alo
        {
            public string Val { get; set; } = "dd";
            public string Qq { get; set; } = "fg";
            public Flo Flo { get; set; } = new Flo();
        }
        public class Flo
        {
            public string Olzo { get; set; } = "dsds";
            public Flo2 Flo2 { get; set; } = new Flo2();
        }
        public class Flo2
        {
            public string De { get; set; } = "dsdaaaaaas";
        }
    }
}
