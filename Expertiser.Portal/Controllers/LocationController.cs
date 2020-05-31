using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;

namespace Expertiser.Portal.Controllers
{
    public class LocationController : Controller
    {
        private readonly ContextExpertiser _context;

        public LocationController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: Location
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableLocation.Include(t => t.City);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: Location/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }
            var tableLocation = await _context.TableLocation
                .Include(t => t.City)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableLocation == null)
            {
                return NotFound();
            }

            return View(tableLocation);
        }

        // GET: Location/Create
        public IActionResult Create()
        {
            ViewData["CityId"] = new SelectList(_context.TableCity, "Id", "Name");
            return View();
        }
        private readonly static string Empty = null;
        public IActionResult GetPostalCodes([FromQuery] IEnumerable<string> selectedItems)
            => PartialView("_PostalCode", (int.Parse(selectedItems.First()), Empty));
        public IActionResult GetLocations([FromQuery] int locationId)
            => PartialView("_GetLocations", locationId);

        // POST: Location/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Address,CityId,PostalCode")] TableLocation tableLocation)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableLocation);
                await _context.SaveChangesAsync();
                return PartialView("_GetLocations", tableLocation.Id);
            }
            return View(tableLocation);
        }

        // GET: Location/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableLocation = await _context.TableLocation.FindAsync(id);
            if (tableLocation == null)
            {
                return NotFound();
            }
            ViewData["CityId"] = new SelectList(_context.TableCity, "Id", "Name", tableLocation.CityId);
            return View(tableLocation);
        }

        // POST: Location/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Address,CityId,PostalCode")] TableLocation tableLocation)
        {
            if (id != tableLocation.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableLocation);
                    await _context.SaveChangesAsync();
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
            ViewData["CityId"] = new SelectList(_context.TableCity, "Id", "Name", tableLocation.CityId);
            return View(tableLocation);
        }

        // GET: Location/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableLocation = await _context.TableLocation
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
            var tableLocation = await _context.TableLocation.FindAsync(id);
            _context.TableLocation.Remove(tableLocation);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableLocationExists(int id)
        {
            return _context.TableLocation.Any(e => e.Id == id);
        }
    }
}
