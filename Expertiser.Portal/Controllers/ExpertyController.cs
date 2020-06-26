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

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = Roles.AdminContributor)]
    public class ExpertyController : BaseController<TableExpertise>
    {
        public ExpertyController(Manager manager, IStringLocalizer<DatabaseShared> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }
        public override INavigation<TableExpertise> GetIndexNavigation()
           => Manager.NavigationManager.GetExpertiseNavigation("Index", this.StringLocalizer, this.User);
        public override INavigation<TableExpertise> GetDeleteNavigation()
           => Manager.NavigationManager.GetExpertiseNavigation("Delete", this.StringLocalizer, this.User);

        public override async Task<IEnumerable<TableExpertise>> GetList()
            => await this.Manager.QueryManager.Expertises
                .Where(t => t.Status <= (int)StatusType.Presented)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

        public override async Task<TableExpertise> GetModel(string id)
            => await Manager.QueryManager.Expertises
                .FirstOrDefaultAsync(x => x.Id == int.Parse(id));

        public override Task<bool> Remove(string id)
        {
            throw new NotImplementedException();
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
                        Manager.ServiceManager.EmailSender.Send(expertise.Member.Email, StringLocalizer["new experty"], string.Format(StringLocalizer["bodynewexperty"], $"{Manager.BaseUri}/Experty/Edit/{tableExpertise.Id}"));
                }
                else
                {
                    var expertise = await Manager.Context.TableExpertise.Include(x => x.Contributor).Include(x => x.Member).AsNoTracking().FirstOrDefaultAsync(x => x.Id == tableExpertise.Id);
                    StatusType statusType = (StatusType)expertise.Status;
                    Manager.Context.Update(tableExpertise);
                    await Manager.Context.SaveChangesAsync();
                    var newExpertise = await Manager.Context.TableExpertise.Include(x => x.Contributor).Include(x => x.Member).FirstOrDefaultAsync(x => x.Id == tableExpertise.Id);
                    StatusType newStatusType = (StatusType)newExpertise.Status;
                    if (statusType != newStatusType || newExpertise.ContributorId != expertise.ContributorId)
                    {
                        string subject = string.Format(StringLocalizer["modify experty"], SharedLocalizer[$"{newStatusType}"]);
                        string message = string.Format(StringLocalizer["bodynewexperty"], $"{Manager.BaseUri}/Experty/Edit/{tableExpertise.Id}");
                        if (newExpertise.Contributor.Email != User.Identity.Name)
                            Manager.ServiceManager.EmailSender.Send(expertise.Contributor.Email, subject, message);
                        if (newExpertise.Member.Email != User.Identity.Name)
                            Manager.ServiceManager.EmailSender.Send(expertise.Member.Email, subject, message);
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
            if (IsAValidModel(tableBill.ExpertiseId))
            {
                if (tableBill.ExpertiseId == 0)
                {
                    tableBill.ExpertiseId = id;
                    Manager.Context.Add(tableBill);
                    await Manager.Context.SaveChangesAsync();
                }
                else
                {
                    Manager.Context.Update(tableBill);
                    await Manager.Context.SaveChangesAsync();
                }
                return PartialView("_EditListBill", await Manager.QueryManager.Expertises.FirstOrDefaultAsync(x => x.Id == id));
            }
            return NotOkMessage(ModelState);
        }
    }
}