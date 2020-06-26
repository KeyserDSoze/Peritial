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
    public class InsuranceCompanyController : BaseController<TableInsuranceCompany>
    {
        public InsuranceCompanyController(Manager manager, IStringLocalizer<DatabaseShared> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }
        public override INavigation<TableInsuranceCompany> GetIndexNavigation()
           => Manager.NavigationManager.GetInsuranceCompanyNavigation("Index", this.StringLocalizer);
        public override INavigation<TableInsuranceCompany> GetDeleteNavigation()
           => Manager.NavigationManager.GetInsuranceCompanyNavigation("Delete", this.StringLocalizer);

        public override async Task<IEnumerable<TableInsuranceCompany>> GetList()
            => await Manager.QueryManager.InsuranceCompanies.ToListAsync();
        public override async Task<TableInsuranceCompany> GetModel(string id)
        => await Manager.QueryManager.InsuranceCompanies.FirstOrDefaultAsync(x => x.Id == int.Parse(id));
        public override async Task<bool> Remove(string id)
        {
            var tableInsuranceCompany = await Manager.QueryManager.InsuranceCompanies.FirstOrDefaultAsync(x => x.Id == int.Parse(id));
            Manager.Context.TableInsuranceCompany.Remove(tableInsuranceCompany);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
        // GET: InsuranceCompany/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return View();
            var tableInsuranceCompany = await Manager.Context.TableInsuranceCompany.FindAsync(id);
            if (tableInsuranceCompany == null)
                return NotFound();
            return View(tableInsuranceCompany);
        }

        // POST: InsuranceCompany/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Label,AddressId,ExternalId,IsActive")] TableInsuranceCompany tableInsuranceCompany)
        {
            if (ModelState.IsValid)
            {
                if (tableInsuranceCompany.Id == 0)
                {
                    Manager.Context.Add(tableInsuranceCompany);
                    await Manager.Context.SaveChangesAsync();
                    return RedirectToAction(nameof(Index));
                }
                else
                {
                    Manager.Context.Update(tableInsuranceCompany);
                    await Manager.Context.SaveChangesAsync();
                }
                return RedirectToAction(nameof(Index));
            }
            return View(tableInsuranceCompany);
        }
    }
}