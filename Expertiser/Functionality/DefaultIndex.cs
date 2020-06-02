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
            var q = new DefaultIndex<Alo>()
                .Include(x => x.Val)
                .Include(x => x.Qq)
                .Include(x => x.Flo)
                    .ThenInclude(x => x.Olzo)
                .Include(x => x.Flo)
                    .ThenInclude(x => x.Flo2)
                        .ThenInclude(x => x.De);
            foreach (string val in q.Headers())
                Console.WriteLine(val);
            foreach (string val in q.Values(new Alo()))
                Console.WriteLine(val);
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
            public IEnumerable<Flo2> Flo2 { get; set; } = new List<Flo2>() { new Flo2("dsdaaaaaas"), new Flo2("kukkaraccia") };
        }
        public class Flo2
        {
            public string De { get; set; }
            public Flo2(string de) => this.De = de;
        }
    }
}
