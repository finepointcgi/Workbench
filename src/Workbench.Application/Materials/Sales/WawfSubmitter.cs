using Workbench.Domain.Materials;

namespace Workbench.Application.Materials;

/// <summary>A WAWF receiving report file handed to a submitter.</summary>
public sealed record WawfSubmission(string Shipment, WawfDocumentType DocumentType, string FileName, byte[] Content);

/// <summary>What the submitter did: the PIEE document number or its own reference, and a message to show.</summary>
public sealed record WawfSubmitResult(string? Reference, string? Message);

/// <summary>
/// The plug point for sending receiving reports to PIEE (WAWF): a web service or EDI/SFTP connection would implement it.
/// Register another implementation in place of <see cref="ManualWawfSubmitter"/>.
/// </summary>
public interface IWawfSubmitter
{
    string Name { get; }

    Task<WawfSubmitResult> SubmitAsync(WawfSubmission submission, CancellationToken cancellationToken);
}

/// <summary>The built-in submitter: sends nothing. Someone uploads the exported file in PIEE and the report is recorded as submitted.</summary>
public sealed class ManualWawfSubmitter : IWawfSubmitter
{
    public string Name => "Manual upload";

    public Task<WawfSubmitResult> SubmitAsync(WawfSubmission submission, CancellationToken cancellationToken) =>
        Task.FromResult(new WawfSubmitResult(null, $"Recorded as submitted. Upload {submission.FileName} in PIEE, then record the government's acceptance or rejection."));
}
