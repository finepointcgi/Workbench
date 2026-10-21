using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Integrations;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Integrations: the webhook outbox, endpoints, deliveries and their log, API keys, connectors and their run log.
// Integration logs aren't accounting records: removing an endpoint or connector removes its deliveries and runs.

internal sealed class IntegrationOutboxEventConfiguration : IEntityTypeConfiguration<IntegrationOutboxEvent>
{
    public void Configure(EntityTypeBuilder<IntegrationOutboxEvent> b)
    {
        b.ToTable("IntegrationOutboxEvents");
        b.Property(e => e.EventType).HasMaxLength(64);
        b.Property(e => e.Subject).HasMaxLength(64);
        b.Property(e => e.Actor).HasMaxLength(320);
        b.HasIndex(e => new { e.DispatchedAtUtc, e.OccurredAtUtc });
        b.HasIndex(e => e.OccurredAtUtc);
    }
}

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> b)
    {
        b.ToTable("WebhookEndpoints");
        b.Property(e => e.Name).HasMaxLength(200);
        b.Property(e => e.Url).HasMaxLength(2000);
        b.Property(e => e.Description).HasMaxLength(1000);
        b.Property(e => e.EventTypes).HasMaxLength(2000);
        b.Property(e => e.SecretHint).HasMaxLength(8);
        b.Property(e => e.Health).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.CreatedBy).HasMaxLength(320);
        b.Property(e => e.UpdatedBy).HasMaxLength(320);
    }
}

internal sealed class IntegrationDeliveryConfiguration : IEntityTypeConfiguration<IntegrationDelivery>
{
    public void Configure(EntityTypeBuilder<IntegrationDelivery> b)
    {
        b.ToTable("IntegrationDeliveries");
        b.Property(d => d.TargetKind).HasConversion<string>().HasMaxLength(16);
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(d => d.EventType).HasMaxLength(64);
        b.Property(d => d.DedupKey).HasMaxLength(120);
        b.Property(d => d.LastError).HasMaxLength(1000);
        b.Property(d => d.RequestedBy).HasMaxLength(320);
        b.Property(d => d.Version).IsConcurrencyToken();
        b.HasIndex(d => d.DedupKey).IsUnique();
        b.HasIndex(d => new { d.Status, d.NextAttemptAtUtc });
        b.HasIndex(d => new { d.EndpointId, d.CreatedAtUtc });
        b.HasIndex(d => new { d.ConnectorId, d.CreatedAtUtc });
        b.HasIndex(d => d.EventId);
        b.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(d => d.EndpointId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<IntegrationConnector>().WithMany().HasForeignKey(d => d.ConnectorId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<IntegrationOutboxEvent>().WithMany().HasForeignKey(d => d.EventId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(d => d.Attempts).WithOne().HasForeignKey(a => a.DeliveryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IntegrationDeliveryAttemptConfiguration : IEntityTypeConfiguration<IntegrationDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<IntegrationDeliveryAttempt> b)
    {
        b.ToTable("IntegrationDeliveryAttempts");
        b.Property(a => a.RequestUrl).HasMaxLength(2000);
        b.Property(a => a.RequestHeaders).HasMaxLength(4000);
        b.Property(a => a.RequestBody).HasMaxLength(70000);
        b.Property(a => a.ResponseHeaders).HasMaxLength(4000);
        b.Property(a => a.ResponseBody).HasMaxLength(4000);
        b.Property(a => a.Error).HasMaxLength(1000);
        b.HasIndex(a => new { a.DeliveryId, a.AttemptNumber }).IsUnique();
    }
}

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("ApiKeys");
        b.Property(k => k.Name).HasMaxLength(200);
        b.Property(k => k.Description).HasMaxLength(1000);
        b.Property(k => k.Prefix).HasMaxLength(16);
        b.Property(k => k.KeyHash).HasMaxLength(64);
        b.Property(k => k.Scopes).HasMaxLength(2000);
        b.Property(k => k.CustomRoleIds).HasMaxLength(2000);
        b.Property(k => k.ServiceUserEmail).HasMaxLength(320);
        b.Property(k => k.CreatedBy).HasMaxLength(320);
        b.Property(k => k.RevokedBy).HasMaxLength(320);
        b.Property(k => k.RevokedReason).HasMaxLength(500);
        b.Property(k => k.LastUsedFrom).HasMaxLength(64);
        b.HasIndex(k => k.KeyHash).IsUnique();
        b.HasIndex(k => k.ServiceUserEmail).IsUnique();
    }
}

internal sealed class IntegrationConnectorConfiguration : IEntityTypeConfiguration<IntegrationConnector>
{
    public void Configure(EntityTypeBuilder<IntegrationConnector> b)
    {
        b.ToTable("IntegrationConnectors");
        b.Property(c => c.Kind).HasMaxLength(40);
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.SettingsJson).HasMaxLength(8000);
        b.Property(c => c.SecretsProtected).HasMaxLength(8000);
        b.Property(c => c.EventTypes).HasMaxLength(2000);
        b.Property(c => c.Schedule).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.LastStatus).HasMaxLength(1000);
        b.Property(c => c.CreatedBy).HasMaxLength(320);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.Property(c => c.Version).IsConcurrencyToken();
        b.HasIndex(c => new { c.IsActive, c.NextRunAtUtc });
    }
}

internal sealed class ConnectorRunConfiguration : IEntityTypeConfiguration<ConnectorRun>
{
    public void Configure(EntityTypeBuilder<ConnectorRun> b)
    {
        b.ToTable("ConnectorRuns");
        b.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Message).HasMaxLength(1000);
        b.Property(r => r.RequestedBy).HasMaxLength(320);
        b.Property(r => r.OutputFileName).HasMaxLength(260);
        b.Property(r => r.OutputBlobKey).HasMaxLength(400);
        b.HasIndex(r => new { r.ConnectorId, r.StartedAtUtc });
        b.HasOne<IntegrationConnector>().WithMany().HasForeignKey(r => r.ConnectorId).OnDelete(DeleteBehavior.Cascade);
    }
}
