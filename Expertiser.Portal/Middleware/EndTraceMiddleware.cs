using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser.Portal.Middleware
{
    public class EndTraceMiddleware : IMiddleware
    {
        private readonly Manager Manager;
        public EndTraceMiddleware(Manager manager) => Manager = manager;
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            await next.Invoke(context);
            await Manager.TelemetryManager.EndTelemetryAsync();
        }
    }
}
