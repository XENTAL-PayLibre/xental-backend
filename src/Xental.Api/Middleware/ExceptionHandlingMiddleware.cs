using Microsoft.AspNetCore.Mvc;
using Xental.Application.Common.Exceptions;

namespace Xental.Api.Middleware;

/// <summary>Maps application exceptions to RFC7807 ProblemDetails responses.</summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
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
                ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
                NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
                NombaIntegrationException => (StatusCodes.Status502BadGateway, "Upstream provider error"),
                _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
            };

            if (status >= 500)
                logger.LogError(ex, "Unhandled exception");
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
