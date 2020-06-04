using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Rystem.Web.Backoffice;

namespace Expertiser.Portal.Controllers
{
    public class LocationController : BaseController<TableLocation>
    {
        public LocationController(Manager manager) : base(manager)
        {

        }
        public override async Task<IEnumerable<TableLocation>> GetList()
        {
            await Task.Delay(0);
            return Manager.QueryManager.Locations;
        }
        public override async Task<TableLocation> GetModel(object id)
            => await Manager.Context.TableLocation.FindAsync((int)id);
        public override async Task<bool> Remove(object id)
        {
            var tableLocation = await Manager.Context.TableLocation.FindAsync((int)id);
            Manager.Context.TableLocation.Remove(tableLocation);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
        public override INavigation<TableLocation> GetNavigation()
            => NavigationStarter<TableLocation>
                .Create(NavigationOptions.CanDoneAll("Index"))
                .Include(x => x.Address)
                .Include(x => x.City)
                    .ThenInclude(x => x.Name)
                .Include(x => x.PostalCode)
                .Include(x => x.Id, PropertyOptions.Key);

        public IActionResult Create()
            => PartialView();
        private readonly static string Empty = null;

        public IActionResult UpdatePostalCodes([FromQuery] IEnumerable<string> selectedItems)
            => PartialView("_PostalCode", (int.Parse(selectedItems.First()), Empty));
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Address,CityId,PostalCode")] TableLocation tableLocation)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Add(tableLocation);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_GetLocations", tableLocation.Id);
            }
            return NotOkMessage(ModelState);
        }

        public async Task<IActionResult> Manage(int? id)
        {
            if (id == null)
                return View();

            var tableLocation = await Manager.Context.TableLocation.FindAsync(id);
            if (tableLocation == null)
                return NotFound();
            return View(tableLocation);
        }

        // POST: Location/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Manage(int id, [Bind("Id,Address,CityId,PostalCode")] TableLocation tableLocation)
        {
            if (ModelState.IsValid)
            {
                if (tableLocation.Id == 0)
                {
                    Manager.Context.Add(tableLocation);
                    await Manager.Context.SaveChangesAsync();
                }
                else
                {
                    Manager.Context.Update(tableLocation);
                    await Manager.Context.SaveChangesAsync();
                }
            }
            return View(tableLocation);
        }
    }
}