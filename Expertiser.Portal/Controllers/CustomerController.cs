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
    public class CustomerController : BaseController<TableCustomer>
    {
        public CustomerController(Manager manager, IStringLocalizer<DatabaseShared> stringLocalizer) : base(manager, stringLocalizer)
        {
        }
        public override async Task<IEnumerable<TableCustomer>> GetList()
        {
            await Task.Delay(0);
            return Manager.QueryManager.Customers;
        }

        public override async Task<TableCustomer> GetModel(object id)
            => await Manager.Context.TableCustomer.FindAsync((int)id);
        public override async Task<bool> Remove(object id)
        {
            var tableCustomer = await Manager.QueryManager.Customers.FirstOrDefaultAsync(x => x.Id == (int)id);
            Manager.Context.TableCustomer.Remove(tableCustomer);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
        public override INavigation<TableCustomer> GetNavigation()
            => NavigationStarter<TableCustomer>.Create()
                .Include(x => x.Id, PropertyOptions.Key)
                .Include(x => x.Name)
                .Include(x => x.FiscalCode)
                .Include(x => x.TableInsurance, PropertyOptions.Count)
                .Include(x => x.Address)
                    .ThenInclude(x => x.Address);

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
    }
}