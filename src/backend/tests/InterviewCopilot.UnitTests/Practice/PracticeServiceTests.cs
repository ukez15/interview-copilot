using System.Runtime.CompilerServices;
using FluentAssertions;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;
using InterviewCopilot.UnitTests.Audio;
using InterviewCopilot.UnitTests.Services;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Practice;

public class PracticeServiceTests
{
    private const string GoodReply = "SCORE: 8\nFEEDBACK: Clear structure.";

    private static readonly Guid Owner = Guid.NewGuid();
    private readonly InMemoryStore _store = new();
    private readonly InMemoryPreferences _prefs = new();
    private readonly PracticeAnswerCollector _collector = new();
    private readonly QueueLlm _llm = new();
    private readonly FixedStreamClock _audioClock = new();
    private readonly PracticeService _service;
    private readonly Session _session;

    public PracticeServiceTests()
    {
        _session = NewSession(SessionMode.MockInterview, start: true);
        var sessions = new SessionService(_store, _store, new FixedClock());
        _service = new PracticeService(
            sessions,
            _store,
            _store,
            _prefs,
            new LlmProviderResolver([_llm]),
            Options.Create(new LlmOptions { Model = "default-model", Temperature = 0.3 }),
            _collector,
            new FixedClock(),
            _audioClock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Session NewSession(SessionMode mode, bool start)
    {
        var session = Session.Create(Owner, "Mock", mode, FixedClock.Default);
        if (start)
        {
            session.Start(FixedClock.Default);
        }

        _store.Add(session);
        return session;
    }

    private async Task AnswerAsync(Guid sessionId, string text) =>
        await _collector.OnSegmentAsync(TranscriptSegment.Create(sessionId, Speaker.Candidate, 0, 1, text), Ct);

    [Fact]
    public async Task Should_AskQuestion_UsingDefaultModel_AndOpenIt()
    {
        _llm.Replies.Enqueue(["  Tell me ", "about yourself.\n"]);

        var next = await _service.NextAsync(Owner, _session.Id, Ct);

        next.Question.Should().Be("Tell me about yourself.");
        _llm.Requests.Single().Model.Should().Be("default-model");
        _collector.Take(_session.Id)!.Question.Should().Be("Tell me about yourself.");
    }

    [Fact]
    public async Task Should_StartTheAnswerWhereTheMicStreamIs_When_TheQuestionIsShown()
    {
        _audioClock.Position = 42_000;
        _llm.Replies.Enqueue(["Why us?"]);

        var next = await _service.NextAsync(Owner, _session.Id, Ct);
        await _collector.OnSegmentAsync(TranscriptSegment.Create(_session.Id, Speaker.Candidate, 30_000, 31_000, "said while it was thinking"), Ct);
        await _collector.OnSegmentAsync(TranscriptSegment.Create(_session.Id, Speaker.Candidate, 43_000, 44_000, "my answer"), Ct);

        next.AnswerFromMs.Should().Be(42_000);
        _collector.Take(_session.Id).Should().Be(new CollectedAnswer("Why us?", "my answer", 42_000));
    }

    [Theory]
    [InlineData(null, "owner-model")]
    [InlineData("ollama", "owner-model")]
    [InlineData("anthropic", "default-model")]
    public async Task Should_UseOwnerModel_OnlyWhenOwnerProviderIsOllama(string? provider, string expectedModel)
    {
        var prefs = UserPreferences.Create(Owner, FixedClock.Default);
        prefs.SetLlm(provider, "owner-model", FixedClock.Default);
        _prefs.Add(prefs);
        _llm.Replies.Enqueue(["Q?"]);

        await _service.NextAsync(Owner, _session.Id, Ct);

        _llm.Requests.Single().Model.Should().Be(expectedModel);
    }

    [Fact]
    public async Task Should_ExcludeAlreadyAskedQuestions_FromThePrompt()
    {
        _store.Add(PracticeAttempt.Create(_session.Id, "Old question?", "a", 5, "f", FixedClock.Default));
        _llm.Replies.Enqueue(["New?"]);

        await _service.NextAsync(Owner, _session.Id, Ct);

        _llm.Requests.Single().Messages[1].Content.Should().Contain("- Old question?");
    }

    [Fact]
    public async Task Should_ReplaceOpenQuestion_When_NextIsCalledAgain()
    {
        _llm.Replies.Enqueue(["First?"]);
        _llm.Replies.Enqueue(["Second?"]);
        await _service.NextAsync(Owner, _session.Id, Ct);
        await AnswerAsync(_session.Id, "stale words");

        await _service.NextAsync(Owner, _session.Id, Ct);

        _collector.Take(_session.Id).Should().Be(new CollectedAnswer("Second?", string.Empty));
    }

    [Fact]
    public async Task Should_ScoreAndSaveAttempt_When_Finished()
    {
        _llm.Replies.Enqueue(["Why us?"]);
        _llm.Replies.Enqueue(["SCORE: 8\n", "FEEDBACK: Clear structure."]);
        await _service.NextAsync(Owner, _session.Id, Ct);
        await AnswerAsync(_session.Id, "Because of the mission.");
        await AnswerAsync(_session.Id, "And the team.");

        var attempt = await _service.FinishAsync(Owner, _session.Id, Ct);

        attempt.Score.Should().Be(8);
        attempt.Feedback.Should().Be("Clear structure.");
        attempt.Question.Should().Be("Why us?");
        attempt.AnswerText.Should().Be("Because of the mission. And the team.");
        _store.Attempts.Should().ContainSingle();
        _store.SaveCount.Should().Be(1);
        _collector.Take(_session.Id).Should().BeNull();
        (await _service.ListAsync(Owner, _session.Id, Ct)).Should().ContainSingle().Which.Id.Should().Be(attempt.Id);
    }

    [Fact]
    public async Task Should_RetryOnce_When_ReplyIsUnparseable()
    {
        _llm.Replies.Enqueue(["Q?"]);
        _llm.Replies.Enqueue(["I think it was good."]);
        _llm.Replies.Enqueue([GoodReply]);
        await _service.NextAsync(Owner, _session.Id, Ct);
        await AnswerAsync(_session.Id, "words");

        var attempt = await _service.FinishAsync(Owner, _session.Id, Ct);

        attempt.Score.Should().Be(8);
        _llm.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Should_Fail_AndKeepAnswer_When_ReplyIsUnparseableTwice()
    {
        _llm.Replies.Enqueue(["Q?"]);
        _llm.Replies.Enqueue(["nope"]);
        _llm.Replies.Enqueue(["still nope"]);
        _llm.Replies.Enqueue([GoodReply]);
        await _service.NextAsync(Owner, _session.Id, Ct);
        await AnswerAsync(_session.Id, "words");

        var act = () => _service.FinishAsync(Owner, _session.Id, Ct);

        (await act.Should().ThrowAsync<LlmUnavailableException>())
            .WithMessage("The model's feedback couldn't be read. Try again.");
        _store.Attempts.Should().BeEmpty();
        (await _service.FinishAsync(Owner, _session.Id, Ct)).AnswerText.Should().Be("words"); // kept for a retry
    }

    [Fact]
    public async Task Should_Throw_AndSaveNothing_When_AnswerIsEmpty()
    {
        _llm.Replies.Enqueue(["Q?"]);
        await _service.NextAsync(Owner, _session.Id, Ct);

        var act = () => _service.FinishAsync(Owner, _session.Id, Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("No answer heard.");
        _store.Attempts.Should().BeEmpty();
        _collector.Take(_session.Id)!.Question.Should().Be("Q?"); // still open
    }

    [Fact]
    public async Task Should_Throw_When_NoQuestionIsOpen()
    {
        var act = () => _service.FinishAsync(Owner, _session.Id, Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("No answer heard.");
    }

    [Fact]
    public async Task Should_Fail_When_ModelReturnsNoQuestion()
    {
        _llm.Replies.Enqueue(["   "]);

        var act = () => _service.NextAsync(Owner, _session.Id, Ct);

        await act.Should().ThrowAsync<LlmUnavailableException>();
        _collector.Take(_session.Id).Should().BeNull();
    }

    [Fact]
    public async Task Should_KeepSessionsSeparate()
    {
        var other = NewSession(SessionMode.MockInterview, start: true);
        _llm.Replies.Enqueue(["A?"]);
        _llm.Replies.Enqueue(["B?"]);
        _llm.Replies.Enqueue([GoodReply]);
        await _service.NextAsync(Owner, _session.Id, Ct);
        await _service.NextAsync(Owner, other.Id, Ct);
        await AnswerAsync(other.Id, "only B");

        var attempt = await _service.FinishAsync(Owner, other.Id, Ct);

        attempt.Question.Should().Be("B?");
        (await _service.ListAsync(Owner, _session.Id, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Should_RejectOtherModes_Strangers_AndNonLiveSessions()
    {
        var live = NewSession(SessionMode.LiveInterview, start: true);
        var notStarted = NewSession(SessionMode.MockInterview, start: false);

        await ((Func<Task>)(() => _service.NextAsync(Owner, live.Id, Ct))).Should().ThrowAsync<DomainException>();
        await ((Func<Task>)(() => _service.ListAsync(Owner, live.Id, Ct))).Should().ThrowAsync<DomainException>();
        await ((Func<Task>)(() => _service.NextAsync(Owner, notStarted.Id, Ct))).Should().ThrowAsync<ConflictException>();
        await ((Func<Task>)(() => _service.FinishAsync(Owner, notStarted.Id, Ct))).Should().ThrowAsync<ConflictException>();
        await ((Func<Task>)(() => _service.NextAsync(Guid.NewGuid(), _session.Id, Ct))).Should().ThrowAsync<NotFoundException>();
    }

    private sealed class QueueLlm : ILlmProvider
    {
        public Queue<string[]> Replies { get; } = new();

        public List<LlmRequest> Requests { get; } = [];

        public string Name => ProviderNames.Ollama;

        public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var token in Replies.Dequeue())
            {
                await Task.Yield();
                yield return token;
            }
        }
    }
}
