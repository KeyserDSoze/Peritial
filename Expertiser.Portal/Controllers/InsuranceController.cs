using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Localization;
using Rystem.Web.Backoffice;
using Expertiser.Base.Constant;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.AdminContributor)]
    public class InsuranceController : BaseController<TableInsurance>
    {
        public InsuranceController(Manager manager, IStringLocalizer<DatabaseShared> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }

        // GET: Insurance/Create
        public async Task<IActionResult> Manage(int? id)
        {
            if (id == null)
                return View();
            return View(await Manager.QueryManager.Insurances.FirstOrDefaultAsync(x => x.Id == id));
        }
        public IActionResult Create()
            => PartialView();
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("CustomerId,ExternalId,InsuranceCompanyId")] TableInsurance tableInsurance)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Add(tableInsurance);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_GetInsurances", tableInsurance.Id);
            }
            return NotOkMessage(ModelState);
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit([Bind("CustomerId,ExternalId,InsuranceCompanyId")] TableInsurance tableInsurance)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Add(tableInsurance);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_GetInsurances", tableInsurance.Id);
            }
            return PartialView(tableInsurance);
        }
        // POST: Insurance/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,CustomerId,ExternalId,InsuranceCompanyId")] TableInsurance tableInsurance)
        {
            if (ModelState.IsValid)
            {
                if (tableInsurance.Id == 0)
                {
                    Manager.Context.Add(tableInsurance);
                    await Manager.Context.SaveChangesAsync();
                }
                else
                {
                    Manager.Context.Update(tableInsurance);
                    await Manager.Context.SaveChangesAsync();
                }
            }
            return View(tableInsurance);
        }
        public override INavigation<TableInsurance> GetIndexNavigation()
           => Manager.NavigationManager.GetInsuranceNavigation("Index", this.StringLocalizer);
        public override INavigation<TableInsurance> GetDeleteNavigation()
           => Manager.NavigationManager.GetInsuranceNavigation("Delete", this.StringLocalizer);

        public override async Task<IEnumerable<TableInsurance>> GetList()
            => await Manager.QueryManager.Insurances.ToListAsync();

        public override async Task<TableInsurance> GetModel(string id)
            => await Manager.QueryManager.Insurances.FirstOrDefaultAsync(x => x.Id == int.Parse(id));

        public override async Task<bool> Remove(string id)
        {
            var tableInsurance = await Manager.Context.TableInsurance.FindAsync(int.Parse(id));
            Manager.Context.TableInsurance.Remove(tableInsurance);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
    }
}