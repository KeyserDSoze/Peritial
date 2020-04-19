using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;

namespace Expertiser.Portal.Views.Expertise
{
    public class ExpertiseController : Controller
    {
        private readonly ContextExpertiser _context;

        public ExpertiseController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: Expertise
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableExpertise.Include(t => t.Contributor).Include(t => t.CounterPart).Include(t => t.Inspectorate).Include(t => t.Insurance).Include(t => t.InsuranceAgency).Include(t => t.Member);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: Expertise/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableExpertise = await _context.TableExpertise
                .Include(t => t.Contributor)
                .Include(t => t.CounterPart)
                .Include(t => t.Inspectorate)
                .Include(t => t.Insurance)
                .Include(t => t.InsuranceAgency)
                .Include(t => t.Member)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableExpertise == null)
            {
                return NotFound();
            }

            return View(tableExpertise);
        }

        // GET: Expertise/Create
        public IActionResult Create()
        {
            ViewData["ContributorId"] = new SelectList(_context.TableContributor, "Id", "Id");
            ViewData["CounterPartId"] = new SelectList(_context.TableCustomer, "Id", "Name");
            ViewData["InspectorateId"] = new SelectList(_context.TableInspectorate, "Id", "Label");
            ViewData["InsuranceId"] = new SelectList(_context.TableInsurance, "Id", "ExternalId");
            ViewData["InsuranceAgencyId"] = new SelectList(_context.TableInsuranceAgency, "Id", "Label");
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name");
            return View();
        }

        // POST: Expertise/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,MemberId,ContributorId,CounterPartId,Status,InsuranceAgencyId,InspectorateId,Branch,BranchType,PlaceId,InsuranceId,ExternalId,AccidentDate,ComplaintDate,AssignmentDate,InspectionDate,AccidentId")] TableExpertise tableExpertise)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableExpertise);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["ContributorId"] = new SelectList(_context.TableContributor, "Id", "Id", tableExpertise.ContributorId);
            ViewData["CounterPartId"] = new SelectList(_context.TableCustomer, "Id", "Name", tableExpertise.CounterPartId);
            ViewData["InspectorateId"] = new SelectList(_context.TableInspectorate, "Id", "Label", tableExpertise.InspectorateId);
            ViewData["InsuranceId"] = new SelectList(_context.TableInsurance, "Id", "ExternalId", tableExpertise.InsuranceId);
            ViewData["InsuranceAgencyId"] = new SelectList(_context.TableInsuranceAgency, "Id", "Label", tableExpertise.InsuranceAgencyId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExpertise.MemberId);
            return View(tableExpertise);
        }

        // GET: Expertise/Edit/5
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
            ViewData["ContributorId"] = new SelectList(_context.TableContributor, "Id", "Id", tableExpertise.ContributorId);
            ViewData["CounterPartId"] = new SelectList(_context.TableCustomer, "Id", "Name", tableExpertise.CounterPartId);
            ViewData["InspectorateId"] = new SelectList(_context.TableInspectorate, "Id", "Label", tableExpertise.InspectorateId);
            ViewData["InsuranceId"] = new SelectList(_context.TableInsurance, "Id", "ExternalId", tableExpertise.InsuranceId);
            ViewData["InsuranceAgencyId"] = new SelectList(_context.TableInsuranceAgency, "Id", "Label", tableExpertise.InsuranceAgencyId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExpertise.MemberId);
            return View(tableExpertise);
        }

        // POST: Expertise/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,MemberId,ContributorId,CounterPartId,Status,InsuranceAgencyId,InspectorateId,Branch,BranchType,PlaceId,InsuranceId,ExternalId,AccidentDate,ComplaintDate,AssignmentDate,InspectionDate,AccidentId")] TableExpertise tableExpertise)
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
            ViewData["ContributorId"] = new SelectList(_context.TableContributor, "Id", "Id", tableExpertise.ContributorId);
            ViewData["CounterPartId"] = new SelectList(_context.TableCustomer, "Id", "Name", tableExpertise.CounterPartId);
            ViewData["InspectorateId"] = new SelectList(_context.TableInspectorate, "Id", "Label", tableExpertise.InspectorateId);
            ViewData["InsuranceId"] = new SelectList(_context.TableInsurance, "Id", "ExternalId", tableExpertise.InsuranceId);
            ViewData["InsuranceAgencyId"] = new SelectList(_context.TableInsuranceAgency, "Id", "Label", tableExpertise.InsuranceAgencyId);
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExpertise.MemberId);
            return View(tableExpertise);
        }

        // GET: Expertise/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableExpertise = await _context.TableExpertise
                .Include(t => t.Contributor)
                .Include(t => t.CounterPart)
                .Include(t => t.Inspectorate)
                .Include(t => t.Insurance)
                .Include(t => t.InsuranceAgency)
                .Include(t => t.Member)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableExpertise == null)
            {
                return NotFound();
            }

            return View(tableExpertise);
        }

        // POST: Expertise/Delete/5
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
