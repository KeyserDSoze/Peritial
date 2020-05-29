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
    public class InsuranceAgencyController : Controller
    {
        private readonly ContextExpertiser _context;

        public InsuranceAgencyController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: InsuranceAgency
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableInsuranceAgency.Include(t => t.Address).Include(t => t.InsuranceCompany);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: InsuranceAgency/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsuranceAgency = await _context.TableInsuranceAgency
                .Include(t => t.Address)
                .Include(t => t.InsuranceCompany)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInsuranceAgency == null)
            {
                return NotFound();
            }

            return View(tableInsuranceAgency);
        }

        // GET: InsuranceAgency/Create
        public IActionResult Create()
        {
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City");
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label");
            return View();
        }

        // POST: InsuranceAgency/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Label,ExternalId,InsuranceCompanyId,AddressId,IsActive")] TableInsuranceAgency tableInsuranceAgency)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableInsuranceAgency);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInsuranceAgency.AddressId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInsuranceAgency.InsuranceCompanyId);
            return View(tableInsuranceAgency);
        }

        // GET: InsuranceAgency/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsuranceAgency = await _context.TableInsuranceAgency.FindAsync(id);
            if (tableInsuranceAgency == null)
            {
                return NotFound();
            }
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInsuranceAgency.AddressId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInsuranceAgency.InsuranceCompanyId);
            return View(tableInsuranceAgency);
        }

        // POST: InsuranceAgency/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Label,ExternalId,InsuranceCompanyId,AddressId,IsActive")] TableInsuranceAgency tableInsuranceAgency)
        {
            if (id != tableInsuranceAgency.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableInsuranceAgency);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableInsuranceAgencyExists(tableInsuranceAgency.Id))
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
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInsuranceAgency.AddressId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInsuranceAgency.InsuranceCompanyId);
            return View(tableInsuranceAgency);
        }

        // GET: InsuranceAgency/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsuranceAgency = await _context.TableInsuranceAgency
                .Include(t => t.Address)
                .Include(t => t.InsuranceCompany)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInsuranceAgency == null)
            {
                return NotFound();
            }

            return View(tableInsuranceAgency);
        }

        // POST: InsuranceAgency/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableInsuranceAgency = await _context.TableInsuranceAgency.FindAsync(id);
            _context.TableInsuranceAgency.Remove(tableInsuranceAgency);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableInsuranceAgencyExists(int id)
        {
            return _context.TableInsuranceAgency.Any(e => e.Id == id);
        }
    }
}
