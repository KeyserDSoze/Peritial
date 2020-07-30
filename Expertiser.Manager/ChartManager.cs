using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Azure.Documents;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Rystem.Data;
using Rystem.Web;
using Rystem.Web.Backoffice;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Expertiser
{
    public class ChartManager
    {
        private readonly Manager Manager;
        public ChartManager(Manager manager)
            => this.Manager = manager;
        public async Task<DataChart> GetExpertisesStatus(IStringLocalizer localizer, DateTime from = default, DateTime to = default)
        {
            var status = (await ExpertisesByDate(from, to).ToListAsync()).GroupBy(x => x.Status);
            return new MyChart(localizer["Status"], status.ToDictionary(x => localizer[$"{(StatusType)x.Key}"].Value, x => new DataModel() { Data = new List<decimal> { (decimal)x.Count() } })).ToDataChart(ChartType.Pie);
        }
        public async Task<DataChart> GetContributorStatus(IStringLocalizer localizer, string contributorId, DateTime from = default, DateTime to = default)
        {
            var status = (await ExpertisesByDate(from, to).Where(x => x.ContributorId == contributorId).ToListAsync()).GroupBy(x => x.Status);
            var contributor = await Manager.Context.AspNetUsers.FirstOrDefaultAsync(x => x.Id == contributorId);
            return new MyChart($"{localizer["Status"]} {contributor.UserName}", status.ToDictionary(x => localizer[$"{(StatusType)x.Key}"].Value, x => new DataModel() { Data = new List<decimal> { (decimal)x.Count() } })).ToDataChart(ChartType.Pie);
        }
        public async Task<DataChart> GetClosingDay(IStringLocalizer localizer, DateTime from = default, DateTime to = default)
        {
            var status = (await ExpertisesByDate(from, to).ToListAsync()).GroupBy(x => x.Status);
            var dictionary = new Dictionary<int, Dictionary<string, int>>();
            //foreach (var s in status)
            //{
            //    if (!dictionary.ContainsKey(s.Key))
            //        dictionary.Add(s.Key, new Dictionary<string, int>());
            //    foreach (var g in s.Where(y => y.CloseDate.HasValue).GroupBy(y => $"{y.CloseDate.Value.Month}{y.CloseDate.Value.Day}"))
            //    {

            //    }
            //}
            //return new MyChart(localizer["Trend"], status.ToDictionary(x => localizer[$"{(StatusType)x.Key}"].Value,
            //    x => new DataModel() { Data = x.Where(y => y.CloseDate.HasValue).GroupBy(y => $"{y.CloseDate.Value.Month}{y.CloseDate.Value.Day}").Select(y => y.Count()) })).ToDataChart();
            return default;
        }
        //public async Task<DataChart> GetClosingDistance(IStringLocalizer localizer, DateTime from = default, DateTime to = default)
        //{
        //}
        private IQueryable<TableExpertise> ExpertisesByDate(DateTime from, DateTime to)
        {
            if (from == default)
                from = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            if (to == default)
                to = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1);
            return Manager.Context.TableExpertise.Where(x => !x.AssignmentDate.HasValue || (x.AssignmentDate.Value >= from && x.AssignmentDate.Value <= to));
        }
        private class MyChart : IChart
        {
            public string X { get; }
            public string Y { get; }
            public string Title { get; }
            public Dictionary<string, DataModel> Datasets { get; }
            public MyChart(string title, Dictionary<string, DataModel> datasets) : this(title, datasets, null, null) { }
            public MyChart(string title, Dictionary<string, DataModel> datasets, string x, string y)
            {
                this.Title = title;
                this.Datasets = datasets;
                this.X = x;
                this.Y = y;
            }
        }
    }
}