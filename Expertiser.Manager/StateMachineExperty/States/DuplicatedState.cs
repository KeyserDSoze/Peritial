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
    public class DuplicatedState : ExpertyState
    {
        public DuplicatedState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer, bool isNotified) : base(expertise, manager, stringLocalizer, isNotified)
        {
        }
        public override StatusType StatusType => StatusType.Duplicated;

        private protected override ExpertyState GetNext()
            => new RegisteredState(this.Expertise, this.Manager, this.StringLocalizer, this.IsNotified);

        private protected override Task InternalExecute(TableExpertise previousExpertise)
        {
            this.Expertise.ContributorId = null;
            this.Expertise.InspectionDate = null;
            this.Expertise.Liquidated = null;
            return Task.CompletedTask;
        }
    }
}