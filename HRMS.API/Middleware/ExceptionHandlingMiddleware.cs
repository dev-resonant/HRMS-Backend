using FluentValidation;
using HRMS.API.RateLimiting;
using HRMS.Application.Common.Exceptions;

namespace HRMS.API.Middleware
{
    public class ExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client disconnected; nothing to write.
            }
            catch (Exception ex)
            {
                if (context.Response.HasStarted)
                {
                    _logger.LogError(ex, "Unhandled exception after the response had started.");
                    throw;
                }

                await WriteErrorAsync(context, ex);
            }
        }

        private async Task WriteErrorAsync(HttpContext context, Exception ex)
        {
            context.Response.Clear();

            switch (ex)
            {
                case ValidationException validationException:
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = "Validation failed.",
                        errors = validationException.Errors
                            .GroupBy(e => e.PropertyName)
                            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())
                    });
                    break;

                case UnauthorizedAccessException:
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { message = ex.Message });
                    break;

                case ForbiddenException:
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { message = ex.Message });
                    break;

                case NotFoundException:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new { message = ex.Message });
                    break;

                case PreconditionFailedException:
                    context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    await context.Response.WriteAsJsonAsync(new { message = ex.Message });
                    break;

                case TooManyRequestsException tooMany:
                    _logger.LogWarning("A request was rejected by an attempt limit: {Method} {Path}.", context.Request.Method, context.Request.Path.Value);
                    await RateLimitingSetup.WriteTooManyRequestsAsync(context, tooMany.RetryAfter);
                    break;

                case ConflictException:
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new { message = ex.Message });
                    break;

                default:
                    _logger.LogError(ex, "Unhandled exception.");
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsJsonAsync(new { message = "An unexpected error occurred." });
                    break;
            }
        }
    }
}
