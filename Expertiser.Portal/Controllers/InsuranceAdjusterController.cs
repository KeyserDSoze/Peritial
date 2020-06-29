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
    public class InsuranceAdjusterController : BaseController<TableInsuranceAdjuster>
    {
        public InsuranceAdjusterController(Manager manager, IStringLocalizer<DatabaseShared> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }

        // GET: InsuranceAgency/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return View();
            var tableInsuranceAdjuster = await Manager.Context.TableInsuranceAdjuster.FirstOrDefaultAsync(x => x.Id == id);
            if (tableInsuranceAdjuster == null)
                return NotFound();
            return View(tableInsuranceAdjuster);
        }

        // POST: InsuranceAgency/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Name")] TableInsuranceAdjuster tableInsuranceAdjuster)
        {
            if (ModelState.IsValid)
            {
                if (tableInsuranceAdjuster.Id == 0)
                {
                    Manager.Context.Add(tableInsuranceAdjuster);
                    await Manager.Context.SaveChangesAsync();
                }
                else
                {
                    Manager.Context.Update(tableInsuranceAdjuster);
                    await Manager.Context.SaveChangesAsync();
                }
                return RedirectToAction(nameof(Index));
            }
            return View(tableInsuranceAdjuster);
        }
        public override INavigation<TableInsuranceAdjuster> GetIndexNavigation()
           => Manager.NavigationManager.GetInsuranceAdjusterNavigation("Index", this.StringLocalizer);
        public override INavigation<TableInsuranceAdjuster> GetDeleteNavigation()
           => Manager.NavigationManager.GetInsuranceAdjusterNavigation("Delete", this.StringLocalizer);

        public override async Task<IEnumerable<TableInsuranceAdjuster>> GetList()
            => await Manager.QueryManager.InsuranceAdjusters.ToListAsync();

        public override async Task<TableInsuranceAdjuster> GetModel(string id)
            => await Manager.QueryManager.InsuranceAdjusters.FirstOrDefaultAsync(x => x.Id == int.Parse(id));
        public IActionResult Create()
            => PartialView();
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Name")] TableInsuranceAdjuster tableInsuranceAdjuster)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Add(tableInsuranceAdjuster);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_GetInsuranceAdjusters", tableInsuranceAdjuster.Id);
            }
            return NotOkMessage(ModelState);
        }
        public override async Task<bool> Remove(string id)
        {
            var tableInsuranceAdjuster = await Manager.Context.TableInsuranceAdjuster.FindAsync(int.Parse(id));
            Manager.Context.TableInsuranceAdjuster.Remove(tableInsuranceAdjuster);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
    }
}