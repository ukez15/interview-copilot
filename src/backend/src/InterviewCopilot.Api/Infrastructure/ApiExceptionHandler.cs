using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InterviewCopilot.Api.Infrastructure;

/// <summary>Maps domain/application exceptions to ProblemDetails. Messages are user-facing and contain no secrets.</summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            DomainException => (StatusCodes.Status400BadRequest, "The request breaks a rule"),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            LlmKeyRejectedException => (StatusCodes.Status401Unauthorized, "The provider rejected the API key"),
            LlmUnavailableException or TranscriptionUnavailableException =>
                (StatusCodes.Status503ServiceUnavailable, "A provider is unavailable"),
            _ => (0, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = exception.Message },
        });
    }
}
