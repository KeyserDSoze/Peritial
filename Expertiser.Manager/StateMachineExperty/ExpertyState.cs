using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser
{
    public abstract class ExpertyState
    {
        public abstract StatusType StatusType { get; }
        private protected abstract Task InternalExecute(TableExpertise previousExpertise);
        public IEnumerable<ExpertiseDocument> Files => ExpertiseDocument.Instance.Where(x => x.MinimumStatus <= this.StatusType);
        public IStringLocalizer StringLocalizer { get; }
        private protected virtual string Body { get; }
        public Manager Manager { get; }
        public TableExpertise Expertise { get; private set; }
        public bool IsNotified { get; private set; }
        private protected abstract ExpertyState GetNext();
        public string Link => $"<a href='{this.Uri}'>{this.StringLocalizer["Click here"]}</a>";
        public string Uri => $"https://{Manager.HttpContextAccessor.HttpContext.Request.Host}/Experty/Edit/{this.Expertise.Id}";
        private async Task UpdateExpertiseAsync()
        {
            Manager.Context.Update(this.Expertise);
            await Manager.Context.SaveChangesAsync().NoContext();
        }
        public ExpertyState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer, bool isNotified)
        {
            this.Expertise = expertise;
            this.Manager = manager;
            this.StringLocalizer = stringLocalizer;
            this.IsNotified = isNotified;
        }
        public async Task ExecuteAndNext(TableExpertise expertiseToUpdate)
        {
            var expertise = this.Expertise;
            this.Expertise = expertiseToUpdate;
            await this.InternalExecute(expertise).NoContext();
            await this.UpdateExpertiseAsync().NoContext();
            this.Expertise = await Manager.Context.TableExpertise
                .Include(x => x.Member)
                .Include(x => x.Contributor)
                        .FirstOrDefaultAsync(x => x.Id == this.Expertise.Id).NoContext();
            if (expertise.StatusType != this.Expertise.StatusType || this.Expertise.StatusType == StatusType.Registered)
                await Next(this.Expertise.StatusType);
        }
        public async Task<ExpertyState> Next(StatusType minimumStatus)
        {
            if (minimumStatus == this.Expertise.StatusType)
            {
                var next = this.GetNext();
                this.Expertise.Status = (int)next.StatusType;
                await this.UpdateExpertiseAsync().NoContext();
                await this.NotifyAsync();
                return next;
            }
            return this;
        }
        private protected async Task NotifyAsync()
        {
            if (!IsNotified)
            {
                List<Task> notifications = new List<Task>();
                foreach (string email in GetEmails())
                    notifications.Add(Manager.ServiceManager.EmailSender.SendEmailAsync(email, $"{this.StringLocalizer["Status"]} {this.StringLocalizer[this.StatusType.ToString()]}", this.Body ?? $"{this.StringLocalizer["Check it out the change of status"]} {this.Link}"));
                await Task.WhenAll(notifications);
                this.IsNotified = true;
            }

            IEnumerable<string> GetEmails()
            {
                if (this.Expertise.Member != null)
                    yield return this.Expertise.Member.Email;
                if (this.Expertise.Contributor != null)
                    yield return this.Expertise.Contributor.Email;
            }
        }
    }
}