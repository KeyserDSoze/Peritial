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
    public class RegisteredState : ExpertyState
    {
        public RegisteredState(TableExpertise expertise, Manager manager, IStringLocalizer stringLocalizer, bool isNotified) : base(expertise, manager, stringLocalizer, isNotified)
        {
        }
        public override StatusType StatusType => StatusType.Registered;

        private protected override ExpertyState GetNext()
            => new AssignedState(this.Expertise, this.Manager, this.StringLocalizer, this.IsNotified);

        private protected override async Task InternalExecute(TableExpertise previousExpertise)
        {
            if (previousExpertise.MemberId != this.Expertise.MemberId)
                await this.NotifyAsync();
        }
    }
}