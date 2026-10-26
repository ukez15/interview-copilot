using System.Net;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure;
using InterviewCopilot.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.UnitTests.Infrastructure;

/// <summary>#30 stress test: a BYOK key may travel in a header but must never show up in logs or exception text.</summary>
public class CloudKeyLeakTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CapturingLogger : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception) + exception);
            }
        }

        public void Dispose()
        {
        }
    }

    private static (ServiceProvider Services, CapturingLogger Log) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var log = new CapturingLogger();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:InterviewCopilot"] = "Data Source=:memory:",
        }).Build());
        services.AddOptions<LlmOptions>();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new CloudFakeHandler(respond)));
        return (services.BuildServiceProvider(), log);
    }

    [Theory]
    [InlineData("anthropic", HttpStatusCode.OK)]
    [InlineData("openai", HttpStatusCode.OK)]
    [InlineData("google", HttpStatusCode.OK)]
    [InlineData("anthropic", HttpStatusCode.Unauthorized)]
    [InlineData("openai", HttpStatusCode.TooManyRequests)]
    [InlineData("google", HttpStatusCode.InternalServerError)]
    public async Task Should_NeverLogOrThrowTheKey(string provider, HttpStatusCode status)
    {
        var key = "sk-" + Guid.NewGuid().ToString("N");
        // The fake provider even echoes the key back, as a hostile or sloppy server might.
        var (services, log) = Build(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent($"data: {{\"echo\":\"{key}\"}}\n\n"),
        });
        await using var scope = services;
        var llm = services.GetServices<ILlmProvider>().Single(p => p.Name == provider);
        var catalog = services.GetServices<ICloudModelCatalog>().Single(c => c.Provider == provider);
        var request = new LlmRequest("m", [new LlmMessage(LlmRole.User, "q")], 0.1, key);
        var messages = new List<string>();

        try
        {
            await llm.StreamAsync(request, Ct).ToListAsync(Ct);
        }
        catch (Exception ex)
        {
            messages.Add(ex.ToString());
        }

        try
        {
            await catalog.ListAsync(key, Ct);
        }
        catch (Exception ex)
        {
            messages.Add(ex.ToString());
        }

        messages.Concat(log.Lines).Should().NotContain(l => l.Contains(key, StringComparison.Ordinal));
        log.Lines.Should().NotBeEmpty("the HTTP pipeline logs requests, which is what this test inspects");
    }
}
