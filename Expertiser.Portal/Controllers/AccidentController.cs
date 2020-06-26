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

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.AdminContributor)]
    public class AccidentController : BaseController<TableAccident>
    {
        public AccidentController(Manager manager, IStringLocalizer<DatabaseShared> stringLocalizer) : base(manager, stringLocalizer)
        {
        }

        public override async Task<IEnumerable<TableAccident>> GetList()
            => await Manager.QueryManager.Accidents
            .Where(x => x.TableExpertise.Any(t => t.Status <= (int)StatusType.Presented)).OrderByDescending(x => x.AccidentDate).ToListAsync();

        public override async Task<TableAccident> GetModel(string id)
            => await Manager.Context.TableAccident.FindAsync(int.Parse(id));
        public override INavigation<TableAccident> GetIndexNavigation()
            => Manager.NavigationManager.GetAccidentNavigation("Index", this.StringLocalizer);
        public override INavigation<TableAccident> GetDeleteNavigation()
            => Manager.NavigationManager.GetAccidentNavigation("Delete", this.StringLocalizer);

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
                    Manager.Context.Add(tableAccident);
                    await Manager.Context.SaveChangesAsync();
                    TableExpertise tableExpertise = new TableExpertise()
                    {
                        AccidentId = tableAccident.Id,
                        MemberId = HttpContext.User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier).Value,
                        Status = 0,
                        BranchId = 1,
                    };
                    Manager.Context.TableExpertise.Add(tableExpertise);
                    await Manager.Context.SaveChangesAsync();
                    return RedirectToAction("Edit", "Experty", new { Id = tableExpertise.Id });
                }
                else
                {
                    Manager.Context.Update(tableAccident);
                    await Manager.Context.SaveChangesAsync();
                }
            }
            return View(tableAccident);
        }

        public override async Task<bool> Remove(string id)
        {
            var tableAccident = await Manager.Context.TableAccident.FindAsync(int.Parse(id));
            Manager.Context.TableAccident.Remove(tableAccident);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
    }
}