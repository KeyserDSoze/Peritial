using Expertiser.Database.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser.Portal.Models
{
    public class UserBillStat : BillStat
    {
        public string UserId { get; set; }
        public List<BillStat> Bills { get; } = new List<BillStat>();
        public override void Sum(TableExpertise expertise)
        {
            base.Sum(expertise);
            if (expertise.ContributorId == this.UserId || (expertise.MemberId == this.UserId && expertise.ContributorId == null))
                this.PersonalTotal += expertise.TotalPercentage ?? 0 - expertise.TotalPercentageNoExtra ?? 0;
            this.Bills.Add(expertise);
        }
    }
    public class BillStat
    {
        public int ExpertiseId { get; set; }
        public string ConcreteId { get; set; }
        public string Member { get; set; }
        public string Contributor { get; set; }
        public decimal TotalPercentage { get; set; }
        public decimal TotalBill { get; set; }
        public decimal TotalMember { get; set; }
        public decimal TotalExtra { get; set; }
        public decimal PersonalTotal { get; set; }
        public decimal TotalPercentageNoExtra => TotalPercentage - TotalExtra;
        public bool HasValue => this.TotalBill > 0 || this.TotalMember > 0 || this.TotalPercentage > 0;
        public virtual void Sum(TableExpertise expertise)
        {
            this.TotalBill += expertise.TotalBill ?? 0;
            this.TotalMember += expertise.TotalMember ?? 0;
            if (!(expertise.MemberId == expertise.ContributorId || expertise.ContributorId == null))
            {
                this.TotalPercentage += expertise.TotalPercentage ?? 0;
                this.TotalExtra += expertise.TotalExtra ?? 0;
            }
        }
        public static implicit operator BillStat(TableExpertise expertise)
            => new BillStat()
            {
                ExpertiseId = expertise.Id,
                ConcreteId = expertise.ConcreteId,
                Contributor = $"{expertise.Contributor?.Name} {expertise.Contributor?.Surname}",
                Member = $"{expertise.Member?.Name} {expertise.Member?.Surname}",
                TotalBill = expertise.TotalBill ?? 0,
                TotalExtra = expertise.TotalExtra ?? 0,
                TotalPercentage = expertise.TotalPercentage ?? 0,
                TotalMember = expertise.TotalMember ?? 0,
            };
    }
}