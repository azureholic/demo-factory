using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.Storage.Blobs;
using DemoFactory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DemoFactory.Infrastructure;

public sealed class FactoryEnvironmentOptions
{
    public DeploymentProfile Profile { get; set; } = DeploymentProfile.Lean;
    public string OwnerLogin { get; set; } = "azureholic";
    public string FactoryAppSlug { get; set; } = "demo-factory";
    public string PoliciesDirectory { get; set; } = "policies/repos";
    public string EvidenceBlobServiceUri { get; set; } = string.Empty;
    public string EvidenceLocalDirectory { get; set; } = "artifacts/evidence";
    public string ServiceBusFullyQualifiedNamespace { get; set; } = string.Empty;
    public string ServiceBusQueueName { get; set; } = "github-webhooks";
    public string PreviewsSubscriptionId { get; set; } = string.Empty;
    public string PreviewsResourceGroup { get; set; } = "rg-demo-factory-previews";
    public string ManagedIdentityClientId { get; set; } = string.Empty;
    public string LocalDataDirectory { get; set; } = string.Empty;
    public bool SandboxMode { get; set; } = true;
    public bool AllowTestHeaderAuth { get; set; } = false;
}

public interface IEvidenceStore
{
    Task<string> SaveEvidenceJsonAsync<T>(string category, string key, T payload, CancellationToken ct = default);
}

public sealed class EvidenceStore(
    IOptions<FactoryEnvironmentOptions> options,
    ILogger<EvidenceStore> logger) : IEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> SaveEvidenceJsonAsync<T>(string category, string key, T payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var blobName = $"{category}/{DateTimeOffset.UtcNow:yyyy/MM/dd}/{Sanitize(key)}.json";

        if (!string.IsNullOrWhiteSpace(options.Value.EvidenceBlobServiceUri)
            && Uri.TryCreate(options.Value.EvidenceBlobServiceUri, UriKind.Absolute, out var serviceUri))
        {
            TokenCredential credential = string.IsNullOrWhiteSpace(options.Value.ManagedIdentityClientId)
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.Value.ManagedIdentityClientId));

            var client = new BlobServiceClient(serviceUri, credential);
            var container = client.GetBlobContainerClient("factory-evidence");
            await container.CreateIfNotExistsAsync(cancellationToken: ct);
            var blob = container.GetBlobClient(blobName);
            await blob.UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken: ct);
            return blob.Uri.ToString();
        }

        var localRoot = Path.GetFullPath(options.Value.EvidenceLocalDirectory);
        var fullPath = Path.Combine(localRoot, blobName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, ct);
        logger.LogDebug("Saved evidence snapshot locally at {Path}", fullPath);
        return $"file:///{fullPath.Replace('\\', '/')}";
    }

    private static string Sanitize(string input) =>
        string.Concat(input.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
}

public sealed record WebhookEnvelope(
    string DeliveryId,
    string EventType,
    string? Action,
    string? SenderLogin,
    string? RepositoryFullName,
    string PayloadJson,
    DateTimeOffset ReceivedAt);

public interface IWebhookEventBus
{
    /// <summary>
    /// De-duplicates on X-GitHub-Delivery and dispatches (§3.5 Lean SQL Inbox vs Reference Service Bus).
    /// Returns false if deliveryId was already seen.
    /// </summary>
    Task<bool> EnqueueIfUniqueAsync(WebhookEnvelope envelope, CancellationToken ct = default);
    ChannelReader<WebhookEnvelope> LocalReader { get; }
}

public sealed class WebhookEventBus : IWebhookEventBus, IAsyncDisposable
{
    private readonly IDbContextFactory<DemoFactoryDbContext> _dbFactory;
    private readonly FactoryEnvironmentOptions _options;
    private readonly ILogger<WebhookEventBus> _logger;
    private readonly Channel<WebhookEnvelope> _channel = Channel.CreateUnbounded<WebhookEnvelope>();
    private readonly ServiceBusClient? _serviceBusClient;
    private readonly ServiceBusSender? _serviceBusSender;

    public WebhookEventBus(
        IDbContextFactory<DemoFactoryDbContext> dbFactory,
        IOptions<FactoryEnvironmentOptions> options,
        ILogger<WebhookEventBus> logger)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _logger = logger;

        if (_options.Profile == DeploymentProfile.Reference
            && !string.IsNullOrWhiteSpace(_options.ServiceBusFullyQualifiedNamespace))
        {
            TokenCredential credential = string.IsNullOrWhiteSpace(_options.ManagedIdentityClientId)
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(_options.ManagedIdentityClientId));

            _serviceBusClient = new ServiceBusClient(_options.ServiceBusFullyQualifiedNamespace, credential);
            _serviceBusSender = _serviceBusClient.CreateSender(_options.ServiceBusQueueName);
        }
    }

    public ChannelReader<WebhookEnvelope> LocalReader => _channel.Reader;

    public async Task<bool> EnqueueIfUniqueAsync(WebhookEnvelope envelope, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var exists = await db.InboxEvents.AnyAsync(e => e.DeliveryId == envelope.DeliveryId, ct);
        if (exists)
        {
            _logger.LogDebug("Duplicate GitHub webhook delivery {DeliveryId} ignored.", envelope.DeliveryId);
            return false;
        }

        var trusted = TrustBoundary.IsTrustedActor(envelope.SenderLogin, _options.OwnerLogin, _options.FactoryAppSlug);

        db.InboxEvents.Add(new InboxEventEntity
        {
            DeliveryId = envelope.DeliveryId,
            EventType = envelope.EventType,
            Action = envelope.Action,
            PayloadJson = envelope.PayloadJson,
            TrustedActor = trusted,
            ReceivedAt = envelope.ReceivedAt
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return false;
        }

        if (_serviceBusSender is not null)
        {
            var msg = new ServiceBusMessage(BinaryData.FromObjectAsJson(envelope))
            {
                MessageId = envelope.DeliveryId,
                Subject = envelope.EventType
            };
            await _serviceBusSender.SendMessageAsync(msg, ct);
        }
        else
        {
            await _channel.Writer.WriteAsync(envelope, ct);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceBusSender is not null)
            await _serviceBusSender.DisposeAsync();
        if (_serviceBusClient is not null)
            await _serviceBusClient.DisposeAsync();
    }
}

public interface IPolicyRepository
{
    Task<(RepoPolicy Policy, string RawYaml, bool IsValid, string? ValidationError)> GetPolicyAsync(string owner, string repo, CancellationToken ct = default);
    Task<string> EnsureDefaultPolicyFileAsync(string owner, string repo, int projectNumber = 1, AutonomyLevel autonomy = AutonomyLevel.L1, CancellationToken ct = default);
}

public sealed class PolicyRepository(IOptions<FactoryEnvironmentOptions> options) : IPolicyRepository
{
    public async Task<(RepoPolicy Policy, string RawYaml, bool IsValid, string? ValidationError)> GetPolicyAsync(
        string owner,
        string repo,
        CancellationToken ct = default)
    {
        var path = ResolvePath(owner, repo);
        if (!File.Exists(path))
        {
            var defaultPolicy = CreateDefault(owner, repo);
            return (defaultPolicy, defaultPolicy.ToYaml(), true, null);
        }

        var raw = await File.ReadAllTextAsync(path, ct);
        try
        {
            var parsed = RepoPolicy.ParseYaml(raw);
            return (parsed, raw, true, null);
        }
        catch (Exception ex)
        {
            var fallback = CreateDefault(owner, repo);
            return (fallback, raw, false, ex.Message);
        }
    }

    public async Task<string> EnsureDefaultPolicyFileAsync(
        string owner,
        string repo,
        int projectNumber = 1,
        AutonomyLevel autonomy = AutonomyLevel.L1,
        CancellationToken ct = default)
    {
        var path = ResolvePath(owner, repo);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var policy = CreateDefault(owner, repo, projectNumber, autonomy);
            await File.WriteAllTextAsync(path, policy.ToYaml(), ct);
        }

        return path;
    }

    private string ResolvePath(string owner, string repo)
    {
        var baseDir = options.Value.PoliciesDirectory;
        if (!Path.IsPathRooted(baseDir))
        {
            var candidateFromCwd = Path.Combine(Directory.GetCurrentDirectory(), baseDir);
            var candidateFromRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", baseDir));
            baseDir = Directory.Exists(candidateFromCwd) ? candidateFromCwd
                : Directory.Exists(candidateFromRoot) ? candidateFromRoot
                : candidateFromCwd;
        }

        return Path.Combine(baseDir, $"{owner}__{repo}.yml");
    }

    private static RepoPolicy CreateDefault(string owner, string repo, int projectNumber = 1, AutonomyLevel autonomy = AutonomyLevel.L1) =>
        new()
        {
            Repository = $"{owner}/{repo}",
            Project = new ProjectBoardTarget { Owner = owner, Number = projectNumber },
            Autonomy = autonomy
        };
}

public interface IAuditLogger
{
    Task RecordAsync(
        string actor,
        string action,
        string entityType,
        string entityId,
        object? before = null,
        object? after = null,
        int? issueNumber = null,
        string? sessionId = null,
        int? prNumber = null,
        string? sha = null,
        CancellationToken ct = default);

    string FormatAsCsv(IEnumerable<AuditEventEntity> events);
}

public sealed class AuditLogger(IDbContextFactory<DemoFactoryDbContext> dbFactory) : IAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(
        string actor,
        string action,
        string entityType,
        string entityId,
        object? before = null,
        object? after = null,
        int? issueNumber = null,
        string? sessionId = null,
        int? prNumber = null,
        string? sha = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditEvents.Add(new AuditEventEntity
        {
            Actor = actor,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, JsonOptions),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, JsonOptions),
            IssueNumber = issueNumber,
            SessionId = sessionId,
            PrNumber = prNumber,
            Sha = sha,
            Timestamp = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public string FormatAsCsv(IEnumerable<AuditEventEntity> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Timestamp,Actor,Action,EntityType,EntityId,IssueNumber,PrNumber,SessionId,Sha,BeforeJson,AfterJson");
        foreach (var e in events)
        {
            sb.Append(EscapeCsv(e.Id.ToString())).Append(',')
              .Append(EscapeCsv(e.Timestamp.ToString("O"))).Append(',')
              .Append(EscapeCsv(e.Actor)).Append(',')
              .Append(EscapeCsv(e.Action)).Append(',')
              .Append(EscapeCsv(e.EntityType)).Append(',')
              .Append(EscapeCsv(e.EntityId)).Append(',')
              .Append(EscapeCsv(e.IssueNumber?.ToString())).Append(',')
              .Append(EscapeCsv(e.PrNumber?.ToString())).Append(',')
              .Append(EscapeCsv(e.SessionId)).Append(',')
              .Append(EscapeCsv(e.Sha)).Append(',')
              .Append(EscapeCsv(e.BeforeJson)).Append(',')
              .Append(EscapeCsv(e.AfterJson)).AppendLine();
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

public interface IPreviewHostingService
{
    Task DeletePreviewAppAsync(PreviewEnvironmentEntity preview, string actor, CancellationToken ct = default);
}

public sealed class PreviewHostingService(
    IDbContextFactory<DemoFactoryDbContext> dbFactory,
    IAuditLogger auditLogger,
    IOptions<FactoryEnvironmentOptions> options,
    ILogger<PreviewHostingService> logger) : IPreviewHostingService
{
    public async Task DeletePreviewAppAsync(PreviewEnvironmentEntity preview, string actor, CancellationToken ct = default)
    {
        if (!options.Value.SandboxMode
            && !string.IsNullOrWhiteSpace(options.Value.PreviewsSubscriptionId))
        {
            TokenCredential credential = string.IsNullOrWhiteSpace(options.Value.ManagedIdentityClientId)
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.Value.ManagedIdentityClientId));

            var arm = new ArmClient(credential);
            var resourceId = ContainerAppResource.CreateResourceIdentifier(
                options.Value.PreviewsSubscriptionId,
                options.Value.PreviewsResourceGroup,
                preview.AppName);
            var appResource = arm.GetContainerAppResource(resourceId);
            await appResource.DeleteAsync(Azure.WaitUntil.Completed, ct);
            logger.LogInformation("Deleted preview Container App {AppName}", preview.AppName);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PreviewEnvironments.FirstOrDefaultAsync(p => p.Id == preview.Id, ct);
        if (entity is not null && entity.State != PreviewState.Deleted)
        {
            entity.State = PreviewState.Deleted;
            entity.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await auditLogger.RecordAsync(
                actor,
                "PreviewDeleted",
                "PreviewEnvironment",
                entity.Id.ToString(),
                before: new { State = "Running", entity.AppName },
                after: new { State = "Deleted", entity.DeletedAt },
                prNumber: entity.PrNumber,
                sha: entity.HeadSha,
                ct: ct);
        }
    }
}
