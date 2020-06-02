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
    public class CustomerController : BaseController
    {
        public CustomerController(Manager manager) : base(manager)
        {
        }

        // GET: Customer
        public IActionResult Index()
            => View(Manager.QueryManager.Customers);

        // GET: Customer/Create
        public IActionResult Create()
            => PartialView();
        // POST: Customer/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Name,FiscalCode,AddressId")] TableCustomer tableCustomer)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Add(tableCustomer);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_GetCustomers", tableCustomer.Id);
            }
            return NotOkMessage(ModelState);
        }

        // GET: Customer/Edit/5
        public async Task<IActionResult> Manage(int? id)
        {
            if (id == null)
                return View();
            var tableCustomer = await Manager.QueryManager.Customers.FirstOrDefaultAsync(x => x.Id == id);
            if (tableCustomer == null)
                return NotFound();
            return View(tableCustomer);
        }

        // POST: Customer/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Manage(int id, [Bind("Id,Name,FiscalCode,AddressId")] TableCustomer tableCustomer)
        {
            if (ModelState.IsValid)
            {
                if (tableCustomer.Id == 0)
                {
                    Manager.Context.Add(tableCustomer);
                    await Manager.Context.SaveChangesAsync();
                }
                else
                {
                    Manager.Context.Update(tableCustomer);
                    await Manager.Context.SaveChangesAsync();
                }
            }
            return View(tableCustomer);
        }

        // GET: Customer/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableCustomer = await Manager.QueryManager.Customers
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableCustomer == null)
            {
                return NotFound();
            }

            return View(tableCustomer);
        }

        // POST: Customer/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableCustomer = await Manager.QueryManager.Customers.FirstOrDefaultAsync(x => x.Id == id);
            Manager.Context.TableCustomer.Remove(tableCustomer);
            await Manager.Context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
    }
}
