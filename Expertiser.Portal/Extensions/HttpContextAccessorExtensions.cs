using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser.Portal
{
    public static class HttpContextAccessorExtensions
    {
        public static string CompleteUri(this IHttpContextAccessor httpContextAccessor)
            => $"{httpContextAccessor.HttpContext.Request.Scheme}://{httpContextAccessor.HttpContext.Request.Host}{httpContextAccessor.HttpContext.Request.Path}{httpContextAccessor.HttpContext.Request.QueryString}";
    }
}
