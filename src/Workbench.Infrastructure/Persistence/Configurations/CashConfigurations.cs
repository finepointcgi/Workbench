using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Cash;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class BankAccountConfiguration : IEntityTypeConfiguration<BankAccount>
{
    public void Configure(EntityTypeBuilder<BankAccount> b)
    {
        b.ToTable("BankAccounts");
        b.Property(a => a.Name).HasMaxLength(200);
        b.Property(a => a.BankName).HasMaxLength(200);
        b.Property(a => a.AccountNumberLast4).HasMaxLength(4);
        b.Property(a => a.ProtectedAccountNumber).HasMaxLength(2000);
        b.Property(a => a.RoutingNumber).HasMaxLength(9);
        b.Property(a => a.Currency).HasMaxLength(3);
        b.Property(a => a.CsvMappingJson).HasMaxLength(4000);
        b.Property(a => a.CreatedBy).HasMaxLength(320);
        b.Property(a => a.UpdatedBy).HasMaxLength(320);
        b.HasIndex(a => a.Name).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.GlAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankStatementImportConfiguration : IEntityTypeConfiguration<BankStatementImport>
{
    public void Configure(EntityTypeBuilder<BankStatementImport> b)
    {
        b.ToTable("BankStatementImports");
        b.Property(i => i.FileName).HasMaxLength(260);
        b.Property(i => i.OpeningBalance).HasPrecision(18, 2);
        b.Property(i => i.ClosingBalance).HasPrecision(18, 2);
        b.Property(i => i.ImportedBy).HasMaxLength(320);
        b.HasIndex(i => new { i.BankAccountId, i.ImportedAtUtc });
        b.HasOne<BankAccount>().WithMany().HasForeignKey(i => i.BankAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankTransactionConfiguration : IEntityTypeConfiguration<BankTransaction>
{
    public void Configure(EntityTypeBuilder<BankTransaction> b)
    {
        b.ToTable("BankTransactions");
        b.Property(t => t.Amount).Money();
        b.Property(t => t.Description).HasMaxLength(500);
        b.Property(t => t.CheckNumber).HasMaxLength(32);
        b.Property(t => t.BankReference).HasMaxLength(100);
        b.Property(t => t.Fingerprint).HasMaxLength(200);
        b.HasIndex(t => new { t.BankAccountId, t.Date });
        b.HasIndex(t => new { t.BankAccountId, t.Fingerprint });
        b.HasIndex(t => t.MatchId);
        b.HasOne<BankAccount>().WithMany().HasForeignKey(t => t.BankAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankStatementImport>().WithMany().HasForeignKey(t => t.ImportId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankMatch>().WithMany().HasForeignKey(t => t.MatchId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(t => t.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankMatchConfiguration : IEntityTypeConfiguration<BankMatch>
{
    public void Configure(EntityTypeBuilder<BankMatch> b)
    {
        b.ToTable("BankMatches");
        b.Property(m => m.Note).HasMaxLength(500);
        b.Property(m => m.MatchedBy).HasMaxLength(320);
        b.HasIndex(m => new { m.BankAccountId, m.ReconciliationId });
        b.HasMany(m => m.GlLines).WithOne().HasForeignKey(l => l.MatchId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<BankAccount>().WithMany().HasForeignKey(m => m.BankAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankReconciliation>().WithMany().HasForeignKey(m => m.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankMatchLineConfiguration : IEntityTypeConfiguration<BankMatchLine>
{
    public void Configure(EntityTypeBuilder<BankMatchLine> b)
    {
        b.ToTable("BankMatchLines");
        b.Property(l => l.Amount).Money();

        // A GL line clears against the bank once.
        b.HasIndex(l => new { l.BankAccountId, l.JournalLineId }).IsUnique();
        b.HasOne<JournalLine>().WithMany().HasForeignKey(l => l.JournalLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankReconciliationConfiguration : IEntityTypeConfiguration<BankReconciliation>
{
    public void Configure(EntityTypeBuilder<BankReconciliation> b)
    {
        b.ToTable("BankReconciliations");
        b.Property(r => r.StatementEndingBalance).Money();
        b.Property(r => r.GlBalance).Money();
        b.Property(r => r.DepositsInTransit).Money();
        b.Property(r => r.OutstandingPayments).Money();
        b.Property(r => r.UnrecordedBankItems).Money();
        b.Property(r => r.Note).HasMaxLength(1000);
        b.Property(r => r.StartedBy).HasMaxLength(320);
        b.Property(r => r.CompletedBy).HasMaxLength(320);
        b.Property(r => r.ReopenedBy).HasMaxLength(320);
        b.Property(r => r.ReopenReason).HasMaxLength(1000);
        b.HasIndex(r => new { r.BankAccountId, r.StatementEndDate }).IsUnique();
        b.HasOne<BankAccount>().WithMany().HasForeignKey(r => r.BankAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankCloseWaiverConfiguration : IEntityTypeConfiguration<BankCloseWaiver>
{
    public void Configure(EntityTypeBuilder<BankCloseWaiver> b)
    {
        b.ToTable("BankCloseWaivers");
        b.Property(w => w.Reason).HasMaxLength(1000);
        b.Property(w => w.WaivedBy).HasMaxLength(320);
        b.HasIndex(w => w.FiscalPeriodId);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(w => w.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CashForecastItemConfiguration : IEntityTypeConfiguration<CashForecastItem>
{
    public void Configure(EntityTypeBuilder<CashForecastItem> b)
    {
        b.ToTable("CashForecastItems");
        b.Property(i => i.Name).HasMaxLength(200);
        b.Property(i => i.Amount).Money();
        b.Property(i => i.UpdatedBy).HasMaxLength(320);
    }
}

internal sealed class CashSettingsConfiguration : IEntityTypeConfiguration<CashSettings>
{
    public void Configure(EntityTypeBuilder<CashSettings> b)
    {
        b.ToTable("CashSettings");
        b.Property(s => s.PayrollAmount).HasPrecision(18, 2);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
    }
}
