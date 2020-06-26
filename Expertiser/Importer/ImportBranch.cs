using Expertiser.Database.Models;
using Rystem.UnitTest;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Expertiser.Importer
{
    public class ImportBranch : IImporter, IUnitTest
    {
        public async Task DoWorkAsync(Action<object> action, UnitTestMetrics metrics, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                foreach (var t in new BranchReader().Get())
                    context.TableBranch.Add(t);
                await context.SaveChangesAsync();
            }
        }
    }
    public class BranchReader
    {
        private static readonly Regex Regex = new Regex("<option[^<]*<");
        public List<TableBranch> Get()
        {
            List<TableBranch> branches = new List<TableBranch>();
            using StreamReader reader = new StreamReader(@"C:\Users\aless\OneDrive\Desktop\ramo.html");
            foreach (var t in Regex.Matches(reader.ReadToEnd()))
            {
                string value = t.ToString();
                _ = value.Split('"')[1];
                string name = value.Split('>')[1].Trim('<');
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                branches.Add(new TableBranch()
                {
                    Label = name,
                    BranchTypeId = 1,
                });
            }
            return branches;
        }
    }
}
