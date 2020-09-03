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
    public static class StateFactory
    {
        public static async Task<ExpertyState> CreateAsync(int expertiseId, Manager manager, IStringLocalizer stringLocalizer, StatusType forced)
        {
            var expertise = await manager.Context.TableExpertise
                .Include(x => x.Member)
                .Include(x => x.Contributor)
                    .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == expertiseId).NoContext();
            StatusType statusType = (StatusType)expertise.Status;
            if (forced > statusType)
                statusType = forced;
            return statusType switch
            {
                StatusType.Registered => new RegisteredState(expertise, manager, stringLocalizer, false),
                StatusType.Assigned => new AssignedState(expertise, manager, stringLocalizer, false),
                StatusType.ToBeRevised => new ToBeRevisedState(expertise, manager, stringLocalizer, false),
                StatusType.Revised => new RevisedState(expertise, manager, stringLocalizer, false),
                StatusType.ToSend => new ToSendState(expertise, manager, stringLocalizer, false),
                StatusType.Billed => new BilledState(expertise, manager, stringLocalizer, false),
                StatusType.Cancelled => new CancelledState(expertise, manager, stringLocalizer, false),
                StatusType.Duplicated => new DuplicatedState(expertise, manager, stringLocalizer, false),
                _ => new RegisteredState(expertise, manager, stringLocalizer, false),
            };
        }
    }
}