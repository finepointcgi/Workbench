using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Security;
using Workbench.Domain.AccountingExport;

namespace Workbench.Application.AccountingLive;

/// <summary>
/// Admin › Integrations › Accounting export › Connections. Connects QuickBooks Online and Xero with OAuth 2.0 (authorisation code flow): the user is
/// sent to the provider's consent page with a one-time <c>state</c> that is remembered (as a hash) for this user, the redirect must bring it back within
/// 15 minutes, and the code is then exchanged on the server. Tokens never leave the server and are stored encrypted.
/// </summary>
public sealed class AccountingConnectionService(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<IAccountingOAuth> oauths,
    IOptions<AccountingProviderOptions> options,
    AccountingSecrets secrets,
    IAccountingHttp http,
    ICurrentUser currentUser,
    SecurityEventLog securityLog,
    TimeProvider clock) : IAccountingConnectionService
{
    public static readonly TimeSpan StateLife = TimeSpan.FromMinutes(15);
    private static readonly AccountingTarget[] Targets = [AccountingTarget.QuickBooks, AccountingTarget.Xero];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string Me => currentUser.Email.Trim().ToLowerInvariant();

    private IAccountingOAuth OAuth(AccountingTarget target) => oauths.FirstOrDefault(o => o.Target == target) ?? throw new ArgumentException($"{target} can't be connected.");

    public async Task<IReadOnlyList<AccountingConnectionDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<AccountingConnection>().AsNoTracking().ToListAsync(cancellationToken);
        return Targets.Select(t => ToDto(t, rows.FirstOrDefault(r => r.Target == t.ToString()))).ToList();
    }

    public async Task<AccountingAuthorizeStart> StartAsync(AccountingTarget target, CancellationToken cancellationToken = default)
    {
        var oauth = OAuth(target);
        var app = options.Value.For(target);
        if (!app.IsConfigured)
        {
            throw new ArgumentException($"{oauth.Name} isn't set up on this server. The host administrator sets Accounting:{target}:ClientId, ClientSecret and RedirectUri (see the admin guide).");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var now = Now;
        var expired = await db.Set<AccountingOAuthState>().Where(s => s.ExpiresAtUtc < now).ToListAsync(cancellationToken);
        db.Set<AccountingOAuthState>().RemoveRange(expired);
        var state = AccountingSecrets.NewState();
        db.Set<AccountingOAuthState>().Add(new AccountingOAuthState { StateHash = AccountingSecrets.HashState(state), Target = target.ToString(), CreatedBy = Me, CreatedAtUtc = now, ExpiresAtUtc = now + StateLife });
        await db.SaveChangesAsync(cancellationToken);
        return new AccountingAuthorizeStart(oauth.AuthorizeUrl(app, state).ToString());
    }

    public async Task<AccountingConnectionDto> CompleteAsync(AccountingOAuthCallback callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var target = callback.Target;
        var oauth = OAuth(target);
        var app = options.Value.For(target);
        if (!app.IsConfigured)
        {
            throw new ArgumentException($"{oauth.Name} isn't set up on this server.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var now = Now;
        if (string.IsNullOrEmpty(callback.State))
        {
            throw new ArgumentException("The connection didn't come back from the provider with its state. Start again.");
        }

        var hash = AccountingSecrets.HashState(callback.State);
        var pending = await db.Set<AccountingOAuthState>().FirstOrDefaultAsync(s => s.StateHash == hash, cancellationToken);
        if (pending is null || pending.Target != target.ToString() || pending.ExpiresAtUtc < now || pending.CreatedBy != Me)
        {
            await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"{oauth.Name} connection", SecurityOutcomes.Failure, "Callback with an unknown, expired or foreign state", cancellationToken: cancellationToken);
            throw new ArgumentException("This connection attempt isn't valid any more (it expired, was already used, or was started by someone else). Start again.");
        }

        db.Set<AccountingOAuthState>().Remove(pending); // one use only
        await db.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(callback.Error))
        {
            throw new ArgumentException(callback.Error == "access_denied" ? $"{oauth.Name} was not authorised: access was denied." : $"{oauth.Name} refused the connection ({callback.Error}).");
        }

        if (string.IsNullOrEmpty(callback.Code))
        {
            throw new ArgumentException("The provider didn't send an authorisation code.");
        }

        TokenSet tokens;
        try
        {
            tokens = await oauth.ExchangeAsync(app, callback.Code, cancellationToken);
        }
        catch (AccountingOAuthException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }

        string? companyId = null;
        string? companyName = null;
        string status = AccountingConnectionStatus.Connected;
        string? choices = null;
        if (target == AccountingTarget.QuickBooks)
        {
            companyId = callback.RealmId?.Trim();
            if (string.IsNullOrEmpty(companyId))
            {
                throw new ArgumentException("QuickBooks didn't say which company was connected (no realmId). Start again and choose a company.");
            }

            companyName = await QuickBooksCompanyNameAsync(app, tokens.AccessToken, companyId, cancellationToken);
        }
        else
        {
            var orgs = await oauth.OrganisationsAsync(tokens.AccessToken, cancellationToken);
            if (orgs.Count == 0)
            {
                throw new ArgumentException("No Xero organisation was connected. Start again and tick the organisation to connect.");
            }

            if (orgs.Count == 1)
            {
                companyId = orgs[0].Id;
                companyName = orgs[0].Name;
            }
            else
            {
                status = AccountingConnectionStatus.ChooseOrganisation;
                choices = JsonSerializer.Serialize(orgs.Select(o => new AccountingOrganisationChoice(o.Id, o.Name)), Json);
            }
        }

        var row = await db.Set<AccountingConnection>().FirstOrDefaultAsync(c => c.Target == target.ToString(), cancellationToken);
        if (row is not null && companyId is not null && row.CompanyId is not null && !string.Equals(row.CompanyId, companyId, StringComparison.Ordinal)
            && await db.Set<AccountingProviderLink>().AnyAsync(l => l.Target == target.ToString(), cancellationToken))
        {
            throw new ArgumentException($"Records were already pushed to {row.CompanyName ?? row.CompanyId}. Connecting a different company would mix two sets of books; the ids Workbench remembers belong to the first one.");
        }

        if (row is null)
        {
            row = new AccountingConnection { Target = target.ToString(), Status = status, ConnectedBy = Me, ConnectedAtUtc = now };
            db.Set<AccountingConnection>().Add(row);
        }

        row.Status = status;
        row.IsSandbox = app.Sandbox;
        row.CompanyId = companyId;
        row.CompanyName = companyName;
        row.ChoicesJson = choices;
        row.AccessTokenProtected = secrets.Protect(tokens.AccessToken);
        row.RefreshTokenProtected = secrets.Protect(tokens.RefreshToken);
        row.AccessExpiresUtc = now.AddSeconds(tokens.AccessExpiresInSeconds);
        row.RefreshExpiresUtc = now.AddSeconds(tokens.RefreshExpiresInSeconds ?? (target == AccountingTarget.Xero ? 60 * 86400 : 100 * 86400));
        row.LastRefreshedUtc = now;
        row.LastError = null;
        row.ConnectedBy = Me;
        row.ConnectedAtUtc = now;
        row.TokenVersion++;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"{oauth.Name}: {companyName ?? companyId ?? "organisation to choose"}", detail: app.Sandbox ? "Connected (sandbox)" : "Connected", cancellationToken: cancellationToken);
        return ToDto(target, row);
    }

    /// <summary>GET /v3/company/{realmId}/companyinfo/{realmId}: the company's name, for display. A failure only means no name is shown.</summary>
    private async Task<string?> QuickBooksCompanyNameAsync(ProviderApp app, string accessToken, string realmId, CancellationToken ct)
    {
        try
        {
            var host = app.Sandbox ? "https://sandbox-quickbooks.api.intuit.com" : "https://quickbooks.api.intuit.com";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{host}/v3/company/{Uri.EscapeDataString(realmId)}/companyinfo/{Uri.EscapeDataString(realmId)}?minorversion={QuickBooksPublisher.MinorVersion}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.Client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("CompanyInfo", out var info) && info.TryGetProperty("CompanyName", out var name) ? name.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<AccountingConnectionDto> ChooseOrganisationAsync(AccountingTarget target, string organisationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<AccountingConnection>().FirstOrDefaultAsync(c => c.Target == target.ToString(), cancellationToken)
            ?? throw new ArgumentException($"{target} isn't connected.");
        var choices = Choices(row);
        var chosen = choices.FirstOrDefault(c => string.Equals(c.Id, organisationId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("That organisation isn't one of the ones you connected.");
        if (row.Status != AccountingConnectionStatus.ChooseOrganisation)
        {
            throw new ArgumentException("An organisation is already chosen. Disconnect and connect again to change it.");
        }

        row.CompanyId = chosen.Id;
        row.CompanyName = chosen.Name;
        row.Status = AccountingConnectionStatus.Connected;
        row.ChoicesJson = null;
        row.TokenVersion++;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"{target}: {chosen.Name}", detail: "Organisation chosen", cancellationToken: cancellationToken);
        return ToDto(target, row);
    }

    public async Task DisconnectAsync(AccountingTarget target, CancellationToken cancellationToken = default)
    {
        var oauth = OAuth(target);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<AccountingConnection>().FirstOrDefaultAsync(c => c.Target == target.ToString(), cancellationToken);
        if (row is null)
        {
            return;
        }

        var revoked = "token revoked";
        try
        {
            if (!string.IsNullOrEmpty(row.RefreshTokenProtected) && options.Value.For(target).IsConfigured)
            {
                await oauth.RevokeAsync(options.Value.For(target), secrets.Unprotect(row.RefreshTokenProtected), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is AccountingOAuthException or HttpRequestException or TaskCanceledException)
        {
            revoked = $"the provider didn't confirm the revoke ({ex.Message}); remove Workbench under the provider's connected apps";
        }

        var name = row.CompanyName ?? row.CompanyId ?? target.ToString();
        db.Set<AccountingConnection>().Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"{oauth.Name}: {name}", detail: $"Disconnected; {revoked}", cancellationToken: cancellationToken);
    }

    private static IReadOnlyList<AccountingOrganisationChoice> Choices(AccountingConnection row) =>
        string.IsNullOrEmpty(row.ChoicesJson) ? [] : JsonSerializer.Deserialize<List<AccountingOrganisationChoice>>(row.ChoicesJson, Json) ?? [];

    private AccountingConnectionDto ToDto(AccountingTarget target, AccountingConnection? row)
    {
        var app = options.Value.For(target);
        var name = OAuth(target).Name;
        return row is null
            ? new AccountingConnectionDto(target, name, app.IsConfigured, false, null, app.Sandbox, null, null, null, null, null, null, null, null, null, [])
            : new AccountingConnectionDto(target, name, app.IsConfigured, row.Status == AccountingConnectionStatus.Connected, row.Status, row.IsSandbox, row.CompanyId, row.CompanyName, row.ConnectedBy,
                row.ConnectedAtUtc, row.RefreshExpiresUtc, row.LastError, row.LastSyncUtc, row.LastSyncStatus, row.LastSyncMessage, Choices(row));
    }
}
