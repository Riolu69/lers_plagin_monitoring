// PodpitkaModule.cs — вкладка «Подпитка»: периоды, пороги V1, ViewModel, чтение объёмов из ЛЭРС.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace LersMonitor
{
    // ---------- Периоды ----------
    public readonly record struct Period(DateTime From, DateTime ToExclusive);

    public static class PodpitkaPeriods
    {
        public static readonly int[] IntervalsMin = { 10, 20, 30, 40, 50, 60, 120 };

        /// <summary>Последний ЗАКРЫТЫЙ отчётный период 25-е → 24-е (включительно).
        /// 13.10 → 25.08–24.09; 29.10 → 25.09–24.10.</summary>
        public static Period Report(DateTime now)
        {
            var endEx = now.Day >= 25 ? new DateTime(now.Year, now.Month, 25)
                                      : new DateTime(now.Year, now.Month, 25).AddMonths(-1);
            return new Period(endEx.AddMonths(-1), endEx);
        }
        public static Period Yesterday(DateTime now) => new(now.Date.AddDays(-1), now.Date);
        public static Period Today(DateTime now) => new(now.Date, new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0));
        public static Period PrevHour(DateTime now)
        {
            var h = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            return new Period(h.AddHours(-1), h);
        }
    }

    // ---------- Пороги (сохраняются в settings.json) ----------
    public class VolumeThresholds
    {
        public double? ReportMin { get; set; } public double? ReportMax { get; set; }
        public double? DayMin { get; set; }    public double? DayMax { get; set; }
        public double? TodayMin { get; set; }  public double? TodayMax { get; set; }
        public double? HourMin { get; set; }   public double? HourMax { get; set; }
    }
    // В UserSettings добавить:
    //   public List<int> PodpitkaPointIds { get; set; } = new();
    //   public int PodpitkaIntervalMin { get; set; } = 30;
    //   public Dictionary<int, VolumeThresholds> PodpitkaLimits { get; set; } = new();

    // ---------- Чтение объёмов ----------
    public sealed class PodpitkaVolumeService
    {
        private readonly HttpClient _http; private readonly Func<string> _baseUrl;
        private readonly SemaphoreSlim _gate = new(4);
        public PodpitkaVolumeService(HttpClient http, Func<string> baseUrl) { _http = http; _baseUrl = baseUrl; }

        /// <summary>Сумма V1 за период. ВНИМАНИЕ: путь и имена query-параметров ЛЭРС Consumption
        /// сверьте в Swagger вашего сервера (http://host:port/swagger) — я их не мог проверить.</summary>
        public async Task<double?> SumV1Async(int pointId, Period p, bool hourly, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                string agg = hourly ? "Hour" : "Day";
                string url = $"{_baseUrl()}/Data/MeasurePoints/{pointId}/Consumption" +
                             $"?start={p.From:yyyy-MM-dd'T'HH:mm:ss}&end={p.ToExclusive:yyyy-MM-dd'T'HH:mm:ss}&dataAggregation={agg}";
                using var res = await _http.GetAsync(url, ct);
                if (!res.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                    root = root.EnumerateObject().Select(x => x.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array);
                if (root.ValueKind != JsonValueKind.Array) return null;

                double sum = 0; bool any = false;
                foreach (var rec in root.EnumerateArray())
                {
                    var v = ReadV1(rec);
                    if (v.HasValue) { sum += v.Value; any = true; }
                }
                return any ? sum : null;
            }
            finally { _gate.Release(); }
        }

        private static double? ReadV1(JsonElement rec)
        {
            foreach (var n in new[] { "v_in", "v1", "volume1", "V1", "V_in", "volume_supply" })
                if (rec.TryGetProperty(n, out var p))
                {
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
                    if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
                }
            return null; // при другой структуре — повторить разбор из ExtractParameterValue
        }

        /// <summary>Один проход по точке: 4 среза параллельно.</summary>
        public async Task RefreshAsync(PodpitkaPointViewModel vm, DateTime now, CancellationToken ct)
        {
            var r = PodpitkaPeriods.Report(now); var y = PodpitkaPeriods.Yesterday(now);
            var t = PodpitkaPeriods.Today(now);  var h = PodpitkaPeriods.PrevHour(now);
            var tasks = new[]
            {
                SumV1Async(vm.Id, r, false, ct), SumV1Async(vm.Id, y, false, ct),
                t.ToExclusive > t.From ? SumV1Async(vm.Id, t, true, ct) : Task.FromResult<double?>(0),
                SumV1Async(vm.Id, h, true, ct)
            };
            var res = await Task.WhenAll(tasks);
            vm.SetVolumes(res[0], res[1], res[2], res[3], DateTime.Now);
        }
    }

    // ---------- ViewModel ----------
    public class PodpitkaPointViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public int Id { get; init; }
        public string Title { get; init; } = "";
        public string NodeTitle { get; init; } = "";
        public VolumeThresholds? Limits { get; set; }

        private PollState _state = PollState.Pending; public PollState State { get => _state; set { _state = value; On(); On(nameof(IsDone)); On(nameof(HasError)); On(nameof(RetryVisibility)); On(nameof(PointTitleColor)); } }
        private string _message = "Ожидание..."; public string Message { get => _message; set { _message = value; On(); } }
        public bool IsDone => State is PollState.Ok or PollState.OkCached or PollState.Error;
        public bool HasError => State == PollState.Error;
        public Visibility RetryVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

        public double? Report { get; private set; } public double? Day { get; private set; }
        public double? Today { get; private set; }  public double? Hour { get; private set; }
        public DateTime? UpdatedAt { get; private set; }

        public void SetVolumes(double? rep, double? day, double? today, double? hour, DateTime at)
        {
            Report = rep; Day = day; Today = today; Hour = hour; UpdatedAt = at;
            On(nameof(Report)); On(nameof(Day)); On(nameof(Today)); On(nameof(Hour)); On(nameof(UpdatedAt));
            RefreshColors();
        }

        public static bool Out(double? v, double? min, double? max) =>
            v.HasValue && ((min.HasValue && v < min) || (max.HasValue && v > max));

        public bool ReportBreach => Out(Report, Limits?.ReportMin, Limits?.ReportMax);
        public bool DayBreach    => Out(Day,    Limits?.DayMin,    Limits?.DayMax);
        public bool TodayBreach  => Out(Today,  Limits?.TodayMin,  Limits?.TodayMax);
        public bool HourBreach   => Out(Hour,   Limits?.HourMin,   Limits?.HourMax);
        public bool HasBreach => ReportBreach || DayBreach || TodayBreach || HourBreach;

        // Те же правила, что на основном дашборде: красный фон + жёлтый текст при нарушении.
        private static Brush Res(string k) => (Brush)Application.Current.MainWindow.FindResource(k);
        private Brush Bg(bool breach) => breach ? Res("AccentRed") : (IsDone ? (HasError ? Res("AccentOrange") : Res("AccentGreen")) : Res("AccentGray"));
        public Brush ReportBg => Bg(ReportBreach); public Brush DayBg => Bg(DayBreach);
        public Brush TodayBg => Bg(TodayBreach);   public Brush HourBg => Bg(HourBreach);
        public Brush ReportFg => ReportBreach ? Brushes.Yellow : Brushes.White; public Brush DayFg => DayBreach ? Brushes.Yellow : Brushes.White;
        public Brush TodayFg => TodayBreach ? Brushes.Yellow : Brushes.White;   public Brush HourFg => HourBreach ? Brushes.Yellow : Brushes.White;
        public Brush PointTitleColor => HasBreach ? Res("AccentRed") : HasError ? Res("AccentOrange") : !IsDone ? Res("AccentGray") : Brushes.White;

        public void RefreshColors() => On(null);
    }

    public class PodpitkaNodeViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string NodeTitle { get; init; } = "";
        public ObservableCollection<PodpitkaPointViewModel> Points { get; } = new();
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

        // Серая, пока НЕ ВСЕ точки опрошены; затем красная > оранжевая > зелёная.
        public Brush TileBackground
        {
            get
            {
                Brush R(string k) => (Brush)Application.Current.MainWindow.FindResource(k);
                if (Points.Any(p => !p.IsDone)) return R("AccentGray");
                if (Points.Any(p => p.HasBreach)) return R("AccentRed");
                if (Points.Any(p => p.HasError)) return R("AccentOrange");
                return R("AccentGreen");
            }
        }
        public string GeneralStatus =>
            Points.Any(p => !p.IsDone) ? $"Опрошено {Points.Count(p => p.IsDone)} из {Points.Count}" :
            Points.FirstOrDefault(p => p.HasError)?.Message ?? "Опрошено успешно";
    }
}
