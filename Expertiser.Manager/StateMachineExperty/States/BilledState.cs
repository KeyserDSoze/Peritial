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
    public class BilledState : ExpertyState
    {
        public BilledState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer, bool isNotified) : base(expertise, manager, stringLocalizer, isNotified)
        {
        }
        public override StatusType StatusType => StatusType.Billed;
        private protected override ExpertyState GetNext()
        {
            if (this.Expertise.CloseDate == null)
                return new SentState(this.Expertise, this.Manager, this.StringLocalizer, this.IsNotified);
            else
                return new BilledState(this.Expertise, this.Manager, this.StringLocalizer, this.IsNotified);
        }

        private protected override Task InternalExecute(TableExpertise previousExpertise)
        {
            if (this.Expertise.CloseDate == null)
                this.Expertise.CloseDate = DateTime.UtcNow;
            return Task.CompletedTask;
        }
    }
}