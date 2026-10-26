using System.Globalization;
using InterviewCopilot.Application.Transcripts;
using InterviewCopilot.Domain.Enums;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace InterviewCopilot.Infrastructure.Pdf;

/// <summary>Transcript + suggested answers as PDF (FR-14). QuestPDF Community licence (free for this use).</summary>
internal sealed class QuestPdfTranscriptExporter : IPdfExporter
{
    static QuestPdfTranscriptExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Export(TranscriptDto transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(style => style.FontSize(10));

            page.Header().Column(header =>
            {
                header.Item().Text(transcript.Title).FontSize(18).SemiBold();
                header.Item().Text(string.Create(CultureInfo.InvariantCulture,
                    $"{transcript.Mode} · {transcript.CreatedAt:yyyy-MM-dd HH:mm} UTC")).FontColor(Colors.Grey.Darken1);
            });

            page.Content().PaddingVertical(12).Column(column =>
            {
                column.Spacing(6);
                column.Item().Text("Transcript").FontSize(13).SemiBold();
                if (transcript.Entries.Count == 0)
                {
                    column.Item().Text("No transcript was recorded.").Italic();
                }

                foreach (var entry in transcript.Entries)
                {
                    column.Item().Text(text =>
                    {
                        text.Span(string.Create(CultureInfo.InvariantCulture, $"[{FormatTime(entry.AtMs)}] ")).FontColor(Colors.Grey.Medium);
                        text.Span(entry.Speaker == Speaker.Interviewer ? "Them: " : "Me: ").SemiBold();
                        text.Span(entry.Text);
                    });
                }

                column.Item().PaddingTop(12).Text("Suggested answers").FontSize(13).SemiBold();
                foreach (var answer in transcript.Answers)
                {
                    column.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(block =>
                    {
                        block.Item().Text(answer.Question).SemiBold();
                        block.Item().Text(string.Create(CultureInfo.InvariantCulture, $"{answer.QuestionType} · version {answer.Version}"))
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                        block.Item().Text(answer.Text);
                    });
                }

                if (transcript.Attempts.Count > 0)
                {
                    column.Item().PaddingTop(12).Text("Practice").FontSize(13).SemiBold();
                    foreach (var attempt in transcript.Attempts)
                    {
                        column.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(block =>
                        {
                            block.Item().Text(attempt.Question).SemiBold();
                            block.Item().Text(string.Create(CultureInfo.InvariantCulture, $"Score {attempt.Score}/10"))
                                .FontSize(8).FontColor(Colors.Grey.Darken1);
                            block.Item().Text(text =>
                            {
                                text.Span("Answer: ").SemiBold();
                                text.Span(attempt.AnswerText);
                            });
                            block.Item().Text(text =>
                            {
                                text.Span("Feedback: ").SemiBold();
                                text.Span(attempt.Feedback);
                            });
                        });
                    }
                }
            });

            page.Footer().AlignCenter().Text(text =>
            {
                text.Span("InterviewCopilot · page ");
                text.CurrentPageNumber();
            });
        })).GeneratePdf();
    }

    private static string FormatTime(long ms) =>
        TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
