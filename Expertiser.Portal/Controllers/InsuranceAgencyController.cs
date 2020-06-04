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

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.AdminContributor)]
    public class InsuranceAgencyController : BaseController<TableInsuranceAgency>
    {
        public InsuranceAgencyController(Manager manager) : base(manager)
        {
        }

        // GET: InsuranceAgency/Edit/5
        public async Task<IActionResult> Manage(int? id)
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
        public async Task<IActionResult> Manage(int id, [Bind("Id,Label,ExternalId,InsuranceCompanyId,AddressId,IsActive")] TableInsuranceAgency tableInsuranceAgency)
        {
            if (ModelState.IsValid)
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
                return RedirectToAction(nameof(Index));
            }
            return View(tableInsuranceAgency);
        }

        public override INavigation<TableInsuranceAgency> GetNavigation()
            => NavigationStarter<TableInsuranceAgency>
                .Create()
                .Include(x => x.Id, PropertyOptions.Key)
                .Include(x => x.Label)
                .Include(x => x.IsActive)
                .Include(x => x.TableAccident, PropertyOptions.Count);

        public override async Task<IEnumerable<TableInsuranceAgency>> GetList()
            => await Manager.QueryManager.InsuranceAgencies.ToListAsync();

        public override async Task<TableInsuranceAgency> GetModel(object id)
            => await Manager.QueryManager.InsuranceAgencies.FirstOrDefaultAsync(x => x.Id == (int)id);

        public override async Task<bool> Remove(object id)
        {
            var tableInsuranceAgency = await Manager.Context.TableInsuranceAgency.FindAsync((int)id);
            Manager.Context.TableInsuranceAgency.Remove(tableInsuranceAgency);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
    }
}