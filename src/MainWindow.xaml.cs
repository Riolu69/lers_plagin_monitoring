using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace LersMonitor
{
    public partial class App : Application { }

    public partial class MainWindow : Window
    {
        private readonly HttpClient _http = new();
        private string? _token;
        private string _baseUrl = "";

        private List<MeasurePointItem> _allPoints = new();
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

                _baseUrl = $"http://{host}:{port}/api/v1";

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
                    throw new Exception($"Код {(int)response.StatusCode}: {loginResponseJson}");

                using (var doc = JsonDocument.Parse(loginResponseJson))
                {
                    if (doc.RootElement.TryGetProperty("token", out var tokenProp))
                        _token = tokenProp.GetString();
                    else
                        _token = loginResponseJson.Trim('"');
                }

                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

                SaveUserSettings();

                TxtStatus.Text = "Загрузка объектов и точек учёта...";

                var nodeTitlesById = new Dictionary<int, string>();
                try
                {
                    var nodesRes = await _http.GetAsync($"{_baseUrl}/Core/Nodes");
                    if (nodesRes.IsSuccessStatusCode)
                    {
                        var nodesJson = await nodesRes.Content.ReadAsStringAsync();
                        using var nDoc = JsonDocument.Parse(nodesJson);
                        
                        JsonElement nodesArr = nDoc.RootElement.ValueKind == JsonValueKind.Array 
                            ? nDoc.RootElement 
                            : (nDoc.RootElement.TryGetProperty("nodes", out var nProp) ? nProp : nDoc.RootElement);

                        foreach (var node in nodesArr.EnumerateArray())
                        {
                            int id = node.TryGetProperty("id", out var idElem) ? idElem.GetInt32() : 0;
                            string title = node.TryGetProperty("title", out var titleElem) ? titleElem.GetString() ?? "" : "";
                            if (id > 0 && !string.IsNullOrEmpty(title))
                            {
                                nodeTitlesById[id] = title;
                            }
                        }
                    }
                }
                catch { }

                var pointsRes = await _http.GetAsync($"{_baseUrl}/Core/MeasurePoints?type=Regular&getCustomers=true");
                pointsRes.EnsureSuccessStatusCode();

                var pointsJson = await pointsRes.Content.ReadAsStringAsync();
                _allPoints = new List<MeasurePointItem>();
                
                using (var pDoc = JsonDocument.Parse(pointsJson))
                {
                    JsonElement arrayElement = pDoc.RootElement.ValueKind == JsonValueKind.Array
                        ? pDoc.RootElement
                        : (pDoc.RootElement.TryGetProperty("measurePoints", out var mProp) ? mProp : pDoc.RootElement);

                    foreach (var item in arrayElement.EnumerateArray())
                    {
                        int id = item.TryGetProperty("id", out var idElem) ? idElem.GetInt32() : 0;
                        string pointTitle = item.TryGetProperty("title", out var titleElem) ? titleElem.GetString() ?? "" : "Точка";
                        
                        int nodeId = 0;
                        if (item.TryGetProperty("nodeId", out var nodeIdElem))
                            nodeId = nodeIdElem.GetInt32();

                        string nodeTitle = "";
                        if (item.TryGetProperty("nodeTitle", out var nodeTitleElem))
                            nodeTitle = nodeTitleElem.GetString() ?? "";

                        if (string.IsNullOrEmpty(nodeTitle) && nodeId > 0 && nodeTitlesById.ContainsKey(nodeId))
                            nodeTitle = nodeTitlesById[nodeId];

                        if (string.IsNullOrEmpty(nodeTitle))
                            nodeTitle = "Объект";

                        if (id > 0)
                        {
                            _allPoints.Add(new MeasurePointItem 
                            { 
                                Id = id, 
                                Title = pointTitle, 
                                NodeTitle = nodeTitle 
                            });
                        }
                    }
                }

                _allPoints = _allPoints.OrderBy(p => p.NodeTitle).ThenBy(p => p.Title).ToList();
                ApplyPointsFilter();

                if (_settings.SelectedPointIds != null && _settings.SelectedPointIds.Any())
                {
                    foreach (var p in _allPoints.Where(p => _settings.SelectedPointIds.Contains(p.Id)))
                    {
                        ListPoints.SelectedItems.Add(p);
                    }
                }

                BtnStartMonitor.IsEnabled = true;
                TxtStatus.Text = $"Подключено к {host}:{port}. Загружено точек: {_allPoints.Count}";
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

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyPointsFilter();
        }

        private void ApplyPointsFilter()
        {
            string query = TxtSearch.Text.Trim().ToLower();
            if (string.IsNullOrEmpty(query))
            {
                ListPoints.ItemsSource = _allPoints;
            }
            else
            {
                ListPoints.ItemsSource = _allPoints
                    .Where(p => p.DisplayName.ToLower().Contains(query))
                    .ToList();
            }
        }

        private void ColumnToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox chk && GridResults != null)
            {
                string headerPrefix = chk.Content?.ToString() + " ";
                foreach (var col in GridResults.Columns)
                {
                    if (col.Header != null && col.Header.ToString()!.StartsWith(headerPrefix))
                    {
                        col.Visibility = chk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    }
                }
            }
        }

        private static string ParseLersError(string jsonResponse, int statusCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonResponse)) return $"Отказ ({statusCode})";
                using var doc = JsonDocument.Parse(jsonResponse);
                if (doc.RootElement.TryGetProperty("errorDescription", out var desc)) return desc.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("errorMessage", out var msg)) return msg.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? "";
            }
            catch { }
            return $"Отказ ({statusCode})";
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
                    NodeTitle = p.NodeTitle,
                    Status = "Подготовка..."
                });
            }

            try
            {
                BtnStartMonitor.IsEnabled = false;
                TxtStatus.Text = "Снимок текущих данных до опроса...";

                var initialTimes = new Dictionary<int, DateTime?>();
                var startErrors = new Dictionary<int, string>();

                await Task.WhenAll(selectedPoints.Select(async p =>
                {
                    try
                    {
                        var res = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{p.Id}/Consumption/Last");
                        if (res.IsSuccessStatusCode)
                        {
                            var json = await res.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
                            {
                                using var doc = JsonDocument.Parse(json);
                                initialTimes[p.Id] = TryGetDate(doc.RootElement, "dateTime", "deviceTime", "date");
                            }
                        }
                    }
                    catch { }
                }));

                TxtStatus.Text = "Запуск ручного опроса приборов...";

                await Task.WhenAll(selectedPoints.Select(async p =>
                {
                    try
                    {
                        var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{p.Id}/Currents?once=true");
                        pollReq.Content = new ByteArrayContent(Array.Empty<byte>());
                        pollReq.Content.Headers.ContentLength = 0;
                        var pollRes = await _http.SendAsync(pollReq);
                        
                        if (!pollRes.IsSuccessStatusCode)
                        {
                            string errText = await pollRes.Content.ReadAsStringAsync();
                            startErrors[p.Id] = ParseLersError(errText, (int)pollRes.StatusCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        startErrors[p.Id] = "Ошибка сети: " + ex.Message;
                    }
                }));

                TxtStatus.Text = "Опрос запущен, ожидание ответа приборов (интерфейс свободен)...";

                const int maxChecks = 25;
                const int checkIntervalMs = 4000;

                var pollingTasks = selectedPoints.Select(async p =>
                {
                    var vm = _results.FirstOrDefault(r => r.Id == p.Id);
                    if (vm == null) return;

                    initialTimes.TryGetValue(p.Id, out var timeBefore);
                    startErrors.TryGetValue(p.Id, out var startErr);
                    
                    bool receivedNewData = false;

                    for (int attempt = 1; attempt <= maxChecks; attempt++)
                    {
                        // Если 422 - это скорее всего опрос соседней точки этого же модема. Просто пишем "Опрос прибора".
                        vm.Status = $"Опрос прибора ({attempt}/{maxChecks})...";

                        await Task.Delay(checkIntervalMs);

                        try
                        {
                            var res = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{p.Id}/Consumption/Last");
                            if (res.IsSuccessStatusCode)
                            {
                                var json = await res.Content.ReadAsStringAsync();
                                if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
                                {
                                    using var doc = JsonDocument.Parse(json);
                                    var root = doc.RootElement;
                                    var timeNow = TryGetDate(root, "dateTime", "deviceTime", "date");

                                    bool isNew = timeBefore == null || (timeNow != null && timeNow > timeBefore);

                                    if (isNew || attempt == maxChecks)
                                    {
                                        // Обновляем ячейки. Благодаря INotifyPropertyChanged интерфейс обновится мгновенно и без зависаний
                                        vm.DeviceTime = timeNow;
                                        
                                        vm.T1 = ExtractParameterValue(root, "t_in", "t1", "temperatureIn", "T1", "t1Value", "T_in", "t_supply");
                                        vm.T2 = ExtractParameterValue(root, "t_out", "t2", "temperatureOut", "T2", "t2Value", "T_out", "t_return");
                                        vm.T3 = ExtractParameterValue(root, "t3", "temperature3", "T3", "t3Value");
                                        
                                        vm.P1 = ExtractParameterValue(root, "p_in", "p1", "pressureIn", "P1", "p1Value", "P_in", "p_supply");
                                        vm.P2 = ExtractParameterValue(root, "p_out", "p2", "pressureOut", "P2", "p2Value", "P_out", "p_return");
                                        
                                        vm.V1 = ExtractParameterValue(root, "v_in", "v1", "volume1", "V1", "V_in", "volume_supply");
                                        vm.V2 = ExtractParameterValue(root, "v_out", "v2", "volume2", "V2", "V_out", "volume_return");
                                        
                                        vm.M1 = ExtractParameterValue(root, "m_in", "m1", "mass1", "M1", "M_in", "mass_supply");
                                        vm.M2 = ExtractParameterValue(root, "m_out", "m2", "mass2", "M2", "M_out", "mass_return");
                                        
                                        vm.Q  = ExtractParameterValue(root, "q", "heat", "energy", "Q", "Q_in", "q_total");

                                        if (isNew)
                                            vm.Status = "Опрошено успешно";
                                        else
                                            vm.Status = startErr != null ? startErr : "Данные не изменились";
                                            
                                        receivedNewData = true;
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (!receivedNewData)
                    {
                        vm.Status = startErr != null ? startErr : "Таймаут (нет связи)";
                    }
                });

                await Task.WhenAll(pollingTasks);
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

        private static double? ExtractParameterValue(JsonElement root, params string[] targetNames)
        {
            foreach (var name in targetNames)
            {
                if (root.TryGetProperty(name, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double val)) return val;
                    if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out double sVal)) return sVal;
                }
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                string typeName = "";
                                if (item.TryGetProperty("dataParameter", out var dpProp)) typeName = dpProp.ToString();
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("parameter", out var pProp)) typeName = pProp.ToString();
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("name", out var nProp)) typeName = nProp.ToString();
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("type", out var tProp)) typeName = tProp.ToString();
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("parameterType", out var ptProp)) typeName = ptProp.ToString();
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("title", out var titProp)) typeName = titProp.ToString();

                                bool matchesName = targetNames.Any(tn => typeName.IndexOf(tn, StringComparison.OrdinalIgnoreCase) >= 0);

                                if (matchesName || prop.Name.Equals("values", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (item.TryGetProperty("value", out var valProp) || 
                                        item.TryGetProperty("val", out valProp) || 
                                        item.TryGetProperty("currentValue", out valProp))
                                    {
                                        if (matchesName || targetNames.Length == 0)
                                        {
                                            if (valProp.ValueKind == JsonValueKind.Number && valProp.TryGetDouble(out double v)) return v;
                                            if (valProp.ValueKind == JsonValueKind.String && double.TryParse(valProp.GetString(), out double sv)) return sv;
                                        }
                                    }
                                }
                            }
                        }
                    }
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
        public string NodeTitle { get; set; } = string.Empty;
        public string DisplayName => $"{NodeTitle} — {Title}";
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

    // РЕШЕНИЕ ЗАВИСАНИЙ: Класс теперь умеет уведомлять WPF об изменениях точечно
    public class PointResultViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private int _id;
        public int Id { get => _id; set { _id = value; OnPropertyChanged(); } }

        private string _nodeTitle = string.Empty;
        public string NodeTitle { get => _nodeTitle; set { _nodeTitle = value; OnPropertyChanged(); } }

        private string _title = string.Empty;
        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }

        private DateTime? _deviceTime;
        public DateTime? DeviceTime { get => _deviceTime; set { _deviceTime = value; OnPropertyChanged(); } }
        
        private double? _t1;
        public double? T1 { get => _t1; set { _t1 = value; OnPropertyChanged(); } }
        
        private double? _t2;
        public double? T2 { get => _t2; set { _t2 = value; OnPropertyChanged(); } }
        
        private double? _t3;
        public double? T3 { get => _t3; set { _t3 = value; OnPropertyChanged(); } }
        
        private double? _p1;
        public double? P1 { get => _p1; set { _p1 = value; OnPropertyChanged(); } }
        
        private double? _p2;
        public double? P2 { get => _p2; set { _p2 = value; OnPropertyChanged(); } }
        
        private double? _v1;
        public double? V1 { get => _v1; set { _v1 = value; OnPropertyChanged(); } }
        
        private double? _v2;
        public double? V2 { get => _v2; set { _v2 = value; OnPropertyChanged(); } }
        
        private double? _m1;
        public double? M1 { get => _m1; set { _m1 = value; OnPropertyChanged(); } }
        
        private double? _m2;
        public double? M2 { get => _m2; set { _m2 = value; OnPropertyChanged(); } }
        
        private double? _q;
        public double? Q { get => _q; set { _q = value; OnPropertyChanged(); } }
        
        private string _status = string.Empty;
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
    }
}
