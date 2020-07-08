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
    public class InsuranceAgencyController : BaseController<TableInsuranceAgency>
    {
        public InsuranceAgencyController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }
        public IActionResult Create()
            => PartialView();
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Label,ExternalId,InsuranceCompanyId,AddressId,IsActive")] TableInsuranceAgency tableInsuranceAgency)
        {
            if (this.IsAValidModel(tableInsuranceAgency.Id))
            {
                Manager.Context.Add(tableInsuranceAgency);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_GetInsuranceAgencies", tableInsuranceAgency.Id);
            }
            return NotOkMessage(ModelState);
        }
        // GET: InsuranceAgency/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return View();
            var tableInsuranceAgency = await Manager.Context.TableInsuranceAgency.FirstOrDefaultAsync(x => x.Id == id);
            if (tableInsuranceAgency == null)
                return NotFound();
            return View(tableInsuranceAgency);
        }

        // POST: InsuranceAgency/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Label,ExternalId,InsuranceCompanyId,AddressId,IsActive")] TableInsuranceAgency tableInsuranceAgency)
        {
            if (this.IsAValidModel(tableInsuranceAgency.Id))
            {
                if (tableInsuranceAgency.Id == 0)
                {
                    Manager.Context.Add(tableInsuranceAgency);
                    await Manager.Context.SaveChangesAsync();
                }
                else
                {
                    Manager.Context.Update(tableInsuranceAgency);
                    await Manager.Context.SaveChangesAsync();
                }
            }
            return View(tableInsuranceAgency);
        }
        public override INavigation<TableInsuranceAgency> GetIndexNavigation()
           => Manager.NavigationManager.GetInsuranceAgencyNavigation("Index", this.StringLocalizer);
        public override INavigation<TableInsuranceAgency> GetDeleteNavigation()
           => Manager.NavigationManager.GetInsuranceAgencyNavigation("Delete", this.StringLocalizer);

        public override async Task<IEnumerable<TableInsuranceAgency>> GetList()
            => await Manager.QueryManager.InsuranceAgencies.ToListAsync();

        public override async Task<TableInsuranceAgency> GetModel(string id)
            => await Manager.QueryManager.InsuranceAgencies.FirstOrDefaultAsync(x => x.Id == int.Parse(id));

        public override async Task<bool> Remove(string id)
        {
            var tableInsuranceAgency = await Manager.Context.TableInsuranceAgency.FindAsync(int.Parse(id));
            Manager.Context.TableInsuranceAgency.Remove(tableInsuranceAgency);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
    }
}