using System.Diagnostics;
using SystemWidget.WidBar.ExtensionApp.Models;

namespace SystemWidget.WidBar.ExtensionApp.Services;

/// <summary>
/// Caches <see cref="UsageApiService"/> results for 180 seconds so the synchronous
/// <see cref="LocalTelemetryCollector.Sample"/> can always read them immediately.
///
/// <para>Calling the API every second lands you in the 429 bucket, so refreshes run as a
/// fire-and-forget background task and the UI keeps getting the last value. Null until the first fetch.</para>
/// </summary>
internal sealed class ClaudeUsageTracker
{
    // At least 180 seconds per call is recommended for Anthropic's unofficial endpoint (most 429 reports poll faster).
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(180);

    // Cooldown after a failure, so repeated failures don't hammer the API.
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    private UsageData? _latest;
    private DateTime _lastSuccessUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private int _refreshInFlight;

    /// <summary>The last usage successfully fetched, or null if none has been fetched yet.</summary>
    public UsageData? Latest => _latest;

    /// <summary>When <see cref="Latest"/> was fetched. Changes only when a new fetch succeeds.</summary>
    public DateTimeOffset? LatestFetchedAt { get; private set; }

    /// <summary>Synchronously checks whether credentials are readable (used by the preview UI to decide on "--").</summary>
    public bool CredentialsExist => CredentialService.CredentialsExist();

    /// <summary>
    /// Kicks off a background refresh if needed. The call itself returns immediately (fire-and-forget).
    /// Does nothing if less than 180 seconds have passed since the last success.
    /// </summary>
    public void EnsureFresh()
    {
        var now = DateTime.UtcNow;
        var sinceSuccess = now - _lastSuccessUtc;
        var sinceAttempt = now - _lastAttemptUtc;

        if (_latest != null && sinceSuccess < RefreshInterval) return;
        if (sinceAttempt < FailureCooldown && _latest != null) return;

        // Prevent concurrent refreshes. If one is already running, don't start another.
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0) return;

        _lastAttemptUtc = now;

        _ = Task.Run(async () =>
        {
            try
            {
                var data = await UsageApiService.GetUsageAsync();
                if (data != null)
                {
                    _latest = data;
                    _lastSuccessUtc = DateTime.UtcNow;
                    LatestFetchedAt = DateTimeOffset.UtcNow;
                }
                // Keep _latest on failure (so the display doesn't suddenly drop to "--").
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClaudeUsageTracker refresh crashed: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
            }
        });
    }
}
