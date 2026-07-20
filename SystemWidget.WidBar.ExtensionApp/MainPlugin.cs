using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using Windows.Foundation;
using WidBar.SDK;

namespace SystemWidget.WidBar.ExtensionApp;

/// <summary>
/// WidBar のタスクバー領域を描き、同一プロセスのローカル計測器から値を表示する。
/// </summary>
public sealed class MainPlugin : WidgetPluginBase
{
    private readonly LocalTelemetryCollector _telemetry = new();
    private readonly Dictionary<string, TextBlock> _previewValues = new();
    private readonly Dictionary<string, Polyline> _previewGraphs = new();
    private readonly Dictionary<string, List<double>> _history = new();
    private readonly List<TextBlock> _flyoutValues = new();
    private Timer? _hostRefreshTimer;
    private DispatcherTimer? _previewTimer;
    private DispatcherTimer? _flyoutTimer;

    public override string Id => "io.github.10tonchan.systemwidget";
    public override string Name => "System Widget";
    public override string Description => "CPU、GPU、Claude Code の使用状況をタスクバーに表示";
    public override WidgetCategory Category => WidgetCategory.Utility;

    // 7列 x 76 logical px。WidBar が空き領域に収める。
    public override int PreviewLogicalWidth => 532;
    public override int FlyoutWidth => 420;
    public override int FlyoutHeight => 360;
    public override WidgetFlyoutBackdrop FlyoutBackdrop => WidgetFlyoutBackdrop.Acrylic;

    public override Task InitializeAsync(IWidgetContext context)
    {
        base.InitializeAsync(context);
        // WidBar 側の再描画要求も定期送信する。実際の値更新は各 UI の DispatcherTimer が担う。
        _hostRefreshTimer = new Timer(
            _ => Context?.RequestPreviewRefresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    public override UIElement CreatePreviewContent()
    {
        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(2, 0, 2, 0),
        };
        foreach (var _ in Enumerable.Range(0, 7))
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var tiles = new[]
        {
            ("cpu", "CPU", ColorHelper.FromArgb(255, 70, 190, 232)),
            ("memory", "RAM", ColorHelper.FromArgb(255, 207, 119, 255)),
            ("gpu", "GPU", ColorHelper.FromArgb(255, 95, 205, 107)),
            ("vram", "VRAM", ColorHelper.FromArgb(255, 84, 160, 255)),
            ("claude5h", "CLAUDE", ColorHelper.FromArgb(255, 244, 164, 79)),
            ("claudeWeek", "CC WEEK", ColorHelper.FromArgb(255, 244, 164, 79)),
            ("codex5h", "CODEX", ColorHelper.FromArgb(255, 91, 180, 255)),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = CreatePreviewTile(tiles[i].Item1, tiles[i].Item2, tiles[i].Item3);
            Grid.SetColumn(tile, i);
            root.Children.Add(tile);
        }

        root.Loaded += (_, _) =>
        {
            UpdatePreview();
            _previewTimer?.Stop();
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _previewTimer.Tick += (_, _) => UpdatePreview();
            _previewTimer.Start();
        };
        return root;
    }

    public override UIElement CreateFlyoutContent()
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(20, 18, 20, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = "System Widget",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "タスクバー表示は1秒ごとに更新",
            FontSize = 12,
            Opacity = 0.62,
        });

        var details = new Grid { ColumnSpacing = 18, RowSpacing = 9 };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var _ in Enumerable.Range(0, 9)) details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddDetail(details, 0, "CPU", "CPU 使用率", "cpu");
        AddDetail(details, 1, "MEM", "メモリ使用率", "memory");
        AddDetail(details, 2, "GPU", "GPU 使用率", "gpu");
        AddDetail(details, 3, "VRAM", "VRAM 使用率", "vram");
        AddDetail(details, 4, "CC 5H", "Claude Code 5時間枠", "claude5h");
        AddDetail(details, 5, "CC WK", "Claude Code 週次枠", "claudeWeek");
        AddDetail(details, 6, "Codex 5H", "Codex 5時間枠", "codex5h");
        AddDetail(details, 7, "Codex WK", "Codex 週次枠", "codexWeek");
        AddDetail(details, 8, "更新", "データ最終更新", "updated");
        panel.Children.Add(details);

        panel.Loaded += (_, _) =>
        {
            UpdateFlyout();
            _flyoutTimer?.Stop();
            _flyoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _flyoutTimer.Tick += (_, _) => UpdateFlyout();
            _flyoutTimer.Start();
        };
        return panel;
    }

    private Border CreatePreviewTile(string key, string title, Windows.UI.Color color)
    {
        var value = new TextBlock
        {
            Text = "--",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _previewValues[key] = value;
        var graph = new Polyline
        {
            Stroke = new SolidColorBrush(color), StrokeThickness = 1.3,
            Height = 12, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _previewGraphs[key] = graph;
        var stack = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 8,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            Opacity = 0.64,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(value);
        stack.Children.Add(graph);
        return new Border
        {
            Child = stack,
            Background = new SolidColorBrush(ColorHelper.FromArgb(16, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(45, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(3, 0, 3, 0),
        };
    }

    private void AddDetail(Grid grid, int row, string title, string description, string key)
    {
        var label = new TextBlock
        {
            Text = $"{title}  {description}",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBlock
        {
            Text = "--",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = key,
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        _flyoutValues.Add(value);
    }

    private void UpdatePreview()
    {
        var snapshot = LoadSnapshot();
        UpdateMetric("cpu", snapshot?.System?.CpuPercent);
        UpdateMetric("memory", snapshot?.System?.MemoryPercent);
        UpdateMetric("gpu", snapshot?.Gpu?.UtilPercent);
        UpdateMetric("vram", snapshot?.Gpu?.VramPercent);
        UpdateMetric("claude5h", snapshot?.Claude?.SessionPercent);
        UpdateMetric("claudeWeek", snapshot?.Claude?.WeekPercent);
        UpdateMetric("codex5h", snapshot?.Codex?.ShortPercent);
    }

    private void UpdateFlyout()
    {
        var snapshot = LoadSnapshot();
        var values = new Dictionary<string, string>
        {
            ["cpu"] = Percent(snapshot?.System?.CpuPercent),
            ["memory"] = snapshot?.System is { } system
                ? $"{Percent(system.MemoryPercent)}  ({system.MemoryUsedGb:0.0}/{system.MemoryTotalGb:0.0} GB)" : "--",
            ["gpu"] = snapshot?.Gpu?.TempC is { } temp
                ? $"{Percent(snapshot.Gpu.UtilPercent)}  ({temp:0}°C)" : Percent(snapshot?.Gpu?.UtilPercent),
            ["vram"] = snapshot?.Gpu is { } gpu
                ? $"{Percent(gpu.VramPercent)}  ({gpu.VramUsedGb:0.0}/{gpu.VramTotalGb:0.0} GB)" : "--",
            ["claude5h"] = WithRemaining(snapshot?.Claude?.SessionPercent, snapshot?.Claude?.SessionSecondsRemaining),
            ["claudeWeek"] = WithRemaining(snapshot?.Claude?.WeekPercent, snapshot?.Claude?.WeekSecondsRemaining),
            ["codex5h"] = WithRemaining(snapshot?.Codex?.ShortPercent, snapshot?.Codex?.ShortSecondsRemaining),
            ["codexWeek"] = WithRemaining(snapshot?.Codex?.WeekPercent, snapshot?.Codex?.WeekSecondsRemaining),
            ["updated"] = snapshot?.UpdatedAt is { } updated ? updated.ToLocalTime().ToString("HH:mm:ss") : "未取得",
        };
        foreach (var text in _flyoutValues)
        {
            if (text.Tag is string key && values.TryGetValue(key, out var value)) text.Text = value;
        }
    }

    private void SetPreview(string key, string text)
    {
        if (_previewValues.TryGetValue(key, out var value)) value.Text = text;
    }

    private void UpdateMetric(string key, double? value)
    {
        SetPreview(key, Percent(value));
        if (value is null || !_previewGraphs.TryGetValue(key, out var graph)) return;
        if (!_history.TryGetValue(key, out var series)) _history[key] = series = new List<double>();
        series.Add(Math.Clamp(value.Value, 0, 100));
        if (series.Count > 24) series.RemoveAt(0);
        var points = new PointCollection();
        var width = 68.0;
        for (var i = 0; i < series.Count; i++)
        {
            var x = series.Count == 1 ? width : width * i / (series.Count - 1);
            points.Add(new Point(x, 11 - series[i] * 0.10));
        }
        graph.Points = points;
    }

    private Snapshot? LoadSnapshot()
    {
        return _telemetry.Sample();
    }

    private static string Percent(double? value) => value is { } n ? $"{n:0}%" : "--";

    private static string WithRemaining(double? percent, int? seconds)
    {
        if (percent is null) return "--";
        if (seconds is null || seconds < 0) return Percent(percent);
        var span = TimeSpan.FromSeconds(seconds.Value);
        return $"{Percent(percent)}  {span.Days * 24 + span.Hours:D2}:{span.Minutes:D2}";
    }

    public override ValueTask DisposeAsync()
    {
        _previewTimer?.Stop();
        _flyoutTimer?.Stop();
        _hostRefreshTimer?.Dispose();
        _previewTimer = null;
        _flyoutTimer = null;
        _hostRefreshTimer = null;
        return ValueTask.CompletedTask;
    }

    internal sealed class Snapshot
    {
        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; init; }
        public SystemStatus? System { get; init; }
        public GpuStatus? Gpu { get; init; }
        public ClaudeStatus? Claude { get; init; }
        public CodexStatus? Codex { get; init; }
    }

    internal sealed class SystemStatus
    {
        [JsonPropertyName("cpu_percent")]
        public double? CpuPercent { get; init; }
        [JsonPropertyName("memory_percent")]
        public double? MemoryPercent { get; init; }
        [JsonPropertyName("memory_used_gb")]
        public double? MemoryUsedGb { get; init; }
        [JsonPropertyName("memory_total_gb")]
        public double? MemoryTotalGb { get; init; }
    }

    internal sealed class GpuStatus
    {
        [JsonPropertyName("util_percent")]
        public double? UtilPercent { get; init; }
        [JsonPropertyName("vram_percent")]
        public double? VramPercent { get; init; }
        [JsonPropertyName("vram_used_gb")]
        public double? VramUsedGb { get; init; }
        [JsonPropertyName("vram_total_gb")]
        public double? VramTotalGb { get; init; }
        [JsonPropertyName("temp_c")]
        public double? TempC { get; init; }
    }

    internal sealed class ClaudeStatus
    {
        [JsonPropertyName("session_percent")]
        public double? SessionPercent { get; init; }
        [JsonPropertyName("week_percent")]
        public double? WeekPercent { get; init; }
        [JsonPropertyName("session_seconds_remaining")]
        public int? SessionSecondsRemaining { get; init; }
        [JsonPropertyName("week_seconds_remaining")]
        public int? WeekSecondsRemaining { get; init; }
    }

    internal sealed class CodexStatus
    {
        [JsonPropertyName("short_percent")]
        public double? ShortPercent { get; init; }
        [JsonPropertyName("week_percent")]
        public double? WeekPercent { get; init; }
        [JsonPropertyName("short_seconds_remaining")]
        public int? ShortSecondsRemaining { get; init; }
        [JsonPropertyName("week_seconds_remaining")]
        public int? WeekSecondsRemaining { get; init; }
    }
}
