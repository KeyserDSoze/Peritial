using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Rystem;
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
        private protected abstract Task<bool> InternalExecute();
        public IStringLocalizer StringLocalizer { get; }
        private protected virtual string Body { get; }
        public Manager Manager { get; }
        public TableExpertise Expertise { get; private set; }
        public TableExpertise PreviousExpertise { get; private set; }
        public bool IsNotified { get; private set; }
        private protected virtual bool IsNotificationNeeded => false;
        private protected abstract ExpertyState GetNext();
        public string Link => $"<a href='{this.Uri}'>{this.StringLocalizer["Click here"]}</a>";
        public string Uri => $"https://{Manager.HttpContextAccessor.HttpContext.Request.Host}/Experty/Edit/{this.Expertise.Id}";
        private async Task UpdateExpertiseAsync()
        {
            Manager.Context.Update(this.Expertise);
            await Manager.Context.SaveChangesAsync().NoContext();
            this.Manager.TelemetryManager.MyTelemetry.TrackTrace(LogLevel.Debug, "Update trace");
            this.Expertise = await Manager.QueryManager.Expertises
                        .FirstOrDefaultAsync(x => x.Id == this.Expertise.Id).NoContext();
        }
        public ExpertyState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer, bool isNotified)
        {
            this.Expertise = expertise;
            this.PreviousExpertise = expertise;
            this.Manager = manager;
            this.StringLocalizer = stringLocalizer;
            this.IsNotified = isNotified;
        }
        private readonly List<string> Attachments = new List<string>();
        public ExpertyState AddAttachment(IEnumerable<string> files)
        {
            Attachments.AddRange(files);
            return this;
        }
        public async Task ExecuteAndNext(TableExpertise expertiseToUpdate)
        {
            this.Expertise = expertiseToUpdate;
            bool handled = await this.InternalExecute().NoContext();
            await this.UpdateExpertiseAsync().NoContext();
            this.Manager.TelemetryManager.MyTelemetry.TrackTrace(LogLevel.Information, $"Handled: {handled} -> {this.Expertise.StatusType}");
            if (handled)
                await Next(this.Expertise.StatusType + 1);
        }
        public async Task<ExpertyState> Next(StatusType minimumStatus)
        {
            this.Manager.TelemetryManager.MyTelemetry.TrackTrace(LogLevel.Information, $"{this.Expertise.StatusType} vs {minimumStatus}");
            var currentState = this;
            int count = 0;
            while (currentState.StatusType < minimumStatus)
            {
                var next = currentState.GetNext();
                currentState.Expertise.Status = (int)next.StatusType;
                await currentState.UpdateExpertiseAsync().NoContext();
                await next.NotifyAsync();
                currentState = next;
                count++;
                if (count > 10)
                    break;
            }
            return currentState;
        }
        private protected async Task NotifyAsync()
        {
            if (!IsNotified && IsNotificationNeeded)
            {
                List<Task> notifications = new List<Task>();
                foreach (string email in GetEmails())
                {
                    this.Manager.TelemetryManager.MyTelemetry.TrackTrace(LogLevel.Information, $"Notify to {email}");
                    StringBuilder body = new StringBuilder(this.Body ?? $"{this.StringLocalizer["Check it out the change of status"]} {this.Link}");
                    body.Append("<br />");
                    if (this.Attachments.Count > 0)
                    {
                        body.Append("<hr />");
                        body.Append($"<div>{this.StringLocalizer["Attachment links"]}</div>");
                        foreach (string attachment in this.Attachments)
                            body.Append($"<a download href='{attachment}'>{attachment}</a><br />");
                    }
                    body.Append("<hr />");
                    body.Append("<br />");
                    body.Append("<br />");
                    body.Append(this.StringLocalizer["Best Regards"]);
                    body.Append("<br />");
                    body.Append(Manager.Title);
                    notifications.Add(Manager.ServiceManager.EmailSender.SendEmailAsync(email, $"{this.Expertise.ConcreteId} - {this.StringLocalizer["Status"]} {this.StringLocalizer[this.StatusType.ToString()]} {(this.Attachments.Count > 0 ? this.StringLocalizer["With Attachments"] : string.Empty)}", body.ToString()));
                }
                await Task.WhenAll(notifications);
                this.IsNotified = true;
            }

            IEnumerable<string> GetEmails()
            {
                if (this.Expertise.Member != null && this.Expertise.MemberId != this.Manager.UserHelperManager.Id)
                    yield return this.Expertise.Member.Email;
                if (this.Expertise.Contributor != null && this.Expertise.ContributorId != this.Manager.UserHelperManager.Id)
                    yield return this.Expertise.Contributor.Email;
            }
        }
    }
}