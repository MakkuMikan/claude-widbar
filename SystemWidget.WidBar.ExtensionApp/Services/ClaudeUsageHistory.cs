using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemWidget.WidBar.ExtensionApp.Services;

/// <summary>
/// Accumulates Claude Code usage across each limit window so the charts can span the whole window.
///
/// <para>The usage API only reports the current percentage and the reset time, so history is built up
/// one point per fetch and saved to the widget's data directory so restarting WidBar doesn't lose it.
/// A changed reset time means a new window has started, which clears that window's history.</para>
/// </summary>
internal sealed class ClaudeUsageHistory
{
    // resets_at can wobble slightly between fetches; anything further out than this is a new window.
    private static readonly TimeSpan SameWindowTolerance = TimeSpan.FromMinutes(10);

    // Keep roughly this many points per window when the value isn't changing (one every ~3 min for 5h,
    // ~1 h for the week), so the weekly file doesn't grow by a point per fetch for seven days.
    private const int FlatPointsPerWindow = 150;

    private readonly string? _path;
    private Dictionary<string, UsageWindowHistory> _windows = new();

    public ClaudeUsageHistory(string? dataDirectory)
    {
        if (string.IsNullOrEmpty(dataDirectory)) return;
        _path = Path.Combine(dataDirectory, "claude_usage_history.json");
        Load();
    }

    public UsageWindowHistory? Get(string key) => _windows.GetValueOrDefault(key);

    /// <summary>Adds a fetched value. Repeat calls for the same fetch are ignored.</summary>
    public void Record(string key, DateTimeOffset resetsAt, TimeSpan length, DateTimeOffset fetchedAt, double percent)
    {
        if (!_windows.TryGetValue(key, out var window)
            || (window.ResetsAt - resetsAt).Duration() > SameWindowTolerance)
        {
            window = new UsageWindowHistory { ResetsAt = resetsAt };
            _windows[key] = window;
        }
        window.ResetsAt = resetsAt;

        if (window.Points.Count > 0)
        {
            var last = window.Points[^1];
            if (fetchedAt <= last.At) return;
            if (Math.Abs(last.Percent - percent) < 0.5 && fetchedAt - last.At < length / FlatPointsPerWindow) return;
        }

        window.Points.Add(new UsagePoint { At = fetchedAt, Percent = percent });
        Save();
    }

    private void Load()
    {
        try
        {
            if (_path is null || !File.Exists(_path)) return;
            _windows = JsonSerializer.Deserialize<Dictionary<string, UsageWindowHistory>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClaudeUsageHistory load failed: {ex.Message}");
            _windows = new();
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Temp + replace so a crash mid-write never leaves a truncated file. The temp name is unique
            // because several widget instances in this process may share the data directory.
            var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_windows));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClaudeUsageHistory save failed: {ex.Message}");
        }
    }
}

internal sealed class UsageWindowHistory
{
    [JsonPropertyName("resets_at")]
    public DateTimeOffset ResetsAt { get; set; }

    [JsonPropertyName("points")]
    public List<UsagePoint> Points { get; set; } = new();
}

internal sealed class UsagePoint
{
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    [JsonPropertyName("percent")]
    public double Percent { get; set; }
}
