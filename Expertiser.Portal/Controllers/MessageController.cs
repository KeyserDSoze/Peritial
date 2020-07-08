using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.Localization;
using Rystem.Web.Backoffice;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = (Roles.All))]
    public class MessageController : BaseController<TableMessage>
    {
        public MessageController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer) : base(manager, stringLocalizer, sharedLocalizer)
        {
        }

        public override INavigation<TableMessage> GetDeleteNavigation()
        {
            throw new NotImplementedException();
        }

        public override INavigation<TableMessage> GetIndexNavigation()
        {
            throw new NotImplementedException();
        }

        public override Task<IEnumerable<TableMessage>> GetList()
        {
            throw new NotImplementedException();
        }

        public override Task<TableMessage> GetModel(string id)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> Remove(string id)
        {
            throw new NotImplementedException();
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(int id, [Bind("ExpertiseId, Value, UserId, Type")] TableMessage message)
        {
            if (this.IsAValidModel(message.Id))
            {
                Manager.Context.TableMessage.Add(message);
                await Manager.Context.SaveChangesAsync();
                return PartialView("_Messages", Manager.Context.TableMessage
                    .Include(x => x.User).Where(x => x.ExpertiseId == id));
            }
            return NotOkMessage(ModelState);
        }
    }
}
