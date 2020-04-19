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
    public class CustomerController : Controller
    {
        private readonly ContextExpertiser _context;

        public CustomerController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: Customer
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableCustomer.Include(t => t.Address);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: Customer/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableCustomer = await _context.TableCustomer
                .Include(t => t.Address)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableCustomer == null)
            {
                return NotFound();
            }

            return View(tableCustomer);
        }

        // GET: Customer/Create
        public IActionResult Create()
        {
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City");
            return View();
        }

        // POST: Customer/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Name,FiscalCode,AddressId")] TableCustomer tableCustomer)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableCustomer);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableCustomer.AddressId);
            return View(tableCustomer);
        }

        // GET: Customer/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableCustomer = await _context.TableCustomer.FindAsync(id);
            if (tableCustomer == null)
            {
                return NotFound();
            }
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableCustomer.AddressId);
            return View(tableCustomer);
        }

        // POST: Customer/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Name,FiscalCode,AddressId")] TableCustomer tableCustomer)
        {
            if (id != tableCustomer.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableCustomer);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableCustomerExists(tableCustomer.Id))
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
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableCustomer.AddressId);
            return View(tableCustomer);
        }

        // GET: Customer/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableCustomer = await _context.TableCustomer
                .Include(t => t.Address)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableCustomer == null)
            {
                return NotFound();
            }

            return View(tableCustomer);
        }

        // POST: Customer/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableCustomer = await _context.TableCustomer.FindAsync(id);
            _context.TableCustomer.Remove(tableCustomer);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableCustomerExists(int id)
        {
            return _context.TableCustomer.Any(e => e.Id == id);
        }
    }
}
