using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace LersMonitor
{
    public partial class App : Application { }

    public partial class MainWindow : Window
    {
        private readonly HttpClient _http = new();
        private string? _token;
        private string _baseUrl = "";

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
            catch { }
        }

        private void SaveUserSettings(List<int>? selectedPointIds = null)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

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
                    _settings.SelectedPointIds = selectedPointIds;

                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
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

                if (string.IsNullOrWhiteSpace(host) || !ushort.TryParse(portStr, out ushort port))
                {
                    MessageBox.Show("Укажите корректный IP/хост и порт.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Префикс API из OpenLuch: /api/v1
                _baseUrl = $"http://{host}:{port}/api/v1";

                // 1. Вход: POST /api/v1/Login с application name
                var loginPayload = JsonSerializer.Serialize(new 
                { 
                    login = login, 
                    password = password,
                    application = "LersMonitor WPF"
                });

                var loginContent = new StringContent(loginPayload, Encoding.UTF8, "application/json");
                _http.DefaultRequestHeaders.Accept.Clear();
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _http.PostAsync($"{_baseUrl}/Login", loginContent);
                var loginResponseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Код {(int)response.StatusCode}: {loginResponseJson}");
                }

                using var doc = JsonDocument.Parse(loginResponseJson);
                if (doc.RootElement.TryGetProperty("token", out var tokenProp))
                    _token = tokenProp.GetString();
                else
                    _token = loginResponseJson.Trim('"');

                if (string.IsNullOrEmpty(_token))
                    throw new Exception("Сервер не вернул токен авторизации.");

                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

                SaveUserSettings();

                // 2. Запрос списка точек учёта: GET /api/v1/Core/MeasurePoints?type=Regular&getCustomers=true
                TxtStatus.Text = "Загрузка точек учёта...";
                var pointsRes = await _http.GetAsync($"{_baseUrl}/Core/MeasurePoints?type=Regular&getCustomers=true");
                pointsRes.EnsureSuccessStatusCode();

                var pointsJson = await pointsRes.Content.ReadAsStringAsync();
                
                // В OpenLuch ответ приходит в обертке: { "measurePoints": [...] }
                var pointsList = new List<MeasurePointItem>();
                using var pDoc = JsonDocument.Parse(pointsJson);
                
                JsonElement arrayElement;
                if (pDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    arrayElement = pDoc.RootElement;
                }
                else if (pDoc.RootElement.TryGetProperty("measurePoints", out var mProp))
                {
                    arrayElement = mProp;
                }
                else
                {
                    arrayElement = pDoc.RootElement;
                }

                foreach (var item in arrayElement.EnumerateArray())
                {
                    int id = item.TryGetProperty("id", out var idElem) ? idElem.GetInt32() : 0;
                    string title = item.TryGetProperty("title", out var titleElem) ? titleElem.GetString() ?? "" : $"Точка #{id}";
                    if (id > 0)
                    {
                        pointsList.Add(new MeasurePointItem { Id = id, Title = title });
                    }
                }

                ListPoints.ItemsSource = pointsList;

                // Восстановление выбранных точек
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
                MessageBox.Show($"Ошибка подключения по REST API:\n\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ошибка подключения";
            }
            finally
            {
                BtnConnect.IsEnabled = true;
            }
        }

        private async void BtnStartMonitor_Click(object sender, RoutedEventArgs e)
        {
            var selectedPoints = ListPoints.SelectedItems.Cast<MeasurePointItem>().ToList();
            if (!selectedPoints.Any())
            {
                MessageBox.Show("Выберите точки учёта в списке слева.");
                return;
            }

            SaveUserSettings(selectedPoints.Select(p => p.Id).ToList());

            _results.Clear();
            foreach (var p in selectedPoints)
            {
                _results.Add(new PointResultViewModel
                {
                    Id = p.Id,
                    Title = p.Title,
                    Status = "Ожидание..."
                });
            }

            try
            {
                BtnStartMonitor.IsEnabled = false;

                // 1. Запуск разового опроса для каждой точки по схеме OpenLuch:
                // POST /api/v1/Poll/MeasurePoints/{id}/Currents?once=true с Content-Length: 0
                foreach (var item in _results)
                {
                    try
                    {
                        TxtStatus.Text = $"Запуск опроса прибора для: {item.Title}...";
                        
                        var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{item.Id}/Currents?once=true");
                        pollReq.Content = new ByteArrayContent(Array.Empty<byte>());
                        pollReq.Content.Headers.ContentLength = 0;

                        await _http.SendAsync(pollReq);
                    }
                    catch
                    {
                        // Если на сервере опрос не настроен, продолжаем считывание последнего значения
                    }
                }

                TxtStatus.Text = "Ожидание ответа приборов (5 сек)...";
                await Task.Delay(5000);

                // 2. Чтение последних данных по схеме OpenLuch:
                // GET /api/v1/Data/MeasurePoints/{id}/Consumption/Last
                foreach (var item in _results)
                {
                    try
                    {
                        var res = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{item.Id}/Consumption/Last");
                        if (res.IsSuccessStatusCode)
                        {
                            var json = await res.Content.ReadAsStringAsync();
                            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
                            {
                                item.Status = "Нет данных на сервере";
                                continue;
                            }

                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            item.DeviceTime = TryGetDate(root, "dateTime", "deviceTime");
                            item.T1 = TryGetDouble(root, "t_in", "t1");
                            item.T2 = TryGetDouble(root, "t_out", "t2");
                            item.P1 = TryGetDouble(root, "p_in", "p1");
                            item.V1 = TryGetDouble(root, "v_in", "v1", "m_in");
                            item.Status = "Опрошено";
                        }
                        else
                        {
                            item.Status = $"Код {res.StatusCode}";
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Status = $"Ошибка: {ex.Message}";
                    }
                }

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

        private static double? TryGetDouble(JsonElement elem, params string[] names)
        {
            foreach (var n in names)
            {
                if (elem.TryGetProperty(n, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double val))
                        return val;
                    if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out double sVal))
                        return sVal;
                }
            }
            return null;
        }

        private static DateTime? TryGetDate(JsonElement elem, params string[] names)
        {
            foreach (var n in names)
            {
                if (elem.TryGetProperty(n, out var prop))
                {
                    if (prop.TryGetDateTime(out var dt))
                        return dt;
                    if (prop.ValueKind == JsonValueKind.String && DateTime.TryParse(prop.GetString(), out var sDt))
                        return sDt;
                }
            }
            return null;
        }
    }

    public class MeasurePointItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
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
