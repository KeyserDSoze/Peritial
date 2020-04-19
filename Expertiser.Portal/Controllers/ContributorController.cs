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
    public class ContributorController : Controller
    {
        private readonly ContextExpertiser _context;

        public ContributorController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: Contributor
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableContributor.Include(t => t.Contributor).Include(t => t.Member);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: Contributor/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableContributor = await _context.TableContributor
                .Include(t => t.Contributor)
                .Include(t => t.Member)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableContributor == null)
            {
                return NotFound();
            }

            return View(tableContributor);
        }

        // GET: Contributor/Create
        public IActionResult Create()
        {
            ViewData["ContributorId"] = new SelectList(_context.TableMember, "Id", "Name");
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name");
            return View();
        }

        // POST: Contributor/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("MemberId,ContributorId,Id,IsActive")] TableContributor tableContributor)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableContributor);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["ContributorId"] = new SelectList(_context.TableMember, "Id", "Name", tableContributor.ContributorId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableContributor.MemberId);
            return View(tableContributor);
        }

        // GET: Contributor/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableContributor = await _context.TableContributor.FindAsync(id);
            if (tableContributor == null)
            {
                return NotFound();
            }
            ViewData["ContributorId"] = new SelectList(_context.TableMember, "Id", "Name", tableContributor.ContributorId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableContributor.MemberId);
            return View(tableContributor);
        }

        // POST: Contributor/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("MemberId,ContributorId,Id,IsActive")] TableContributor tableContributor)
        {
            if (id != tableContributor.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableContributor);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableContributorExists(tableContributor.Id))
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
            ViewData["ContributorId"] = new SelectList(_context.TableMember, "Id", "Name", tableContributor.ContributorId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableContributor.MemberId);
            return View(tableContributor);
        }

        // GET: Contributor/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableContributor = await _context.TableContributor
                .Include(t => t.Contributor)
                .Include(t => t.Member)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableContributor == null)
            {
                return NotFound();
            }

            return View(tableContributor);
        }

        // POST: Contributor/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableContributor = await _context.TableContributor.FindAsync(id);
            _context.TableContributor.Remove(tableContributor);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableContributorExists(int id)
        {
            return _context.TableContributor.Any(e => e.Id == id);
        }
    }
}
