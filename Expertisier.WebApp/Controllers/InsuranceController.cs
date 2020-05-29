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
    public class InsuranceController : Controller
    {
        private readonly ContextExpertiser _context;

        public InsuranceController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: Insurance
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableInsurance.Include(t => t.Customer).Include(t => t.InsuranceCompany);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: Insurance/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsurance = await _context.TableInsurance
                .Include(t => t.Customer)
                .Include(t => t.InsuranceCompany)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInsurance == null)
            {
                return NotFound();
            }

            return View(tableInsurance);
        }

        // GET: Insurance/Create
        public IActionResult Create()
        {
            ViewData["CustomerId"] = new SelectList(_context.TableCustomer, "Id", "Name");
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label");
            return View();
        }

        // POST: Insurance/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,CustomerId,ExternalId,InsuranceCompanyId")] TableInsurance tableInsurance)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableInsurance);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["CustomerId"] = new SelectList(_context.TableCustomer, "Id", "Name", tableInsurance.CustomerId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInsurance.InsuranceCompanyId);
            return View(tableInsurance);
        }

        // GET: Insurance/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsurance = await _context.TableInsurance.FindAsync(id);
            if (tableInsurance == null)
            {
                return NotFound();
            }
            ViewData["CustomerId"] = new SelectList(_context.TableCustomer, "Id", "Name", tableInsurance.CustomerId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInsurance.InsuranceCompanyId);
            return View(tableInsurance);
        }

        // POST: Insurance/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,CustomerId,ExternalId,InsuranceCompanyId")] TableInsurance tableInsurance)
        {
            if (id != tableInsurance.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableInsurance);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableInsuranceExists(tableInsurance.Id))
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
            ViewData["CustomerId"] = new SelectList(_context.TableCustomer, "Id", "Name", tableInsurance.CustomerId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInsurance.InsuranceCompanyId);
            return View(tableInsurance);
        }

        // GET: Insurance/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInsurance = await _context.TableInsurance
                .Include(t => t.Customer)
                .Include(t => t.InsuranceCompany)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInsurance == null)
            {
                return NotFound();
            }

            return View(tableInsurance);
        }

        // POST: Insurance/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableInsurance = await _context.TableInsurance.FindAsync(id);
            _context.TableInsurance.Remove(tableInsurance);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableInsuranceExists(int id)
        {
            return _context.TableInsurance.Any(e => e.Id == id);
        }
    }
}
