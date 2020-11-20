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
    public class RevisedState : ExpertyState
    {
        public RevisedState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer, bool isNotified) : base(expertise, manager, stringLocalizer, isNotified)
        {
        }
        public override StatusType StatusType => StatusType.Revised;
        private protected override ExpertyState GetNext()
            => new ToSendState(this.Expertise, this.Manager, this.StringLocalizer, this.IsNotified);

        private protected override Task<bool> InternalExecute()
            => Task.FromResult(this.PreviousExpertise.StatusType == this.Expertise.StatusType);
    }
}