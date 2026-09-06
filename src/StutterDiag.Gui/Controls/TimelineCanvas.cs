using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StutterDiag.Gui.Models;

namespace StutterDiag.Gui.Controls;

/// <summary>
/// Lightweight timeline drawn directly with <see cref="DrawingContext"/> — no chart library
/// (docs/ARCHITECTURE.md §2). Horizontal axis is seconds from the session start. Supports
/// wheel-zoom about the cursor, left-drag pan, and click-to-select the nearest stutter mark.
/// </summary>
public sealed class TimelineCanvas : FrameworkElement
{
    private const double MinPixelsPerSecond = 0.05;
    private const double MaxPixelsPerSecond = 400.0;
    private const double AxisHeight = 18.0;
    private const double HitTolerancePx = 8.0;

    private Point _lastDragPoint;
    private bool _dragging;

    public TimelineCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;

        // Keep the CollectionChanged subscription tied to the element lifetime so a
        // recreated view (DataTemplate re-realisation) does not leak handlers on a
        // long-lived ItemsSource.
        Loaded += (_, _) => Subscribe(Items);
        Unloaded += (_, _) => Unsubscribe(Items);
    }

    private void Subscribe(IEnumerable? items)
    {
        if (items is INotifyCollectionChanged n) n.CollectionChanged += OnCollectionChanged;
    }

    private void Unsubscribe(IEnumerable? items)
    {
        if (items is INotifyCollectionChanged n) n.CollectionChanged -= OnCollectionChanged;
    }

    // ---- Dependency properties --------------------------------------------------------

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(TimelineCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty TotalSecondsProperty = DependencyProperty.Register(
        nameof(TotalSeconds), typeof(double), typeof(TimelineCanvas),
        new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender, OnViewChanged));

    public double TotalSeconds
    {
        get => (double)GetValue(TotalSecondsProperty);
        set => SetValue(TotalSecondsProperty, value);
    }

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(TimelineCanvas),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender, OnViewChanged));

    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond));
    }

    public static readonly DependencyProperty OffsetSecondsProperty = DependencyProperty.Register(
        nameof(OffsetSeconds), typeof(double), typeof(TimelineCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnViewChanged));

    /// <summary>Time (seconds from session start) shown at the left edge.</summary>
    public double OffsetSeconds
    {
        get => (double)GetValue(OffsetSecondsProperty);
        set => SetValue(OffsetSecondsProperty, value);
    }

    public static readonly DependencyProperty SelectedStutterIdProperty = DependencyProperty.Register(
        nameof(SelectedStutterId), typeof(long), typeof(TimelineCanvas),
        new FrameworkPropertyMetadata(-1L,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public long SelectedStutterId
    {
        get => (long)GetValue(SelectedStutterIdProperty);
        set => SetValue(SelectedStutterIdProperty, value);
    }

    // ---- Data plumbing --------------------------------------------------------------

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (TimelineCanvas)d;
        self.Unsubscribe(e.OldValue as IEnumerable);
        if (self.IsLoaded) self.Subscribe(e.NewValue as IEnumerable);
        self.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private static void OnViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimelineCanvas)d).InvalidateVisual();

    private IReadOnlyList<TimelineItem> Snapshot() =>
        Items?.OfType<TimelineItem>().OrderBy(i => i.OffsetSeconds).ToList() ?? (IReadOnlyList<TimelineItem>)Array.Empty<TimelineItem>();

    // ---- Interaction --------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ActualWidth <= 0) return;

        double mouseX = e.GetPosition(this).X;
        double anchorTime = OffsetSeconds + mouseX / PixelsPerSecond;

        double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        double newPps = Clamp(PixelsPerSecond * factor, MinPixelsPerSecond, MaxPixelsPerSecond);

        PixelsPerSecond = newPps;
        OffsetSeconds = anchorTime - mouseX / newPps;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var pos = e.GetPosition(this);
        SelectNearest(pos);

        _dragging = true;
        _lastDragPoint = pos;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || PixelsPerSecond <= 0) return;

        var pos = e.GetPosition(this);
        double dx = pos.X - _lastDragPoint.X;
        _lastDragPoint = pos;
        OffsetSeconds -= dx / PixelsPerSecond;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void SelectNearest(Point p)
    {
        var items = Snapshot();
        if (items.Count == 0 || PixelsPerSecond <= 0) return;

        TimelineItem? best = null;
        double bestDist = double.MaxValue;
        foreach (var it in items)
        {
            double x = (it.OffsetSeconds - OffsetSeconds) * PixelsPerSecond;
            double dist = Math.Abs(x - p.X);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = it;
            }
        }

        if (best is not null && bestDist <= Math.Max(HitTolerancePx, PixelsPerSecond * 0.5))
            SelectedStutterId = best.SyntheticId;
    }

    /// <summary>Re-centre on the next (+1) or previous (-1) stutter relative to the viewport centre.</summary>
    public void JumpToStutter(int direction)
    {
        var stutters = Snapshot().Where(i => i.IsStutter).OrderBy(i => i.OffsetSeconds).ToList();
        if (stutters.Count == 0 || ActualWidth <= 0) return;

        double centreTime = OffsetSeconds + (ActualWidth / 2.0) / PixelsPerSecond;
        TimelineItem? target = direction >= 0
            ? stutters.FirstOrDefault(s => s.OffsetSeconds > centreTime + 1e-6)
            : stutters.LastOrDefault(s => s.OffsetSeconds < centreTime - 1e-6);

        target ??= direction >= 0 ? stutters[^1] : stutters[0];

        OffsetSeconds = target.OffsetSeconds - (ActualWidth / 2.0) / PixelsPerSecond;
        SelectedStutterId = target.SyntheticId;
    }

    /// <summary>Fit the whole session into the current width.</summary>
    public void ZoomToFit()
    {
        if (ActualWidth <= 0 || TotalSeconds <= 0) return;
        PixelsPerSecond = Clamp(ActualWidth / TotalSeconds, MinPixelsPerSecond, MaxPixelsPerSecond);
        OffsetSeconds = 0;
    }

    // ---- Layout & rendering ---------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        // A bare FrameworkElement measures to 0x0 by default; fill the container instead.
        double w = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 160 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var bg = ResBrush("Brush.Surface", Color.FromRgb(0xFA, 0xFA, 0xFA));
        var axisBrush = ResBrush("Brush.Muted", Color.FromRgb(0x88, 0x88, 0x88));
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)), 1);
        var textBrush = ResBrush("Brush.Text", Color.FromRgb(0x22, 0x22, 0x22));

        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        if (w <= 0 || h <= 0 || PixelsPerSecond <= 0) return;

        double laneTop = AxisHeight;
        double laneBottom = h;

        // Grid + axis labels at a "nice" second interval.
        double targetPxPerLabel = 90;
        double rawStep = targetPxPerLabel / PixelsPerSecond;
        double step = NiceStep(rawStep);
        double firstTick = Math.Ceiling(OffsetSeconds / step) * step;

        for (double t = firstTick; ; t += step)
        {
            double x = (t - OffsetSeconds) * PixelsPerSecond;
            if (x > w) break;
            if (x < 0) continue;
            dc.DrawLine(gridPen, new Point(x, laneTop), new Point(x, laneBottom));
            var label = FormatSeconds(t);
            var ft = MakeText(label, 10, axisBrush);
            dc.DrawText(ft, new Point(Math.Min(x + 3, w - ft.Width - 2), 2));
        }

        // Marks.
        foreach (var it in Snapshot())
        {
            double x = (it.OffsetSeconds - OffsetSeconds) * PixelsPerSecond;
            if (x < -20 || x > w + 20) continue;

            bool selected = it.SyntheticId == SelectedStutterId;
            var (fill, isBar) = MarkStyle(it);

            if (isBar && it.DurationMs is { } ms && ms > 0)
            {
                double barW = Math.Max(2.0, (ms / 1000.0) * PixelsPerSecond);
                var rect = new Rect(x, laneTop + 6, barW, Math.Max(6, laneBottom - laneTop - 12));
                dc.DrawRectangle(fill, selected ? new Pen(textBrush, 2) : null, rect);
            }
            else
            {
                double r = selected ? 6 : 4;
                dc.DrawEllipse(fill, selected ? new Pen(textBrush, 2) : null,
                    new Point(x, (laneTop + laneBottom) / 2), r, r);
            }

            if (selected)
            {
                var caption = MakeText(
                    string.IsNullOrWhiteSpace(it.Label) ? it.Kind : it.Label, 10, textBrush);
                double cx = Math.Min(x + 8, Math.Max(0, w - caption.Width - 2));
                dc.DrawText(caption, new Point(cx, laneTop + 2));
            }
        }
    }

    private (Brush fill, bool isBar) MarkStyle(TimelineItem it)
    {
        string k = it.Kind.ToLowerInvariant();
        if (k.Contains("stutter")) return (ResBrush("Brush.Bad", Color.FromRgb(0xC6, 0x28, 0x28)), true);
        if (k.Contains("mark")) return (ResBrush("Brush.Accent", Color.FromRgb(0x15, 0x65, 0xC0)), true);
        if (k.Contains("tpm") || k.Contains("tbs")) return (ResBrush("Brush.Warn", Color.FromRgb(0xB4, 0x6A, 0x00)), false);
        if (k.Contains("whea") || k.Contains("error")) return (new SolidColorBrush(Color.FromRgb(0x8E, 0x24, 0xAA)), false);
        if (k.Contains("dpc") || k.Contains("isr")) return (new SolidColorBrush(Color.FromRgb(0x00, 0x83, 0x8F)), false);
        return (ResBrush("Brush.Muted", Color.FromRgb(0x9E, 0x9E, 0x9E)), false);
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double nice = norm switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };
        return Math.Max(0.001, nice * mag);
    }

    private static string FormatSeconds(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private FormattedText MakeText(string text, double size, Brush brush)
    {
        double dpi = 1.0;
        try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { /* not in a visual tree yet */ }
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            dpi);
    }

    private static Brush ResBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
