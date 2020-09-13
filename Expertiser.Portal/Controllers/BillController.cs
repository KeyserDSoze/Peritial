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
            DateTime now = DateTime.UtcNow;
            var searchingDate = new DateStats()
            {
                From = new DateTime(now.Year, now.Month, 1),
                To = new DateTime(now.Year, now.Month, 1).AddMonths(1).AddMilliseconds(-1),
            };
            return View("Index", (searchingDate, await GetBills(searchingDate)));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index([FromForm] DateStats dateStats)
        {
            dateStats.To = new DateTime(dateStats.To.Year, dateStats.To.Month, dateStats.To.Day, 23, 59, 59, 999);
            return PartialView("_Bill", await GetBills(dateStats));
        }

        private async Task<IEnumerable<UserBillStat>> GetBills(DateStats dateStats)
        {
            Dictionary<string, UserBillStat> bills = new Dictionary<string, UserBillStat>();
            foreach (var billByMember in (await FindCorrectBills()
                .ToListAsync()).GroupBy(x => x.MemberId))
            {
                string memberName = $"{billByMember.FirstOrDefault().Member?.Name} {billByMember.FirstOrDefault().Member?.Surname}";
                bills.Add(billByMember.Key, new UserBillStat
                {
                    UserId = billByMember.Key,
                    Member = memberName
                });
                foreach (var billByContributor in billByMember.GroupBy(x => x.ContributorId))
                {
                    bool isContributorNull = billByContributor == null;
                    string contributorKey = billByContributor.Key ?? billByMember.Key;
                    string contributorName = isContributorNull ? memberName : $"{billByContributor.FirstOrDefault().Contributor?.Name} {billByContributor.FirstOrDefault().Contributor?.Surname}";
                    if (!bills.ContainsKey(contributorKey))
                        bills.Add(contributorKey, new UserBillStat
                        {
                            UserId = contributorKey,
                            Contributor = contributorName
                        });
                    foreach (var bill in billByContributor)
                    {
                        bills[billByMember.Key].Sum(bill);
                        if (billByMember.Key != contributorKey)
                            bills[contributorKey].Sum(bill);
                    }
                }
            }
            return bills.Select(x => x.Value);

            IQueryable<TableExpertise> FindCorrectBills()
            {
                if (!Manager.UserHelperManager.IsManager)
                    return Manager.QueryManager.Bills
                    .Where(x => x.CloseDate >= dateStats.From && x.CloseDate <= dateStats.To && x.ContributorId == Manager.UserHelperManager.Id);
                else
                    return Manager.QueryManager.Bills
                    .Where(x => x.CloseDate >= dateStats.From && x.CloseDate <= dateStats.To);
            }
        }
    }
}