using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InterviewCopilot.Infrastructure.Persistence;

/// <summary>Local SQLite store (v1). Mode C swaps the provider for PostgreSQL without touching Application.</summary>
public sealed class InterviewCopilotDbContext(DbContextOptions<InterviewCopilotDbContext> options)
    : DbContext(options), IUnitOfWork
{
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<SessionDocument> SessionDocuments => Set<SessionDocument>();

    public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();

    public DbSet<SuggestedAnswer> SuggestedAnswers => Set<SuggestedAnswer>();

    public DbSet<PracticeAttempt> PracticeAttempts => Set<PracticeAttempt>();

    public DbSet<UserPreferences> UserPreferences => Set<UserPreferences>();

    public DbSet<PairedBrowser> PairedBrowsers => Set<PairedBrowser>();

    Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) => SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InterviewCopilotDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite can't order or compare DateTimeOffset stored as text; store as sortable binary.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}
