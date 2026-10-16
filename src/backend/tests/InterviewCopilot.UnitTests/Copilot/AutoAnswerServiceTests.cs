using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Copilot;

public sealed class AutoAnswerServiceTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly Recorder _events = new();
    private readonly ServiceProvider _provider;
    private readonly Session _session = Session.Create(Guid.NewGuid(), "Interview", SessionMode.LiveInterview, FixedClock.Default);
    private ScriptedLlm _llm = new();
    private AutoAnswerService _service;

    public AutoAnswerServiceTests()
    {
        _session.Start(FixedClock.Default);
        _store.Add(_session);
        _provider = Build();
        _service = Create();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _service.Dispose();
        _provider.Dispose();
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionRepository>(_store);
        services.AddSingleton<ITranscriptRepository>(_store);
        services.AddSingleton<IUnitOfWork>(_store);
        services.AddScoped<ILlmProvider>(provider => _llm);
        services.AddSingleton<ISessionEventPublisher>(_events);
        services.AddSingleton<TimeProvider>(new FixedClock());
        services.AddSingleton(Options.Create(new LlmOptions { Model = "m", Temperature = 0.2 }));
        services.AddScoped<AnswerGenerator>();
        return services.BuildServiceProvider();
    }

    private AutoAnswerService Create(TimeSpan? timeout = null) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _events, NullLogger<AutoAnswerService>.Instance, timeout);

    private TranscriptSegment Segment(Speaker speaker, string text) =>
        TranscriptSegment.Create(_session.Id, speaker, 0, 1_000, text);

    private IEnumerable<string> Types => _events.Published.Select(e => e.Type);

    [Fact]
    public async Task Should_PublishQuestionDetected_ThenStreamAnswer_When_InterviewerAsksQuestion()
    {
        var segment = Segment(Speaker.Interviewer, "Tell me about a time you led a team.");

        await _service.OnSegmentAsync(segment, Ct);
        await _service.WhenIdleAsync(_session.Id);

        Types.Should().Equal(
            SessionEventTypes.QuestionDetected,
            SessionEventTypes.AnswerDelta,
            SessionEventTypes.AnswerDelta,
            SessionEventTypes.AnswerDone);
        var detected = JsonSerializer.Serialize(_events.Published[0].Data);
        detected.Should().Contain(segment.Id.ToString()).And.Contain("Tell me about a time you led a team.");
        _store.Answers.Should().ContainSingle().Which.Question.Should().Be("Tell me about a time you led a team.");
    }

    [Fact]
    public async Task Should_Ignore_When_CandidateSpeaks()
    {
        await _service.OnSegmentAsync(Segment(Speaker.Candidate, "Tell me about a time you led a team."), Ct);
        await _service.WhenIdleAsync(_session.Id);

        _events.Published.Should().BeEmpty();
        _store.Answers.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_Ignore_When_NotAQuestion()
    {
        await _service.OnSegmentAsync(Segment(Speaker.Interviewer, "Thanks for joining us today."), Ct);
        await _service.WhenIdleAsync(_session.Id);

        _events.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_DetectButNotAnswer_When_AutoAnswerPaused()
    {
        _session.SetAutoAnswer(false);

        await _service.OnSegmentAsync(Segment(Speaker.Interviewer, "Tell me about a time you led a team."), Ct);
        await _service.WhenIdleAsync(_session.Id);

        Types.Should().Equal(SessionEventTypes.QuestionDetected);
        _store.Answers.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_DoNothing_When_SessionNotLive()
    {
        _session.End(FixedClock.Default);

        await _service.OnSegmentAsync(Segment(Speaker.Interviewer, "Tell me about a time you led a team."), Ct);
        await _service.WhenIdleAsync(_session.Id);

        _events.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_DoNothing_When_SessionUnknown()
    {
        var orphan = TranscriptSegment.Create(Guid.NewGuid(), Speaker.Interviewer, 0, 1_000, "Why do you want this job?");

        await _service.OnSegmentAsync(orphan, Ct);

        _events.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_CancelOlderAnswer_When_NewerQuestionArrives()
    {
        _llm = new ScriptedLlm { BlockFirstCall = true };
        _service.Dispose();
        _service = Create();

        await _service.OnSegmentAsync(Segment(Speaker.Interviewer, "Tell me about a time you led a team."), Ct);
        await _llm.FirstTokenSent.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await _service.OnSegmentAsync(Segment(Speaker.Interviewer, "Why do you want this job?"), Ct);
        await _service.WhenIdleAsync(_session.Id);

        _store.Answers.Should().ContainSingle().Which.Question.Should().Be("Why do you want this job?");
        Types.Count(t => t == SessionEventTypes.QuestionDetected).Should().Be(2);
        Types.Count(t => t == SessionEventTypes.AnswerDone).Should().Be(1);
        _llm.FirstCallCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Should_PublishError_When_ModelUnavailable()
    {
        _llm = new ScriptedLlm { Fail = true };

        await _service.OnSegmentAsync(Segment(Speaker.Interviewer, "Tell me about a time you led a team."), Ct);
        await _service.WhenIdleAsync(_session.Id);

        Types.Should().Equal(SessionEventTypes.QuestionDetected, SessionEventTypes.Error);
        JsonSerializer.Serialize(_events.Published[1].Data).Should().Contain("reach Ollama.");
    }

    [Fact]
    public async Task Should_StopAnswer_When_TimeoutElapses()
    {
        _llm = new ScriptedLlm { BlockFirstCall = true };
        _service.Dispose();
        _service = Create(TimeSpan.FromMilliseconds(50));

        await _service.OnSegmentAsync(Segment(Speaker.Interviewer, "Tell me about a time you led a team."), Ct);
        await _service.WhenIdleAsync(_session.Id);

        _store.Answers.Should().BeEmpty();
        Types.Should().NotContain(SessionEventTypes.AnswerDone);
        var error = _events.Published.Should().ContainSingle(e => e.Type == SessionEventTypes.Error).Subject;
        JsonSerializer.Serialize(error.Data).Should().Contain("took too long", "the card must not hang on Thinking…");
    }

    [Fact]
    public void Should_UseSixtySecondTimeout_ByDefault() =>
        AutoAnswerService.DefaultAnswerTimeout.Should().Be(TimeSpan.FromSeconds(60));

    private sealed class Recorder : ISessionEventPublisher
    {
        private readonly List<(Guid SessionId, string Type, object Data)> _published = [];

        public IReadOnlyList<(Guid SessionId, string Type, object Data)> Published
        {
            get
            {
                lock (_published)
                {
                    return [.. _published];
                }
            }
        }

        public void Publish(Guid sessionId, string type, object data)
        {
            lock (_published)
            {
                _published.Add((sessionId, type, data));
            }
        }
    }

    private sealed class ScriptedLlm : ILlmProvider
    {
        private int _calls;

        public bool BlockFirstCall { get; init; }

        public bool Fail { get; init; }

        public bool FirstCallCancelled { get; private set; }

        public TaskCompletionSource FirstTokenSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "scripted";

        public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new LlmUnavailableException("Can't reach Ollama.");
            }

            var first = Interlocked.Increment(ref _calls) == 1;
            yield return "One ";
            if (first && BlockFirstCall)
            {
                FirstTokenSent.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCallCancelled = true;
                    throw;
                }
            }

            await Task.Yield();
            yield return "two.";
        }
    }
}
