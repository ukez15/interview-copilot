using System.Runtime.CompilerServices;
using FluentAssertions;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Preferences;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;
using InterviewCopilot.Infrastructure.Persistence;
using InterviewCopilot.UnitTests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Domain;

/// <summary>Step-0 contracts for wave 3 (docs/atlas-gotcha/wave-3.md).</summary>
public class Wave3ContractTests
{
    private static readonly DateTimeOffset Now = FixedClock.Default;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_NormalizeProviders_TreatBlankAsDefault_And_RejectTooLong()
    {
        var preferences = UserPreferences.Create(Guid.NewGuid(), Now);

        preferences.SetLlm(" OpenAI ", "gpt-test", Now.AddMinutes(1));
        preferences.LlmProvider.Should().Be("openai");
        preferences.LlmModel.Should().Be("gpt-test");
        preferences.UpdatedAt.Should().Be(Now.AddMinutes(1));

        preferences.SetSpeechToTextProvider("Deepgram", Now);
        preferences.SpeechToTextProvider.Should().Be("deepgram");

        preferences.SetLlm(" ", null, Now);
        preferences.LlmProvider.Should().BeNull();
        preferences.LlmModel.Should().BeNull();

        var act = () => preferences.SetSpeechToTextProvider(new string('x', UserPreferences.MaxProviderLength + 1), Now);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Should_CreatePairedBrowser_And_RevokeOnce()
    {
        var browser = PairedBrowser.Create(Guid.NewGuid(), "  Chrome on laptop ", "abc123", Now);

        browser.Name.Should().Be("Chrome on laptop");
        browser.IsRevoked.Should().BeFalse();
        browser.MarkUsed(Now.AddMinutes(1));
        browser.LastUsedAt.Should().Be(Now.AddMinutes(1));

        browser.Revoke(Now.AddMinutes(2));
        browser.Revoke(Now.AddMinutes(3));
        browser.IsRevoked.Should().BeTrue();
        browser.RevokedAt.Should().Be(Now.AddMinutes(2));
    }

    [Theory]
    [InlineData("", "hash")]
    [InlineData("name", " ")]
    public void Should_RejectInvalidPairedBrowser(string name, string hash)
    {
        var act = () => PairedBrowser.Create(Guid.NewGuid(), name, hash, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Should_RejectTooLongBrowserName()
    {
        var act = () => PairedBrowser.Create(Guid.NewGuid(), new string('x', PairedBrowser.MaxNameLength + 1), "hash", Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Should_KeepKeysPerSessionAndProvider_And_ClearOnlyThatSession()
    {
        var store = new InMemorySessionKeyStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        store.Store(first, "OpenAI", "k1");
        store.Store(first, "deepgram", "k2");
        store.Store(second, "openai", "k3");

        store.TryGet(first, "openai").Should().Be("k1");
        store.TryGet(first, "anthropic").Should().BeNull();
        store.Clear(first);
        store.TryGet(first, "openai").Should().BeNull();
        store.TryGet(first, "deepgram").Should().BeNull();
        store.TryGet(second, "openai").Should().Be("k3");
    }

    [Fact]
    public void Should_ResolveProviderByName_Else_ThrowUserFacingError()
    {
        var resolver = new LlmProviderResolver([new FakeLlm("x")]);

        resolver.Resolve("OLLAMA").Should().BeOfType<FakeLlm>();
        var act = () => resolver.Resolve("openai");
        act.Should().Throw<LlmUnavailableException>().WithMessage("*openai*");
    }

    [Fact]
    public async Task Should_UseOwnersProvider_And_PreferSessionKeyOverRequestKey()
    {
        var store = new InMemoryStore();
        var session = Session.Create(Guid.NewGuid(), "Live", SessionMode.LiveInterview, Now);
        store.Add(session);
        var preferences = new InMemoryPreferences();
        var choice = UserPreferences.Create(session.OwnerId, Now);
        choice.SetLlm("openai", "gpt-test", Now);
        preferences.Add(choice);
        var keys = new InMemorySessionKeyStore();
        var cloud = new NamedLlm("openai");
        var generator = new AnswerGenerator(store, store, store, new LlmProviderResolver([new FakeLlm("local"), cloud]), keys,
            Options.Create(new LlmOptions()), new FixedClock(), new NullSessionEventPublisher(), preferences);

        await DrainAsync(generator, new AnswerRequest(session.Id, "Why us?", ApiKey: "request-key"));
        cloud.LastRequest!.ApiKey.Should().Be("request-key");
        cloud.LastRequest.Model.Should().Be("gpt-test");

        keys.Store(session.Id, "openai", "session-key");
        await DrainAsync(generator, new AnswerRequest(session.Id, "Why us?", ApiKey: "request-key"));
        cloud.LastRequest.ApiKey.Should().Be("session-key");
    }

    [Fact]
    public async Task Should_HandKeyOnlyToLiveOwnedSession_And_ClearItOnEnd()
    {
        var store = new InMemoryStore();
        var keys = new InMemorySessionKeyStore();
        var service = new SessionService(store, store, new FixedClock(), sessionKeys: keys);
        var owner = Guid.NewGuid();
        var created = await service.CreateAsync(
            new CreateSessionCommand(owner, "Live", SessionMode.LiveInterview, AnswerLength.Balanced, AnswerFramework.None, null, null), Ct);

        var draft = () => service.SetSessionKeyAsync(owner, created.Id, "openai", "k", Ct);
        await draft.Should().ThrowAsync<ConflictException>();

        await service.StartAsync(owner, created.Id, Ct);
        await service.SetSessionKeyAsync(owner, created.Id, "OpenAI", " k ", Ct);
        keys.TryGet(created.Id, "openai").Should().Be("k");

        var stranger = () => service.SetSessionKeyAsync(Guid.NewGuid(), created.Id, "openai", "k", Ct);
        await stranger.Should().ThrowAsync<NotFoundException>();

        await service.EndAsync(owner, created.Id, Ct);
        keys.TryGet(created.Id, "openai").Should().BeNull();
    }

    [Theory]
    [InlineData("ollama", "k")]
    [InlineData("whisper", "k")]
    [InlineData("openai", " ")]
    public async Task Should_RejectKeyForUnkeyedProvider_Or_BlankKey(string provider, string key)
    {
        var store = new InMemoryStore();
        var service = new SessionService(store, store, new FixedClock(), sessionKeys: new InMemorySessionKeyStore());

        var act = () => service.SetSessionKeyAsync(Guid.NewGuid(), Guid.NewGuid(), provider, key, Ct);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Should_RejectTooLongKey()
    {
        var store = new InMemoryStore();
        var service = new SessionService(store, store, new FixedClock(), sessionKeys: new InMemorySessionKeyStore());

        var act = () => service.SetSessionKeyAsync(
            Guid.NewGuid(), Guid.NewGuid(), "openai", new string('k', SessionService.MaxApiKeyLength + 1), Ct);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Should_SaveProviderChoices_OnlyForProvidersTheCatalogOffers()
    {
        var store = new InMemoryStore();
        var repo = new InMemoryPreferences();
        var transcription = new TranscriptionOptions();
        var service = new PreferencesService(repo, store, new FixedClock(), Options.Create(new LlmOptions()), Options.Create(transcription),
            new ProviderCatalog(Options.Create(transcription), Options.Create(new LlmOptions())));
        var user = Guid.NewGuid();

        var saved = await service.SetLlmAsync(user, "ollama", "qwen2.5:3b", Ct);
        saved.LlmProvider.Should().Be("ollama");
        saved.LlmModel.Should().Be("qwen2.5:3b");
        saved.DefaultLlmProvider.Should().Be("ollama");
        saved.DefaultSpeechToTextProvider.Should().Be("whisper");

        (await service.SetSpeechToTextProviderAsync(user, "whisper", Ct)).SpeechToTextProvider.Should().Be("whisper");

        (await service.SetLlmAsync(user, "openai", "gpt", Ct)).LlmProvider.Should().Be("openai");
        var unknown = () => service.SetLlmAsync(user, "skynet", "gpt", Ct);
        await unknown.Should().ThrowAsync<DomainException>();
        var deepgram = () => service.SetSpeechToTextProviderAsync(user, "deepgram", Ct);
        await deepgram.Should().ThrowAsync<DomainException>();

        (await service.SetLlmAsync(user, null, null, Ct)).LlmProvider.Should().BeNull();
    }

    [Fact]
    public async Task Should_PersistPairedBrowsers_And_FindThemByTokenHash()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        var options = new DbContextOptionsBuilder<InterviewCopilotDbContext>().UseSqlite(connection).Options;
        var account = UserAccount.Create("pairing", "hash", Now);
        var browser = PairedBrowser.Create(account.Id, "Edge", "deadbeef", Now);

        await using (var db = new InterviewCopilotDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
            db.UserAccounts.Add(account);
            new PairedBrowserRepository(db).Add(browser);
            var preferences = UserPreferences.Create(account.Id, Now);
            preferences.SetLlm("openai", "gpt-test", Now);
            preferences.SetSpeechToTextProvider("deepgram", Now);
            db.UserPreferences.Add(preferences);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = new InterviewCopilotDbContext(options))
        {
            var repository = new PairedBrowserRepository(db);
            (await repository.FindByTokenHashAsync("deadbeef", Ct))!.Id.Should().Be(browser.Id);
            (await repository.FindByTokenHashAsync("nope", Ct)).Should().BeNull();
            (await repository.GetAsync(browser.Id, Ct))!.Name.Should().Be("Edge");
            (await repository.ListAsync(account.Id, Ct)).Should().ContainSingle();
            (await repository.ListAsync(Guid.NewGuid(), Ct)).Should().BeEmpty();

            var stored = await new UserPreferencesRepository(db).GetAsync(account.Id, Ct);
            stored!.LlmProvider.Should().Be("openai");
            stored.SpeechToTextProvider.Should().Be("deepgram");
        }
    }

    private static async Task DrainAsync(AnswerGenerator generator, AnswerRequest request)
    {
        var (_, tokens) = await generator.GenerateAsync(request, Ct);
        await foreach (var token in tokens)
        {
            _ = token;
        }
    }

    private sealed class NamedLlm(string name) : ILlmProvider
    {
        public LlmRequest? LastRequest { get; private set; }

        public string Name => name;

        public async IAsyncEnumerable<string> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            await Task.Yield();
            yield return "cloud";
        }
    }
}
