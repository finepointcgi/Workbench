using System.Security.Cryptography.X509Certificates;

namespace Workbench.Application.Payroll;

/// <summary>
/// An adapter that calls its provider's API with the customer's own credentials (saved on the connect screen,
/// <see cref="PayrollConnections"/>). Hours and the register can always go by file too; the API adds employee sync,
/// sending hours to the open payroll and reading processed payroll back.
/// </summary>
public abstract class ConnectedPayrollProvider(PayrollConnections connections, IPayrollHttpHandlerFactory handlers) : IPayrollProvider
{
    public abstract string Key { get; }

    public abstract string Name { get; }

    public bool IsSupported => true;

    public bool SupportsEmployeeSync => true;

    /// <summary>Not known without a database read: see <see cref="IsConnectedAsync"/>.</summary>
    public bool ApiConfigured => false;

    public abstract IReadOnlyList<Contracts.Payroll.EarningCodeMapping> DefaultEarningCodes { get; }

    public abstract PayrollConnectionSpec ConnectionSpec { get; }

    PayrollConnectionSpec? IPayrollProvider.ConnectionSpec => ConnectionSpec;

    public bool SupportsHoursApi => true;

    public bool SupportsRegisterApi => true;

    public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => connections.IsConnectedAsync(Key, cancellationToken);

    public abstract PayrollExportFile ExportHours(PayrollHoursExport export);

    public abstract ParsedPayrollRegister ParseRegister(string content);

    public abstract Task<string> TestConnectionAsync(CancellationToken cancellationToken);

    public abstract Task<PayrollHoursSubmission> SendHoursAsync(PayrollHoursExport export, CancellationToken cancellationToken);

    public abstract Task<ParsedPayrollRegister> FetchRegisterAsync(PayrollRegisterQuery query, CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<PayrollSyncOutcome>> SyncEmployeesAsync(
        string companyCode, IReadOnlyList<(PayrollEmployeeRecord Employee, PayrollSyncAction Action)> changes, CancellationToken cancellationToken);

    protected PayrollConnections Connections => connections;

    protected async Task<PayrollCredentials> CredentialsAsync(CancellationToken ct) =>
        await connections.GetAsync(Key, ct)
        ?? throw new PayrollProviderApiException($"{Name} isn't connected. Open Payroll › Providers › {Name} and enter your API credentials.");

    protected PayrollApi Client(string baseUrl, X509Certificate2? certificate = null) =>
        new(new HttpClient(handlers.Create(Key, certificate), disposeHandler: false) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) }, Name);

    /// <summary>Runs each change, turning a provider error into a failed outcome so one bad record doesn't stop the rest.</summary>
    protected static async Task<IReadOnlyList<PayrollSyncOutcome>> EachAsync(
        IReadOnlyList<(PayrollEmployeeRecord Employee, PayrollSyncAction Action)> changes,
        Func<PayrollEmployeeRecord, PayrollSyncAction, Task<PayrollSyncOutcome>> apply)
    {
        var outcomes = new List<PayrollSyncOutcome>();
        foreach (var (employee, action) in changes)
        {
            try
            {
                outcomes.Add(await apply(employee, action));
            }
            catch (Exception ex) when (ex is PayrollProviderApiException or HttpRequestException or TaskCanceledException)
            {
                outcomes.Add(new(employee.EmployeeId, action, false, employee.ProviderEmployeeId, ex.Message));
            }
        }

        return outcomes;
    }

    protected static void RequireLines(PayrollHoursExport export, string name)
    {
        if (export.Lines.Count == 0)
        {
            throw new ArgumentException($"There are no hours to send to {name}.");
        }
    }
}
