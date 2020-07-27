using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rystem.Web.Backoffice;

namespace Expertiser.Portal.Models
{
    public class BillModel
    {
        public string Title { get; set; }
        public List<string> Headers { get; set; }
        public IEnumerable<NavigationValue> Values { get; set; }
    }
}