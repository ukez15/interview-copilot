using FluentAssertions;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Providers;

public class ProviderCatalogTests
{
    private static ProviderCatalog Catalog(bool deepgramEnabled, string provider = ProviderNames.Whisper)
    {
        var transcription = new TranscriptionOptions { Provider = provider };
        transcription.Providers.Deepgram.Enabled = deepgramEnabled;
        return new ProviderCatalog(Options.Create(transcription), Options.Create(new LlmOptions()));
    }

    [Fact]
    public void Should_OfferOnlyWhisper_When_DeepgramDisabled()
    {
        var catalog = Catalog(deepgramEnabled: false);

        catalog.SpeechToTextProviders().Should().ContainSingle().Which.Name.Should().Be(ProviderNames.Whisper);
        catalog.IsSpeechToTextProviderAvailable(ProviderNames.Deepgram).Should().BeFalse();
    }

    [Fact]
    public void Should_OfferDeepgram_When_Enabled()
    {
        var catalog = Catalog(deepgramEnabled: true, provider: ProviderNames.Deepgram);

        var providers = catalog.SpeechToTextProviders();

        providers.Should().HaveCount(2);
        providers.Single(p => p.Name == ProviderNames.Deepgram).IsDefault.Should().BeTrue();
        providers.Single(p => p.Name == ProviderNames.Deepgram).RequiresApiKey.Should().BeTrue();
        catalog.IsSpeechToTextProviderAvailable("DEEPGRAM").Should().BeTrue();
    }

    [Fact]
    public void Should_DefaultToLocalFreeProviders()
    {
        var llm = Catalog(deepgramEnabled: false).LlmProviders();
        var defaults = new LlmOptions();

        llm.Where(p => p.IsDefault).Should().ContainSingle().Which.Name.Should().Be(ProviderNames.Ollama);
        llm.Single(p => p.Name == ProviderNames.Ollama).Should().Match<ProviderInfo>(p => p.IsLocal && !p.RequiresApiKey);
        llm.Where(p => !p.IsLocal).Should().OnlyContain(p => p.RequiresApiKey);
        defaults.Model.Should().Be("llama3.2:3b");
        new TranscriptionOptions().Providers.Deepgram.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Should_ListCloudChatProviders_WithConfiguredDefault()
    {
        var catalog = new ProviderCatalog(
            Options.Create(new TranscriptionOptions()),
            Options.Create(new LlmOptions { Provider = "OpenAI" }));

        var llm = catalog.LlmProviders();

        llm.Select(p => p.Name).Should().Equal(ProviderNames.Ollama, ProviderNames.Anthropic, ProviderNames.OpenAi, ProviderNames.Google);
        llm.Select(p => p.DisplayName).Should().Equal(
            "Ollama (local, free)",
            "Anthropic (cloud, your key)",
            "OpenAI (cloud, your key)",
            "Google Gemini (cloud, your key)");
        llm.Where(p => p.IsDefault).Should().ContainSingle().Which.Name.Should().Be(ProviderNames.OpenAi);
    }

    [Fact]
    public void Should_DefaultCloudBaseUrls()
    {
        var options = new LlmOptions();

        options.Anthropic.BaseUrl.Should().Be(new Uri("https://api.anthropic.com/"));
        options.OpenAi.BaseUrl.Should().Be(new Uri("https://api.openai.com/"));
        options.Google.BaseUrl.Should().Be(new Uri("https://generativelanguage.googleapis.com/"));
    }

    [Fact]
    public void Should_KeepMessages_When_ProviderExceptionsConstructed()
    {
        new LlmKeyRejectedException().Should().NotBeNull();
        new LlmKeyRejectedException("k").Message.Should().Be("k");
        new LlmKeyRejectedException("k", new InvalidOperationException()).InnerException.Should().NotBeNull();
        new LlmUnavailableException().Should().NotBeNull();
        new LlmUnavailableException("x").Message.Should().Be("x");
        new TranscriptionUnavailableException().Should().NotBeNull();
        new TranscriptionUnavailableException("y", new InvalidOperationException()).InnerException.Should().NotBeNull();
    }
}
