using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Expertiser.Database.Models;

namespace Expertiser.Portal.Controllers
{
    public class ExternalAccountController : Controller
    {
        private readonly ContextExpertiser _context;

        public ExternalAccountController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: ExternalAccount
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableExternalAccount.Include(t => t.Member);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: ExternalAccount/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableExternalAccount = await _context.TableExternalAccount
                .Include(t => t.Member)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableExternalAccount == null)
            {
                return NotFound();
            }

            return View(tableExternalAccount);
        }

        // GET: ExternalAccount/Create
        public IActionResult Create()
        {
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name");
            return View();
        }

        // POST: ExternalAccount/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Type,Username,Passowrd,MemberId")] TableExternalAccount tableExternalAccount)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableExternalAccount);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExternalAccount.MemberId);
            return View(tableExternalAccount);
        }

        // GET: ExternalAccount/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableExternalAccount = await _context.TableExternalAccount.FindAsync(id);
            if (tableExternalAccount == null)
            {
                return NotFound();
            }
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExternalAccount.MemberId);
            return View(tableExternalAccount);
        }

        // POST: ExternalAccount/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Type,Username,Passowrd,MemberId")] TableExternalAccount tableExternalAccount)
        {
            if (id != tableExternalAccount.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableExternalAccount);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableExternalAccountExists(tableExternalAccount.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            ViewData["MemberId"] = new SelectList(_context.TableMember, "Id", "Name", tableExternalAccount.MemberId);
            return View(tableExternalAccount);
        }

        // GET: ExternalAccount/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableExternalAccount = await _context.TableExternalAccount
                .Include(t => t.Member)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableExternalAccount == null)
            {
                return NotFound();
            }

            return View(tableExternalAccount);
        }

        // POST: ExternalAccount/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableExternalAccount = await _context.TableExternalAccount.FindAsync(id);
            _context.TableExternalAccount.Remove(tableExternalAccount);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableExternalAccountExists(int id)
        {
            return _context.TableExternalAccount.Any(e => e.Id == id);
        }
    }
}
