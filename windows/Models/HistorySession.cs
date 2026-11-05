using System.Text.Json.Serialization;

namespace HeartRate.Models;

/// <summary>
/// 一次连接会话的历史记录：统计值（均值/极值）按全部采样累计，
/// 曲线采样点 T/V 最多保留 <c>HistoryService.MaxSamples</c> 个（超出后按 2 倍步长抽稀）。
/// </summary>
public sealed class HistorySession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>首个有效心率的时刻（UTC 起算，显示时转北京时间）。</summary>
    public DateTimeOffset StartTime { get; set; }

    /// <summary>最后一个有效心率的时刻；进行中的会话随采样不断更新。</summary>
    public DateTimeOffset EndTime { get; set; }

    public string DeviceName { get; set; } = "";

    /// <summary>数据来源：蓝牙 / 局域网。</summary>
    public string Source { get; set; } = "";

    /// <summary>有效采样总数（不受曲线抽稀影响）。</summary>
    public int Count { get; set; }

    /// <summary>全部采样的 bpm 之和，用于精确计算均值与跨会话加权均值。</summary>
    public long Sum { get; set; }

    public int Min { get; set; }
    public int Max { get; set; }

    /// <summary>当前曲线采样的最小时间间隔（秒），抽稀时翻倍。</summary>
    public int Step { get; set; } = 1;

    /// <summary>曲线采样点：相对 StartTime 的秒数。</summary>
    public List<int> T { get; set; } = new();

    /// <summary>曲线采样点：bpm，与 T 一一对应。</summary>
    public List<int> V { get; set; } = new();

    [JsonIgnore]
    public double Avg => Count > 0 ? (double)Sum / Count : 0;

    [JsonIgnore]
    public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
}

/// <summary>history.json 的根对象。</summary>
public sealed class HistoryFile
{
    public int Version { get; set; } = 1;
    public List<HistorySession> Sessions { get; set; } = new();
}
