using Microsoft.AspNetCore.Mvc;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;

namespace Xental.Api.Middleware;

/// <summary>Maps application exceptions to RFC7807 ProblemDetails responses.</summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IErrorAlerter alerter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var (status, title) = ex switch
            {
                ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
                AuthenticationException => (StatusCodes.Status401Unauthorized, "Authentication failed"),
                EmailNotVerifiedException => (StatusCodes.Status403Forbidden, "Email not verified"),
                OnboardingNotApprovedException => (StatusCodes.Status403Forbidden, "Onboarding not approved"),
                ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden"),
                ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
                NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
                NombaIntegrationException => (StatusCodes.Status502BadGateway, "Upstream provider error"),
                _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
            };

            if (status >= 500)
            {
                logger.LogError(ex, "Unhandled exception");
                // Fire-and-forget operational alert (throttled + best-effort); never delays the response.
                var path = context.Request.Path.ToString();
                var method = context.Request.Method;
                var traceId = context.TraceIdentifier;
                _ = Task.Run(async () =>
                {
                    try { await alerter.NotifyServerErrorAsync(ex, path, method, traceId); }
                    catch (Exception alertEx) { logger.LogWarning(alertEx, "Server-error alert failed to send."); }
                });
            }
            else
                logger.LogWarning("Request failed: {Message}", ex.Message);

            var problem = new ProblemDetails
            {
                Status = status,
                Title = title,
                // Do not leak internal details on 500s.
                Detail = status >= 500 ? null : ex.Message,
            };

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(problem);
        }
    }
}
