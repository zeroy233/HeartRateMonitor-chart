namespace HeartRate.Views
{
    public sealed partial class HistoryControl
    {
        public HistoryViewModel ViewModel { get; set; } = null!;

        public HistoryControl()
        {
            this.InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.DetailChanged += OnDetailChanged;
            UpdateChart();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.DetailChanged -= OnDetailChanged;
        }

        private void OnDetailChanged(object? sender, EventArgs e) => UpdateChart();

        private void UpdateChart() => DetailChart.ShowSamples(ViewModel.SelectedPoints, ViewModel.SelectedOrigin);

        /// <summary>确认清空后收起弹出层（命令本身由绑定执行）。</summary>
        private void OnClearConfirmClick(object sender, RoutedEventArgs e) => ClearFlyout.Hide();
    }
}
