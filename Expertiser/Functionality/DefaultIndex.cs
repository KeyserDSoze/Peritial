using Microsoft.Extensions.Localization;
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
        public Task DoWorkAsync(Action<object> action, UnitTestMetrics metrics, params string[] args)
        {
            List<Alo> alos = new List<Alo>() { new Alo(), new Alo(), new Alo() };
            //var q = new DefaultIndex<Alo>(alos)
            //    .Include(x => x.Val)
            //    .Include(x => x.Qq)
            //    .Include(x => x.Flo2, new PropertyOptions() { OutputType = OutputType.Count })
            //    .Include(x => x.Flo)
            //        .ThenInclude(x => x.Olzo)
            //    .Include(x => x.Flo)
            //        .ThenInclude(x => x.Flo2)
            //            .ThenInclude(x => x.De);
            //Console.WriteLine(string.Join(" | ", q.Headers()));
            //Console.WriteLine("-----------------");
            //foreach (IEnumerable<string> vals in q.Values())
            //    Console.WriteLine(string.Join(" | ", vals));
            return Task.FromResult(true);
        }

        public class Alo
        {
            public string Val { get; set; } = "dd";
            public string Qq { get; set; } = "fg";
            public Flo Flo { get; set; } = new Flo();
            public IEnumerable<Flo2> Flo2 { get; set; } = new List<Flo2>() { new Flo2("dsdaaaaaas"), new Flo2("kukkaraccia") };
        }
        public class Flo
        {
            public string Olzo { get; set; } = "dsds";
            public IEnumerable<Flo2> Flo2 { get; set; } = new List<Flo2>() { new Flo2("dsdaaaaaas"), new Flo2("kukkaraccia") };
            public Flo2[] Flo3 { get; set; } = new Flo2[2] { new DefaultIndex.Flo2("aaaaaa"), new DefaultIndex.Flo2("mamnd") };
        }
        public class Flo2
        {
            public string De { get; set; }
            public Flo2(string de) => this.De = de;
        }
    }
}
