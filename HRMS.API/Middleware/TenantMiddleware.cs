using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Common;
using System.Security.Claims;

namespace HRMS.API.Middleware
{
    public class TenantMiddleware
    {
        private readonly RequestDelegate _next;

        public TenantMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context,ITenantResolver tenantResolver, ITenantContext tenantContext)
        {
            // Swagger and other non-API routes do not require tenant resolution
            if(!context.Request.Path.StartsWithSegments("/api"))
            {
                await _next(context);
                return;
            }

            var host = context.Request.Host.Host;

            if(string.IsNullOrWhiteSpace(host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { message = "Tenant Could not be resolved." });

                return;
            }

            var resolved = await tenantResolver.ResolveAsync(host, context.RequestAborted);

            if(!resolved)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { message = "Tenant not found or inactive." });

                return;
            }

            if (context.User.Identity?.IsAuthenticated == true)
            {
                var companyIdClaim = context.User.FindFirstValue("companyId");

                if(!Guid.TryParse(companyIdClaim, out var tokenCompanyId))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    await context.Response.WriteAsJsonAsync(new { message = "Invalid tenant information in token." });

                    return;
                }

                if(tokenCompanyId != tenantContext.CompanyId)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;

                    await context.Response.WriteAsJsonAsync(new { message = "Tenant access denied." });

                    return;
                }
            }

            await _next(context);
        }

    }
}
