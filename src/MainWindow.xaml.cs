using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Lers;
using Lers.Core;
using Lers.Networking;

namespace LersMonitor
{
    public partial class App : Application { }

    public partial class MainWindow : Window
    {
        private LersServer? _server;
        private readonly ObservableCollection<PointResultViewModel> _results = new();

        public MainWindow()
        {
            InitializeComponent();
            GridResults.ItemsSource = _results;
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TxtStatus.Text = "Подключение к серверу...";
                _server = new LersServer();

                var auth = new BasicAuthenticationInfo(TxtLogin.Text, TxtPassword.Password);
                await Task.Run(() => _server.Connect(TxtHost.Text, 10000, auth));

                var points = await Task.Run(() => _server.MeasurePoints.GetList());
                ListPoints.ItemsSource = points.ToList();

                BtnStartMonitor.IsEnabled = true;
                TxtStatus.Text = $"Подключено. Загружено точек: {points.Count()}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка подключения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ошибка подключения";
            }
        }

        private async void BtnStartMonitor_Click(object sender, RoutedEventArgs e)
        {
            var selectedPoints = ListPoints.SelectedItems.Cast<MeasurePoint>().ToList();
            if (!selectedPoints.Any())
            {
                MessageBox.Show("Выберите хотя бы одну точку учёта в списке слева.");
                return;
            }

            _results.Clear();
            foreach (var point in selectedPoints)
            {
                _results.Add(new PointResultViewModel
                {
                    Id = point.Id,
                    Title = point.Title,
                    Status = "В очереди..."
                });
            }

            try
            {
                TxtStatus.Text = "Запуск опроса и чтение данных...";

                await Task.Run(async () =>
                {
                    // 1. Попытка запуска ручного опроса через доступный метод в PollManager или самой точке
                    TryStartPoll(_server!, selectedPoints);

                    await Task.Delay(5000);

                    // 2. Считывание текущих параметров по точкам
                    foreach (var item in _results)
                    {
                        var point = selectedPoints.FirstOrDefault(p => p.Id == item.Id);
                        if (point == null) continue;

                        ReadPointCurrentData(point, item);
                    }
                });

                GridResults.Items.Refresh();
                TxtStatus.Text = "Мониторинг завершен.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка во время опроса: {ex.Message}");
                TxtStatus.Text = "Сбой операции";
            }
        }

        private void TryStartPoll(LersServer server, System.Collections.Generic.List<MeasurePoint> points)
        {
            try
            {
                var pollMgr = server.Poll;
                var pollType = pollMgr.GetType();

                // Ищем метод запуска опроса в PollManager
                var method = pollType.GetMethods()
                    .FirstOrDefault(m => m.Name.Contains("Poll") || m.Name.Contains("Start"));

                if (method != null)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(MeasurePoint[])))
                    {
                        method.Invoke(pollMgr, new object[] { points.ToArray() });
                    }
                }
            }
            catch
            {
                // Если ручной опрос недоступен для учетной записи, переходим сразу к чтению данных
            }
        }

        private void ReadPointCurrentData(MeasurePoint point, PointResultViewModel vm)
        {
            try
            {
                // Обновляем сведения о точке с сервера
                var refreshMethod = point.GetType().GetMethod("Refresh", Type.EmptyTypes);
                refreshMethod?.Invoke(point, null);

                object? dataObj = point.Data;
                if (dataObj == null)
                {
                    vm.Status = "Нет объекта Data";
                    return;
                }

                var dataType = dataObj.GetType();

                // Ищем метод получения текущих/архивных данных (GetCurrent, GetConsumption, GetTotals и т.д.)
                var getMethod = dataType.GetMethods()
                    .FirstOrDefault(m => m.Name.Contains("Current") && m.GetParameters().Length == 0);

                object? record = null;

                if (getMethod != null)
                {
                    record = getMethod.Invoke(dataObj, null);
                    if (record is Task task)
                    {
                        task.GetAwaiter().GetResult();
                        var resultProp = task.GetType().GetProperty("Result");
                        record = resultProp?.GetValue(task);
                    }
                }
                else
                {
                    // Если нет метода без параметров, ищем свойство с данными
                    var prop = dataType.GetProperties()
                        .FirstOrDefault(p => p.Name.Contains("Current"));
                    record = prop?.GetValue(dataObj);
                }

                if (record is IEnumerable enumerable && record is not string)
                {
                    record = enumerable.Cast<object>().LastOrDefault();
                }

                if (record != null)
                {
                    vm.DeviceTime = GetPropValue<DateTime?>(record, "DateTime", "DeviceTime", "Date") ?? DateTime.Now;
                    vm.T1 = GetPropValue<double?>(record, "T_in", "T1", "TemperatureIn");
                    vm.T2 = GetPropValue<double?>(record, "T_out", "T2", "TemperatureOut");
                    vm.P1 = GetPropValue<double?>(record, "P_in", "P1", "PressureIn");
                    vm.V1 = GetPropValue<double?>(record, "V_in", "V1", "FlowIn", "VolumeIn");
                    vm.Status = "Данные получены";
                }
                else
                {
                    // Выводим в статус список доступных методов point.Data для наглядности
                    var availableMethods = string.Join(", ", dataType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name).Distinct());
                    vm.Status = $"Методы Data: {availableMethods}";
                }
            }
            catch (Exception ex)
            {
                vm.Status = $"Ошибка: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        private static T? GetPropValue<T>(object obj, params string[] propNames)
        {
            var type = obj.GetType();
            foreach (var name in propNames)
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null)
                {
                    var val = prop.GetValue(obj);
                    if (val is T typedVal) return typedVal;
                    if (val != null)
                    {
                        try
                        {
                            return (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
                        }
                        catch { }
                    }
                }
            }
            return default;
        }
    }

    public class PointResultViewModel
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime? DeviceTime { get; set; }
        public double? T1 { get; set; }
        public double? T2 { get; set; }
        public double? P1 { get; set; }
        public double? V1 { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
