using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Rystem.Web;
using System.Net;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Rystem.Web.Backoffice;
using Microsoft.Extensions.Localization;

namespace Expertiser.Portal.Controllers
{
    public abstract class BaseController<T> : BackOfficeController<T>
        where T : class
    {
        private protected readonly Manager Manager;
        private protected readonly IStringLocalizer<SharedResource> StringLocalizer;
        private protected readonly IStringLocalizer<SharedResource> SharedLocalizer;
        public BaseController(Manager manager, IStringLocalizer<SharedResource> stringLocalizer, IStringLocalizer<SharedResource> sharedLocalizer)
        {
            this.Manager = manager;
            this.StringLocalizer = stringLocalizer;
            this.SharedLocalizer = sharedLocalizer;
        }
        private protected bool IsAValidModel(int id)
        {
            if (ModelState.IsValid)
                return true;
            else if (id == 0)
            {
                foreach (ModelStateEntry child in ModelState.Root.Children)
                {
                    string key = (string)child.GetType().GetProperties().First(x => x.Name == "Key").GetValue(child);
                    if (key.ToLower() != "id" && child.ValidationState == ModelValidationState.Invalid)
                        return false;
                }
                return true;
            }
            else
                return false;
        }
    }
}
