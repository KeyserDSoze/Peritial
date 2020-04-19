using Expertiser.Database.Models;
using Rystem.Interfaces.Utility.Tester;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Expertiser.Importer
{
    public class ImportBranch : IImporter, ITest
    {
        public bool DoWork(Action<object> action, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                foreach (var t in new BranchReader().Get())
                    context.TableBranch.Add(t);
                context.SaveChanges();
            }
            return true;
        }
    }
    public class BranchReader
    {
        private static Regex Regex = new Regex("<option[^<]*<");
        public List<TableBranch> Get()
        {
            List<TableBranch> branches = new List<TableBranch>();
            using StreamReader reader = new StreamReader(@"C:\Users\aless\OneDrive\Desktop\ramo.html");
            foreach (var t in Regex.Matches(reader.ReadToEnd()))
            {
                string value = t.ToString();
                string externalId = value.Split('"')[1];
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
