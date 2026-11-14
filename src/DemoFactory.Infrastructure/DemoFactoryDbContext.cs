using DemoFactory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DemoFactory.Infrastructure;

public sealed class DemoFactoryDbContext(DbContextOptions<DemoFactoryDbContext> options) : DbContext(options)
{
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<RepositoryEntity> Repositories => Set<RepositoryEntity>();
    public DbSet<RuntimeSettingsEntity> RuntimeSettings => Set<RuntimeSettingsEntity>();
    public DbSet<WorkItemEntity> WorkItems => Set<WorkItemEntity>();
    public DbSet<AgentSessionEntity> AgentSessions => Set<AgentSessionEntity>();
    public DbSet<ReviewRoundEntity> ReviewRounds => Set<ReviewRoundEntity>();
    public DbSet<FindingEntity> Findings => Set<FindingEntity>();
    public DbSet<GateEvaluationEntity> GateEvaluations => Set<GateEvaluationEntity>();
    public DbSet<ApprovalEntity> Approvals => Set<ApprovalEntity>();
    public DbSet<InboxEventEntity> InboxEvents => Set<InboxEventEntity>();
    public DbSet<BoardSnapshotEntity> BoardSnapshots => Set<BoardSnapshotEntity>();
    public DbSet<SpecVersionEntity> SpecVersions => Set<SpecVersionEntity>();
    public DbSet<RequirementEntity> Requirements => Set<RequirementEntity>();
    public DbSet<FeatureSpecEntity> FeatureSpecs => Set<FeatureSpecEntity>();
    public DbSet<InboxItemEntity> InboxItems => Set<InboxItemEntity>();
    public DbSet<ChangeProposalEntity> ChangeProposals => Set<ChangeProposalEntity>();
    public DbSet<LineStateEntity> LineStates => Set<LineStateEntity>();
    public DbSet<PreviewEnvironmentEntity> PreviewEnvironments => Set<PreviewEnvironmentEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<ModelCatalogueEntity> ModelCatalogue => Set<ModelCatalogueEntity>();
    public DbSet<AllowlistUserEntity> AllowlistUsers => Set<AllowlistUserEntity>();
    public DbSet<EncryptedUserTokenEntity> EncryptedUserTokens => Set<EncryptedUserTokenEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.State).HasConversion<string>();
            b.Property(x => x.DefaultAutonomy).HasConversion<string>();
        });

        modelBuilder.Entity<RepositoryEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Owner, x.Name }).IsUnique();
            b.Ignore(x => x.FullName);
        });

        modelBuilder.Entity<RuntimeSettingsEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Scope, x.TargetId });
            b.Property(x => x.Scope).HasConversion<string>();
            b.Property(x => x.Autonomy).HasConversion<string>();
        });

        modelBuilder.Entity<WorkItemEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.RepoId, x.IssueNumber }).IsUnique();
            b.Property(x => x.State).HasConversion<string>();
            b.Property(x => x.BoardStatus).HasConversion<string>();
            b.Property(x => x.Priority).HasConversion<string>();
            b.Property(x => x.Size).HasConversion<string>();
            b.Property(x => x.GateProfile).HasConversion<string>();
        });

        modelBuilder.Entity<AgentSessionEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.WorkItemId);
            b.Property(x => x.Mode).HasConversion<string>();
            b.Property(x => x.State).HasConversion<string>();
            b.Property(x => x.EstimatedCostUsd).HasPrecision(10, 4);
        });

        modelBuilder.Entity<ReviewRoundEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.WorkItemId, x.Round });
        });

        modelBuilder.Entity<FindingEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.WorkItemId, x.Fingerprint });
            b.Property(x => x.Severity).HasConversion<string>();
            b.Property(x => x.Status).HasConversion<string>();
        });

        modelBuilder.Entity<GateEvaluationEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.WorkItemId, x.HeadSha });
            b.Property(x => x.Profile).HasConversion<string>();
        });

        modelBuilder.Entity<ApprovalEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.WorkItemId, x.HeadSha, x.Kind });
            b.Property(x => x.Kind).HasConversion<string>();
            b.Property(x => x.Decision).HasConversion<string>();
        });

        modelBuilder.Entity<InboxEventEntity>(b =>
        {
            b.HasKey(x => x.DeliveryId);
            b.HasIndex(x => x.ReceivedAt);
        });

        modelBuilder.Entity<BoardSnapshotEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.GitHubProjectId, x.ItemId }).IsUnique();
            b.Property(x => x.Status).HasConversion<string>();
        });

        modelBuilder.Entity<SpecVersionEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ProjectId, x.Version }).IsUnique();
        });

        modelBuilder.Entity<RequirementEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique();
            b.Property(x => x.Status).HasConversion<string>();
        });

        modelBuilder.Entity<FeatureSpecEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ProjectId, x.Slug }).IsUnique();
            b.Property(x => x.State).HasConversion<string>();
            b.Property(x => x.Priority).HasConversion<string>();
        });

        modelBuilder.Entity<InboxItemEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.RepoId, x.IssueNumber }).IsUnique();
            b.Property(x => x.State).HasConversion<string>();
        });

        modelBuilder.Entity<ChangeProposalEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ProjectId);
            b.Property(x => x.Kind).HasConversion<string>();
            b.Property(x => x.State).HasConversion<string>();
        });

        modelBuilder.Entity<LineStateEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.RepoId).IsUnique();
            b.Property(x => x.State).HasConversion<string>();
        });

        modelBuilder.Entity<PreviewEnvironmentEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.RepoId, x.PrNumber });
            b.Property(x => x.State).HasConversion<string>();
        });

        modelBuilder.Entity<AuditEventEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Timestamp);
            b.HasIndex(x => x.IssueNumber);
        });

        modelBuilder.Entity<ModelCatalogueEntity>(b =>
        {
            b.HasKey(x => x.Id);
        });

        modelBuilder.Entity<AllowlistUserEntity>(b =>
        {
            b.HasKey(x => x.GitHubLogin);
            b.Property(x => x.Role).HasConversion<string>();
        });

        modelBuilder.Entity<EncryptedUserTokenEntity>(b =>
        {
            b.HasKey(x => x.GitHubLogin);
        });

        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            var dtoConverter = new DateTimeOffsetToBinaryConverter();
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var prop in entityType.GetProperties())
                {
                    if (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?))
                    {
                        prop.SetValueConverter(dtoConverter);
                    }
                }
            }
        }
    }
}
