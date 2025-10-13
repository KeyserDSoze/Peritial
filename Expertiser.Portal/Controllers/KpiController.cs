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

        public async Task<IActionResult> Index()
        {
            var model = new KpiViewModel
            {
                From = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
                To = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1),
                ContributorAssignments = await GetContributorAssignments(),
                ContributorClosingTimes = await GetContributorClosingTimes(),
                ContributorLiquidatedByBranch = await GetContributorLiquidatedByBranch()
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> ExportAssignmentsCsv()
        {
            var data = await GetContributorAssignments();
            var csv = new StringBuilder();

            // Header
            csv.AppendLine("Consulente,Totale Incarichi");

            // Data rows
            foreach (var item in data)
            {
                csv.AppendLine($"\"{item.ContributorName}\",\"{item.TotalAssignments}\"");
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            var fileName = $"Incarichi_In_Pancia_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            return File(bytes, "text/csv", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExportClosingTimesCsv()
        {
            var data = await GetContributorClosingTimes();
            var csv = new StringBuilder();

            // Header
            csv.AppendLine("Consulente,Pratiche Chiuse,Giorni Medi Chiusura");

            // Data rows
            foreach (var item in data)
            {
                csv.AppendLine($"\"{item.ContributorName}\",\"{item.ClosedCases}\",\"{item.AverageClosingDays:F2}\"");
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            var fileName = $"Tempo_Medio_Chiusura_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            return File(bytes, "text/csv", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExportLiquidatedByBranchCsv()
        {
            var data = await GetContributorLiquidatedByBranch();
            var csv = new StringBuilder();

            // Header
            csv.AppendLine("Consulente,Ramo,Numero Pratiche,Totale Liquidato,Liquidato Medio");

            // Data rows
            foreach (var item in data)
            {
                csv.AppendLine($"\"{item.ContributorName}\",\"{item.BranchName}\",\"{item.TotalCases}\",\"{item.TotalLiquidated:F2}\",\"{item.AverageLiquidated:F2}\"");
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            var fileName = $"Liquidato_Per_Ramo_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            return File(bytes, "text/csv", fileName);
        }

        private async Task<List<ContributorAssignmentsKpi>> GetContributorAssignments()
        {
            var query = from e in Manager.Context.TableExpertise
                        join a in Manager.Context.AspNetUsers on e.ContributorId equals a.Id
                        where e.CloseDate == null || e.Status < 5
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

        private async Task<List<ContributorClosingTimeKpi>> GetContributorClosingTimes()
        {
            var query = from e in Manager.Context.TableExpertise
                        join a in Manager.Context.AspNetUsers on e.ContributorId equals a.Id
                        where e.CloseDate != null && e.AssignmentDate != null
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

        private async Task<List<ContributorLiquidatedByBranchKpi>> GetContributorLiquidatedByBranch()
        {
            var query = from e in Manager.Context.TableExpertise
                        join a in Manager.Context.AspNetUsers on e.ContributorId equals a.Id
                        join b in Manager.Context.TableBranch on e.BranchId equals b.Id
                        where e.Liquidated != null && e.Status >= 5
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
