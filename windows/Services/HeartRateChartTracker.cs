using System.Diagnostics;

namespace HeartRate.Services;

/// <summary>
/// 实时心率曲线数据：保存最近 <see cref="WindowSeconds"/> 秒的 (时间, bpm) 采样点。
/// 与手机端 SessionChartTracker 的无 RR 回退分支一致：以首个有效点为时间零点，
/// 无效值（传感器接触丢失）不入图，避免曲线尖刺。仅在 UI 线程调用。
/// </summary>
public sealed class HeartRateChartTracker
{
    public const double WindowSeconds = 60.0;
    private const int MaxPoints = 10_000;
    private const int MinValidBpm = 30;
    private const int MaxValidBpm = 220;

    public readonly record struct ChartPoint(double TimeSec, int Bpm);

    private readonly List<ChartPoint> _points = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _startSec = -1;

    /// <summary>采样点或会话被清空后触发（UI 线程）。</summary>
    public event EventHandler? Changed;

    /// <summary>窗口内的采样点，按时间升序。</summary>
    public IReadOnlyList<ChartPoint> Points => _points;

    /// <summary>本次会话首个有效采样的时刻（UTC）；曲线横轴据此换算成北京时间。</summary>
    public DateTimeOffset? StartTime { get; private set; }

    public void Add(int bpm)
    {
        if (bpm < MinValidBpm || bpm > MaxValidBpm) return;

        var now = _clock.Elapsed.TotalSeconds;
        if (_startSec < 0)
        {
            _startSec = now;
            StartTime = DateTimeOffset.UtcNow;
        }
        var t = now - _startSec;
        _points.Add(new ChartPoint(t, bpm));

        var windowStart = t - WindowSeconds;
        var drop = 0;
        while (drop < _points.Count - 1 && _points[drop].TimeSec < windowStart) drop++;
        if (_points.Count - drop > MaxPoints) drop = _points.Count - MaxPoints;
        if (drop > 0) _points.RemoveRange(0, drop);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>新会话/断开时清空，下次采样重新以 0 秒起算。</summary>
    public void Clear()
    {
        if (_points.Count == 0 && _startSec < 0) return;
        _points.Clear();
        _startSec = -1;
        StartTime = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
