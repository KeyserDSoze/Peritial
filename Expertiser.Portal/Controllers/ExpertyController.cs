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
    public class ExpertyController : Controller
    {
        private readonly ContextExpertiser _context;

        public ExpertyController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: Experty
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableExpertise.Include(t => t.Accident).Include(t => t.Branch).Include(t => t.Contributor).Include(t => t.Member);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: Experty/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableExpertise = await _context.TableExpertise
                .Include(t => t.Accident)
                .Include(t => t.Branch)
                .Include(t => t.Contributor)
                .Include(t => t.Member)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableExpertise == null)
            {
                return NotFound();
            }

            return View(tableExpertise);
        }

        // GET: Experty/Create
        public IActionResult Create()
        {
            ViewData["AccidentId"] = new SelectList(_context.TableAccident, "Id", "ExternalId");
            ViewData["BranchId"] = new SelectList(_context.TableBranch, "Id", "Label");
            ViewData["ContributorId"] = new SelectList(_context.TableContributor, "Id", "Id");
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name");
            return View();
        }

        // POST: Experty/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,MemberId,ContributorId,Status,BranchId,ExternalId,AssignmentDate,InspectionDate,Liquidated,AccidentId")] TableExpertise tableExpertise)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableExpertise);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["AccidentId"] = new SelectList(_context.TableAccident, "Id", "ExternalId", tableExpertise.AccidentId);
            ViewData["BranchId"] = new SelectList(_context.TableBranch, "Id", "Label", tableExpertise.BranchId);
            ViewData["ContributorId"] = new SelectList(_context.TableContributor, "Id", "Id", tableExpertise.ContributorId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExpertise.MemberId);
            return View(tableExpertise);
        }

        // GET: Experty/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableExpertise = await _context.TableExpertise.FindAsync(id);
            if (tableExpertise == null)
            {
                return NotFound();
            }
            ViewData["AccidentId"] = new SelectList(_context.TableAccident, "Id", "ExternalId", tableExpertise.AccidentId);
            ViewData["BranchId"] = new SelectList(_context.TableBranch, "Id", "Label", tableExpertise.BranchId);
            ViewData["ContributorId"] = new SelectList(_context.TableContributor, "Id", "Id", tableExpertise.ContributorId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExpertise.MemberId);
            return View(tableExpertise);
        }

        // POST: Experty/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,MemberId,ContributorId,Status,BranchId,ExternalId,AssignmentDate,InspectionDate,Liquidated,AccidentId")] TableExpertise tableExpertise)
        {
            if (id != tableExpertise.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableExpertise);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableExpertiseExists(tableExpertise.Id))
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
            ViewData["AccidentId"] = new SelectList(_context.TableAccident, "Id", "ExternalId", tableExpertise.AccidentId);
            ViewData["BranchId"] = new SelectList(_context.TableBranch, "Id", "Label", tableExpertise.BranchId);
            ViewData["ContributorId"] = new SelectList(_context.TableContributor, "Id", "Id", tableExpertise.ContributorId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExpertise.MemberId);
            return View(tableExpertise);
        }

        // GET: Experty/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableExpertise = await _context.TableExpertise
                .Include(t => t.Accident)
                .Include(t => t.Branch)
                .Include(t => t.Contributor)
                .Include(t => t.Member)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableExpertise == null)
            {
                return NotFound();
            }

            return View(tableExpertise);
        }

        // POST: Experty/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableExpertise = await _context.TableExpertise.FindAsync(id);
            _context.TableExpertise.Remove(tableExpertise);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableExpertiseExists(int id)
        {
            return _context.TableExpertise.Any(e => e.Id == id);
        }
    }
}
