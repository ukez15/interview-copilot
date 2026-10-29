using InterviewCopilot.Domain;
using InterviewCopilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InterviewCopilot.Infrastructure.Persistence.Configurations;

internal sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever(); // ids are created in the domain
        builder.Property(a => a.Username).HasMaxLength(UserAccount.MaxUsernameLength).UseCollation("NOCASE").IsRequired();
        builder.HasIndex(a => a.Username).IsUnique();
        builder.Property(a => a.PasswordHash).IsRequired();
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever(); // ids are created in the domain
        builder.Property(s => s.Title).HasMaxLength(SessionLimits.MaxTitleLength).IsRequired();
        builder.Property(s => s.SessionPrompt).HasMaxLength(SessionLimits.MaxSessionPromptLength);
        builder.HasIndex(s => s.OwnerId);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(s => s.Documents).WithOne().HasForeignKey(d => d.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class SessionDocumentConfiguration : IEntityTypeConfiguration<SessionDocument>
{
    public void Configure(EntityTypeBuilder<SessionDocument> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever(); // ids are created in the domain
        builder.Property(d => d.FileName).HasMaxLength(SessionLimits.MaxFileNameLength).IsRequired();
        builder.Property(d => d.Content).IsRequired();
    }
}

internal sealed class TranscriptSegmentConfiguration : IEntityTypeConfiguration<TranscriptSegment>
{
    public void Configure(EntityTypeBuilder<TranscriptSegment> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever(); // ids are created in the domain
        builder.HasIndex(t => new { t.SessionId, t.StartMs });
        builder.HasOne<Session>().WithMany().HasForeignKey(t => t.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SuggestedAnswerConfiguration : IEntityTypeConfiguration<SuggestedAnswer>
{
    public void Configure(EntityTypeBuilder<SuggestedAnswer> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever(); // ids are created in the domain
        builder.HasIndex(a => new { a.SessionId, a.CreatedAt });
        builder.HasOne<Session>().WithMany().HasForeignKey(a => a.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PracticeAttemptConfiguration : IEntityTypeConfiguration<PracticeAttempt>
{
    public void Configure(EntityTypeBuilder<PracticeAttempt> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever(); // ids are created in the domain
        builder.HasIndex(p => p.SessionId);
        builder.HasOne<Session>().WithMany().HasForeignKey(p => p.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserPreferencesConfiguration : IEntityTypeConfiguration<UserPreferences>
{
    public void Configure(EntityTypeBuilder<UserPreferences> builder)
    {
        builder.HasKey(p => p.UserId);
        builder.Property(p => p.UserId).ValueGeneratedNever();
        builder.Property(p => p.LlmModel).HasMaxLength(UserPreferences.MaxModelLength);
        builder.Property(p => p.LlmProvider).HasMaxLength(UserPreferences.MaxProviderLength);
        builder.Property(p => p.SpeechToTextProvider).HasMaxLength(UserPreferences.MaxProviderLength);
        builder.Property(p => p.SpeechModel).HasMaxLength(UserPreferences.MaxModelLength);
        builder.HasOne<UserAccount>().WithOne().HasForeignKey<UserPreferences>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PairedBrowserConfiguration : IEntityTypeConfiguration<PairedBrowser>
{
    public void Configure(EntityTypeBuilder<PairedBrowser> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Name).HasMaxLength(PairedBrowser.MaxNameLength).IsRequired();
        builder.Property(b => b.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(b => b.TokenHash).IsUnique();
        builder.HasIndex(b => b.UserId);
        builder.Ignore(b => b.IsRevoked);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
