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
    public class CountryController : Controller
    {
        private readonly ContextExpertiser _context;

        public CountryController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: Country
        public async Task<IActionResult> Index()
        {
            return View(await _context.TableCountry.ToListAsync());
        }

        // GET: Country/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableCountry = await _context.TableCountry
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableCountry == null)
            {
                return NotFound();
            }

            return View(tableCountry);
        }

        // GET: Country/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: Country/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Label")] TableCountry tableCountry)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableCountry);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(tableCountry);
        }

        // GET: Country/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableCountry = await _context.TableCountry.FindAsync(id);
            if (tableCountry == null)
            {
                return NotFound();
            }
            return View(tableCountry);
        }

        // POST: Country/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Label")] TableCountry tableCountry)
        {
            if (id != tableCountry.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableCountry);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableCountryExists(tableCountry.Id))
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
            return View(tableCountry);
        }

        // GET: Country/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableCountry = await _context.TableCountry
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableCountry == null)
            {
                return NotFound();
            }

            return View(tableCountry);
        }

        // POST: Country/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableCountry = await _context.TableCountry.FindAsync(id);
            _context.TableCountry.Remove(tableCountry);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableCountryExists(int id)
        {
            return _context.TableCountry.Any(e => e.Id == id);
        }
    }
}
