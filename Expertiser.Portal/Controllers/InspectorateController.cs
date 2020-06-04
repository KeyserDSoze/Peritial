using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Expertiser.Base.Constant;
using Rystem.Web.Backoffice;
using Microsoft.Extensions.Localization;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.AdminContributor)]
    public class InspectorateController : BaseController<TableInspectorate>
    {
        public InspectorateController(Manager manager, IStringLocalizer<DatabaseShared> stringLocalizer) : base(manager, stringLocalizer)
        {
        }
        public override async Task<IEnumerable<TableInspectorate>> GetList()
        {
            await Task.Delay(0);
            return Manager.QueryManager.Inspectorates;
        }
        public override async Task<TableInspectorate> GetModel(object id)
            => await Manager.Context.TableInspectorate.FindAsync((int)id);
        public override async Task<bool> Remove(object id)
        {
            var tableInspectorate = await Manager.Context.TableInspectorate.FindAsync((int)id);
            Manager.Context.TableInspectorate.Remove(tableInspectorate);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
        public override INavigation<TableInspectorate> GetNavigation()
            => NavigationStarter<TableInspectorate>
                .Create(NavigationOptions.CanDoneAll("Index"))
               .Include(x => x.Label)
               .Include(x => x.InsuranceCompany)
                    .ThenInclude(x => x.Label)
               .Include(x => x.IsActive)
               .Include(x => x.TableAccident, PropertyOptions.Count)
               .Include(x => x.Address)
                    .ThenInclude(x => x.City)
               .Include(x => x.Id, PropertyOptions.Key);

        // GET: Inspectorate/Edit/5
        public async Task<IActionResult> Manage(int? id)
        {
            if (id == null)
                return View();
            var tableInspectorate = await Manager.QueryManager.Inspectorates.FirstOrDefaultAsync(x => x.Id == id);
            if (tableInspectorate == null)
                return NotFound();
            return View(tableInspectorate);
        }

        // POST: Inspectorate/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Manage(int id, [Bind("Id,Label,ExternalId,AddressId,InsuranceCompanyId")] TableInspectorate tableInspectorate)
        {
            if (ModelState.IsValid)
            {
                if (tableInspectorate.Id == 0)
                {
                    Manager.Context.Add(tableInspectorate);
                    await Manager.Context.SaveChangesAsync();
                }
                else
                {
                    Manager.Context.Update(tableInspectorate);
                    await Manager.Context.SaveChangesAsync();
                }
                return RedirectToAction(nameof(Index));
            }
            return View(tableInspectorate);
        }
    }
}