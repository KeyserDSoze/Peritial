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

        public override NavigationIndex<TableLocation> GetNavigationIndex()
            => new NavigationManager<TableLocation>()
                .Create(Manager.QueryManager.Locations, NavigationOptions.CanDoneAll("Index"))
                .Include(x => x.Address)
                .Include(x => x.City)
                    .ThenInclude(x => x.Name)
                .Include(x => x.PostalCode)
                .Include(x => x.Id, PropertyOptions.Key)
            .ToIndex();
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
            {
                return NotFound();
            }

            var tableLocation = await Manager.Context.TableLocation.FindAsync(id);
            if (tableLocation == null)
            {
                return NotFound();
            }
            ViewData["CityId"] = new SelectList(Manager.Context.TableCity, "Id", "Name", tableLocation.CityId);
            return View(tableLocation);
        }

        // POST: Location/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Manage(int id, [Bind("Id,Address,CityId,PostalCode")] TableLocation tableLocation)
        {
            if (id != tableLocation.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    Manager.Context.Update(tableLocation);
                    await Manager.Context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableLocationExists(tableLocation.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            ViewData["CityId"] = new SelectList(Manager.Context.TableCity, "Id", "Name", tableLocation.CityId);
            return View(tableLocation);
        }

        // GET: Location/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableLocation = await Manager.Context.TableLocation
                .Include(t => t.City)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableLocation == null)
            {
                return NotFound();
            }

            return View(tableLocation);
        }

        // POST: Location/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableLocation = await Manager.Context.TableLocation.FindAsync(id);
            Manager.Context.TableLocation.Remove(tableLocation);
            await Manager.Context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableLocationExists(int id)
        {
            return Manager.Context.TableLocation.Any(e => e.Id == id);
        }


    }
}
