using System.Security.Claims;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Documents;

namespace InterviewCopilot.Api.Endpoints;

public sealed record JobPostingLinkRequest(string Url);

internal static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions/{sessionId:guid}/documents").WithTags("Documents").RequireAuthorization();

        // Bearer-token API (no cookies), so antiforgery tokens add nothing here.
        group.MapPost("/", async (Guid sessionId, IFormFile file, ClaimsPrincipal user, DocumentService documents, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var document = await documents.AttachAsync(user.UserId(), sessionId, file.FileName, stream, file.Length, ct);
            return TypedResults.Created($"/api/sessions/{sessionId}/documents/{document.Id}", document);
        })
        .DisableAntiforgery();

        // Fetches the page and returns the job description for the user to edit. Nothing is stored here: the UI
        // saves the (edited) text through the upload endpoint above.
        group.MapPost("/from-link/preview", async (
            Guid sessionId,
            JobPostingLinkRequest request,
            ClaimsPrincipal user,
            JobPostingImportService import,
            CancellationToken ct) =>
            TypedResults.Ok(await import.PreviewAsync(user.UserId(), sessionId, request.Url, ct)));

        group.MapDelete("/{documentId:guid}", async (
            Guid sessionId,
            Guid documentId,
            ClaimsPrincipal user,
            DocumentService documents,
            CancellationToken ct) =>
        {
            await documents.RemoveAsync(user.UserId(), sessionId, documentId, ct);
            return TypedResults.NoContent();
        });
    }
}
