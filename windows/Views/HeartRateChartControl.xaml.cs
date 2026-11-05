using HeartRate.Helpers;
using HeartRate.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace HeartRate.Views;

/// <summary>
/// 实时心率曲线（对应手机端 RealtimeChart）：最近 60 秒、平滑曲线 + 红色渐变填充，
/// Y 轴按数据极值 ±10 取整到 10 的倍数，窗口内最高/最低值画虚线参考线并标注。
/// 纯 Canvas 绘制，不引入额外图表库。
/// </summary>
public sealed partial class HeartRateChartControl : UserControl
{
    private static readonly Color LineColor = Color.FromArgb(255, 0xF4, 0x43, 0x36);

    private const double PadLeft = 34;
    private const double PadRight = 10;
    private const double PadTop = 14;
    private const double PadBottom = 22;

    public static readonly DependencyProperty TrackerProperty = DependencyProperty.Register(
        nameof(Tracker), typeof(HeartRateChartTracker), typeof(HeartRateChartControl),
        new PropertyMetadata(null, OnTrackerChanged));

    public HeartRateChartTracker? Tracker
    {
        get => (HeartRateChartTracker?)GetValue(TrackerProperty);
        set => SetValue(TrackerProperty, value);
    }

    // 历史模式：由 ShowSamples 直接给定采样点，优先于实时 Tracker
    private IReadOnlyList<HeartRateChartTracker.ChartPoint>? _staticPoints;
    private DateTimeOffset? _staticOrigin;

    public HeartRateChartControl()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Redraw();
        Unloaded += (_, _) => Detach(Tracker);
    }

    /// <summary>
    /// 显示一份固定的历史采样（横轴为从 <paramref name="origin"/> 起算的北京时间）；
    /// 传 null 清空。历史页使用，实时卡片仍走 <see cref="Tracker"/>。
    /// </summary>
    public void ShowSamples(IReadOnlyList<HeartRateChartTracker.ChartPoint>? points, DateTimeOffset? origin)
    {
        _staticPoints = points;
        _staticOrigin = origin;
        Redraw();
    }

    private static void OnTrackerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (HeartRateChartControl)d;
        self.Detach(e.OldValue as HeartRateChartTracker);
        if (e.NewValue is HeartRateChartTracker next) next.Changed += self.OnTrackerUpdated;
        self.Redraw();
    }

    private void Detach(HeartRateChartTracker? tracker)
    {
        if (tracker is not null) tracker.Changed -= OnTrackerUpdated;
    }

    private void OnTrackerUpdated(object? sender, EventArgs e) => Redraw();

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        Plot.Children.Clear();

        var points = _staticPoints ?? Tracker?.Points;
        var origin = _staticPoints is not null ? _staticOrigin : Tracker?.StartTime;
        var enough = points is { Count: >= 2 };
        EmptyText.Visibility = enough ? Visibility.Collapsed : Visibility.Visible;

        var w = Plot.ActualWidth;
        var h = Plot.ActualHeight;
        if (!enough || points is null || w < 120 || h < 80) return;

        var dark = ActualTheme == ElementTheme.Dark;
        var textBrush = new SolidColorBrush(dark ? Color.FromArgb(0xB3, 255, 255, 255) : Color.FromArgb(0x99, 0, 0, 0));
        var gridBrush = new SolidColorBrush(dark ? Color.FromArgb(0x24, 255, 255, 255) : Color.FromArgb(0x1F, 0, 0, 0));
        var labelBg = new SolidColorBrush(dark ? Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A) : Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE));

        // ── 坐标范围 ────────────────────────────────────────────────────
        var minV = double.MaxValue;
        var maxV = 0.0;
        foreach (var p in points)
        {
            if (p.Bpm < minV) minV = p.Bpm;
            if (p.Bpm > maxV) maxV = p.Bpm;
        }

        var yMin = Math.Floor((minV - 10) / 10) * 10;
        var yMax = Math.Ceiling((maxV + 10) / 10) * 10;
        // 手机端固定 10 bpm 一格；窗口内波动很大时放宽到 20，避免网格过密
        var step = yMax - yMin > 80 ? 20.0 : 10.0;
        yMin = Math.Floor(yMin / step) * step;
        yMax = Math.Ceiling(yMax / step) * step;

        var x0 = points[0].TimeSec;
        var span = Math.Max(points[^1].TimeSec - x0, 1.0);
        var plotW = w - PadLeft - PadRight;
        var plotH = h - PadTop - PadBottom;
        var bottom = PadTop + plotH;

        double PX(double t) => PadLeft + (t - x0) / span * plotW;
        double PY(double v) => PadTop + (yMax - v) / (yMax - yMin) * plotH;

        // ── 网格 + Y 轴刻度 ─────────────────────────────────────────────
        for (var v = yMin; v <= yMax + 0.001; v += step)
        {
            var y = PY(v);
            AddLine(PadLeft, y, w - PadRight, y, gridBrush, 1, dashed: false);
            AddText(((int)v).ToString(), 0, y - 7, PadLeft - 6, textBrush, 10, TextAlignment.Right);
        }

        // ── X 轴时间刻度：北京时间时钟（跨度 ≥1 小时只显示 HH:mm）────────
        // 拿不到起始时刻时退回会话相对时间 mm:ss
        const int ticks = 4;
        const double labelW = 56;
        var clockFormat = span >= 3600 ? "HH:mm" : "HH:mm:ss";
        for (var i = 0; i < ticks; i++)
        {
            var t = x0 + span * i / (ticks - 1);
            var label = origin is { } o
                ? BeijingTime.ToBeijing(o.AddSeconds(t)).ToString(clockFormat)
                : TimeSpan.FromSeconds(Math.Max(0, t)).ToString(@"mm\:ss");
            var left = Math.Clamp(PX(t) - labelW / 2, 0, w - labelW);
            AddText(label, left, bottom + 4, labelW, textBrush, 10, TextAlignment.Center);
        }

        // ── 渐变填充 + 曲线 ─────────────────────────────────────────────
        var coords = new Point[points.Count];
        for (var i = 0; i < coords.Length; i++)
            coords[i] = new Point(PX(points[i].TimeSec), PY(points[i].Bpm));

        var area = new PathFigure { StartPoint = new Point(coords[0].X, bottom), IsClosed = true };
        area.Segments.Add(new LineSegment { Point = coords[0] });
        AppendSmoothSegments(area.Segments, coords, PadTop, bottom);
        area.Segments.Add(new LineSegment { Point = new Point(coords[^1].X, bottom) });

        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fill.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x59, LineColor.R, LineColor.G, LineColor.B), Offset = 0 });
        fill.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, LineColor.R, LineColor.G, LineColor.B), Offset = 1 });
        Plot.Children.Add(new Path { Data = new PathGeometry { Figures = { area } }, Fill = fill });

        var line = new PathFigure { StartPoint = coords[0], IsClosed = false };
        AppendSmoothSegments(line.Segments, coords, PadTop, bottom);
        Plot.Children.Add(new Path
        {
            Data = new PathGeometry { Figures = { line } },
            Stroke = new SolidColorBrush(LineColor),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        // ── 窗口内最高/最低参考线 ───────────────────────────────────────
        var refBrush = new SolidColorBrush(Color.FromArgb(0x66, LineColor.R, LineColor.G, LineColor.B));
        AddLine(PadLeft, PY(maxV), w - PadRight, PY(maxV), refBrush, 1, dashed: true);
        AddExtremaLabel($"Max {(int)maxV}", w - PadRight, PY(maxV), above: true, labelBg);
        if (minV > 0 && minV != maxV)
        {
            AddLine(PadLeft, PY(minV), w - PadRight, PY(minV), refBrush, 1, dashed: true);
            AddExtremaLabel($"Min {(int)minV}", w - PadRight, PY(minV), above: false, labelBg);
        }
    }

    /// <summary>Catmull-Rom → 三次贝塞尔，等价于手机端 Vico 的 cubic 插值；控制点 Y 夹在绘图区内避免过冲。</summary>
    private static void AppendSmoothSegments(PathSegmentCollection segments, Point[] c, double top, double bottom)
    {
        for (var i = 0; i < c.Length - 1; i++)
        {
            var p0 = c[Math.Max(i - 1, 0)];
            var p1 = c[i];
            var p2 = c[i + 1];
            var p3 = c[Math.Min(i + 2, c.Length - 1)];

            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, top, bottom));
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, top, bottom));
            segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = p2 });
        }
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness, bool dashed)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = thickness };
        // DoubleCollection 只能挂在一个元素上，共用同一实例会抛 E_INVALIDARG，因此每条虚线单独创建
        if (dashed) line.StrokeDashArray = new DoubleCollection { 4, 3 };
        Plot.Children.Add(line);
    }

    private void AddText(string text, double left, double top, double width, Brush foreground, double size, TextAlignment align)
    {
        var tb = new TextBlock
        {
            Text = text,
            Width = width,
            FontSize = size,
            Foreground = foreground,
            TextAlignment = align,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, top);
        Plot.Children.Add(tb);
    }

    private void AddExtremaLabel(string text, double right, double lineY, bool above, Brush background)
    {
        var chip = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new SolidColorBrush(LineColor)
            }
        };
        chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = chip.DesiredSize;
        Canvas.SetLeft(chip, right - size.Width);
        Canvas.SetTop(chip, above ? lineY - size.Height - 1 : lineY + 1);
        Plot.Children.Add(chip);
    }
}
