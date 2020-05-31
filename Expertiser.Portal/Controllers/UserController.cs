using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Expertiser.Base.Constant;

namespace Expertiser.Portal.Controllers
{
    [Authorize(Roles = (Roles.Admin))]
    public class UserController : BaseController
    {
        public UserController(Manager manager) : base(manager)
        {

        }

        // GET: User
        public IActionResult Index()
            => View(Manager.UserManager.Users);

        // GET: User/Manage
        public async Task<IActionResult> Manage(string id)
        {
            if (id == null)
                return NotFound();
            var user = await Manager.UserManager.Users
                .FirstOrDefaultAsync(x => x.Id == id);
            if (user == null)
                return NotFound();
            return View(user);
        }

        // POST: User/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to, for 
        // more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Manage([Bind("Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,SecurityStamp,ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnd,LockoutEnabled,AccessFailedCount")] AspNetUsers aspNetUsers)
        {
            if (ModelState.IsValid)
            {
                Manager.Context.Update(aspNetUsers);
                await Manager.Context.SaveChangesAsync();
            }
            return View(aspNetUsers);
        }
        [HttpGet]
        public async Task<IActionResult> UpdateRole(string id, [FromQuery] IEnumerable<string> roles)
        {
            if (id == null)
                return NotFound();
            var user = await Manager.UserManager.Users
                .FirstOrDefaultAsync(x => x.Id == id);
            if (user == null)
                return NotFound();
            foreach (var role in await Manager.RoleManager.Roles.ToListAsync())
            {
                if (roles.Contains(role.Id))
                {
                    if (!await Manager.UserManager.IsInRoleAsync(user, role.Name))
                        await Manager.UserManager.AddToRoleAsync(user, role.Name);
                }
                else
                {
                    if (await Manager.UserManager.IsInRoleAsync(user, role.Name))
                        await Manager.UserManager.RemoveFromRoleAsync(user, role.Name);
                }
            }
            return Ok();
        }

        public async Task<IActionResult> CreateRoles()
        {
            var r = await Manager.RoleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole()
            {
                Name = Roles.Admin
            });
            await Manager.RoleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole()
            {
                Name = Roles.Contributor
            });
            await Manager.RoleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole()
            {
                Name = Roles.Reader
            });
            return Ok();
        }

        // GET: User/Delete/5
        public async Task<IActionResult> Delete(string id)
        {
            if (id == null)
                return NotFound();
            var aspNetUser = await Manager.Context.AspNetUsers
                .FirstOrDefaultAsync(m => m.Id == id);
            if (aspNetUser == null)
                return NotFound();
            return View(aspNetUser);
        }

        // POST: User/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var aspNetUser = await Manager.Context.AspNetUsers.FindAsync(id);
            Manager.Context.AspNetUsers.Remove(aspNetUser);
            await Manager.Context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
    }
}
