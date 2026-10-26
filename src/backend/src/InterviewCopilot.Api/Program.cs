using System.Text.Json.Serialization;
using InterviewCopilot.Api.Endpoints;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Infrastructure;
using InterviewCopilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
// The in-memory hub replaces the no-op default publisher registered by AddApplication (#23).
builder.Services.AddSingleton<SessionEventHub>();
builder.Services.AddSingleton<ISessionEventPublisher>(provider => provider.GetRequiredService<SessionEventHub>());

builder.Services.AddAuthentication(BearerTokenDefaults.AuthenticationScheme).AddBearerToken(options =>
    options.Events.OnMessageReceived = context =>
    {
        // Browsers can't set headers on WebSockets, so /ws/* takes the token from ?access_token=.
        if (context.Request.Path.StartsWithSegments(AudioEndpoints.AudioPathPrefix.TrimEnd('/'), StringComparison.Ordinal)
            && context.Request.Query.TryGetValue("access_token", out var token))
        {
            context.Token = token;
        }

        return Task.CompletedTask;
    });
builder.Services.AddAuthorization();
// Enums travel as names ("LiveInterview", "Star") so the UI and extension stay readable.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InterviewCopilotDbContext>();
    await db.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseCors();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapAuthEndpoints();
app.MapProviderEndpoints();
app.MapStorageEndpoints();
app.MapPreferencesEndpoints();
app.MapSessionEndpoints();
app.MapDocumentEndpoints();
app.MapCopilotEndpoints();
app.MapTranscriptEndpoints();
app.MapPracticeEndpoints();
app.MapAudioEndpoints();
app.MapEventsEndpoints();

await app.RunAsync();

/// <summary>Entry point, public for WebApplicationFactory in integration tests.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
