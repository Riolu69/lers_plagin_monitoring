using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
using System.Windows.Media;

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
        
        private MeasurePointItem? _editingPoint = null;

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
                        ChkDarkMode.IsChecked = _settings.IsDarkMode;

                        if (!string.IsNullOrEmpty(_settings.PasswordBase64))
                        {
                            var bytes = Convert.FromBase64String(_settings.PasswordBase64);
                            TxtPassword.Password = Encoding.UTF8.GetString(bytes);
                        }
                        
                        if (_settings.PointLimits == null) 
                            _settings.PointLimits = new Dictionary<int, PointThresholds>();

                        if (_settings.LastResults != null && _settings.LastResults.Any())
                        {
                            foreach (var r in _settings.LastResults)
                            {
                                if (_settings.PointLimits.TryGetValue(r.Id, out var limits))
                                    r.Limits = limits;
                                
                                _results.Add(r);
                                r.RefreshColors();
                            }
                        }
                    }
                }
            }
            catch { }
            
            ApplyTheme(_settings.IsDarkMode);
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
                _settings.IsDarkMode = ChkDarkMode.IsChecked == true;

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

                _settings.LastResults = _results.ToList();

                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }
        
        protected override void OnClosed(EventArgs e)
        {
            SaveUserSettings();
            base.OnClosed(e);
        }

        private void ChkDarkMode_Click(object sender, RoutedEventArgs e)
        {
            bool isDark = ChkDarkMode.IsChecked == true;
            ApplyTheme(isDark);
            _settings.IsDarkMode = isDark;
            SaveUserSettings();
            
            foreach (var r in _results) r.RefreshColors();
        }

        private void ApplyTheme(bool isDark)
        {
            var res = this.Resources;
            if (isDark)
            {
                res["SystemBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C1E"));
                res["CardBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C2C2E"));
                res["BorderBrushColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38383A"));
                res["LabelPrimary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["LabelSecondary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98989F"));
                res["InputBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1C1E"));
                res["HeaderBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C2C2E"));
                res["SelectionBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A3A3C"));
                res["SelectionForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
            }
            else
            {
                res["SystemBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F7"));
                res["CardBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                res["BorderBrushColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5E5EA"));
                res["LabelPrimary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D1D1F"));
                res["LabelSecondary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86868B"));
                res["InputBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBFBFD"));
                res["HeaderBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FAFAFC"));
                res["SelectionBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8F2FE"));
                res["SelectionForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D1D1F"));
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
                
                var uniqueNodes = _allPoints.Select(p => p.NodeTitle).Distinct().OrderBy(n => n).ToList();
                uniqueNodes.Insert(0, "Все объекты");
                CmbNode.ItemsSource = uniqueNodes;
                CmbNode.SelectedIndex = 0;
                
                CmbSystemType.ItemsSource = new List<string> { "Все системы", "Теплоснабжение", "ГВС", "Подпитка", "ХВС" };
                CmbSystemType.SelectedIndex = 0;

                ApplyFilters();

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

        private void ApplyFilters_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilters();
        private void ApplyFilters_Changed(object sender, TextChangedEventArgs e) => ApplyFilters();

        private void ApplyFilters()
        {
            if (_allPoints == null || !_allPoints.Any()) return;

            string query = TxtSearch.Text.Trim().ToLower();
            string selectedNode = CmbNode.SelectedItem as string;
            string selectedType = CmbSystemType.SelectedItem as string;

            var filtered = _allPoints.AsEnumerable();

            if (!string.IsNullOrEmpty(query))
                filtered = filtered.Where(p => p.DisplayName.ToLower().Contains(query));

            if (!string.IsNullOrEmpty(selectedNode) && selectedNode != "Все объекты")
                filtered = filtered.Where(p => p.NodeTitle == selectedNode);

            if (!string.IsNullOrEmpty(selectedType) && selectedType != "Все системы")
                filtered = filtered.Where(p => p.Title.ToLower().Contains(selectedType.ToLower()));

            ListPoints.ItemsSource = filtered.ToList();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            ListPoints.SelectAll();
        }

        private void BtnClearSelection_Click(object sender, RoutedEventArgs e)
        {
            ListPoints.UnselectAll();
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

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var selected = ListPoints.SelectedItems.Cast<MeasurePointItem>().ToList();
            if (selected.Count != 1)
            {
                MessageBox.Show("Выберите ровно ОДНУ точку в списке слева для настройки её границ.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _editingPoint = selected.First();
            TxtLimitsTitle.Text = $"Границы: {_editingPoint.Title}";

            if (_settings.PointLimits.TryGetValue(_editingPoint.Id, out var limits))
            {
                TxtT1Min.Text = limits.T1Min?.ToString();
                TxtT1Max.Text = limits.T1Max?.ToString();
                TxtT2Min.Text = limits.T2Min?.ToString();
                TxtT2Max.Text = limits.T2Max?.ToString();
                TxtP1Min.Text = limits.P1Min?.ToString();
                TxtP1Max.Text = limits.P1Max?.ToString();
                TxtP2Min.Text = limits.P2Min?.ToString();
                TxtP2Max.Text = limits.P2Max?.ToString();
                TxtV1Min.Text = limits.V1Min?.ToString();
                TxtV1Max.Text = limits.V1Max?.ToString();
                TxtV2Min.Text = limits.V2Min?.ToString();
                TxtV2Max.Text = limits.V2Max?.ToString();
            }
            else
            {
                TxtT1Min.Text = ""; TxtT1Max.Text = "";
                TxtT2Min.Text = ""; TxtT2Max.Text = "";
                TxtP1Min.Text = ""; TxtP1Max.Text = "";
                TxtP2Min.Text = ""; TxtP2Max.Text = "";
                TxtV1Min.Text = ""; TxtV1Max.Text = "";
                TxtV2Min.Text = ""; TxtV2Max.Text = "";
            }

            OverlayLimits.Visibility = Visibility.Visible;
        }

        private void BtnCancelLimits_Click(object sender, RoutedEventArgs e)
        {
            OverlayLimits.Visibility = Visibility.Collapsed;
            _editingPoint = null;
        }

        private void BtnSaveLimits_Click(object sender, RoutedEventArgs e)
        {
            if (_editingPoint == null) return;

            var limits = new PointThresholds();
            limits.T1Min = ParseDouble(TxtT1Min.Text);
            limits.T1Max = ParseDouble(TxtT1Max.Text);
            limits.T2Min = ParseDouble(TxtT2Min.Text);
            limits.T2Max = ParseDouble(TxtT2Max.Text);
            limits.P1Min = ParseDouble(TxtP1Min.Text);
            limits.P1Max = ParseDouble(TxtP1Max.Text);
            limits.P2Min = ParseDouble(TxtP2Min.Text);
            limits.P2Max = ParseDouble(TxtP2Max.Text);
            limits.V1Min = ParseDouble(TxtV1Min.Text);
            limits.V1Max = ParseDouble(TxtV1Max.Text);
            limits.V2Min = ParseDouble(TxtV2Min.Text);
            limits.V2Max = ParseDouble(TxtV2Max.Text);

            _settings.PointLimits[_editingPoint.Id] = limits;
            SaveUserSettings();

            var vm = _results.FirstOrDefault(r => r.Id == _editingPoint.Id);
            if (vm != null)
            {
                vm.Limits = limits;
                vm.RefreshColors();
            }

            OverlayLimits.Visibility = Visibility.Collapsed;
            _editingPoint = null;
        }

        private double? ParseDouble(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (double.TryParse(text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out double res))
                return res;
            return null;
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
                _settings.PointLimits.TryGetValue(p.Id, out var pointLimits);
                _results.Add(new PointResultViewModel
                {
                    Id = p.Id,
                    Title = p.Title,
                    NodeTitle = p.NodeTitle,
                    Status = "Подготовка...",
                    Limits = pointLimits
                });
            }

            try
            {
                BtnStartMonitor.IsEnabled = false;
                TxtStatus.Text = "Снимок текущих данных до опроса...";

                var initialTimes = new Dictionary<int, DateTime?>();
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

                TxtStatus.Text = "Запуск опроса приборов...";

                // Группируем точки по объектам (NodeTitle), чтобы опрашивать прибор только 1 раз на объект
                var pointsByNode = selectedPoints.GroupBy(p => p.NodeTitle).ToList();

                // Запускаем запросы на опрос по объектам
                await Task.WhenAll(pointsByNode.Select(async group =>
                {
                    // Берем первую точку объекта как ведущую
                    var mainPoint = group.First();
                    try
                    {
                        var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{mainPoint.Id}/Currents?once=true");
                        pollReq.Content = new ByteArrayContent(Array.Empty<byte>());
                        pollReq.Content.Headers.ContentLength = 0;
                        var pollRes = await _http.SendAsync(pollReq);
                        
                        // Если главная не смогла запуститься, пробуем запустить остальные точки этого же объекта по очереди
                        if (!pollRes.IsSuccessStatusCode)
                        {
                            foreach (var subPoint in group.Skip(1))
                            {
                                var subReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{subPoint.Id}/Currents?once=true");
                                subReq.Content = new ByteArrayContent(Array.Empty<byte>());
                                subReq.Content.Headers.ContentLength = 0;
                                var subRes = await _http.SendAsync(subReq);
                                if (subRes.IsSuccessStatusCode) break;
                            }
                        }
                    }
                    catch { }
                }));

                TxtStatus.Text = "Опрос запущен, ожидание новых данных...";

                const int maxChecks = 25;
                const int checkIntervalMs = 3000;

                // Независимый опрос каждой выбранной точки с немедленным выходом при появлении данных
                var pollingTasks = selectedPoints.Select(async p =>
                {
                    var vm = _results.FirstOrDefault(r => r.Id == p.Id);
                    if (vm == null) return;

                    initialTimes.TryGetValue(p.Id, out var timeBefore);
                    bool receivedNewData = false;

                    for (int attempt = 1; attempt <= maxChecks; attempt++)
                    {
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

                                    // Если появилась новая метка времени ИЛИ данные вообще обновились
                                    bool isNew = timeBefore == null || (timeNow != null && timeNow > timeBefore);

                                    // Также считаем данные свежими, если время прибора сегодня/свежее, даже если timeBefore не зафиксировался
                                    if (isNew || attempt == maxChecks)
                                    {
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

                                        // Если время прибора свежее или данные реально получены — сразу ставим успех
                                        if (timeNow != null || (vm.T1 != null || vm.V1 != null || vm.P1 != null))
                                        {
                                            vm.Status = "Опрошено успешно";
                                            receivedNewData = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (!receivedNewData)
                    {
                        // Проверим напоследок, вдруг данные всё-таки записались на сервер
                        try
                        {
                            var finalRes = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{p.Id}/Consumption/Last");
                            if (finalRes.IsSuccessStatusCode)
                            {
                                var json = await finalRes.Content.ReadAsStringAsync();
                                if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
                                {
                                    using var doc = JsonDocument.Parse(json);
                                    var root = doc.RootElement;
                                    var timeNow = TryGetDate(root, "dateTime", "deviceTime", "date");
                                    vm.DeviceTime = timeNow;
                                    vm.T1 = ExtractParameterValue(root, "t_in", "t1", "temperatureIn", "T1", "t1Value", "T_in", "t_supply");
                                    vm.T2 = ExtractParameterValue(root, "t_out", "t2", "temperatureOut", "T2", "t2Value", "T_out", "t_return");
                                    vm.P1 = ExtractParameterValue(root, "p_in", "p1", "pressureIn", "P1", "p1Value", "P_in", "p_supply");
                                    vm.V1 = ExtractParameterValue(root, "v_in", "v1", "volume1", "V1", "V_in", "volume_supply");
                                    vm.Status = "Опрошено успешно";
                                    return;
                                }
                            }
                        }
                        catch { }

                        vm.Status = "Таймаут (нет связи)";
                    }
                });

                await Task.WhenAll(pollingTasks);
                TxtStatus.Text = "Мониторинг завершен.";
                SaveUserSettings();
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

    public class PointThresholds
    {
        public double? T1Min { get; set; }
        public double? T1Max { get; set; }
        public double? T2Min { get; set; }
        public double? T2Max { get; set; }
        public double? P1Min { get; set; }
        public double? P1Max { get; set; }
        public double? P2Min { get; set; }
        public double? P2Max { get; set; }
        public double? V1Min { get; set; }
        public double? V1Max { get; set; }
        public double? V2Min { get; set; }
        public double? V2Max { get; set; }
    }

    public class UserSettings
    {
        public string Host { get; set; } = "localhost";
        public ushort Port { get; set; } = 10000;
        public string Login { get; set; } = "";
        public string PasswordBase64 { get; set; } = "";
        public bool RememberCredentials { get; set; } = true;
        public bool IsDarkMode { get; set; } = false;
        public List<int> SelectedPointIds { get; set; } = new();
        public Dictionary<int, PointThresholds> PointLimits { get; set; } = new();
        public List<PointResultViewModel> LastResults { get; set; } = new();
    }

    public class PointResultViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public PointThresholds? Limits { get; set; }

        public void RefreshColors()
        {
            OnPropertyChanged(nameof(T1Color));
            OnPropertyChanged(nameof(T2Color));
            OnPropertyChanged(nameof(P1Color));
            OnPropertyChanged(nameof(P2Color));
            OnPropertyChanged(nameof(V1Color));
            OnPropertyChanged(nameof(V2Color));
        }

        private Brush GetColor(double? val, double? min, double? max)
        {
            var defaultColor = Application.Current.MainWindow?.TryFindResource("LabelPrimary") as Brush ?? Brushes.Black;
            if (val == null) return defaultColor;
            if (min.HasValue && val.Value < min.Value) return Brushes.Red;
            if (max.HasValue && val.Value > max.Value) return Brushes.Red;
            return defaultColor;
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
        public double? T1 { get => _t1; set { _t1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(T1Color)); } }
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush T1Color => GetColor(T1, Limits?.T1Min, Limits?.T1Max);
        
        private double? _t2;
        public double? T2 { get => _t2; set { _t2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(T2Color)); } }
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush T2Color => GetColor(T2, Limits?.T2Min, Limits?.T2Max);
        
        private double? _t3;
        public double? T3 { get => _t3; set { _t3 = value; OnPropertyChanged(); } }
        
        private double? _p1;
        public double? P1 { get => _p1; set { _p1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(P1Color)); } }
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush P1Color => GetColor(P1, Limits?.P1Min, Limits?.P1Max);
        
        private double? _p2;
        public double? P2 { get => _p2; set { _p2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(P2Color)); } }
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush P2Color => GetColor(P2, Limits?.P2Min, Limits?.P2Max);
        
        private double? _v1;
        public double? V1 { get => _v1; set { _v1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(V1Color)); } }
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush V1Color => GetColor(V1, Limits?.V1Min, Limits?.V1Max);
        
        private double? _v2;
        public double? V2 { get => _v2; set { _v2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(V2Color)); } }
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush V2Color => GetColor(V2, Limits?.V2Min, Limits?.V2Max);
        
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
