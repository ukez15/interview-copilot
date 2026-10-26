using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.IntegrationTests;

/// <summary>DELETE /api/sessions/{id} (#80): removes the session and, through SQLite cascades, everything in it.</summary>
public sealed class SessionDeleteApiTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ApiFactory _factory;

    public SessionDeleteApiTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SessionDto> CreateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/sessions", new
        {
            Title = "Old onsite",
            Mode = "MockInterview",
            AnswerLength = "Short",
            Framework = "None",
            PinnedQuestionType = (string?)null,
            SessionPrompt = "",
        }, Ct);
        return (await response.Content.ReadFromJsonAsync<SessionDto>(Json, Ct))!;
    }

    private async Task<(int Documents, int Segments, int Answers, int Attempts, bool Session)> CountAsync(Guid sessionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewCopilotDbContext>();
        return (
            await db.SessionDocuments.CountAsync(d => d.SessionId == sessionId, Ct),
            await db.TranscriptSegments.CountAsync(t => t.SessionId == sessionId, Ct),
            await db.SuggestedAnswers.CountAsync(a => a.SessionId == sessionId, Ct),
            await db.PracticeAttempts.CountAsync(a => a.SessionId == sessionId, Ct),
            await db.Sessions.AnyAsync(s => s.Id == sessionId, Ct));
    }

    [Fact]
    public async Task Should_DeleteTheSessionAndEverythingInIt()
    {
        var client = await _factory.CreateSignedInClientAsync("deleter");
        var session = await CreateAsync(client);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Led a team of five at Acme."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "resume.txt");
        (await client.PostAsync($"/api/sessions/{session.Id}/documents", form, Ct)).EnsureSuccessStatusCode();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InterviewCopilotDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.TranscriptSegments.Add(TranscriptSegment.Create(session.Id, Speaker.Candidate, 0, 1_000, "my answer"));
            db.SuggestedAnswers.Add(SuggestedAnswer.Create(session.Id, "Why us?", QuestionType.Behavioral, "Because.", 1, AnswerModifier.None, now));
            db.PracticeAttempts.Add(PracticeAttempt.Create(session.Id, "Why us?", "Because.", 6, "Add numbers.", now));
            await db.SaveChangesAsync(Ct);
        }

        (await CountAsync(session.Id)).Should().Be((1, 1, 1, 1, true));

        var deleted = await client.DeleteAsync($"/api/sessions/{session.Id}", Ct);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/sessions/{session.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountAsync(session.Id)).Should().Be((0, 0, 0, 0, false));
    }

    [Fact]
    public async Task Should_Return409AndKeepEverything_When_TheSessionIsLive()
    {
        var client = await _factory.CreateSignedInClientAsync("live-deleter");
        var session = await CreateAsync(client);
        (await client.PostAsync($"/api/sessions/{session.Id}/start", null, Ct)).EnsureSuccessStatusCode();

        var deleted = await client.DeleteAsync($"/api/sessions/{session.Id}", Ct);

        deleted.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await deleted.Content.ReadAsStringAsync(Ct)).Should().Contain(SessionService.EndBeforeDeletingMessage);
        (await CountAsync(session.Id)).Session.Should().BeTrue();
    }

    [Fact]
    public async Task Should_Return404ForAnotherUsersSession_And401WhenAnonymous()
    {
        var owner = await _factory.CreateSignedInClientAsync("owner-of-session");
        var stranger = await _factory.CreateSignedInClientAsync("stranger");
        var session = await CreateAsync(owner);

        (await stranger.DeleteAsync($"/api/sessions/{session.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.CreateClient().DeleteAsync($"/api/sessions/{session.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountAsync(session.Id)).Session.Should().BeTrue();
    }
}
