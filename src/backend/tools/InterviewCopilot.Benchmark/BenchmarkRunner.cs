using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace InterviewCopilot.Benchmark;

internal static class PasswordGenerator
{
    private const int ByteCount = 24;

    /// <summary>A fresh random password for the throwaway account; never stored or printed.</summary>
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(ByteCount));
}

/// <summary>
/// Registers a throwaway account, runs a warm-up question and then every benchmark question sequentially
/// against a live session, and ends the session. Talks to the backend over HTTP only.
/// </summary>
internal sealed class BenchmarkRunner(
    HttpClient http,
    IClock clock,
    TimeProvider time,
    TimeSpan questionTimeout,
    Func<string> passwordFactory)
{
    private const string WarmUpQuestion = "Tell me about yourself.";
    private const string BackendHint = "Start the backend first: pnpm dev:backend";

    public async Task<BenchmarkReport> RunAsync(
        IReadOnlyList<Question> questions,
        string model,
        HardwareInfo hardware,
        IProgress<QuestionResult>? progress,
        CancellationToken cancellationToken)
    {
        var dateUtc = time.GetUtcNow();
        var username = $"benchmark-{dateUtc.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        var credentials = new Credentials(username, passwordFactory());

        await RegisterAndSignInAsync(credentials, cancellationToken);
        var sessionId = await CreateAndStartSessionAsync(cancellationToken);

        var runner = new QuestionRunner(http, clock, questionTimeout);
        var results = new List<QuestionResult>(questions.Count);
        try
        {
            await runner.RunAsync(sessionId, 0, new Question("TellMeAboutYourself", WarmUpQuestion), cancellationToken); // warm-up, not counted
            for (var i = 0; i < questions.Count; i++)
            {
                var result = await runner.RunAsync(sessionId, i + 1, questions[i], cancellationToken);
                results.Add(result);
                progress?.Report(result);
            }
        }
        finally
        {
            await EndSessionAsync(sessionId);
        }

        return new BenchmarkReport(dateUtc, hardware, model, results);
    }

    private async Task RegisterAndSignInAsync(Credentials credentials, CancellationToken ct)
    {
        using var register = await PostAsync("/api/auth/register", credentials, ct);
        if (!register.IsSuccessStatusCode)
        {
            throw new BenchmarkSetupException($"Could not register the benchmark account (HTTP {(int)register.StatusCode}).");
        }

        using var signIn = await PostAsync("/api/auth/sign-in", credentials, ct);
        if (!signIn.IsSuccessStatusCode)
        {
            throw new BenchmarkSetupException($"Could not sign in with the benchmark account (HTTP {(int)signIn.StatusCode}).");
        }

        var token = await ReadStringAsync(signIn, "accessToken", ct)
            ?? throw new BenchmarkSetupException("Could not sign in: the response had no access token.");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<Guid> CreateAndStartSessionAsync(CancellationToken ct)
    {
        var create = new CreateSession("Latency benchmark", "LiveInterview", "Balanced", "None", null, null);
        using var created = await PostAsync("/api/sessions", create, ct);
        if (!created.IsSuccessStatusCode)
        {
            throw new BenchmarkSetupException($"Could not create the benchmark session (HTTP {(int)created.StatusCode}).");
        }

        var id = await ReadStringAsync(created, "id", ct);
        if (!Guid.TryParse(id, out var sessionId))
        {
            throw new BenchmarkSetupException("Could not create the benchmark session: the response had no session id.");
        }

        using var started = await PostAsync($"/api/sessions/{sessionId}/start", null, ct);
        if (!started.IsSuccessStatusCode)
        {
            throw new BenchmarkSetupException($"Could not start the benchmark session (HTTP {(int)started.StatusCode}).");
        }

        return sessionId;
    }

    /// <summary>Best effort: the measurement is already taken, and cancellation must not skip the cleanup.</summary>
    private async Task EndSessionAsync(Guid sessionId)
    {
        try
        {
            using var response = await PostAsync($"/api/sessions/{sessionId}/end", null, CancellationToken.None);
        }
        catch (BenchmarkSetupException)
        {
            // Backend went away; nothing left to clean up.
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object? body, CancellationToken ct)
    {
        try
        {
            return await http.PostAsJsonAsync(path, body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new BenchmarkSetupException($"Cannot reach the backend at {http.BaseAddress}: {ex.Message}. {BackendHint}", ex);
        }
    }

    private static async Task<string?> ReadStringAsync(HttpResponseMessage response, string property, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Credentials(string Username, string Password);

    private sealed record CreateSession(
        string Title,
        string Mode,
        string AnswerLength,
        string Framework,
        string? PinnedQuestionType,
        string? SessionPrompt);
}
