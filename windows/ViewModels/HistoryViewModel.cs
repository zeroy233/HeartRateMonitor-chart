using System.Collections.ObjectModel;
using HeartRate.Helpers;
using HeartRate.Models;
using HeartRate.Services;
using Microsoft.UI.Dispatching;

namespace HeartRate.ViewModels
{
    /// <summary>历史列表中的一行：一次连接会话的摘要。进行中的会话由定时刷新更新。</summary>
    public partial class HistoryItem : ObservableObject
    {
        public HistorySession Session { get; }
        public string Id => Session.Id;

        [ObservableProperty]
        private string _title = "";

        [ObservableProperty]
        private string _timeText = "";

        [ObservableProperty]
        private string _statsText = "";

        [ObservableProperty]
        private bool _inProgress;

        public HistoryItem(HistorySession session, bool inProgress)
        {
            Session = session;
            Update(inProgress);
        }

        /// <summary>按会话当前数据重算显示文本（进行中的会话在不断增长）。</summary>
        public void Update(bool inProgress)
        {
            var s = Session;
            InProgress = inProgress;
            Title = string.IsNullOrEmpty(s.DeviceName) ? Loc.GetString("History_UnknownDevice") : s.DeviceName;

            var start = BeijingTime.ToBeijing(s.StartTime);
            var end = BeijingTime.ToBeijing(s.EndTime);
            var endText = inProgress ? Loc.GetString("History_InProgress")
                : end.Date == start.Date ? end.ToString("HH:mm:ss") : end.ToString("MM-dd HH:mm:ss");
            TimeText = $"{start:yyyy-MM-dd HH:mm:ss} – {endText}";

            StatsText = Loc.Format("History_StatsFormat",
                Math.Round(s.Avg).ToString("0"), s.Min, s.Max, s.Count);
        }
    }

    /// <summary>
    /// 历史记录页：会话列表、选中会话的详情（均值/极值/时长 + 曲线）、
    /// 以及按北京时间划分的“今日统计”，并显示实时的北京时间。
    /// </summary>
    public partial class HistoryViewModel : ObservableObject
    {
        private readonly HistoryService _history = HistoryService.Instance;
        private readonly DispatcherQueue _uiDispatcher;
        private readonly DispatcherQueueTimer _timer;
        private int _tick;

        public ObservableCollection<HistoryItem> Items { get; } = new();

        /// <summary>选中会话的曲线数据与起点，由页面读取后交给曲线控件。</summary>
        public IReadOnlyList<HeartRateChartTracker.ChartPoint>? SelectedPoints { get; private set; }
        public DateTimeOffset? SelectedOrigin { get; private set; }

        /// <summary>详情区内容变化（切换选中项或进行中会话刷新）。</summary>
        public event EventHandler? DetailChanged;

        [ObservableProperty]
        private string _nowText = "";

        [ObservableProperty]
        private bool _isEmpty = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
        private HistoryItem? _selectedItem;

        // 今日统计（北京时间当天开始的全部会话）
        [ObservableProperty] private string _todayTitle = "";
        [ObservableProperty] private bool _hasToday;
        [ObservableProperty] private string _todayAvgText = "--";
        [ObservableProperty] private string _todayMaxText = "--";
        [ObservableProperty] private string _todayMinText = "--";
        [ObservableProperty] private string _todayDurationText = "--";
        [ObservableProperty] private string _todayCountText = "--";

        // 选中会话详情
        [ObservableProperty] private bool _hasDetail;
        [ObservableProperty] private string _detailTitle = "";
        [ObservableProperty] private string _detailAvgText = "--";
        [ObservableProperty] private string _detailMaxText = "--";
        [ObservableProperty] private string _detailMinText = "--";
        [ObservableProperty] private string _detailDurationText = "--";
        [ObservableProperty] private string _detailCountText = "--";
        [ObservableProperty] private string _detailRangeText = "--";

        public HistoryViewModel(DispatcherQueue uiDispatcher)
        {
            _uiDispatcher = uiDispatcher;
            NowText = FormatNow();
            _history.Changed += (_, _) => _uiDispatcher.TryEnqueue(Refresh);

            _timer = uiDispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;
            _timer.Start();

            Refresh();
        }

        private static string FormatNow() => BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        private bool CanDeleteSelected() => SelectedItem is { InProgress: false };

        [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
        private void DeleteSelected()
        {
            if (SelectedItem is { InProgress: false } item) _history.Delete(item.Id);
        }

        [RelayCommand]
        private void ClearAll() => _history.Clear();

        partial void OnSelectedItemChanged(HistoryItem? value) => UpdateDetail();

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            NowText = FormatNow();
            // 进行中的会话每 3 秒刷新一次列表行、详情和今日统计
            if (++_tick % 3 != 0) return;
            if (_history.Current is null) return;
            var live = Items.FirstOrDefault(i => i.InProgress);
            if (live is null) return;
            live.Update(inProgress: true);
            UpdateToday();
            if (ReferenceEquals(SelectedItem, live)) UpdateDetail();
        }

        /// <summary>重建会话列表（会话开始/结束/删除后），并尽量保持当前选中项。</summary>
        public void Refresh()
        {
            var keepId = SelectedItem?.Id;
            var current = _history.Current;

            Items.Clear();
            foreach (var s in _history.Snapshot())
                Items.Add(new HistoryItem(s, ReferenceEquals(s, current)));

            IsEmpty = Items.Count == 0;
            SelectedItem = Items.FirstOrDefault(i => i.Id == keepId) ?? Items.FirstOrDefault();
            UpdateToday();
            UpdateDetail();
        }

        private void UpdateToday()
        {
            var today = BeijingTime.Now.Date;
            TodayTitle = Loc.Format("History_TodayTitle", today.ToString("yyyy-MM-dd"));

            var todays = _history.Snapshot()
                .Where(s => BeijingTime.ToBeijing(s.StartTime).Date == today)
                .ToList();

            HasToday = todays.Count > 0;
            if (!HasToday)
            {
                TodayAvgText = TodayMaxText = TodayMinText = TodayDurationText = TodayCountText = "--";
                return;
            }

            long sum = 0;
            long count = 0;
            var duration = TimeSpan.Zero;
            foreach (var s in todays)
            {
                sum += s.Sum;
                count += s.Count;
                duration += s.Duration;
            }

            // 跨会话均值按采样数加权，而不是简单平均各会话均值
            TodayAvgText = Bpm(Math.Round((double)sum / count));
            TodayMaxText = Bpm(todays.Max(s => s.Max));
            TodayMinText = Bpm(todays.Min(s => s.Min));
            TodayDurationText = FormatDuration(duration);
            TodayCountText = Loc.Format("History_CountFormat", todays.Count, count);
        }

        private void UpdateDetail()
        {
            var item = SelectedItem;
            HasDetail = item is not null;

            if (item is null)
            {
                SelectedPoints = null;
                SelectedOrigin = null;
                DetailTitle = "";
                DetailAvgText = DetailMaxText = DetailMinText = DetailDurationText = DetailCountText = DetailRangeText = "--";
            }
            else
            {
                var s = item.Session;
                DetailTitle = item.Title;
                DetailAvgText = Bpm(Math.Round(s.Avg));
                DetailMaxText = Bpm(s.Max);
                DetailMinText = Bpm(s.Min);
                DetailDurationText = FormatDuration(s.Duration);
                DetailCountText = s.Count.ToString();

                var start = BeijingTime.ToBeijing(s.StartTime);
                var end = BeijingTime.ToBeijing(s.EndTime);
                DetailRangeText = $"{start:MM-dd HH:mm:ss} – {end:HH:mm:ss}";

                var points = new List<HeartRateChartTracker.ChartPoint>(s.T.Count);
                for (var i = 0; i < s.T.Count; i++)
                    points.Add(new HeartRateChartTracker.ChartPoint(s.T[i], s.V[i]));
                SelectedPoints = points;
                SelectedOrigin = s.StartTime;
            }

            DetailChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string Bpm(double value) => $"{value:0} bpm";

        private static string FormatDuration(TimeSpan d) =>
            $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}";
    }
}
