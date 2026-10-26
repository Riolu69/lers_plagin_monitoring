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

namespace LersMonitor
{
    public partial class App : Application { }

    public partial class MainWindow : Window
    {
        private object? _server;
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

                // Инициализация LersServer через рефлексию для совместимости с любой версией сборки
                var lersServerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "LersServer");

                if (lersServerType == null)
                {
                    throw new Exception("Библиотека Lers.Framework не найдена. Проверьте установку пакетов.");
                }

                _server = Activator.CreateInstance(lersServerType);

                // Создание учетных данных
                var authType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "BasicAuthenticationInfo");

                object? auth = null;
                if (authType != null)
                {
                    auth = Activator.CreateInstance(authType, login, password);
                }

                // Вызов метода Connect / ConnectAsync
                var connectMethod = lersServerType.GetMethods()
                    .FirstOrDefault(m => m.Name.StartsWith("Connect") && m.GetParameters().Length >= 3);

                if (connectMethod == null)
                {
                    throw new Exception("Не найден подходящий метод Connect у LersServer.");
                }

                var connectResult = connectMethod.Invoke(_server, new object[] { host, port, auth! });
                if (connectResult is Task task)
                {
                    await task;
                }

                SaveUserSettings();

                TxtStatus.Text = "Загрузка точек учёта...";

                // Получение точек учёта
                var measurePointsProp = lersServerType.GetProperty("MeasurePoints");
                var pointsMgr = measurePointsProp?.GetValue(_server);

                var getListMethod = pointsMgr?.GetType().GetMethods()
                    .FirstOrDefault(m => (m.Name == "GetList" || m.Name == "GetListAsync") && m.GetParameters().Length == 0);

                object? rawPoints = getListMethod?.Invoke(pointsMgr, null);
                if (rawPoints is Task t)
                {
                    await t;
                    var resProp = t.GetType().GetProperty("Result");
                    rawPoints = resProp?.GetValue(t);
                }

                var pointsList = ((IEnumerable)rawPoints!).Cast<object>().ToList();

                ListPoints.ItemsSource = pointsList;

                // Восстановление выбранных точек
                if (_settings.SelectedPointIds != null && _settings.SelectedPointIds.Any())
                {
                    foreach (var p in pointsList)
                    {
                        var idProp = p.GetType().GetProperty("Id");
                        if (idProp != null && _settings.SelectedPointIds.Contains((int)idProp.GetValue(p)!))
                        {
                            ListPoints.SelectedItems.Add(p);
                        }
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
            var selectedPoints = ListPoints.SelectedItems.Cast<object>().ToList();
            if (!selectedPoints.Any())
            {
                MessageBox.Show("Выберите хотя бы одну точку учёта в списке слева.");
                return;
            }

            var ids = new List<int>();
            _results.Clear();

            foreach (var point in selectedPoints)
            {
                var id = (int)point.GetType().GetProperty("Id")!.GetValue(point)!;
                var title = (string)point.GetType().GetProperty("Title")!.GetValue(point)!;
                ids.Add(id);

                _results.Add(new PointResultViewModel
                {
                    Id = id,
                    Title = title,
                    Status = "В очереди..."
                });
            }

            SaveUserSettings(ids);

            try
            {
                BtnStartMonitor.IsEnabled = false;
                TxtStatus.Text = "Запуск опроса и считывание текущих...";

                var itemsToProcess = _results.ToList();

                await Task.Run(async () =>
                {
                    TryStartPoll(_server!, selectedPoints);

                    await Task.Delay(4000);

                    foreach (var item in itemsToProcess)
                    {
                        var point = selectedPoints.FirstOrDefault(p => (int)p.GetType().GetProperty("Id")!.GetValue(p)! == item.Id);
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

        private void TryStartPoll(object server, List<object> points)
        {
            try
            {
                var pollProp = server.GetType().GetProperty("Poll");
                var pollMgr = pollProp?.GetValue(server);
                if (pollMgr == null) return;

                var pollType = pollMgr.GetType();
                var method = pollType.GetMethods()
                    .FirstOrDefault(m => m.Name.Contains("Poll") || m.Name.Contains("Start"));

                if (method != null)
                {
                    var pars = method.GetParameters();
                    if (pars.Length == 1)
                    {
                        var elemType = pars[0].ParameterType.GetElementType() ?? typeof(object);
                        var arr = Array.CreateInstance(elemType, points.Count);
                        for (int i = 0; i < points.Count; i++) arr.SetValue(points[i], i);
                        method.Invoke(pollMgr, new object[] { arr });
                    }
                }
            }
            catch
            {
            }
        }

        private void ReadPointCurrentData(object point, PointResultViewModel vm)
        {
            try
            {
                var refreshMethod = point.GetType().GetMethod("Refresh", Type.EmptyTypes);
                refreshMethod?.Invoke(point, null);

                var dataProp = point.GetType().GetProperty("Data");
                object? dataObj = dataProp?.GetValue(point);

                if (dataObj == null)
                {
                    vm.Status = "Нет объекта данных";
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
                    vm.Status = "Опрошено";
                }
                else
                {
                    vm.Status = "Ожидание данных...";
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
