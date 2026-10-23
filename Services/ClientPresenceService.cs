using System.Net.Http.Json;

namespace MidiAutoPlayer.Services;

public sealed class ClientPresenceService : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);
    private readonly Uri _endpoint;
    private readonly string _version;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private int _started;

    public ClientPresenceService(Uri endpoint, string version)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("The presence endpoint must use HTTP or HTTPS.", nameof(endpoint));

        _endpoint = endpoint;
        _version = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
        _stopToken = _stop.Token;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _ = Task.Run(SendHeartbeatsAsync);
    }

    private async Task SendHeartbeatsAsync()
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (!_stopToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = JsonContent.Create(new HeartbeatRequest(_sessionId, _version))
                };
                using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    _stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException)
            {
                // A failed heartbeat is retried on the next scheduled interval.
            }
            catch (TaskCanceledException) when (!_stopToken.IsCancellationRequested)
            {
                // HttpClient's timeout only skips this heartbeat.
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(_stopToken).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private sealed record HeartbeatRequest(Guid SessionId, string Version);
}
