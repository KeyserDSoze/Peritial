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
    public class InsuranceCompanyController : Controller
    {
        private readonly ContextExpertiser _context;

        public InsuranceCompanyController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: InsuranceCompany
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableInsuranceCompany.Include(t => t.Address);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: InsuranceCompany/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsuranceCompany = await _context.TableInsuranceCompany
                .Include(t => t.Address)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInsuranceCompany == null)
            {
                return NotFound();
            }

            return View(tableInsuranceCompany);
        }

        // GET: InsuranceCompany/Create
        public IActionResult Create()
        {
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City");
            return View();
        }

        // POST: InsuranceCompany/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Label,AddressId,ExternalId,IsActive")] TableInsuranceCompany tableInsuranceCompany)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableInsuranceCompany);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInsuranceCompany.AddressId);
            return View(tableInsuranceCompany);
        }

        // GET: InsuranceCompany/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsuranceCompany = await _context.TableInsuranceCompany.FindAsync(id);
            if (tableInsuranceCompany == null)
            {
                return NotFound();
            }
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInsuranceCompany.AddressId);
            return View(tableInsuranceCompany);
        }

        // POST: InsuranceCompany/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Label,AddressId,ExternalId,IsActive")] TableInsuranceCompany tableInsuranceCompany)
        {
            if (id != tableInsuranceCompany.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableInsuranceCompany);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableInsuranceCompanyExists(tableInsuranceCompany.Id))
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
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInsuranceCompany.AddressId);
            return View(tableInsuranceCompany);
        }

        // GET: InsuranceCompany/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsuranceCompany = await _context.TableInsuranceCompany
                .Include(t => t.Address)
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
            var tableInsuranceCompany = await _context.TableInsuranceCompany.FindAsync(id);
            _context.TableInsuranceCompany.Remove(tableInsuranceCompany);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableInsuranceCompanyExists(int id)
        {
            return _context.TableInsuranceCompany.Any(e => e.Id == id);
        }
    }
}
