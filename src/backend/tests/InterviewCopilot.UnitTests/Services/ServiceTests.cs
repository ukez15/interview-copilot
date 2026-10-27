using System.Text;
using FluentAssertions;
using InterviewCopilot.Application.Auth;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Application.Transcripts;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;
using InterviewCopilot.Infrastructure.Documents;

namespace InterviewCopilot.UnitTests.Services;

public class ServiceTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private readonly InMemoryStore _store = new();
    private readonly FixedClock _clock = new();
    private readonly SessionService _sessions;

    public ServiceTests() => _sessions = new SessionService(_store, _store, _clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<SessionDto> CreateAsync() => _sessions.CreateAsync(
        new CreateSessionCommand(Owner, "Acme", SessionMode.LiveInterview, AnswerLength.Long, AnswerFramework.Star, QuestionType.Coding, "Be concise"),
        Ct);

    [Fact]
    public async Task Should_DeleteOwnSession_When_ItIsNotLive()
    {
        var created = await CreateAsync();

        await _sessions.DeleteAsync(Owner, created.Id, Ct);

        _store.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_RefuseToDeleteAnotherUsersSession_AsNotFound()
    {
        var created = await CreateAsync();

        var act = () => _sessions.DeleteAsync(Guid.NewGuid(), created.Id, Ct);

        await act.Should().ThrowAsync<NotFoundException>();
        _store.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task Should_RefuseToDeleteALiveSession()
    {
        var created = await CreateAsync();
        await _sessions.StartAsync(Owner, created.Id, Ct);

        var act = () => _sessions.DeleteAsync(Owner, created.Id, Ct);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage(SessionService.EndBeforeDeletingMessage);
        _store.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task Should_CreateListUpdateStartAndEndSessions()
    {
        var created = await CreateAsync();
        var listed = await _sessions.ListAsync(Owner, Ct);
        var updated = await _sessions.UpdateSettingsAsync(Owner, created.Id,
            new UpdateSessionSettingsCommand(AnswerLength.Short, AnswerFramework.None, null, null), Ct);
        var started = await _sessions.StartAsync(Owner, created.Id, Ct);
        var ended = await _sessions.EndAsync(Owner, created.Id, Ct);

        created.PinnedQuestionType.Should().Be(QuestionType.Coding);
        listed.Should().ContainSingle();
        updated.AnswerLength.Should().Be(AnswerLength.Short);
        started.Status.Should().Be(SessionStatus.Live);
        ended.Status.Should().Be(SessionStatus.Ended);
        (await _sessions.GetAsync(Owner, created.Id, Ct)).Title.Should().Be("Acme");
    }

    [Fact]
    public async Task Should_CopySettingsAndAllDocuments_When_CopyFromSessionIdIsSet()
    {
        var source = await CreateAsync();
        await _sessions.SetAutoAnswerAsync(Owner, source.Id, false, Ct);
        var stored = _store.Sessions.Single(s => s.Id == source.Id);
        for (var i = 0; i < 10; i++)
        {
            stored.AttachDocument($"doc{i}.txt", $"text {i}", _clock.GetUtcNow());
        }

        var copy = await _sessions.CreateAsync(
            new CreateSessionCommand(Owner, "Zoom meeting", SessionMode.Conversation, AnswerLength.Short, AnswerFramework.None, null, "ignored", source.Id),
            Ct);

        copy.Id.Should().NotBe(source.Id);
        copy.Title.Should().Be("Zoom meeting");
        copy.Status.Should().Be(SessionStatus.Draft);
        copy.Mode.Should().Be(SessionMode.LiveInterview);
        copy.AnswerLength.Should().Be(AnswerLength.Long);
        copy.Framework.Should().Be(AnswerFramework.Star);
        copy.PinnedQuestionType.Should().Be(QuestionType.Coding);
        copy.SessionPrompt.Should().Be("Be concise");
        copy.AutoAnswer.Should().BeFalse();
        copy.Documents.Should().HaveCount(10);
        copy.Documents.Select(d => d.FileName).Should().BeEquivalentTo(Enumerable.Range(0, 10).Select(i => $"doc{i}.txt"));
        var copied = _store.Sessions.Single(s => s.Id == copy.Id);
        copied.Documents.Select(d => d.Content).Should().BeEquivalentTo(stored.Documents.Select(d => d.Content));
        copied.Documents.Select(d => d.Id).Should().NotIntersectWith(stored.Documents.Select(d => d.Id));
    }

    [Fact]
    public async Task Should_NotFind_When_CopySourceBelongsToSomeoneElse()
    {
        var source = await CreateAsync();

        var act = () => _sessions.CreateAsync(
            new CreateSessionCommand(Guid.NewGuid(), "Zoom meeting", SessionMode.LiveInterview, AnswerLength.Short, AnswerFramework.None, null, null, source.Id),
            Ct);

        await act.Should().ThrowAsync<NotFoundException>();
        _store.Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task Should_HideOtherUsersSessions()
    {
        var created = await CreateAsync();

        var act = () => _sessions.GetAsync(Guid.NewGuid(), created.Id, Ct);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Should_AttachAndRemoveDocuments()
    {
        var created = await CreateAsync();
        var documents = new DocumentService(_sessions, [new PlainTextExtractor()], _store, _clock);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Ten years of C#."));

        var attached = await documents.AttachAsync(Owner, created.Id, "resume.txt", content, content.Length, Ct);
        await documents.RemoveAsync(Owner, created.Id, attached.Id, Ct);

        attached.CharacterCount.Should().Be(16);
        _store.Sessions[0].Documents.Should().BeEmpty();
    }

    [Theory]
    [InlineData("resume.exe", 10)]
    [InlineData("resume.txt", DocumentService.MaxFileSizeBytes + 1)]
    public async Task Should_RejectUnsupportedOrHugeDocuments(string fileName, long length)
    {
        var created = await CreateAsync();
        var documents = new DocumentService(_sessions, [new PlainTextExtractor()], _store, _clock);

        var act = () => documents.AttachAsync(Owner, created.Id, fileName, Stream.Null, length, Ct);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Should_RegisterAndSignIn()
    {
        var accounts = new AccountService(_store, new FakePasswordHasher(), _store, _clock);

        var registered = await accounts.RegisterAsync("alice", "correct horse", Ct);
        var signedIn = await accounts.SignInAsync("ALICE", "correct horse", Ct);
        var wrong = await accounts.SignInAsync("alice", "wrong password", Ct);
        var unknown = await accounts.SignInAsync("bob", "whatever1", Ct);
        var blank = await accounts.SignInAsync(" ", "", Ct);

        signedIn!.Id.Should().Be(registered.Id);
        wrong.Should().BeNull();
        unknown.Should().BeNull();
        blank.Should().BeNull();
        _store.Accounts[0].PasswordHash.Should().NotBe("correct horse");
    }

    [Fact]
    public async Task Should_RejectShortPasswordAndDuplicateUsername()
    {
        var accounts = new AccountService(_store, new FakePasswordHasher(), _store, _clock);
        await accounts.RegisterAsync("alice", "correct horse", Ct);

        await ((Func<Task>)(() => accounts.RegisterAsync("bob", "short", Ct))).Should().ThrowAsync<DomainException>();
        await ((Func<Task>)(() => accounts.RegisterAsync("Alice", "another password", Ct))).Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Should_BuildTranscript_And_ExportPdf()
    {
        var created = await CreateAsync();
        _store.AddSegment(TranscriptSegment.Create(created.Id, Speaker.Candidate, 2000, 3000, "Second"));
        _store.AddSegment(TranscriptSegment.Create(created.Id, Speaker.Interviewer, 0, 1000, "First?"));
        _store.AddAnswer(SuggestedAnswer.Create(created.Id, "First?", QuestionType.Experience, "Answer", 1, AnswerModifier.None, _clock.GetUtcNow()));
        _store.Add(PracticeAttempt.Create(created.Id, "Why?", "Because", 7, "Good", _clock.GetUtcNow()));
        var exporter = new CapturingPdfExporter();
        var transcripts = new TranscriptService(_sessions, _store, _store, exporter);

        var transcript = await transcripts.GetAsync(Owner, created.Id, Ct);
        var (fileName, content) = await transcripts.ExportPdfAsync(Owner, created.Id, Ct);

        transcript.Entries.Select(e => e.Text).Should().Equal("First?", "Second");
        transcript.Answers.Should().ContainSingle();
        transcript.Attempts.Should().ContainSingle().Which.Score.Should().Be(7);
        fileName.Should().Be("Acme-2026-10-15.pdf");
        content.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Should_FallBackToGenericFileName_When_TitleHasNoLetters()
    {
        var dto = new TranscriptDto(Guid.NewGuid(), "!!!", SessionMode.Conversation, FixedClock.Default, [], [], []);

        TranscriptService.FileNameFor(dto).Should().Be("transcript-2026-10-15.pdf");
    }

    private sealed class CapturingPdfExporter : IPdfExporter
    {
        public byte[] Export(TranscriptDto transcript) => [1, 2, 3];
    }
}
