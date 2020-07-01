using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Rystem.Web.Backoffice;
using Microsoft.Extensions.Localization;
using Expertiser.Base.Constant;
using Expertiser.Interfaces;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.OData.Edm;
using System.Security.Claims;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.AdminContributor)]
    public class ExpertyController : BaseController<TableExpertise>
    {
        public ExpertyController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }
        public override INavigation<TableExpertise> GetIndexNavigation()
           => Manager.NavigationManager.GetExpertiseNavigation("Expertise", this.StringLocalizer, this.User);
        public override INavigation<TableExpertise> GetDeleteNavigation()
           => Manager.NavigationManager.GetExpertiseNavigation("Delete", this.StringLocalizer, this.User);

        public override async Task<IEnumerable<TableExpertise>> GetList()
        {
            if (Manager.UserHelperManager.IsManager)
            {
                return await this.Manager.QueryManager.Expertises
                           .OrderBy(t => t.Status)
                           .ThenByDescending(x => x.Id)
                           .ToListAsync();
            }
            else if (Manager.UserHelperManager.IsReader)
            {
                return await this.Manager.QueryManager.Expertises
                           .Where(t => t.ContributorId == Manager.UserHelperManager.Id)
                           .OrderBy(x => x.Id)
                           .ThenByDescending(x => x.Id)
                           .ToListAsync();
            }
            else
                return new List<TableExpertise>();
        }

        public override async Task<TableExpertise> GetModel(string id)
        {
            var experty = await Manager.QueryManager.Expertises
                           .FirstOrDefaultAsync(x => x.Id == int.Parse(id));
            if (Manager.UserHelperManager.IsReader && experty.ContributorId != Manager.UserHelperManager.Id)
                throw new UnauthorizedAccessException($"{Manager.HttpContextAccessor.HttpContext.User.Identity.Name} not authorized");
            else
                return experty;
        }

        public override async Task<bool> Remove(string id)
        {
            var expertise = await Manager.Context.TableExpertise
                .Include(x => x.TableBill)
                .Include(x => x.TableMessage)
                .FirstOrDefaultAsync(x => x.Id == int.Parse(id));
            foreach (var bill in expertise.TableBill)
                Manager.Context.TableBill.Remove(bill);
            foreach (var message in expertise.TableMessage)
                Manager.Context.TableMessage.Remove(message);
            Manager.Context.TableExpertise.Remove(expertise);
            await Manager.Context.SaveChangesAsync();
            return true;
        }
        public async Task<IActionResult> CreateNew(int? id)
        {
            if (Manager.UserHelperManager.IsManager && id.HasValue)
            {
                TableExpertise tableExpertise = new TableExpertise()
                {
                    AccidentId = id.Value,
                    MemberId = HttpContext.User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier).Value,
                    Status = -1,
                    BranchId = 1,
                };
                Manager.Context.Add(tableExpertise);
                await Manager.Context.SaveChangesAsync();
                return View("Edit", tableExpertise);
            }
            else
                throw new UnauthorizedAccessException($"{Manager.HttpContextAccessor.HttpContext.User.Identity.Name} not authorized");
        }
        // GET: Experty/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return View();
            var tableExpertise = await Manager.QueryManager.Expertises.FirstOrDefaultAsync(x => x.Id == id);
            if (tableExpertise == null)
                return NotFound();
            return View(tableExpertise);
        }

        // POST: Experty/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,MemberId,ContributorId,Status,BranchId,ExternalId,AssignmentDate,InspectionDate,Liquidated,AccidentId")] TableExpertise tableExpertise)
        {
            if (ModelState.IsValid)
            {
                if (tableExpertise.Id == 0)
                {
                    Manager.Context.Add(tableExpertise);
                    await Manager.Context.SaveChangesAsync();
                    var expertise = await Manager.Context.TableExpertise.Include(x => x.Contributor).FirstOrDefaultAsync(x => x.Id == tableExpertise.Id);
                    if (expertise.Member != null)
                        await Manager.ServiceManager.EmailSender.SendEmailAsync(expertise.Member.Email, StringLocalizer["new experty"], string.Format(StringLocalizer["bodynewexperty"], $"{Manager.BaseUri}/Experty/Edit/{tableExpertise.Id}"));
                }
                else
                {
                    var expertise = await Manager.Context.TableExpertise.Include(x => x.Contributor).Include(x => x.Member).AsNoTracking().FirstOrDefaultAsync(x => x.Id == tableExpertise.Id);
                    StatusType statusType = (StatusType)expertise.Status;
                    StatusType newStatusType = (StatusType)tableExpertise.Status;
                    if (newStatusType == StatusType.Billed)
                        tableExpertise.CloseDate = DateTime.UtcNow;
                    else
                        tableExpertise.CloseDate = null;
                    Manager.Context.Update(tableExpertise);
                    await Manager.Context.SaveChangesAsync();
                    var newExpertise = await Manager.Context.TableExpertise.Include(x => x.Contributor).Include(x => x.Member).FirstOrDefaultAsync(x => x.Id == tableExpertise.Id);
                    if (statusType != newStatusType || newExpertise.ContributorId != expertise.ContributorId)
                    {
                        string subject = string.Format(StringLocalizer["modify experty"], SharedLocalizer[$"{newStatusType}"]);
                        string message = string.Format(StringLocalizer["bodynewexperty"], $"{Manager.BaseUri}/Experty/Edit/{tableExpertise.Id}");
                        if (newExpertise.Contributor.Email != User.Identity.Name)
                            await Manager.ServiceManager.EmailSender.SendEmailAsync(expertise.Contributor.Email, subject, message);
                        if (newExpertise.Member.Email != User.Identity.Name)
                            await Manager.ServiceManager.EmailSender.SendEmailAsync(expertise.Member.Email, subject, message);
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            return View(tableExpertise);
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Bill(int id, [Bind("Id", "Amount", "Extra", "ExpertiseId", "Percentage")] TableBill tableBill)
        {
            if (ModelState.IsValid)
            {
                if (tableBill.Id == 0)
                {
                    Manager.Context.Add(tableBill);
                    await Manager.Context.SaveChangesAsync();
                }
                else
                {
                    Manager.Context.Update(tableBill);
                    await Manager.Context.SaveChangesAsync();
                }
                return PartialView("_EditListBill", await Manager.QueryManager.ExpertiseBills.FirstOrDefaultAsync(x => x.Id == tableBill.ExpertiseId));
            }
            return NotOkMessage(ModelState);
        }
        [HttpPost]
        public async Task<IActionResult> DeleteBill(int id)
        {
            var bill = await Manager.Context.TableBill.FirstOrDefaultAsync(x => x.Id == id);
            Manager.Context.Remove(bill);
            await Manager.Context.SaveChangesAsync();
            return PartialView("_EditListBill", await Manager.QueryManager.ExpertiseBills.FirstOrDefaultAsync(x => x.Id == bill.ExpertiseId));
        }
    }
}