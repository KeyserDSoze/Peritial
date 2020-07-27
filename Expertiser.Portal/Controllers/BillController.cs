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
        private List<string> Headers(string amountOf) => new List<string>
        {
            this.StringLocalizer["ConcreteId"],
            this.StringLocalizer["Member"],
            this.StringLocalizer["Contributor"],
            this.StringLocalizer["Bill"],
            amountOf,
            this.StringLocalizer["Percentage"]
        };
        public BillController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer)
        {
            Manager = manager;
            this.StringLocalizer = stringLocalizer;
        }

        public async Task<IActionResult> Index()
        {
            var now = new DateStats()
            {
                From = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
                To = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1).AddMilliseconds(-1),
            };
            return View("Index", (now, await GetBills(now)));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index([FromForm] DateStats dateStats)
        {
            dateStats.To = new DateTime(dateStats.To.Year, dateStats.To.Month, dateStats.To.Day, 23, 59, 59, 999);
            return PartialView("_Bill", await GetBills(dateStats));
        }

        private async Task<IEnumerable<BillModel>> GetBills(DateStats dateStats)
        {
            List<BillModel> bills = new List<BillModel>();
            foreach (var billByMember in (await FindCorrectBills()
                .ToListAsync()).GroupBy(x => x.MemberId))
            {
                string memberName = $"{billByMember.FirstOrDefault().Member?.Name} {billByMember.FirstOrDefault().Member?.Surname}";
                BillStat memberTotalBill = new BillStat()
                {
                    Member = memberName,
                    ConcreteId = StringLocalizer["Total"]
                };
                foreach (var billByContributor in billByMember.GroupBy(x => x.ContributorId))
                {
                    string contributorName = $"{billByMember.FirstOrDefault().Contributor?.Name} {billByMember.FirstOrDefault().Contributor?.Surname}";
                    BillModel billModel = new BillModel
                    {
                        Title = $"{contributorName} {StringLocalizer["for"]} {memberName}",
                        Headers = this.Headers($"{this.StringLocalizer["for"]} {memberName}")
                    };
                    BillStat contributorTotalBill = new BillStat()
                    {
                        Member = memberName,
                        Contributor = contributorName,
                        ConcreteId = StringLocalizer["Total"]
                    };
                    List<NavigationValue> billsByContributor = new List<NavigationValue>();
                    foreach (var bill in billByContributor)
                    {
                        memberTotalBill.Sum(bill);
                        contributorTotalBill.Sum(bill);
                        TransformToNavigationValueAndAdd(new BillStat()
                        {
                            ConcreteId = bill.ConcreteId,
                            Member = memberName,
                            Contributor = contributorName,
                            TotalBill = Manager.UserHelperManager.IsManager ? bill.TotalBill ?? 0 : 0,
                            TotalMember = Manager.UserHelperManager.IsManager ? bill.TotalMember ?? 0 : 0,
                            TotalPercentage = bill.TotalPercentage ?? 0
                        }, x => billsByContributor.Add(x));
                    }
                    TransformToNavigationValueAndAdd(contributorTotalBill, x => billsByContributor.Add(x));
                    billModel.Values = billsByContributor;
                    bills.Add(billModel);
                }
                if (Manager.UserHelperManager.IsManager)
                {
                    List<NavigationValue> billsByMember = new List<NavigationValue>();
                    TransformToNavigationValueAndAdd(memberTotalBill, x => billsByMember.Add(x));
                    bills.Add(new BillModel
                    {
                        Title = $"{StringLocalizer["Total"]} {StringLocalizer["of"]} {memberName}",
                        Headers = this.Headers($"{this.StringLocalizer["of"]} {memberName}"),
                        Values = billsByMember
                    });
                }
            }
            return bills.Where(x => x.Values.Any());

            IQueryable<TableExpertise> FindCorrectBills()
            {
                if (!Manager.UserHelperManager.IsManager)
                    return Manager.QueryManager.Bills
                    .Where(x => x.Accident.AccidentDate >= dateStats.From && x.Accident.AccidentDate <= dateStats.To && x.ContributorId == Manager.UserHelperManager.Id);
                else
                    return Manager.QueryManager.Bills
                    .Where(x => x.Accident.AccidentDate >= dateStats.From && x.Accident.AccidentDate <= dateStats.To);
            }

            void TransformToNavigationValueAndAdd(BillStat bill, Action<NavigationValue> adder)
            {
                if (bill.HasValue)
                {
                    NavigationValue value = new NavigationValue();
                    value.Elements.Add(new NavigationObject(bill.ConcreteId, null, null));
                    value.Elements.Add(new NavigationObject(bill.Member, null, null));
                    value.Elements.Add(new NavigationObject(bill.Contributor, null, null));
                    value.Elements.Add(new NavigationObject(bill.TotalBill.ToString("N2"), null, null));
                    value.Elements.Add(new NavigationObject(bill.TotalMember.ToString("N2"), null, null));
                    value.Elements.Add(new NavigationObject(bill.TotalPercentage.ToString("N2"), null, null));
                    adder(value);
                }
            }
        }
    }
}