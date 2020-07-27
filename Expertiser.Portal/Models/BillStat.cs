using Expertiser.Database.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser.Portal.Models
{
    public class BillStat
    {
        public string ConcreteId { get; set; }
        public string Member { get; set; }
        public string Contributor { get; set; }
        public decimal TotalPercentage { get; set; }
        public decimal TotalBill { get; set; }
        public decimal TotalMember { get; set; }
        public bool HasValue => this.TotalBill > 0 || this.TotalMember > 0 || this.TotalPercentage > 0;
        public void Sum(TableExpertise expertise)
        {
            this.TotalPercentage += expertise.TotalPercentage ?? 0;
            this.TotalMember += expertise.TotalMember ?? 0;
            this.TotalBill += expertise.TotalBill ?? 0;
        }
    }
}