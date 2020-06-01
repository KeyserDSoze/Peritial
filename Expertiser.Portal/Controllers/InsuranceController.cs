using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;

namespace Expertiser.Portal.Controllers
{
    [Authorize]
    public class InsuranceController : BaseController
    {
        public InsuranceController(Manager manager) : base(manager)
        {
        }
        // GET: Insurance
        public IActionResult Index()
            => View(Manager.QueryManager.Insurances);

        // GET: Insurance/Create
        public async Task<IActionResult> Manage(int? id)
        {
            if (id == null)
                return View();
            return View(await Manager.QueryManager.Insurances.FirstOrDefaultAsync(x => x.Id == id));
        }
        public IActionResult Create()
            => View();
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
            return View(tableInsurance);
        }
        // POST: Insurance/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Manage(int id, [Bind("Id,CustomerId,ExternalId,InsuranceCompanyId")] TableInsurance tableInsurance)
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

        // GET: Insurance/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsurance = await Manager.QueryManager.Insurances
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInsurance == null)
            {
                return NotFound();
            }

            return View(tableInsurance);
        }

        // POST: Insurance/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableInsurance = await Manager.Context.TableInsurance.FindAsync(id);
            Manager.Context.TableInsurance.Remove(tableInsurance);
            await Manager.Context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
    }
}
