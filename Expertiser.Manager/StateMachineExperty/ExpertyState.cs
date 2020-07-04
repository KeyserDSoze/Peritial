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
    public class ExpertiseDocument
    {
        public string Name { get; }
        public StatusType MinimumStatus { get; }
        private ExpertiseDocument(string name, StatusType statusType)
        {
            this.Name = name;
            this.MinimumStatus = statusType;
        }
        public static List<ExpertiseDocument> Instance { get; } = new List<ExpertiseDocument>()
        {
            new ExpertiseDocument("AccidentDocs", StatusType.Registered),
            new ExpertiseDocument("Photos", StatusType.Registered),
            new ExpertiseDocument("RawExpertise", StatusType.Assigned),
            new ExpertiseDocument("CorrectedExpertise", StatusType.ToBeRevised),
            new ExpertiseDocument("CompletedExpertise", StatusType.Revised),
            new ExpertiseDocument("ReservedNote", StatusType.Revised),
        };
    }
    public class DuplicatedState : ExpertyState
    {
        public DuplicatedState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.Duplicated;
        private protected override string Body { get; }

        private protected override ExpertyState GetNext()
            => new RegisteredState(this.Expertise, this.Manager, this.StringLocalizer);

        private protected override Task InternalExecute()
        {
            this.Expertise.ContributorId = null;
            this.Expertise.InspectionDate = null;
            this.Expertise.Liquidated = null;
            return Task.CompletedTask;
        }
    }
    public class CancelledState : ExpertyState
    {
        public CancelledState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.Cancelled;
        private protected override string Body { get; }

        private protected override ExpertyState GetNext()
            => new RegisteredState(this.Expertise, this.Manager, this.StringLocalizer);

        private protected override Task InternalExecute()
        {
            this.Expertise.ContributorId = null;
            this.Expertise.InspectionDate = null;
            this.Expertise.Liquidated = null;
            return Task.CompletedTask;
        }
    }
    public class BilledState : ExpertyState
    {
        public BilledState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.Billed;
        private protected override string Body { get; }
        private protected override ExpertyState GetNext()
        {
            if (this.Expertise.CloseDate == null)
                return new SentState(this.Expertise, this.Manager, this.StringLocalizer);
            else
                return new BilledState(this.Expertise, this.Manager, this.StringLocalizer);
        }

        private protected override Task InternalExecute()
        {
            if (this.Expertise.CloseDate == null)
                this.Expertise.CloseDate = DateTime.UtcNow;
            else
                this.Expertise.CloseDate = null;
            return Task.CompletedTask;
        }
    }
    public class SentState : ExpertyState
    {
        public SentState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.Sent;
        private protected override string Body { get; }

        private protected override ExpertyState GetNext()
            => new BilledState(this.Expertise, this.Manager, this.StringLocalizer);

        private protected override Task InternalExecute()
            => Task.CompletedTask;
    }
    public class ToSendState : ExpertyState
    {
        public ToSendState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.ToSend;
        private protected override string Body { get; }

        private protected override ExpertyState GetNext()
            => new SentState(this.Expertise, this.Manager, this.StringLocalizer);

        private protected override Task InternalExecute()
            => Task.CompletedTask;
    }
    public class RevisedState : ExpertyState
    {
        public RevisedState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.Revised;
        private protected override string Body { get; }

        private protected override ExpertyState GetNext()
            => new ToSendState(this.Expertise, this.Manager, this.StringLocalizer);

        private protected override Task InternalExecute()
            => Task.CompletedTask;
    }
    public class ToBeRevisedState : ExpertyState
    {
        public ToBeRevisedState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.ToBeRevised;
        private protected override string Body { get; }

        private protected override ExpertyState GetNext()
            => new RevisedState(this.Expertise, this.Manager, this.StringLocalizer);

        private protected override Task InternalExecute()
            => Task.CompletedTask;
    }
    public class AssignedState : ExpertyState
    {
        public AssignedState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.Assigned;
        private protected override string Body { get; }

        private protected override ExpertyState GetNext()
            => new ToBeRevisedState(this.Expertise, this.Manager, this.StringLocalizer);

        private protected override Task InternalExecute()
            => Task.CompletedTask;
    }
    public class RegisteredState : ExpertyState
    {
        public RegisteredState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer) : base(expertise, manager, stringLocalizer)
        {
            this.Body = "";
        }
        public override StatusType StatusType => StatusType.Registered;
        private protected override string Body { get; }

        private protected override ExpertyState GetNext()
            => new AssignedState(this.Expertise, this.Manager, this.StringLocalizer);

        private protected override Task InternalExecute()
            => Task.CompletedTask;
    }
    public abstract class ExpertyState
    {
        public abstract StatusType StatusType { get; }
        private protected abstract Task InternalExecute();
        public IEnumerable<ExpertiseDocument> Files => ExpertiseDocument.Instance.Where(x => this.StatusType <= x.MinimumStatus);
        public IStringLocalizer StringLocalizer { get; }
        private protected abstract string Body { get; }
        public Manager Manager { get; }
        public TableExpertise Expertise { get; private set; }
        public bool IsNotified { get; private set; }
        private protected abstract ExpertyState GetNext();
        private async Task UpdateExpertiseAsync()
        {
            Manager.Context.Update(this.Expertise);
            await Manager.Context.SaveChangesAsync().NoContext();
        }
        public ExpertyState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer)
        {
            this.Expertise = expertise;
            this.Manager = manager;
            this.StringLocalizer = stringLocalizer;
        }

        public async Task Execute(TableExpertise expertiseToUpdate)
        {
            var expertise = this.Expertise;
            this.Expertise = expertiseToUpdate;
            await this.UpdateExpertiseAsync().NoContext();
            if (expertise.ContributorId != expertiseToUpdate.ContributorId)
                await this.NotifyAsync($"{this.StringLocalizer["New expertise in status"]} {this.StringLocalizer[this.StatusType.ToString()]}", this.Body);
        }
        public async Task<ExpertyState> Next()
        {
            var next = this.GetNext();
            this.Expertise.Status = (int)next.StatusType;
            await this.UpdateExpertiseAsync().NoContext();
            await this.NotifyAsync($"{this.StringLocalizer["Status changed in"]} {this.StringLocalizer[this.StatusType.ToString()]}", this.Body);
            return next;
        }
        private protected async Task NotifyAsync(string subject, string body)
        {
            if (!IsNotified)
            {
                foreach (string email in GetEmails())
                    await Manager.ServiceManager.EmailSender.SendEmailAsync(email, subject, body).NoContext();
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
    public static class StateFactory
    {
        public static async Task<ExpertyState> CreateAsync(int expertiseId, Manager manager, IStringLocalizer stringLocalizer)
        {
            var expertise = await manager.Context.TableExpertise
                .Include(x => x.Member)
                .Include(x => x.Contributor)
                    .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == expertiseId).NoContext();
            StatusType statusType = (StatusType)expertise.Status;
            return statusType switch
            {
                StatusType.Registered => new RegisteredState(expertise, manager, stringLocalizer),
                StatusType.Assigned => new AssignedState(expertise, manager, stringLocalizer),
                StatusType.ToBeRevised => new ToBeRevisedState(expertise, manager, stringLocalizer),
                StatusType.Revised => new RevisedState(expertise, manager, stringLocalizer),
                StatusType.ToSend => new ToSendState(expertise, manager, stringLocalizer),
                StatusType.Billed => new BilledState(expertise, manager, stringLocalizer),
                StatusType.Cancelled => new CancelledState(expertise, manager, stringLocalizer),
                StatusType.Duplicated => new DuplicatedState(expertise, manager, stringLocalizer),
                _ => new RegisteredState(expertise, manager, stringLocalizer),
            };
        }
    }
}