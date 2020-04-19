using Expertiser.Base.Utility;
using Expertiser.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Expertiser
{
    public class AllCities
    {
        //Archivio istat https://www.istat.it/it/archivio/6789
        private const string Uri = "https://captivesportalstorage.blob.core.windows.net/staticcontent/italiancities.csv";
        public static readonly List<Comune> Comunes = new List<Comune>();
        static AllCities()
        {
            foreach (string city in Request.Make(Uri).Skip(3))
                Comunes.Add(new Comune() { Name = city.Split(';')[5] });
            Comunes = Comunes.OrderBy(x => x.Name.Length).ToList();
        }
    }
}
