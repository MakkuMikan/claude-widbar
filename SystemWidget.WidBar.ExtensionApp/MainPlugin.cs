using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Line = Microsoft.UI.Xaml.Shapes.Line;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using Windows.Foundation;
using SystemWidget.WidBar.ExtensionApp.Services;
using WidBar.SDK;

namespace SystemWidget.WidBar.ExtensionApp;

/// <summary>
/// Draws the WidBar taskbar area and displays values from the in-process local collector.
/// </summary>
public sealed class MainPlugin : WidgetPluginBase, IConfigurableWidgetPlugin
{
    private const int TileWidth = 76;

    // Same slate-700 @ 80% card as the SIT.Track WidBar widgets' default ("Not tracking") state.
    private static readonly Windows.UI.Color CardColor = ColorHelper.FromArgb(0xCC, 0x33, 0x41, 0x55);
    private static readonly Windows.UI.Color LimitColor = ColorHelper.FromArgb(255, 239, 68, 68);

    // Flyout window charts: the flyout's 420px width minus its 20px side padding.
    private const double ChartWidth = 380;
    private const double ChartHeight = 56;
    private const double ChartTop = 6;
    private const double ChartBottom = ChartHeight - 4;

    // Below this fraction of the window elapsed, a straight-line projection is mostly noise.
    private const double MinElapsedForProjection = 0.1;

    private static readonly StatDefinition[] Stats =
    {
        new("cpu", "CPU", "CPU", "CPU usage", ColorHelper.FromArgb(255, 70, 190, 232)),
        new("memory", "RAM", "MEM", "Memory usage", ColorHelper.FromArgb(255, 207, 119, 255)),
        new("gpu", "GPU", "GPU", "GPU usage", ColorHelper.FromArgb(255, 95, 205, 107)),
        new("vram", "VRAM", "VRAM", "VRAM usage", ColorHelper.FromArgb(255, 84, 160, 255)),
        new("claude5h", "CC 5H", "CC 5H", "Claude Code 5-hour limit", ColorHelper.FromArgb(255, 244, 164, 79)),
        new("claudeWeek", "CC WEEK", "CC WK", "Claude Code weekly limit", ColorHelper.FromArgb(255, 244, 164, 79)),
    };

    private readonly LocalTelemetryCollector _telemetry = new();
    private readonly Dictionary<string, TextBlock> _previewValues = new();
    private readonly Dictionary<string, Polyline> _previewGraphs = new();
    private readonly Dictionary<string, List<double>> _history = new();
    private readonly List<TextBlock> _flyoutValues = new();
    private readonly Dictionary<string, WindowChart> _charts = new();
    private ClaudeUsageHistory _claudeHistory = new(null);
    private WidgetSettings _settings = new();
    private Grid? _previewRoot;
    private Grid? _flyoutDetails;
    private StackPanel? _flyoutCharts;
    private Timer? _hostRefreshTimer;
    private DispatcherTimer? _previewTimer;
    private DispatcherTimer? _flyoutTimer;

    public override string Id => "io.github.10tonchan.claudecodexwidbar";
    public override string Name => "System Widget";
    public override string Description => "Shows CPU, GPU and Claude Code usage on the taskbar";
    public override WidgetCategory Category => WidgetCategory.Utility;

    // 76 logical px per visible tile. WidBar fits it into the free space.
    public override int PreviewLogicalWidth => Math.Max(1, VisibleStats.Count()) * TileWidth;
    public override int FlyoutWidth => 420;
    // Header, one row per visible stat plus "Updated", and a chart per visible Claude Code window.
    public override int FlyoutHeight =>
        110 + (VisibleStats.Count() + 1) * 26 + VisibleStats.Count(s => WindowLength(s.Key) is not null) * 116;
    public override WidgetFlyoutBackdrop FlyoutBackdrop => WidgetFlyoutBackdrop.Acrylic;

    private IEnumerable<StatDefinition> VisibleStats => Stats.Where(s => _settings.IsVisible(s.Key));

    public override Task InitializeAsync(IWidgetContext context)
    {
        _settings = WidgetSettings.FromJson(context.SettingsJson);
        _claudeHistory = new ClaudeUsageHistory(context.DataDirectory);
        base.InitializeAsync(context);
        // Also periodically ask WidBar to redraw. Actual value updates are driven by each UI's DispatcherTimer.
        _hostRefreshTimer = new Timer(
            _ => Context?.RequestPreviewRefresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    public override UIElement CreatePreviewContent()
    {
        _previewRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        BuildPreviewTiles();

        var card = new Border
        {
            Background = new SolidColorBrush(CardColor),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 4, 0, 4),
            Child = _previewRoot,
        };

        card.Loaded += (_, _) =>
        {
            UpdatePreview();
            _previewTimer?.Stop();
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _previewTimer.Tick += (_, _) => UpdatePreview();
            _previewTimer.Start();
        };
        return card;
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
            Text = "Taskbar values refresh every second",
            FontSize = 12,
            Opacity = 0.62,
        });

        _flyoutDetails = new Grid { ColumnSpacing = 18, RowSpacing = 9 };
        _flyoutDetails.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _flyoutDetails.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        BuildFlyoutDetails();
        panel.Children.Add(_flyoutDetails);

        _flyoutCharts = new StackPanel { Spacing = 14, Margin = new Thickness(0, 4, 0, 0) };
        BuildFlyoutCharts();
        panel.Children.Add(_flyoutCharts);

        panel.Loaded += (_, _) =>
        {
            UpdateFlyout();
            _flyoutTimer?.Stop();
            _flyoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _flyoutTimer.Tick += (_, _) => UpdateFlyout();
            _flyoutTimer.Start();
        };
        // Scrolls rather than clips if the host doesn't re-read FlyoutHeight after a settings change.
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public UIElement? CreateSettingsContent(IWidgetSettingsContext context)
    {
        var draft = WidgetSettings.FromJson(context.SettingsJson);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Visible stats",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose which stats appear on the taskbar and in the flyout. At least one must stay visible.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var toggles = new List<ToggleSwitch>();
        foreach (var stat in Stats)
        {
            var toggle = new ToggleSwitch
            {
                Header = $"{stat.Description} ({stat.TileTitle})",
                IsOn = draft.IsVisible(stat.Key),
            };
            toggle.Toggled += (_, _) =>
            {
                // Keep at least one tile so the preview (and the gear in its flyout) stays reachable.
                if (!toggle.IsOn && toggles.All(t => !t.IsOn))
                {
                    toggle.IsOn = true;
                    return;
                }
                draft.SetVisible(stat.Key, toggle.IsOn);
                context.SaveSettings(draft.ToJson());
                context.RequestPreviewRefresh();
            };
            toggles.Add(toggle);
            panel.Children.Add(toggle);
        }
        return panel;
    }

    // Called on every settings edit, and once more with the original JSON on cancel.
    public override void OnSettingsDraftChanged(string json)
    {
        _settings = WidgetSettings.FromJson(json);
        BuildPreviewTiles();
        BuildFlyoutDetails();
        BuildFlyoutCharts();
        UpdatePreview();
        UpdateFlyout();
        Context?.RequestPreviewRefresh();
    }

    private void BuildPreviewTiles()
    {
        if (_previewRoot is null) return;
        _previewRoot.Children.Clear();
        _previewRoot.ColumnDefinitions.Clear();
        _previewValues.Clear();
        _previewGraphs.Clear();

        var visible = VisibleStats.ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            _previewRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Dividers only between tiles; the card's rounded edge closes off the last one.
            var tile = CreatePreviewTile(visible[i].Key, visible[i].TileTitle, visible[i].Color, showDivider: i < visible.Count - 1);
            Grid.SetColumn(tile, i);
            _previewRoot.Children.Add(tile);
        }
    }

    private void BuildFlyoutDetails()
    {
        if (_flyoutDetails is null) return;
        _flyoutDetails.Children.Clear();
        _flyoutDetails.RowDefinitions.Clear();
        _flyoutValues.Clear();

        var row = 0;
        foreach (var stat in VisibleStats) AddDetail(_flyoutDetails, row++, stat.FlyoutTitle, stat.Description, stat.Key);
        AddDetail(_flyoutDetails, row, "Updated", "Last data refresh", "updated");
    }

    private Grid CreatePreviewTile(string key, string title, Windows.UI.Color color, bool showDivider)
    {
        var value = new TextBlock
        {
            Text = "--",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            // Trim the line box to cap height (as the SIT.Track widgets do) so the stack fits inside the card.
            TextLineBounds = TextLineBounds.TrimToCapHeight,
        };
        _previewValues[key] = value;
        var graph = new Polyline
        {
            Stroke = new SolidColorBrush(color), StrokeThickness = 1.3,
            Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _previewGraphs[key] = graph;
        var stack = new StackPanel
        {
            Spacing = 3,
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
            TextLineBounds = TextLineBounds.TrimToCapHeight,
        });
        stack.Children.Add(value);
        stack.Children.Add(graph);
        var tile = new Grid { Padding = new Thickness(3, 0, 3, 0) };
        tile.Children.Add(stack);
        if (showDivider)
        {
            // Inset vertically so the divider stops short of the card's rounded edges.
            tile.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 1,
                Fill = new SolidColorBrush(ColorHelper.FromArgb(45, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, -3, 6),
            });
        }
        return tile;
    }

    private void AddDetail(Grid grid, int row, string title, string description, string key)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
        UpdateWindowMetric("claude5h", snapshot?.Claude?.SessionPercent);
        UpdateWindowMetric("claudeWeek", snapshot?.Claude?.WeekPercent);
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
            ["updated"] = snapshot?.UpdatedAt is { } updated ? updated.ToLocalTime().ToString("HH:mm:ss") : "Not yet",
        };
        foreach (var text in _flyoutValues)
        {
            if (text.Tag is string key && values.TryGetValue(key, out var value)) text.Text = value;
        }
        UpdateCharts(snapshot?.Claude);
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
        var width = GraphWidth(graph);
        for (var i = 0; i < series.Count; i++)
        {
            var x = series.Count == 1 ? width : width * i / (series.Count - 1);
            points.Add(new Point(x, 5.5 - series[i] * 0.05));
        }
        graph.Points = points;
    }

    private Snapshot? LoadSnapshot()
    {
        // Skip GPU counters and the Claude usage API entirely when nothing would display them.
        var snapshot = _telemetry.Sample(
            includeGpu: _settings.IsVisible("gpu") || _settings.IsVisible("vram"),
            includeClaude: _settings.IsVisible("claude5h") || _settings.IsVisible("claudeWeek"));

        if (snapshot.Claude is { FetchedAt: { } fetchedAt } claude)
        {
            if (claude.SessionResetsAt is { } sessionReset && claude.SessionPercent is { } session)
                _claudeHistory.Record("claude5h", sessionReset, WindowLength("claude5h")!.Value, fetchedAt, session);
            if (claude.WeekResetsAt is { } weekReset && claude.WeekPercent is { } week)
                _claudeHistory.Record("claudeWeek", weekReset, WindowLength("claudeWeek")!.Value, fetchedAt, week);
        }
        return snapshot;
    }

    private static TimeSpan? WindowLength(string key) => key switch
    {
        "claude5h" => TimeSpan.FromHours(5),
        "claudeWeek" => TimeSpan.FromDays(7),
        _ => null,
    };

    // Span the tile's real width so the line sits centred, whatever the tile count. Measured from the
    // tile's stack because a Polyline's own width follows its points, which for a window line stop at "now".
    private static double GraphWidth(Polyline graph) =>
        graph.Parent is FrameworkElement { ActualWidth: > 0 } parent ? parent.ActualWidth : 62.0;

    /// <summary>Claude Code tiles: the line spans the whole limit window, filling in from the left as it passes.</summary>
    private void UpdateWindowMetric(string key, double? value)
    {
        SetPreview(key, Percent(value));
        if (!_previewGraphs.TryGetValue(key, out var graph)) return;
        graph.Points = WindowPoints(_claudeHistory.Get(key), WindowLength(key)!.Value, GraphWidth(graph), 0.5, graph.Height - 0.5);
    }

    private static PointCollection WindowPoints(UsageWindowHistory? window, TimeSpan length, double width, double top, double bottom)
    {
        var points = new PointCollection();
        if (window is null || window.Points.Count == 0) return points;
        var start = window.ResetsAt - length;
        // Usage restarts from zero with each window, so the line starts at 0% at the window's start.
        points.Add(new Point(0, ChartY(0, top, bottom)));
        foreach (var point in window.Points)
            points.Add(new Point(WindowFraction(point.At, start, length) * width, ChartY(point.Percent, top, bottom)));
        return points;
    }

    private static double WindowFraction(DateTimeOffset at, DateTimeOffset start, TimeSpan length) =>
        Math.Clamp((at - start) / length, 0, 1);

    private static double ChartY(double percent, double top, double bottom) =>
        bottom - Math.Clamp(percent, 0, 100) / 100 * (bottom - top);

    private void BuildFlyoutCharts()
    {
        if (_flyoutCharts is null) return;
        _flyoutCharts.Children.Clear();
        _charts.Clear();
        foreach (var stat in VisibleStats.Where(s => WindowLength(s.Key) is not null))
            _flyoutCharts.Children.Add(CreateWindowChart(stat));
    }

    private UIElement CreateWindowChart(StatDefinition stat)
    {
        var title = new TextBlock { Text = stat.Description, FontSize = 12, FontWeight = FontWeights.SemiBold };
        var reset = new TextBlock { FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(reset, 1);
        header.Children.Add(title);
        header.Children.Add(reset);

        var canvas = new Canvas { Width = ChartWidth, Height = ChartHeight };
        canvas.Children.Add(new Line
        {
            X1 = 0, X2 = ChartWidth, Y1 = ChartY(50, ChartTop, ChartBottom), Y2 = ChartY(50, ChartTop, ChartBottom),
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(30, 255, 255, 255)), StrokeThickness = 1,
        });
        canvas.Children.Add(new Line
        {
            X1 = 0, X2 = ChartWidth, Y1 = ChartY(100, ChartTop, ChartBottom), Y2 = ChartY(100, ChartTop, ChartBottom),
            Stroke = new SolidColorBrush(LimitColor), StrokeThickness = 1, Opacity = 0.7,
            StrokeDashArray = new DoubleCollection { 3, 3 },
        });
        var now = new Line
        {
            Y1 = ChartTop, Y2 = ChartBottom,
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(70, 255, 255, 255)), StrokeThickness = 1,
        };
        var projection = new Polyline
        {
            Stroke = new SolidColorBrush(stat.Color), StrokeThickness = 1.4, Opacity = 0.75,
            StrokeDashArray = new DoubleCollection { 3, 2 },
        };
        var usage = new Polyline { Stroke = new SolidColorBrush(stat.Color), StrokeThickness = 1.6 };
        canvas.Children.Add(now);
        canvas.Children.Add(projection);
        canvas.Children.Add(usage);

        var frame = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(14, 255, 255, 255)),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = canvas,
        };
        var summary = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

        _charts[stat.Key] = new WindowChart(reset, usage, projection, now, summary);

        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(header);
        section.Children.Add(frame);
        section.Children.Add(summary);
        return section;
    }

    private void UpdateCharts(ClaudeStatus? claude)
    {
        foreach (var (key, chart) in _charts)
        {
            var length = WindowLength(key)!.Value;
            var (percent, resetsAt) = key == "claude5h"
                ? (claude?.SessionPercent, claude?.SessionResetsAt)
                : (claude?.WeekPercent, claude?.WeekResetsAt);

            chart.Usage.Points = WindowPoints(_claudeHistory.Get(key), length, ChartWidth, ChartTop, ChartBottom);
            chart.Projection.Points = new PointCollection();
            chart.Summary.ClearValue(TextBlock.ForegroundProperty);

            if (percent is not { } used || resetsAt is not { } reset)
            {
                chart.Now.Visibility = Visibility.Collapsed;
                chart.Reset.Text = "";
                chart.Summary.Text = percent is null ? "No data yet" : "No usage in this window yet";
                continue;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var start = reset - length;
            var elapsed = WindowFraction(nowUtc, start, length);
            var nowX = elapsed * ChartWidth;
            chart.Now.X1 = chart.Now.X2 = nowX;
            chart.Now.Visibility = Visibility.Visible;
            chart.Reset.Text = $"resets {FormatTime(reset, length)}";

            var status = $"{used:0}% used · {FormatDuration(reset - nowUtc)} left";
            if (used >= 100)
            {
                chart.Summary.Text = $"{status} · limit reached";
                chart.Summary.Foreground = new SolidColorBrush(LimitColor);
            }
            else if (elapsed < MinElapsedForProjection || used <= 0)
            {
                chart.Summary.Text = $"{status} · too early to project";
            }
            else
            {
                // Straight-line pace from the window's start: used / elapsed fraction.
                var projected = used / elapsed;
                var from = new Point(nowX, ChartY(used, ChartTop, ChartBottom));
                if (projected < 100)
                {
                    chart.Projection.Points = new PointCollection { from, new Point(ChartWidth, ChartY(projected, ChartTop, ChartBottom)) };
                    chart.Summary.Text = $"{status} · on pace for {projected:0}% at reset";
                }
                else
                {
                    var hitFraction = elapsed * 100 / used;
                    var hitAt = start + length * hitFraction;
                    chart.Projection.Points = new PointCollection { from, new Point(hitFraction * ChartWidth, ChartY(100, ChartTop, ChartBottom)) };
                    chart.Summary.Text = $"{status} · on pace to hit the limit around {FormatTime(hitAt, length)}";
                    chart.Summary.Foreground = new SolidColorBrush(LimitColor);
                }
            }
        }
    }

    // Weekly times need the day; 5-hour ones are always within the next few hours.
    private static string FormatTime(DateTimeOffset at, TimeSpan windowLength) =>
        at.ToLocalTime().ToString(windowLength > TimeSpan.FromDays(1) ? "ddd HH:mm" : "HH:mm");

    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m";
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

    private sealed record WindowChart(TextBlock Reset, Polyline Usage, Polyline Projection, Line Now, TextBlock Summary);

    private sealed record StatDefinition(
        string Key, string TileTitle, string FlyoutTitle, string Description, Windows.UI.Color Color);

    /// <summary>
    /// Per-instance settings JSON. Stores hidden keys so newly added stats default to visible.
    /// A fresh instance shows only the Claude Code tiles, keeping the default footprint small enough to place.
    /// </summary>
    private sealed class WidgetSettings
    {
        [JsonPropertyName("hidden")]
        public List<string> Hidden { get; set; } = new() { "cpu", "memory", "gpu", "vram" };

        public bool IsVisible(string key) => !Hidden.Contains(key);

        public void SetVisible(string key, bool visible)
        {
            Hidden.Remove(key);
            if (!visible) Hidden.Add(key);
        }

        public static WidgetSettings FromJson(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new WidgetSettings()
                    : JsonSerializer.Deserialize<WidgetSettings>(json) ?? new WidgetSettings();
            }
            catch
            {
                return new WidgetSettings();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this);
    }

    internal sealed class Snapshot
    {
        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; init; }
        public SystemStatus? System { get; init; }
        public GpuStatus? Gpu { get; init; }
        public ClaudeStatus? Claude { get; init; }
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
        public DateTimeOffset? SessionResetsAt { get; init; }
        public DateTimeOffset? WeekResetsAt { get; init; }
        public DateTimeOffset? FetchedAt { get; init; }
    }
}
