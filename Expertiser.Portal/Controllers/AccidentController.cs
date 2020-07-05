using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Rystem.Web;
using Expertiser.Base.Constant;
using Rystem.Web.Backoffice;
using Microsoft.Extensions.Localization;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.OData.Edm;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.All)]
    public class AccidentController : BaseController<TableAccident>
    {
        public AccidentController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }

        public override async Task<IEnumerable<TableAccident>> GetList()
        {
            if (Manager.UserHelperManager.IsManager)
            {
                return await Manager.QueryManager.Accidents
                           .Where(x => x.TableExpertise.Any(t => t.Status <= (int)StatusType.ToSend))
                           .OrderByDescending(x => x.AccidentDate)
                           .ToListAsync();
            }
            else if (Manager.UserHelperManager.IsReader)
            {
                return await Manager.QueryManager.Accidents
                           .Where(x => x.TableExpertise.Any(t => t.Status <= (int)StatusType.ToSend) && x.TableExpertise.Any(c => c.ContributorId == Manager.UserHelperManager.Id))
                           .OrderByDescending(x => x.AccidentDate)
                           .ToListAsync();
            }
            else
                return new List<TableAccident>();
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
            => Ok(await Manager.ImportManager.FinalizeAsync(file, (ImporterType)importType));
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
                    Manager.Context.Update(tableAccident);
                    await Manager.Context.SaveChangesAsync();
                    await ManageDamageds(tableAccident.Id);
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