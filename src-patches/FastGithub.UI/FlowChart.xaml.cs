using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Wpf;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace FastGithub.UI
{
    /// <summary>
    /// FlowChart.xaml 的交互逻辑
    /// </summary>
    public partial class FlowChart : UserControl
    {
        private readonly LineSeries readSeries = new LineSeries
        {
            Title = "上行速率",
            PointGeometry = null,
            LineSmoothness = 1D,
            Values = new ChartValues<RateTick>()
        };

        private readonly LineSeries writeSeries = new LineSeries()
        {
            Title = "下行速率",
            PointGeometry = null,
            LineSmoothness = 1D,
            Values = new ChartValues<RateTick>()
        };

        private static DateTime GetDateTime(double timestamp) => new DateTime(1970, 1, 1).Add(TimeSpan.FromMilliseconds(timestamp)).ToLocalTime();

        private static double GetTimestamp(DateTime dateTime) => dateTime.ToUniversalTime().Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;


        public SeriesCollection Series { get; } = new SeriesCollection(Mappers.Xy<RateTick>().X(item => item.Timestamp).Y(item => item.Rate));

        public Func<double, string> XFormatter { get; } = timestamp => GetDateTime(timestamp).ToString("HH:mm:ss");

        public Func<double, string> YFormatter { get; } = value => $"{FlowStatistics.ToNetworkSizeString((long)value)}/s";

        public FlowChart()
        {
            InitializeComponent();

            this.Series.Add(this.readSeries);
            this.Series.Add(this.writeSeries);

            this.DataContext = this;
            this.InitFlowChartAsync();
        }

        private async void InitFlowChartAsync()
        {
            using var httpClient = new HttpClient();
            while (this.Dispatcher.HasShutdownStarted == false)
            {
                try
                {
                    // [PATCH] 可见性门控。MainWindow.xaml 里内联构造了本控件，因此即使从不打开
                    // 流量页，这个循环也会每秒请求一次 http://localhost/flowStatistics
                    // （穿 Kestrel + 各中间件 + JSON 序列化 + UI 反序列化 + 图表重绘，86400 次/天）。
                    // 控件不可见时只等待，不发请求、不重绘；用户切到流量页后立刻恢复刷新。
                    if (this.IsVisible == false)
                    {
                        continue;
                    }

                    await this.FlushFlowStatisticsAsync(httpClient);
                }
                catch (Exception)
                {
                }
                finally
                {
                    await Task.Delay(TimeSpan.FromSeconds(1d));
                }
            }
        }

        private async Task FlushFlowStatisticsAsync(HttpClient httpClient)
        {
            var response = await httpClient.GetAsync("http://localhost/flowStatistics");
            var json = await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            var flowStatistics = JsonConvert.DeserializeObject<FlowStatistics>(json);
            if (flowStatistics == null)
            {
                return;
            }

            this.textBlockRead.Text = FlowStatistics.ToNetworkSizeString(flowStatistics.TotalRead);
            this.textBlockWrite.Text = FlowStatistics.ToNetworkSizeString(flowStatistics.TotalWrite);

            var timestamp = GetTimestamp(DateTime.Now);
            this.readSeries.Values.Add(new RateTick(flowStatistics.ReadRate, timestamp));
            this.writeSeries.Values.Add(new RateTick(flowStatistics.WriteRate, timestamp));

            if (this.readSeries.Values.Count > 60)
            {
                this.readSeries.Values.RemoveAt(0);
                this.writeSeries.Values.RemoveAt(0);
            }
        }

        private class RateTick
        {
            public double Rate { get; }

            public double Timestamp { get; }

            public RateTick(double rate, double timestamp)
            {
                this.Rate = rate;
                this.Timestamp = timestamp;
            }
        }

    }
}
