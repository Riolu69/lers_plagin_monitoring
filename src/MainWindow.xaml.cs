using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
        private UserSettings _settings = new();

        private static string SettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LersMonitor", "settings.json");

        public MainWindow()
        {
            InitializeComponent();
            GridResults.ItemsSource = _results;
            LoadUserSettings();
        }

        private void LoadUserSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonSerializer.Deserialize<UserSettings>(json);
                    if (loaded != null)
                    {
                        _settings = loaded;
                        TxtHost.Text = string.IsNullOrWhiteSpace(_settings.Host) ? "localhost" : _settings.Host;
                        TxtPort.Text = _settings.Port == 0 ? "10000" : _settings.Port.ToString();
                        TxtLogin.Text = _settings.Login;
                        ChkRemember.IsChecked = _settings.RememberCredentials;

                        if (!string.IsNullOrEmpty(_settings.PasswordBase64))
                        {
                            var bytes = Convert.FromBase64String(_settings.PasswordBase64);
                            TxtPassword.Password = Encoding.UTF8.GetString(bytes);
                        }
                    }
                }
            }
            catch
            {
                // Ошибка чтения настроек — значения по умолчанию
            }
        }

        private void SaveUserSettings(List<int>? selectedPointIds = null)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                bool remember = ChkRemember.IsChecked == true;
                _settings.RememberCredentials = remember;

                if (remember)
                {
                    _settings.Host = TxtHost.Text.Trim();
                    ushort.TryParse(TxtPort.Text.Trim(), out ushort p);
                    _settings.Port = p == 0 ? (ushort)10000 : p;
                    _settings.Login = TxtLogin.Text.Trim();
                    _settings.PasswordBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(TxtPassword.Password));
                }
                else
                {
                    _settings.Host = "localhost";
                    _settings.Port = 10000;
                    _settings.Login = "";
                    _settings.PasswordBase64 = "";
                }

                if (selectedPointIds != null)
                {
                    _settings.SelectedPointIds = selectedPointIds;
                }

                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
            }
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TxtStatus.Text = "Подключение к серверу...";
                BtnConnect.IsEnabled = false;

                string host = TxtHost.Text.Trim();
                string portStr = TxtPort.Text.Trim();
                string login = TxtLogin.Text.Trim();
                string password = TxtPassword.Password;

                if (string.IsNullOrWhiteSpace(host))
                {
                    MessageBox.Show("Укажите IP-адрес или домен сервера.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!ushort.TryParse(portStr, out ushort port) || port == 0)
                {
                    MessageBox.Show("Укажите корректный порт (число от 1 до 65535). По умолчанию: 10000", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _server = new LersServer();
                var auth = new BasicAuthenticationInfo(login, password);

                await _server.ConnectAsync(host, port, auth);

                SaveUserSettings();

                TxtStatus.Text = "Загрузка точек учёта...";

                var points = await _server.MeasurePoints.GetListAsync();
                var pointsList = points.ToList();

                ListPoints.ItemsSource = pointsList;

                if (_settings.SelectedPointIds != null && _settings.SelectedPointIds.Any())
                {
                    foreach (var p in pointsList.Where(p => _settings.SelectedPointIds.Contains(p.Id)))
                    {
                        ListPoints.SelectedItems.Add(p);
                    }
                }

                BtnStartMonitor.IsEnabled = true;
                TxtStatus.Text = $"Подключено к {host}:{port}. Загружено точек: {pointsList.Count}";
            }
            catch (Exception ex)
            {
                string details = ex.InnerException != null 
                    ? $"{ex.Message}\n\nПодробности: {ex.InnerException.Message}" 
                    : ex.Message;

                MessageBox.Show($"Ошибка подключения:\n\n{details}", 
                    "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
                
                TxtStatus.Text = $"Ошибка: {ex.Message}";
            }
            finally
            {
                BtnConnect.IsEnabled = true;
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

            SaveUserSettings(selectedPoints.Select(p => p.Id).ToList());

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
                BtnStartMonitor.IsEnabled = false;
                TxtStatus.Text = "Запуск опроса и чтение данных...";

                var itemsToProcess = _results.ToList();

                await Task.Run(async () =>
                {
                    TryStartPoll(_server!, selectedPoints);

                    await Task.Delay(5000);

                    foreach (var item in itemsToProcess)
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
            finally
            {
                BtnStartMonitor.IsEnabled = true;
            }
        }

        private void TryStartPoll(LersServer server, List<MeasurePoint> points)
        {
            try
            {
                var pollMgr = server.Poll;
                var pollType = pollMgr.GetType();

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
            }
        }

        private void ReadPointCurrentData(MeasurePoint point, PointResultViewModel vm)
        {
            try
            {
                var refreshMethod = point.GetType().GetMethod("Refresh", Type.EmptyTypes);
                refreshMethod?.Invoke(point, null);

                object? dataObj = point.Data;
                if (dataObj == null)
                {
                    vm.Status = "Нет объекта Data";
                    return;
                }

                var dataType = dataObj.GetType();

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

    public class UserSettings
    {
        public string Host { get; set; } = "localhost";
        public ushort Port { get; set; } = 10000;
        public string Login { get; set; } = "";
        public string PasswordBase64 { get; set; } = "";
        public bool RememberCredentials { get; set; } = true;
        public List<int> SelectedPointIds { get; set; } = new();
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
