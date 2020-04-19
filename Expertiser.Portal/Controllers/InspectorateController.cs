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
    public class InspectorateController : Controller
    {
        private readonly ContextExpertiser _context;

        public InspectorateController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: Inspectorate
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableInspectorate.Include(t => t.Address).Include(t => t.InsuranceCompany);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: Inspectorate/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInspectorate = await _context.TableInspectorate
                .Include(t => t.Address)
                .Include(t => t.InsuranceCompany)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInspectorate == null)
            {
                return NotFound();
            }

            return View(tableInspectorate);
        }

        // GET: Inspectorate/Create
        public IActionResult Create()
        {
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City");
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label");
            return View();
        }

        // POST: Inspectorate/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Label,ExternalId,AddressId,InsuranceCompanyId")] TableInspectorate tableInspectorate)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableInspectorate);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInspectorate.AddressId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInspectorate.InsuranceCompanyId);
            return View(tableInspectorate);
        }

        // GET: Inspectorate/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInspectorate = await _context.TableInspectorate.FindAsync(id);
            if (tableInspectorate == null)
            {
                return NotFound();
            }
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInspectorate.AddressId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInspectorate.InsuranceCompanyId);
            return View(tableInspectorate);
        }

        // POST: Inspectorate/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Label,ExternalId,AddressId,InsuranceCompanyId")] TableInspectorate tableInspectorate)
        {
            if (id != tableInspectorate.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableInspectorate);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableInspectorateExists(tableInspectorate.Id))
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
            ViewData["AddressId"] = new SelectList(_context.TableLocation, "Id", "City", tableInspectorate.AddressId);
            ViewData["InsuranceCompanyId"] = new SelectList(_context.TableInsuranceCompany, "Id", "Label", tableInspectorate.InsuranceCompanyId);
            return View(tableInspectorate);
        }

        // GET: Inspectorate/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableInspectorate = await _context.TableInspectorate
                .Include(t => t.Address)
                .Include(t => t.InsuranceCompany)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableInspectorate == null)
            {
                return NotFound();
            }

            return View(tableInspectorate);
        }

        // POST: Inspectorate/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableInspectorate = await _context.TableInspectorate.FindAsync(id);
            _context.TableInspectorate.Remove(tableInspectorate);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableInspectorateExists(int id)
        {
            return _context.TableInspectorate.Any(e => e.Id == id);
        }
    }
}
