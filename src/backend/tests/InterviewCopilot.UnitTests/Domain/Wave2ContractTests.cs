using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;
using InterviewCopilot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InterviewCopilot.UnitTests.Domain;

/// <summary>Step-0 contracts for wave 2 (docs/atlas-gotcha/wave-2.md).</summary>
public class Wave2ContractTests
{
    private static readonly DateTimeOffset Now = FixedClock.Default;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_AutoAnswerByDefault_And_AllowPausing()
    {
        var session = Session.Create(Guid.NewGuid(), "Live", SessionMode.LiveInterview, Now);

        session.AutoAnswer.Should().BeTrue();
        session.SetAutoAnswer(false);
        SessionDto.From(session).AutoAnswer.Should().BeFalse();
    }

    [Fact]
    public void Should_TrimModelName_TreatBlankAsDefault_And_RejectTooLong()
    {
        var preferences = UserPreferences.Create(Guid.NewGuid(), Now);

        preferences.SetLlmModel("  qwen2.5:3b ", Now.AddMinutes(1));
        preferences.LlmModel.Should().Be("qwen2.5:3b");
        preferences.UpdatedAt.Should().Be(Now.AddMinutes(1));

        preferences.SetLlmModel("  ", Now);
        preferences.LlmModel.Should().BeNull();

        ((Action)(() => preferences.SetLlmModel(new string('m', UserPreferences.MaxModelLength + 1), Now)))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public async Task Should_StorePreferencesPerAccount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        var options = new DbContextOptionsBuilder<InterviewCopilotDbContext>().UseSqlite(connection).Options;
        var account = UserAccount.Create("prefs", "hash", Now);

        await using (var db = new InterviewCopilotDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
            db.UserAccounts.Add(account);
            var repository = new UserPreferencesRepository(db);
            var preferences = UserPreferences.Create(account.Id, Now);
            preferences.SetLlmModel("llama3.2:3b", Now);
            repository.Add(preferences);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = new InterviewCopilotDbContext(options))
        {
            var stored = await new UserPreferencesRepository(db).GetAsync(account.Id, Ct);
            stored!.LlmModel.Should().Be("llama3.2:3b");
            (await new UserPreferencesRepository(db).GetAsync(Guid.NewGuid(), Ct)).Should().BeNull();
        }
    }

    [Fact]
    public async Task Should_DefaultStreamEndToNoOp_And_NameAutoAnswerEvent()
    {
        IAudioFrameSink sink = new AudioStatsSink(new FixedClock());

        await sink.OnStreamEndedAsync(Guid.NewGuid(), Ct);

        SessionEventTypes.AutoAnswerState.Should().Be("autoanswer.state");
    }
}
