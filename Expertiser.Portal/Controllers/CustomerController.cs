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
        public CustomerController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }
        public override async Task<IEnumerable<TableCustomer>> GetList()
        {
            await Task.Delay(0);
            return Manager.QueryManager.Customers;
        }

        public override async Task<TableCustomer> GetModel(string id)
            => await Manager.Context.TableCustomer.FindAsync(int.Parse(id));
        public override async Task<bool> Remove(string id)
        {
            var tableCustomer = await Manager.QueryManager.Customers.FirstOrDefaultAsync(x => x.Id == int.Parse(id));
            Manager.Context.TableCustomer.Remove(tableCustomer);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
        public override INavigation<TableCustomer> GetIndexNavigation()
           => Manager.NavigationManager.GetCustomerNavigation("Index", this.StringLocalizer);
        public override INavigation<TableCustomer> GetDeleteNavigation()
            => Manager.NavigationManager.GetCustomerNavigation("Delete", this.StringLocalizer);

        // GET: Customer/Create
        public IActionResult Create()
            => PartialView();
        // POST: Customer/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind] TableCustomer tableCustomer)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Add(tableCustomer);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_GetCustomers", ("CustomerId", tableCustomer.Id));
            }
            return NotOkMessage(ModelState);
        }
        public IActionResult CreateContractor()
           => PartialView();
        // POST: Customer/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateContractor([Bind] TableCustomer tableCustomer)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Add(tableCustomer);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_GetCustomers", ("ContractorId", tableCustomer.Id));
            }
            return NotOkMessage(ModelState);
        }
        public IActionResult CreateDamaged()
           => PartialView();
        // POST: Customer/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateDamaged([Bind] TableCustomer tableCustomer)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Add(tableCustomer);
                await Manager.Context.SaveChangesAsync();
                return Ok($"<optgroup label=''><option selected=selected value={tableCustomer.Id}>{tableCustomer.Name}</option></optgroup>");
            }
            return NotOkMessage(ModelState);
        }

        // GET: Customer/Edit/5
        public async Task<IActionResult> Edit(int? id)
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
        public async Task<IActionResult> Edit(int id, [Bind] TableCustomer tableCustomer)
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