using Expertiser.Portal.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser.Portal.Controllers
{
    [Authorize]
    public class KpiController : Controller
    {
        private readonly Manager Manager;
        public KpiController(Manager manager)
            => Manager = manager;

        // Index now accepts optional date range. Defaults to the last 3 months (inclusive).
        public async Task<IActionResult> Index(DateTime? from, DateTime? to)
        {
            // toDateInclusive is today by default
            var toDateInclusive = to?.Date ?? DateTime.UtcNow.Date;
            // fromDate default = same day 3 months earlier
            var fromDate = from?.Date ?? toDateInclusive.AddMonths(-3);

            // use exclusive upper bound for queries
            var toDateExclusive = toDateInclusive.AddDays(1);

            var model = new KpiViewModel
            {
                From = fromDate,
                To = toDateInclusive,
                ContributorAssignments = await GetContributorAssignments(fromDate, toDateExclusive),
                ContributorClosingTimes = await GetContributorClosingTimes(fromDate, toDateExclusive),
                ContributorLiquidatedByBranch = await GetContributorLiquidatedByBranch(fromDate, toDateExclusive)
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> ExportAssignmentsCsv(DateTime? from, DateTime? to)
        {
            var toDateInclusive = to?.Date ?? DateTime.UtcNow.Date;
            var fromDate = from?.Date ?? toDateInclusive.AddMonths(-3);
            var toDateExclusive = toDateInclusive.AddDays(1);

            var data = await GetContributorAssignments(fromDate, toDateExclusive);
            var csv = new StringBuilder();

            // Header
            csv.AppendLine("Consulente,Totale Incarichi");

            // Data rows
            foreach (var item in data)
            {
                csv.AppendLine($"\"{item.ContributorName}\",\"{item.TotalAssignments}\"");
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            var fileName = $"Incarichi_In_Pancia_{fromDate:yyyyMMdd}_{toDateInclusive:yyyyMMdd}.csv";

            return File(bytes, "text/csv", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExportClosingTimesCsv(DateTime? from, DateTime? to)
        {
            var toDateInclusive = to?.Date ?? DateTime.UtcNow.Date;
            var fromDate = from?.Date ?? toDateInclusive.AddMonths(-3);
            var toDateExclusive = toDateInclusive.AddDays(1);

            var data = await GetContributorClosingTimes(fromDate, toDateExclusive);
            var csv = new StringBuilder();

            // Header
            csv.AppendLine("Consulente,Pratiche Chiuse,Giorni Medi Chiusura");

            // Data rows
            foreach (var item in data)
            {
                csv.AppendLine($"\"{item.ContributorName}\",\"{item.ClosedCases}\",\"{item.AverageClosingDays:F2}\"");
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            var fileName = $"Tempo_Medio_Chiusura_{fromDate:yyyyMMdd}_{toDateInclusive:yyyyMMdd}.csv";

            return File(bytes, "text/csv", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExportLiquidatedByBranchCsv(DateTime? from, DateTime? to)
        {
            var toDateInclusive = to?.Date ?? DateTime.UtcNow.Date;
            var fromDate = from?.Date ?? toDateInclusive.AddMonths(-3);
            var toDateExclusive = toDateInclusive.AddDays(1);

            var data = await GetContributorLiquidatedByBranch(fromDate, toDateExclusive);
            var csv = new StringBuilder();

            // Header
            csv.AppendLine("Consulente,Ramo,Numero Pratiche,Totale Liquidato,Liquidato Medio");

            // Data rows
            foreach (var item in data)
            {
                csv.AppendLine($"\"{item.ContributorName}\",\"{item.BranchName}\",\"{item.TotalCases}\",\"{item.TotalLiquidated:F2}\",\"{item.AverageLiquidated:F2}\"");
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            var fileName = $"Liquidato_Per_Ramo_{fromDate:yyyyMMdd}_{toDateInclusive:yyyyMMdd}.csv";

            return File(bytes, "text/csv", fileName);
        }

        // All Get... methods accept a [fromDate, toDateExclusive) range and filter on AssignmentDate.
        private async Task<List<ContributorAssignmentsKpi>> GetContributorAssignments(DateTime fromDate, DateTime toDateExclusive)
        {
            var query = from e in Manager.Context.TableExpertise
                        join a in Manager.Context.AspNetUsers on e.ContributorId equals a.Id
                        where (e.CloseDate == null || e.Status < 5)
                              && e.AssignmentDate != null
                              && e.AssignmentDate >= fromDate
                              && e.AssignmentDate < toDateExclusive
                        group e by new { a.Id, a.UserName, a.Name, a.Surname } into g
                        orderby g.Count() descending
                        select new ContributorAssignmentsKpi
                        {
                            ContributorId = g.Key.Id,
                            ContributorName = g.Key.Name != null && g.Key.Surname != null
                                ? $"{g.Key.Name} {g.Key.Surname}"
                                : g.Key.UserName,
                            TotalAssignments = g.Count()
                        };

            return await query.ToListAsync();
        }

        private async Task<List<ContributorClosingTimeKpi>> GetContributorClosingTimes(DateTime fromDate, DateTime toDateExclusive)
        {
            var query = from e in Manager.Context.TableExpertise
                        join a in Manager.Context.AspNetUsers on e.ContributorId equals a.Id
                        where e.CloseDate != null
                              && e.AssignmentDate != null
                              && e.AssignmentDate >= fromDate
                              && e.AssignmentDate < toDateExclusive
                        group e by new { a.Id, a.UserName, a.Name, a.Surname } into g
                        select new ContributorClosingTimeKpi
                        {
                            ContributorId = g.Key.Id,
                            ContributorName = g.Key.Name != null && g.Key.Surname != null
                                ? $"{g.Key.Name} {g.Key.Surname}"
                                : g.Key.UserName,
                            ClosedCases = g.Count(),
                            AverageClosingDays = g.Average(x => EF.Functions.DateDiffDay(x.AssignmentDate, x.CloseDate)) ?? 0
                        };

            return await query.OrderBy(x => x.AverageClosingDays).ToListAsync();
        }

        private async Task<List<ContributorLiquidatedByBranchKpi>> GetContributorLiquidatedByBranch(DateTime fromDate, DateTime toDateExclusive)
        {
            var query = from e in Manager.Context.TableExpertise
                        join a in Manager.Context.AspNetUsers on e.ContributorId equals a.Id
                        join b in Manager.Context.TableBranch on e.BranchId equals b.Id
                        where e.Liquidated != null
                              && e.Status >= 5
                              && e.AssignmentDate != null
                              && e.AssignmentDate >= fromDate
                              && e.AssignmentDate < toDateExclusive
                        group e by new { a.Id, a.UserName, a.Name, a.Surname, e.BranchId, b.Label } into g
                        orderby g.Key.UserName, g.Key.BranchId
                        select new ContributorLiquidatedByBranchKpi
                        {
                            ContributorId = g.Key.Id,
                            ContributorName = g.Key.Name != null && g.Key.Surname != null
                                ? $"{g.Key.Name} {g.Key.Surname}"
                                : g.Key.UserName,
                            BranchId = g.Key.BranchId,
                            BranchName = g.Key.Label,
                            TotalCases = g.Count(),
                            TotalLiquidated = g.Sum(x => x.Liquidated.Value),
                            AverageLiquidated = g.Average(x => x.Liquidated.Value)
                        };

            return await query.ToListAsync();
        }
    }
}
