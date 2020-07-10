using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Expertiser.Portal.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Rystem.Web.Backoffice;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.All)]
    public class BillController : Controller
    {
        private readonly Manager Manager;
        private readonly IStringLocalizer<SharedResource> StringLocalizer;
        private List<string> Headers = new List<string>();
        public BillController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer)
        {
            Manager = manager;
            this.StringLocalizer = stringLocalizer;
            this.Headers.Add(this.StringLocalizer["ConcreteId"]);
            this.Headers.Add(this.StringLocalizer["Member"]);
            this.Headers.Add(this.StringLocalizer["Contributor"]);
            this.Headers.Add(this.StringLocalizer["Bill"]);
            this.Headers.Add(this.StringLocalizer["Amount"]);
            this.Headers.Add(this.StringLocalizer["Percentage"]);
        }

        public async Task<IActionResult> Index()
            => View("Index",
                (this.Headers, await GetBills(new DateStats()
                {
                    From = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
                    To = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1),
                })));
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index([FromForm] DateStats dateStats)
            => View("Index", (this.Headers, await GetBills(dateStats)));
        private async Task<IEnumerable<NavigationValue>> GetBills(DateStats dateStats)
        {
            List<BillStat> bills = new List<BillStat>();
            foreach (var billByMember in (await Manager.QueryManager.Bills
                .Where(x => x.Accident.AccidentDate >= dateStats.From && x.Accident.AccidentDate < dateStats.To)
                .ToListAsync()).GroupBy(x => x.MemberId))
            {
                BillStat memberTotalBill = new BillStat()
                {
                    Member = billByMember.FirstOrDefault().Member?.Name,
                };
                foreach (var billByContributor in billByMember.GroupBy(x => x.ContributorId))
                {
                    BillStat contributorTotalBill = new BillStat()
                    {
                        Member = billByMember.FirstOrDefault().Member?.Name,
                        Contributor = billByContributor.FirstOrDefault().Contributor?.Name
                    };
                    foreach (var bill in billByContributor)
                    {
                        memberTotalBill.Add(bill);
                        contributorTotalBill.Add(bill);
                        bills.Add(new BillStat()
                        {
                            ConcreteId = bill.ConcreteId,
                            Member = bill.Member?.Name,
                            Contributor = bill.Contributor?.Name,
                            TotalBill = bill.TotalBill ?? 0,
                            TotalMember = bill.TotalMember ?? 0,
                            TotalPercentage = bill.TotalPercentage ?? 0
                        });
                    }
                    bills.Add(contributorTotalBill);
                }
                bills.Add(memberTotalBill);
            }
            List<NavigationValue> values = new List<NavigationValue>();
            foreach (var bill in bills.Where(x => x.HasValue))
            {
                NavigationValue value = new NavigationValue();
                value.Elements.Add(new NavigationObject(bill.ConcreteId, null, null));
                value.Elements.Add(new NavigationObject(bill.Contributor, null, null));
                value.Elements.Add(new NavigationObject(bill.Member, null, null));
                value.Elements.Add(new NavigationObject(bill.TotalBill.ToString("N2"), null, null));
                value.Elements.Add(new NavigationObject(bill.TotalPercentage.ToString("N2"), null, null));
                value.Elements.Add(new NavigationObject(bill.TotalMember.ToString("N2"), null, null));
                values.Add(value);
            }
            return values;
        }
    }
}