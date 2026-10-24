using System.Net.Http.Json;
using Workbench.Contracts.Time;

namespace Workbench.ApiClient;

public sealed class HttpTimesheetTimerService(HttpClient http) : ITimesheetTimerService
{
    public async Task<TimerStateDto> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/time/timer", cancellationToken);
        return await response.ReadAsync<TimerStateDto>(cancellationToken);
    }

    public async Task<RunningTimerDto> StartAsync(StartTimerRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/time/timer/start", request, cancellationToken);
        return await response.ReadAsync<RunningTimerDto>(cancellationToken);
    }

    public async Task<RunningTimerDto> SetNoteAsync(string? note, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync("api/v1/time/timer/note", new TimerNoteRequest(note), cancellationToken);
        return await response.ReadAsync<RunningTimerDto>(cancellationToken);
    }

    public async Task<StopTimerResult> StopAsync(StopTimerRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/time/timer/stop", request, cancellationToken);
        return await response.ReadAsync<StopTimerResult>(cancellationToken);
    }

    public async Task DiscardAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync("api/v1/time/timer", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }
}
