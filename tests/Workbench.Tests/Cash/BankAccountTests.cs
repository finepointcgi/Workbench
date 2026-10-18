using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Cash;
using Workbench.Domain.Cash;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Cash;

public sealed class BankAccountTests
{
    private const string FullNumber = "123456784321";

    [Fact]
    public async Task The_account_number_is_masked_everywhere_and_stored_encrypted()
    {
        await using var kit = await CashKit.CreateAsync();
        var dto = kit.Bank;
        Assert.Equal(("••••4321", "4321", true), (dto.MaskedAccountNumber, dto.AccountNumberLast4, dto.HasFullAccountNumber));
        Assert.DoesNotContain(FullNumber, JsonSerializer.Serialize(dto));
        Assert.DoesNotContain(FullNumber, JsonSerializer.Serialize(await kit.BankAccounts.ListAsync()));
        Assert.DoesNotContain(FullNumber, JsonSerializer.Serialize(await kit.Forecast.GetPositionAsync()));

        await using var db = await kit.Factory.CreateAsync();
        var row = await db.Set<BankAccount>().AsNoTracking().SingleAsync();
        Assert.NotNull(row.ProtectedAccountNumber);
        Assert.DoesNotContain(FullNumber, row.ProtectedAccountNumber);
        var audit = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == nameof(BankAccount)).ToListAsync();
        Assert.NotEmpty(audit);
        Assert.All(audit, a => Assert.DoesNotContain(FullNumber, a.AfterJson ?? ""));
    }

    [Fact]
    public async Task Revealing_the_number_returns_it_and_logs_a_security_event()
    {
        await using var kit = await CashKit.CreateAsync();
        var revealed = await kit.BankAccounts.RevealAccountNumberAsync(kit.Bank.Id);
        Assert.Equal(FullNumber, revealed.AccountNumber);

        await using var db = await kit.Factory.CreateAsync();
        var e = await db.Set<SecurityEvent>().AsNoTracking().SingleAsync(x => x.EventType == CashSecurityEvents.AccountNumberRevealed);
        Assert.Equal((Finance, "bank account Operating"), (e.Actor, e.Target));
        Assert.DoesNotContain(FullNumber, e.Detail ?? "");

        // Last four only: nothing to reveal.
        var lastFour = await kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("Payroll", "First Capital Bank", "8765", null, kit.Account("1300")));
        Assert.False(lastFour.HasFullAccountNumber);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.BankAccounts.RevealAccountNumberAsync(lastFour.Id));
    }

    [Fact]
    public async Task Account_validation_checks_numbers_routing_and_the_gl_account()
    {
        await using var kit = await CashKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("X", "B", "12", null, kit.Account("1010"))));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("X", "B", "1234", "054001726", kit.Account("1300"))));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("X", "B", "1234", null, kit.Account("6240"))));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("X", "B", "1234", null, kit.Account("1000"))));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("X", "B", "1234", null, kit.Account("1010")))); // 1010 is taken
        await Assert.ThrowsAsync<ArgumentException>(() => kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("Operating", "B", "1234", null, kit.Account("1300"))));

        // An update without a number keeps the stored one.
        var updated = await kit.BankAccounts.UpdateAsync(kit.Bank.Id, new SaveBankAccountRequest("Operating account", "First Capital Bank", null, "054001725", kit.Account("1010")));
        Assert.Equal(("Operating account", "4321", true), (updated.Name, updated.AccountNumberLast4, updated.HasFullAccountNumber));
        Assert.Contains(await kit.BankAccounts.ListCashGlAccountsAsync(), a => a.Number == "1010");
    }

    [Fact]
    public async Task Cash_management_is_for_finance_and_admins_only()
    {
        await using var kit = await CashKit.CreateAsync();
        var id = kit.Bank.Id;
        foreach (var who in new[] { Owner, Viewer, Timekeeper })
        {
            kit.As(who);
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.BankAccounts.ListAsync());
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.BankAccounts.RevealAccountNumberAsync(id));
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Statements.ImportAsync(new ImportStatementRequest(id, "s.csv", "x")));
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Reconciliations.ListAsync());
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Reconciliations.AutoMatchAsync(new AutoMatchRequest(id)));
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Forecast.GetForecastAsync());
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Forecast.GetPositionAsync());
        }

        kit.As(Owner);
        await using (var db = await kit.Factory.CreateAsync())
        {
            // The refused reveal never decrypted anything.
            Assert.False(await db.Set<SecurityEvent>().AnyAsync(e => e.EventType == CashSecurityEvents.AccountNumberRevealed));
        }

        foreach (var who in new[] { Finance, Admin })
        {
            kit.As(who);
            Assert.Single(await kit.BankAccounts.ListAsync());
            Assert.Equal(13, (await kit.Forecast.GetForecastAsync()).Weeks.Count);
        }
    }
}
