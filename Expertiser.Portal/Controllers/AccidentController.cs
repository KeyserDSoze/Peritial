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
using Expertiser.Base.Constant;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.AdminContributor)]
    public class AccidentController : BaseController
    {
        public AccidentController(Manager manager) : base(manager)
        {
        }
        // GET: Accident
        public ActionResult Index()
            => View(Manager.QueryManager.Accidents);

        // GET: Accident/Manage
        public async Task<IActionResult> Manage(int? id)
        {
            if (id == null)
                return View();
            var tableAccident = await Manager.QueryManager.Accidents.FirstOrDefaultAsync(x => x.Id == id);
            if (tableAccident == null)
                return NotFound();
            return View(tableAccident);
        }

        // POST: Accident/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0037:Use inferred member name", Justification = "<Pending>")]
        public async Task<IActionResult> Manage([Bind("Id,PlaceId,InsuranceId,InsuranceAgencyId,InspectorateId,ExternalId,AccidentDate,DenunciationDate")] TableAccident tableAccident)
        {
            if (ModelState.IsValid)
            {
                if (tableAccident.Id == 0)
                {
                    Manager.Context.Add(tableAccident);
                    await Manager.Context.SaveChangesAsync();
                    TableExpertise tableExpertise = new TableExpertise()
                    {
                        AccidentId = tableAccident.Id,
                        MemberId = HttpContext.User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier).Value,
                        Status = 0,
                        BranchId = 1,
                        ExternalId = string.Empty
                    };
                    Manager.Context.TableExpertise.Add(tableExpertise);
                    await Manager.Context.SaveChangesAsync();
                    return RedirectToAction("Edit", "Experty", new { Id = tableExpertise.Id });
                }
                else
                {
                    Manager.Context.Update(tableAccident);
                    await Manager.Context.SaveChangesAsync();
                }
            }
            return View(tableAccident);
        }
        // GET: Accident/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableAccident = await Manager.QueryManager.Accidents
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
            var tableAccident = await Manager.Context.TableAccident.FindAsync(id);
            Manager.Context.TableAccident.Remove(tableAccident);
            await Manager.Context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
    }
}