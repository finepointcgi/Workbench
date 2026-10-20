using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Workbench.Application.Tenancy;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing.Edi;

/// <summary>EDI settings (config <c>Edi</c>).</summary>
public sealed class EdiOptions
{
    public const string Section = "Edi";

    /// <summary>Our interchange id (ISA06 on files we send, ISA08 on files we accept).</summary>
    public string InterchangeId { get; set; } = "WORKBENCH";

    public string InterchangeQualifier { get; set; } = "ZZ";

    /// <summary>"P" production or "T" test, on files we send.</summary>
    public string UsageIndicator { get; set; } = "P";

    /// <summary>The file drop root; each tenant and partner gets a folder below it with <c>inbound</c> and <c>outbound</c>.</summary>
    public string FileDropRoot { get; set; } = "App_Data/edi";
}

/// <summary>A file waiting for us at a partner's inbox.</summary>
public sealed record EdiInboundFile(string Name, string Content, string Handle);

/// <summary>
/// Moves X12 files to and from a trading partner. Implementations: <see cref="FileDropEdiTransport"/> (a folder per
/// partner) and <see cref="SftpEdiTransport"/> (needs an <see cref="ISftpEdiClient"/> from the host). Errors are thrown;
/// the EDI log records them.
/// </summary>
public interface IEdiTransport
{
    EdiTransportKind Kind { get; }

    Task SendAsync(EdiPartner partner, string fileName, string content, CancellationToken ct);

    Task<IReadOnlyList<EdiInboundFile>> ReceiveAsync(EdiPartner partner, CancellationToken ct);

    /// <summary>Moves a picked-up file out of the inbox (to <c>processed</c> or <c>failed</c>), so it isn't read twice.</summary>
    Task CompleteAsync(EdiPartner partner, EdiInboundFile file, bool succeeded, CancellationToken ct);
}

/// <summary>
/// A folder per partner: <c>{root}/{tenant}/{partner address}/outbound</c> for what we send and <c>…/inbound</c> for what
/// they send (picked-up files move to <c>inbound/processed</c> or <c>inbound/failed</c>). The address is a plain folder
/// name, never a path.
/// </summary>
public sealed partial class FileDropEdiTransport(IOptions<EdiOptions> options) : IEdiTransport
{
    private const int MaxFileBytes = 5 * 1024 * 1024;

    public EdiTransportKind Kind => EdiTransportKind.FileDrop;

    public static bool IsValidAddress(string address) => FolderName().IsMatch(address);

    public string PartnerFolder(EdiPartner partner)
    {
        if (!IsValidAddress(partner.Address))
        {
            throw new InvalidOperationException("The partner's file drop folder must be a plain folder name (letters, digits, dashes).");
        }

        var tenant = TenantScope.Key.Length == 0 ? "default" : TenantScope.Key;
        return Path.Combine(Path.GetFullPath(options.Value.FileDropRoot), tenant, partner.Address);
    }

    public async Task SendAsync(EdiPartner partner, string fileName, string content, CancellationToken ct)
    {
        var folder = Path.Combine(PartnerFolder(partner), "outbound");
        Directory.CreateDirectory(folder);

        // Write under a temporary name and rename, so a partner polling the folder never reads half a file.
        var temp = Path.Combine(folder, $".{Path.GetFileName(fileName)}.tmp");
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
        File.Move(temp, Path.Combine(folder, Path.GetFileName(fileName)), overwrite: true);
    }

    public async Task<IReadOnlyList<EdiInboundFile>> ReceiveAsync(EdiPartner partner, CancellationToken ct)
    {
        var folder = Path.Combine(PartnerFolder(partner), "inbound");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var files = new List<EdiInboundFile>();
        foreach (var path in Directory.EnumerateFiles(folder).Where(p => !Path.GetFileName(p).StartsWith('.')).Order(StringComparer.Ordinal).Take(200))
        {
            var info = new FileInfo(path);
            var content = info.Length > MaxFileBytes ? "" : await File.ReadAllTextAsync(path, ct);
            files.Add(new EdiInboundFile(info.Name, content, path));
        }

        return files;
    }

    public Task CompleteAsync(EdiPartner partner, EdiInboundFile file, bool succeeded, CancellationToken ct)
    {
        var inbound = Path.Combine(PartnerFolder(partner), "inbound");
        var full = Path.GetFullPath(file.Handle);
        if (!full.StartsWith(inbound + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
        {
            return Task.CompletedTask;
        }

        var target = Path.Combine(inbound, succeeded ? "processed" : "failed");
        Directory.CreateDirectory(target);
        File.Move(full, Path.Combine(target, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{file.Name}"), overwrite: true);
        return Task.CompletedTask;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex FolderName();
}

/// <summary>An SFTP connection, supplied by the host (Workbench ships none, so there's no third-party SSH code by default).</summary>
public interface ISftpEdiClient
{
    Task UploadAsync(string host, string remotePath, string content, CancellationToken ct);

    Task<IReadOnlyList<(string Name, string Content)>> DownloadAsync(string host, string remoteFolder, CancellationToken ct);

    Task MoveAsync(string host, string fromPath, string toPath, CancellationToken ct);
}

/// <summary>SFTP: <c>outbound</c> and <c>inbound</c> folders on the partner's host, through the host-supplied <see cref="ISftpEdiClient"/>.</summary>
public sealed class SftpEdiTransport(ISftpEdiClient? client = null) : IEdiTransport
{
    public EdiTransportKind Kind => EdiTransportKind.Sftp;

    private ISftpEdiClient Client => client ?? throw new InvalidOperationException(
        "This server has no SFTP client set up for EDI. Use a file drop for this partner, or ask your administrator to add one.");

    public Task SendAsync(EdiPartner partner, string fileName, string content, CancellationToken ct) =>
        Client.UploadAsync(partner.Address, $"outbound/{Path.GetFileName(fileName)}", content, ct);

    public async Task<IReadOnlyList<EdiInboundFile>> ReceiveAsync(EdiPartner partner, CancellationToken ct) =>
        (await Client.DownloadAsync(partner.Address, "inbound", ct)).Select(f => new EdiInboundFile(f.Name, f.Content, $"inbound/{f.Name}")).ToList();

    public Task CompleteAsync(EdiPartner partner, EdiInboundFile file, bool succeeded, CancellationToken ct) =>
        Client.MoveAsync(partner.Address, file.Handle, $"inbound/{(succeeded ? "processed" : "failed")}/{file.Name}", ct);
}
