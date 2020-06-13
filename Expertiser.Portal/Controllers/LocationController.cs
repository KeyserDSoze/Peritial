using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Rystem.Web.Backoffice;
using Microsoft.Extensions.Localization;

namespace Expertiser.Portal.Controllers
{
    public class LocationController : BaseController<TableLocation>
    {
        public LocationController(Manager manager, IStringLocalizer<DatabaseShared> stringLocalizer) : base(manager, stringLocalizer)
        {

        }
        public override async Task<IEnumerable<TableLocation>> GetList()
        {
            await Task.Delay(0);
            return Manager.QueryManager.Locations;
        }
        public override async Task<TableLocation> GetModel(string id)
            => await Manager.Context.TableLocation.FindAsync(int.Parse(id));
        public override async Task<bool> Remove(string id)
        {
            var tableLocation = await Manager.Context.TableLocation.FindAsync(int.Parse(id));
            Manager.Context.TableLocation.Remove(tableLocation);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
        public override INavigation<TableLocation> GetIndexNavigation()
           => Manager.NavigationManager.GetLocationNavigation("Index", this.StringLocalizer);
        public override INavigation<TableLocation> GetDeleteNavigation()
           => Manager.NavigationManager.GetLocationNavigation("Delete", this.StringLocalizer);


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

        public async Task<IActionResult> Edit(int? id)
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
        public async Task<IActionResult> Edit(int id, [Bind("Id,Address,CityId,PostalCode")] TableLocation tableLocation)
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