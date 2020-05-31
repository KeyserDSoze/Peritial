using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Rystem.Web;

namespace Expertiser.Portal.Controllers
{
    [Authorize]
    public class AccidentController : Controller
    {
        private readonly ContextExpertiser _context;
        private readonly IQueryable<TableLocation> Locations;
        private readonly IQueryable<TableInsurance> InsuranceAgencies;
        public AccidentController(ContextExpertiser context)
        {
            _context = context;
            Locations = _context.TableLocation.Include(x => x.City);
            InsuranceAgencies = _context.TableInsurance
                    .Include(x => x.InsuranceCompany)
                    .ThenInclude(x => x.Address);
        }

        // GET: Accident
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableAccident.Include(t => t.Inspectorate).Include(t => t.Insurance).Include(t => t.InsuranceAgency).Include(t => t.Place);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: Accident/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableAccident = await _context.TableAccident
                .Include(t => t.Inspectorate)
                .Include(t => t.Insurance)
                .Include(t => t.InsuranceAgency)
                .Include(t => t.Place)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableAccident == null)
            {
                return NotFound();
            }

            return View(tableAccident);
        }

        // GET: Accident/Create
        public IActionResult Create()
        {
            ViewData["InspectorateId"] = new SelectList(_context.TableInspectorate, "Id", "Label");
            ViewData["InsuranceId"] = new SelectList(_context.TableInsurance, "Id", "ExternalId");
            ViewData["InsuranceAgencyId"] = new SelectList(_context.TableInsuranceAgency, "Id", "Label");
            ViewData["PlaceId"] = Locations.Select(x => new DropdownItem() { Value = x.Id.ToString(), Group = x.City.Name, Label = x.Address });
            return View();
        }

        // POST: Accident/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0037:Use inferred member name", Justification = "<Pending>")]
        public async Task<IActionResult> Create([Bind("Id,PlaceId,InsuranceId,InsuranceAgencyId,InspectorateId,ExternalId,AccidentDate,DenunciationDate")] TableAccident tableAccident)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableAccident);
                await _context.SaveChangesAsync();
                TableExpertise tableExpertise = new TableExpertise()
                {
                    AccidentId = tableAccident.Id,
                    MemberId = HttpContext.User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier).Value,
                    Status = 0,
                    BranchId = 1,
                    ExternalId = string.Empty
                };
                _context.TableExpertise.Add(tableExpertise);
                await _context.SaveChangesAsync();
                return RedirectToAction("Edit", "Experty", new { Id = tableExpertise.Id });
            }
            ViewData["InspectorateId"] = new SelectList(_context.TableInspectorate, "Id", "Label", tableAccident.InspectorateId);
            ViewData["InsuranceId"] = new SelectList(_context.TableInsurance, "Id", "ExternalId", tableAccident.InsuranceId);
            ViewData["InsuranceAgencyId"] = new SelectList(_context.TableInsuranceAgency, "Id", "Label", tableAccident.InsuranceAgencyId);
            ViewData["PlaceId"] = new SelectList(_context.TableLocation, "Id", "Id", tableAccident.PlaceId);
            return View(tableAccident);
        }

        // GET: Accident/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableAccident = await _context.TableAccident.FindAsync(id);
            if (tableAccident == null)
            {
                return NotFound();
            }
            ViewData["InspectorateId"] = new SelectList(_context.TableInspectorate, "Id", "Label", tableAccident.InspectorateId);
            ViewData["InsuranceId"] = new SelectList(_context.TableInsurance, "Id", "ExternalId", tableAccident.InsuranceId);
            ViewData["InsuranceAgencyId"] = new SelectList(_context.TableInsuranceAgency, "Id", "Label", tableAccident.InsuranceAgencyId);
            ViewData["PlaceId"] = new SelectList(_context.TableLocation, "Id", "Id", tableAccident.PlaceId);
            return View(tableAccident);
        }

        // POST: Accident/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,PlaceId,InsuranceId,InsuranceAgencyId,InspectorateId,ExternalId,AccidentDate,DenunciationDate")] TableAccident tableAccident)
        {
            if (id != tableAccident.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableAccident);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableAccidentExists(tableAccident.Id))
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
            ViewData["InspectorateId"] = new SelectList(_context.TableInspectorate, "Id", "Label", tableAccident.InspectorateId);
            ViewData["InsuranceId"] = new SelectList(_context.TableInsurance, "Id", "ExternalId", tableAccident.InsuranceId);
            ViewData["InsuranceAgencyId"] = new SelectList(_context.TableInsuranceAgency, "Id", "Label", tableAccident.InsuranceAgencyId);
            ViewData["PlaceId"] = new SelectList(_context.TableLocation, "Id", "Id", tableAccident.PlaceId);
            return View(tableAccident);
        }

        // GET: Accident/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableAccident = await _context.TableAccident
                .Include(t => t.Inspectorate)
                .Include(t => t.Insurance)
                .Include(t => t.InsuranceAgency)
                .Include(t => t.Place)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableAccident == null)
            {
                return NotFound();
            }

            return View(tableAccident);
        }

        // POST: Accident/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableAccident = await _context.TableAccident.FindAsync(id);
            _context.TableAccident.Remove(tableAccident);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableAccidentExists(int id)
        {
            return _context.TableAccident.Any(e => e.Id == id);
        }
    }
}
