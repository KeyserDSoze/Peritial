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
    public class MessageController : Controller
    {
        private readonly ContextExpertiser _context;

        public MessageController(ContextExpertiser context)
        {
            _context = context;
        }

        // GET: TableMessage
        public async Task<IActionResult> Index()
        {
            var contextExpertiser = _context.TableMessages.Include(t => t.Expertise);
            return View(await contextExpertiser.ToListAsync());
        }

        // GET: TableMessage/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableMessages = await _context.TableMessages
                .Include(t => t.Expertise)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableMessages == null)
            {
                return NotFound();
            }

            return View(tableMessages);
        }

        // GET: TableMessage/Create
        public IActionResult Create()
        {
            ViewData["ExpertiseId"] = new SelectList(_context.TableExpertise, "Id", "AccidentId");
            return View();
        }

        // POST: TableMessage/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,ExpertiseId,Value,Type")] TableMessages tableMessages)
        {
            if (ModelState.IsValid)
            {
                _context.Add(tableMessages);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["ExpertiseId"] = new SelectList(_context.TableExpertise, "Id", "AccidentId", tableMessages.ExpertiseId);
            return View(tableMessages);
        }

        // GET: TableMessage/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableMessages = await _context.TableMessages.FindAsync(id);
            if (tableMessages == null)
            {
                return NotFound();
            }
            ViewData["ExpertiseId"] = new SelectList(_context.TableExpertise, "Id", "AccidentId", tableMessages.ExpertiseId);
            return View(tableMessages);
        }

        // POST: TableMessage/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,ExpertiseId,Value,Type")] TableMessages tableMessages)
        {
            if (id != tableMessages.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(tableMessages);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TableMessagesExists(tableMessages.Id))
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
            ViewData["ExpertiseId"] = new SelectList(_context.TableExpertise, "Id", "AccidentId", tableMessages.ExpertiseId);
            return View(tableMessages);
        }

        // GET: TableMessage/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tableMessages = await _context.TableMessages
                .Include(t => t.Expertise)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (tableMessages == null)
            {
                return NotFound();
            }

            return View(tableMessages);
        }

        // POST: TableMessage/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tableMessages = await _context.TableMessages.FindAsync(id);
            _context.TableMessages.Remove(tableMessages);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool TableMessagesExists(int id)
        {
            return _context.TableMessages.Any(e => e.Id == id);
        }
    }
}
