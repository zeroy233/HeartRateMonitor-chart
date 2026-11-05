using System.Text.Json;
using HeartRate.Models;

namespace HeartRate.Services;

/// <summary>
/// 心率历史记录：每次连接（蓝牙或局域网）从首个有效心率起记为一个会话，断开时结束并落盘。
/// 文件位于 %LOCALAPPDATA%\HeartRate\history.json；进行中的会话每 30 秒自动保存一次，
/// 异常退出最多丢失最近 30 秒。最多保留 <see cref="MaxSessions"/> 条，超出时丢弃最旧的。
/// </summary>
public sealed class HistoryService
{
    public static HistoryService Instance { get; } = new();

    private const int MaxSessions = 200;
    public const int MaxSamples = 2000;
    private const int MinValidBpm = 30;
    private const int MaxValidBpm = 220;
    /// <summary>采样数不足此值的会话（刚连上就断）不入库。</summary>
    private const int MinSessionCount = 2;

    private readonly object _sync = new();
    private readonly List<HistorySession> _sessions; // 已结束，最新在前
    private HistorySession? _current;
    private bool _dirty;
    private readonly System.Threading.Timer _autosave;

    /// <summary>会话开始/结束/删除后触发；单次采样不触发。</summary>
    public event EventHandler? Changed;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeartRate", "history.json");

    private HistoryService()
    {
        _sessions = Load();
        _autosave = new System.Threading.Timer(_ => Flush(), null, 30_000, 30_000);
        // Environment.Exit / 托盘退出等路径不会走窗口关闭，进程退出时兜底保存
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    /// <summary>进行中的会话（无则为 null）。</summary>
    public HistorySession? Current
    {
        get { lock (_sync) return _current; }
    }

    /// <summary>全部会话，最新在前；进行中的会话（若已有足够采样）排在最前。</summary>
    public IReadOnlyList<HistorySession> Snapshot()
    {
        lock (_sync)
        {
            var list = new List<HistorySession>(_sessions.Count + 1);
            if (_current is { Count: >= MinSessionCount } c) list.Add(c);
            list.AddRange(_sessions);
            return list;
        }
    }

    /// <summary>记录一次心率采样（UI 线程调用）。无效值（传感器脱落）忽略。</summary>
    public void Record(int bpm, string deviceName, string source)
    {
        if (bpm < MinValidBpm || bpm > MaxValidBpm) return;

        var started = false;
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (_current is null)
            {
                _current = new HistorySession { StartTime = now, Min = bpm, Max = bpm, Source = source };
                started = true;
            }

            var s = _current;
            s.Count++;
            s.Sum += bpm;
            if (bpm < s.Min) s.Min = bpm;
            if (bpm > s.Max) s.Max = bpm;
            s.EndTime = now;
            // 设备名在连接初期可能还没解析出来，之后补全
            if (!string.IsNullOrEmpty(deviceName) && deviceName != "--") s.DeviceName = deviceName;

            var t = (int)(now - s.StartTime).TotalSeconds;
            if (s.T.Count == 0 || t - s.T[^1] >= s.Step)
            {
                s.T.Add(t);
                s.V.Add(bpm);
                if (s.T.Count > MaxSamples) Decimate(s);
            }
            _dirty = true;
        }

        if (started) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>结束当前会话（断开连接/退出）；采样过少则直接丢弃。</summary>
    public void EndSession()
    {
        var changed = false;
        lock (_sync)
        {
            if (_current is null) return;
            var s = _current;
            _current = null;
            changed = true;
            if (s.Count >= MinSessionCount)
            {
                _sessions.Insert(0, s);
                while (_sessions.Count > MaxSessions) _sessions.RemoveAt(_sessions.Count - 1);
            }
            WriteLocked();
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删除指定的已结束会话；进行中的会话不可删除。</summary>
    public void Delete(string id)
    {
        lock (_sync)
        {
            if (_sessions.RemoveAll(s => s.Id == id) == 0) return;
            WriteLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空全部已结束的会话；进行中的会话保留。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            if (_sessions.Count == 0) return;
            _sessions.Clear();
            WriteLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>有未保存的采样时落盘（定时器与退出时调用）。</summary>
    public void Flush()
    {
        lock (_sync)
        {
            if (_dirty) WriteLocked();
        }
    }

    /// <summary>进程退出：结束进行中的会话并保存，不触发界面事件。</summary>
    public void Shutdown()
    {
        lock (_sync)
        {
            if (_current is { } s)
            {
                _current = null;
                if (s.Count >= MinSessionCount)
                {
                    _sessions.Insert(0, s);
                    while (_sessions.Count > MaxSessions) _sessions.RemoveAt(_sessions.Count - 1);
                }
                _dirty = true;
            }
            if (_dirty) WriteLocked();
        }
    }

    /// <summary>曲线采样点超过上限：隔点保留，并把后续采样间隔翻倍。</summary>
    private static void Decimate(HistorySession s)
    {
        var t = new List<int>(s.T.Count / 2 + 1);
        var v = new List<int>(s.V.Count / 2 + 1);
        for (var i = 0; i < s.T.Count; i += 2)
        {
            t.Add(s.T[i]);
            v.Add(s.V[i]);
        }
        s.T = t;
        s.V = v;
        s.Step *= 2;
    }

    // 调用方须已持有 _sync。进行中的会话也写入，EndTime 即最后一次采样时刻。
    private void WriteLocked()
    {
        try
        {
            var all = new List<HistorySession>(_sessions.Count + 1);
            if (_current is { Count: >= MinSessionCount } c) all.Add(c);
            all.AddRange(_sessions);

            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new HistoryFile { Sessions = all }));
            File.Move(tmp, path, overwrite: true);
            _dirty = false;
        }
        catch (Exception)
        {
            // 磁盘写入失败不能影响心率采集；保持 _dirty，下个周期重试
        }
    }

    private static List<HistorySession> Load()
    {
        var path = FilePath;
        try
        {
            if (!File.Exists(path)) return new List<HistorySession>();
            var file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path));
            var list = file?.Sessions ?? new List<HistorySession>();
            list.RemoveAll(s => s.Count < MinSessionCount || s.T.Count != s.V.Count);
            list.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));
            return list;
        }
        catch (Exception)
        {
            // 文件损坏：改名保留以便排查，从空记录重新开始
            try { File.Move(path, path + ".bad", overwrite: true); } catch (Exception) { }
            return new List<HistorySession>();
        }
    }
}
