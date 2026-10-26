using FluentAssertions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Preferences;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Services;

internal sealed class InMemoryPreferences : IUserPreferencesRepository
{
    public List<UserPreferences> Items { get; } = [];

    public Task<UserPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Find(p => p.UserId == userId));

    public void Add(UserPreferences preferences) => Items.Add(preferences);
}

public class PreferencesServiceTests
{
    private static readonly Guid User = Guid.NewGuid();
    private readonly InMemoryPreferences _repo = new();
    private readonly InMemoryStore _store = new();
    private readonly PreferencesService _service;

    public PreferencesServiceTests() =>
        _service = new PreferencesService(
            _repo,
            _store,
            new FixedClock(),
            Options.Create(new LlmOptions { Model = "llama3.2:3b" }),
            Options.Create(new TranscriptionOptions()),
            new ProviderCatalog(Options.Create(new TranscriptionOptions()), Options.Create(new LlmOptions())));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReturnNoModel_When_NothingSavedYet()
    {
        var result = await _service.GetAsync(User, Ct);

        result.LlmModel.Should().BeNull();
        result.DefaultLlmModel.Should().Be("llama3.2:3b");
        _repo.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_CreatePreferences_OnFirstWrite()
    {
        var result = await _service.SetLlmModelAsync(User, " qwen2.5:7b ", Ct);

        result.LlmModel.Should().Be("qwen2.5:7b");
        _repo.Items.Should().ContainSingle().Which.LlmModel.Should().Be("qwen2.5:7b");
        _store.SaveCount.Should().Be(1);
        (await _service.GetAsync(User, Ct)).LlmModel.Should().Be("qwen2.5:7b");
    }

    [Fact]
    public async Task Should_UpdateExistingPreferences_OnLaterWrites()
    {
        await _service.SetLlmModelAsync(User, "a", Ct);

        await _service.SetLlmModelAsync(User, "b", Ct);

        _repo.Items.Should().ContainSingle().Which.LlmModel.Should().Be("b");
        _store.SaveCount.Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Should_ClearModel_When_BlankOrNull(string? blank)
    {
        await _service.SetLlmModelAsync(User, "a", Ct);

        var result = await _service.SetLlmModelAsync(User, blank, Ct);

        result.LlmModel.Should().BeNull();
    }

    [Fact]
    public async Task Should_Reject_When_ModelNameTooLong()
    {
        var act = () => _service.SetLlmModelAsync(User, new string('x', UserPreferences.MaxModelLength + 1), Ct);

        await act.Should().ThrowAsync<DomainException>();
        _repo.Items.Should().BeEmpty();
    }
}
