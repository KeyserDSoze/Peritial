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

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.AdminContributor)]
    public class InsuranceCompanyController : BaseController
    {
        public InsuranceCompanyController(Manager manager) : base(manager)
        {
        }

        // GET: InsuranceCompany
        public IActionResult Index()
            => View(Manager.QueryManager.InsuranceCompanies);

        // GET: InsuranceCompany/Edit/5
        public async Task<IActionResult> Manage(int? id)
        {
            if (id == null)
            {
                return View();
            }

            var tableInsuranceCompany = await Manager.Context.TableInsuranceCompany.FindAsync(id);
            if (tableInsuranceCompany == null)
            {
                return NotFound();
            }
            return View(tableInsuranceCompany);
        }

        // POST: InsuranceCompany/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Manage(int id, [Bind("Id,Label,AddressId,ExternalId,IsActive")] TableInsuranceCompany tableInsuranceCompany)
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

        // GET: InsuranceCompany/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsuranceCompany = await Manager.QueryManager.InsuranceCompanies
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInsuranceCompany == null)
            {
                return NotFound();
            }

            return View(tableInsuranceCompany);
        }

        // POST: InsuranceCompany/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableInsuranceCompany = await Manager.QueryManager.InsuranceCompanies.FirstOrDefaultAsync(x => x.Id == id);
            Manager.Context.TableInsuranceCompany.Remove(tableInsuranceCompany);
            await Manager.Context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
    }
}