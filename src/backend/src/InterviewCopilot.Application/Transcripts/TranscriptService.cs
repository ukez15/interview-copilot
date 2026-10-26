using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.Application.Transcripts;

public sealed record TranscriptEntryDto(long AtMs, Speaker Speaker, string Text);

public sealed record AnswerEntryDto(DateTimeOffset CreatedAt, string Question, QuestionType QuestionType, int Version, string Text);

public sealed record TranscriptDto(
    Guid SessionId,
    string Title,
    SessionMode Mode,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TranscriptEntryDto> Entries,
    IReadOnlyList<AnswerEntryDto> Answers,
    IReadOnlyList<PracticeAttemptDto> Attempts);

/// <summary>Renders a transcript, including suggested answers, as a PDF (FR-14).</summary>
public interface IPdfExporter
{
    byte[] Export(TranscriptDto transcript);
}

/// <summary>Saved transcripts and export (FR-13, FR-14).</summary>
public sealed class TranscriptService(
    SessionService sessions,
    ITranscriptRepository transcripts,
    IPracticeAttemptRepository practice,
    IPdfExporter pdf)
{
    public async Task<TranscriptDto> GetAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        var segments = await transcripts.GetSegmentsAsync(sessionId, cancellationToken);
        var answers = await transcripts.GetAnswersAsync(sessionId, cancellationToken);
        var attempts = await practice.ListAsync(sessionId, cancellationToken);
        return ToDto(session, segments, answers, attempts);
    }

    public async Task<(string FileName, byte[] Content)> ExportPdfAsync(
        Guid ownerId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var transcript = await GetAsync(ownerId, sessionId, cancellationToken);
        return (FileNameFor(transcript), pdf.Export(transcript));
    }

    internal static string FileNameFor(TranscriptDto transcript)
    {
        var safeTitle = new string([.. transcript.Title.Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        if (string.IsNullOrEmpty(safeTitle))
        {
            safeTitle = "transcript";
        }

        return $"{safeTitle}-{transcript.CreatedAt:yyyy-MM-dd}.pdf";
    }

    private static TranscriptDto ToDto(
        Session session,
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SuggestedAnswer> answers,
        IReadOnlyList<PracticeAttempt> attempts) =>
        new(
            session.Id,
            session.Title,
            session.Mode,
            session.CreatedAt,
            [.. segments.OrderBy(s => s.StartMs).Select(s => new TranscriptEntryDto(s.StartMs, s.Speaker, s.Text))],
            [.. answers.OrderBy(a => a.CreatedAt).Select(a => new AnswerEntryDto(a.CreatedAt, a.Question, a.QuestionType, a.Version, a.Text))],
            [.. attempts.OrderBy(a => a.CreatedAt).Select(PracticeAttemptDto.From)]);
}
