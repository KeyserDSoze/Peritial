using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.OData.Edm;
using Rystem.Web;
using Rystem.Web.Backoffice;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.All)]
    public class AccidentController : BaseController<TableAccident>
    {
        private readonly int StatusId = -2;
        private readonly int Year = int.MaxValue;
        public AccidentController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer, IHttpContextAccessor httpContextAccessor) : base(manager, stringLocalizer, sharedLocalizer)
        {
            if (httpContextAccessor.HttpContext.Request.Query.ContainsKey("Status"))
                StatusId = int.Parse(httpContextAccessor.HttpContext.Request.Query["Status"]);
            if (httpContextAccessor.HttpContext.Request.Query.ContainsKey("Year"))
                Year = int.Parse(httpContextAccessor.HttpContext.Request.Query["Year"]);
        }
        public override async Task<IEnumerable<TableAccident>> GetList()
        {
            IQueryable<TableAccident> accidents = Manager.QueryManager.Accidents;
            if (Manager.UserHelperManager.IsManager)
            {
                accidents = accidents
                           .Where(x => x.TableExpertise.Any(t => StatusId == -3 || (StatusId == -2 && t.Status < 5) || t.Status == StatusId))
                           .OrderByDescending(x => x.AccidentDate);
            }
            else if (Manager.UserHelperManager.IsReader)
            {
                accidents = accidents
                           .Where(x => x.TableExpertise.Any(t => StatusId == -3 || (StatusId == -2 && t.Status < 5) || t.Status == StatusId) && x.TableExpertise.Any(c => c.ContributorId == Manager.UserHelperManager.Id))
                           .OrderByDescending(x => x.AccidentDate);
            }
            else
                return new List<TableAccident>();
            if (Year == int.MaxValue)
                return await accidents.ToListAsync();
            else
                return await accidents.Where(x => x.AccidentDate.Year == Year).ToListAsync();
        }

        public override async Task<TableAccident> GetModel(string id)
            => await Manager.Context.TableAccident.FindAsync(int.Parse(id));
        public override INavigation<TableAccident> GetIndexNavigation()
            => Manager.NavigationManager.GetAccidentNavigation("Assignment", this.StringLocalizer, this.Manager);
        public override INavigation<TableAccident> GetDeleteNavigation()
            => Manager.NavigationManager.GetAccidentNavigation("Delete", this.StringLocalizer, this.Manager);
        //
        // GET: Accident/Manage
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return View();
            var tableAccident = await Manager.QueryManager.Accidents.FirstOrDefaultAsync(x => x.Id == id);
            if (tableAccident == null)
                return NotFound();
            return View(tableAccident);
        }
        public IActionResult Import()
            => View();
        [HttpPost]
        public async Task<IActionResult> Import(int importType, IFormFile file)
        {
            try
            {
                return Ok(await Manager.ImportManager.FinalizeAsync(file, (ImporterType)importType));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        //$"{this.Accident?.YearlyId}{this.Accident?.InsuranceAgency?.InsuranceCompany?.Alias}{this.Accident?.UploadDate:yyyy}{this.Member?.Alias}{this.Contributor?.Alias ?? this.Member?.Alias}"

        public async Task<IEnumerable<string>> Autocomplete([FromQuery] string query)
        {
            var t = await Manager.QueryManager.Expertises
                    .Where(x => (
                            query.Contains(x.Accident.YearlyId.ToString()) ||
                            query.Contains(x.Accident.UploadDate.ToString()) ||
                            x.Accident.ExternalId.Contains(query))
                        && (Manager.UserHelperManager.IsManager || x.ContributorId == Manager.UserHelperManager.Id)
                        )
                    .Take(4)
                    .ToListAsync();
            return t
                .Select(x => $"{x.ConcreteId} - {x.Member?.TotalName} - {x.Accident.ExternalId}{(!string.IsNullOrWhiteSpace(x.Accident.Insurance?.Customer?.Name) ? $" - {x.Accident.Insurance?.Customer?.Name}" : string.Empty)}");
        }
        public async Task<IActionResult> SetAutocomplete([FromQuery] string query)
        {
            string realQuery = query.Split('-').Skip(2).FirstOrDefault().Trim();
            var c = await Manager.QueryManager.Expertises.FirstOrDefaultAsync(x => x.Accident.ExternalId == realQuery
                && (Manager.UserHelperManager.IsManager || x.ContributorId == Manager.UserHelperManager.Id));
            return RedirectToAction("Edit", "Experty", new { id = c.Id });
        }

        private static bool CheckIfContainsQuery(string query, object value)
            => value?.ToString().ToLower().Contains(query.ToLower()) ?? false;

        // POST: Accident/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0037:Use inferred member name", Justification = "<Pending>")]
        public async Task<IActionResult> Edit([FromForm] TableAccident tableAccident)
        {
            if (this.IsAValidModel(tableAccident.Id))
            {
                if (tableAccident.Id == 0)
                {
                    tableAccident.UploadDate = DateTime.UtcNow;
                    await Manager.QueryManager.InsertAccidentAsync(tableAccident);
                    TableExpertise tableExpertise = new TableExpertise()
                    {
                        AccidentId = tableAccident.Id,
                        MemberId = HttpContext.User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier).Value,
                        Status = -1,
                        BranchId = 1,
                        FileId = Guid.NewGuid().ToString("N")
                    };
                    Manager.Context.TableExpertise.Add(tableExpertise);
                    await Manager.Context.SaveChangesAsync();
                    await ManageDamageds(tableAccident.Id);
                    return RedirectToAction("Edit", "Experty", new { Id = tableExpertise.Id });
                }
                else
                {
                    if (tableAccident.UploadDate == null)
                        tableAccident.UploadDate = DateTime.UtcNow;
                    Manager.Context.Update(tableAccident);
                    await Manager.Context.SaveChangesAsync();
                    await ManageDamageds(tableAccident.Id);
                    return View(await Manager.QueryManager.Accidents.FirstOrDefaultAsync(x => x.Id == tableAccident.Id));
                }
                async Task ManageDamageds(int accidentId)
                {
                    var damageds = await Manager.Context.TableDamaged.Where(x => x.AccidentId == accidentId).ToListAsync();
                    foreach (var damaged in damageds)
                        Manager.Context.Remove(damaged);
                    if (tableAccident.Damageds != null)
                        for (int i = 0; i < tableAccident.Damageds.Length; i++)
                        {
                            Manager.Context.Add(new TableDamaged()
                            {
                                AccidentId = accidentId,
                                CustomerId = tableAccident.Damageds[i]
                            });
                        }
                    await Manager.Context.SaveChangesAsync();
                }
            }
            return View(tableAccident);
        }

        public override async Task<bool> Remove(string id)
        {
            var tableAccident = await Manager.Context.TableAccident
                .Include(x => x.TableExpertise)
                .ThenInclude(x => x.TableBill)
                .Include(x => x.TableExpertise)
                .ThenInclude(x => x.TableMessage)
                .FirstOrDefaultAsync(x => x.Id == int.Parse(id));
            foreach (var expertise in tableAccident.TableExpertise)
            {
                foreach (var bill in expertise.TableBill)
                    Manager.Context.TableBill.Remove(bill);
                foreach (var message in expertise.TableMessage)
                    Manager.Context.TableMessage.Remove(message);
                Manager.Context.TableExpertise.Remove(expertise);
            }
            Manager.Context.TableAccident.Remove(tableAccident);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
    }
}