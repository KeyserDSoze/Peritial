using Expertiser.Base.Utility;
using Expertiser.Database.Models;
using Newtonsoft.Json;
using Rystem.Interfaces.Utility.Tester;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Expertiser.Importer
{
    public class ImportCity : IImporter, ITest
    {
        private const string Uri = "https://raw.githubusercontent.com/matteocontrini/comuni-json/master/comuni.json";
        public bool DoWork(Action<object> action, params string[] args)
        {
            List<City> cities = JsonConvert.DeserializeObject<List<City>>(Request.Make(Uri));
            using (ContextExpertiser context = new ContextExpertiser())
            {
                foreach (City city in cities)
                    context.TableCity.Add(new TableCity()
                    {
                        CountryId = 380,
                        Name = city.Name,
                        PostalCode = JsonConvert.SerializeObject(city.PostalCodes),
                        Province = city.Province.Name,
                        Region = city.Region.Name,
                    });
                context.SaveChanges();
            }
            return true;
        }
    }
    public class City
    {
        [JsonProperty("nome")]
        public string Name { get; set; }
        [JsonProperty("codice")]
        public string Code { get; set; }
        [JsonProperty("provincia")]
        public NamedProperty Province { get; set; }
        [JsonProperty("regione")]
        public NamedProperty Region { get; set; }
        [JsonProperty("sigla")]
        public string Abbreviation { get; set; }
        [JsonProperty("codiceCatastale")]
        public string LandRegistryCode { get; set; }
        [JsonProperty("cap")]
        public List<string> PostalCodes { get; set; }
        [JsonProperty("popolazione")]
        public int Population { get; set; }

    }
    public class NamedProperty
    {
        [JsonProperty("nome")]
        public string Name { get; set; }
    }
}
