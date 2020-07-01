using Expertiser.Base.Utility;
using Expertiser.Database.Models;
using Newtonsoft.Json;
using Rystem.UnitTest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser.Importer
{
    public class ImportRome : IImporter, IUnitTest
    {
        public async Task DoWorkAsync(Action<object> action, UnitTestMetrics metrics, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                var roma = context.TableCity.FirstOrDefault(x => x.Name == "Roma");
                List<string> postalCodes = roma.PostalCodes;
                // for (int i = 100; i < 118; i++)
                //    postalCodes.Add($"00{i}");
                roma.PostalCode = JsonConvert.SerializeObject(postalCodes);
                context.Update(roma);
                await context.SaveChangesAsync();
            }
        }
    }
}