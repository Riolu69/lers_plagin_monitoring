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
using System.Threading;
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
        private readonly ObservableCollection<PointResultViewModel> _trendHistory = new();
        private UserSettings _settings = new();
        
        private List<MeasurePointItem> _editingPoints = new();
        private CancellationTokenSource? _generalMonitorCts;
        private CancellationTokenSource? _trendCts;
        private int _activeTrendPointId = 0;

        private static string SettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LersMonitor", "settings.json");

        public MainWindow()
        {
            InitializeComponent();
            GridResults.ItemsSource = _results;
            GridTrend.ItemsSource = _trendHistory;
            
            CmbTrendDuration.ItemsSource = Enumerable.Range(1, 10).ToList();
            CmbTrendDuration.SelectedItem = 3;

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
            _generalMonitorCts?.Cancel();
            StopNativeLersTrend();
            SaveUserSettings();
            base.OnClosed(e);
        }

        private void TabMode_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewGeneral == null || ViewTrend == null) return;

            if (TabAll.IsChecked == true)
            {
                ViewGeneral.Visibility = Visibility.Visible;
                ViewTrend.Visibility = Visibility.Collapsed;
            }
            else
            {
                ViewGeneral.Visibility = Visibility.Collapsed;
                ViewTrend.Visibility = Visibility.Visible;
            }
        }

        private void ChkDarkMode_Click(object sender, RoutedEventArgs e)
        {
            bool isDark = ChkDarkMode.IsChecked == true;
            ApplyTheme(isDark);
            _settings.IsDarkMode = isDark;
            SaveUserSettings();
            
            foreach (var r in _results) r.RefreshColors();
            foreach (var r in _trendHistory) r.RefreshColors();
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

                CmbTrendPoint.ItemsSource = _allPoints;
                if (_allPoints.Any()) CmbTrendPoint.SelectedIndex = 0;

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

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => ListPoints.SelectAll();
        private void BtnClearSelection_Click(object sender, RoutedEventArgs e) => ListPoints.UnselectAll();

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

        // --- ГРУППОВАЯ И ОДИНОЧНАЯ НАСТРОЙКА ГРАНИЦ ---
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var selected = ListPoints.SelectedItems.Cast<MeasurePointItem>().ToList();
            if (!selected.Any())
            {
                MessageBox.Show("Выберите хотя бы одну точку для настройки границ.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _editingPoints = selected;

            if (_editingPoints.Count == 1)
            {
                var point = _editingPoints.First();
                TxtLimitsTitle.Text = $"Границы: {point.Title}";
                TxtLimitsSubtitle.Text = point.NodeTitle;

                if (_settings.PointLimits.TryGetValue(point.Id, out var limits))
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
                    ClearLimitsFields();
                }
            }
            else
            {
                TxtLimitsTitle.Text = $"Групповая настройка ({_editingPoints.Count} точек)";
                TxtLimitsSubtitle.Text = "Заполните только те поля, которые хотите применить ко всем выбранным";
                ClearLimitsFields();
            }

            OverlayLimits.Visibility = Visibility.Visible;
        }

        private void ClearLimitsFields()
        {
            TxtT1Min.Text = ""; TxtT1Max.Text = "";
            TxtT2Min.Text = ""; TxtT2Max.Text = "";
            TxtP1Min.Text = ""; TxtP1Max.Text = "";
            TxtP2Min.Text = ""; TxtP2Max.Text = "";
            TxtV1Min.Text = ""; TxtV1Max.Text = "";
            TxtV2Min.Text = ""; TxtV2Max.Text = "";
        }

        private void BtnCancelLimits_Click(object sender, RoutedEventArgs e)
        {
            OverlayLimits.Visibility = Visibility.Collapsed;
            _editingPoints.Clear();
        }

        private void BtnSaveLimits_Click(object sender, RoutedEventArgs e)
        {
            if (!_editingPoints.Any()) return;

            var t1Min = ParseDouble(TxtT1Min.Text);
            var t1Max = ParseDouble(TxtT1Max.Text);
            var t2Min = ParseDouble(TxtT2Min.Text);
            var t2Max = ParseDouble(TxtT2Max.Text);
            var p1Min = ParseDouble(TxtP1Min.Text);
            var p1Max = ParseDouble(TxtP1Max.Text);
            var p2Min = ParseDouble(TxtP2Min.Text);
            var p2Max = ParseDouble(TxtP2Max.Text);
            var v1Min = ParseDouble(TxtV1Min.Text);
            var v1Max = ParseDouble(TxtV1Max.Text);
            var v2Min = ParseDouble(TxtV2Min.Text);
            var v2Max = ParseDouble(TxtV2Max.Text);

            foreach (var point in _editingPoints)
            {
                if (!_settings.PointLimits.TryGetValue(point.Id, out var limits))
                {
                    limits = new PointThresholds();
                    _settings.PointLimits[point.Id] = limits;
                }

                if (!string.IsNullOrWhiteSpace(TxtT1Min.Text)) limits.T1Min = t1Min;
                if (!string.IsNullOrWhiteSpace(TxtT1Max.Text)) limits.T1Max = t1Max;
                if (!string.IsNullOrWhiteSpace(TxtT2Min.Text)) limits.T2Min = t2Min;
                if (!string.IsNullOrWhiteSpace(TxtT2Max.Text)) limits.T2Max = t2Max;
                if (!string.IsNullOrWhiteSpace(TxtP1Min.Text)) limits.P1Min = p1Min;
                if (!string.IsNullOrWhiteSpace(TxtP1Max.Text)) limits.P1Max = p1Max;
                if (!string.IsNullOrWhiteSpace(TxtP2Min.Text)) limits.P2Min = p2Min;
                if (!string.IsNullOrWhiteSpace(TxtP2Max.Text)) limits.P2Max = p2Max;
                if (!string.IsNullOrWhiteSpace(TxtV1Min.Text)) limits.V1Min = v1Min;
                if (!string.IsNullOrWhiteSpace(TxtV1Max.Text)) limits.V1Max = v1Max;
                if (!string.IsNullOrWhiteSpace(TxtV2Min.Text)) limits.V2Min = v2Min;
                if (!string.IsNullOrWhiteSpace(TxtV2Max.Text)) limits.V2Max = v2Max;

                var vm = _results.FirstOrDefault(r => r.Id == point.Id);
                if (vm != null)
                {
                    vm.Limits = limits;
                    vm.RefreshColors();
                }
            }

            SaveUserSettings();
            OverlayLimits.Visibility = Visibility.Collapsed;
            _editingPoints.Clear();
        }

        private double? ParseDouble(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (double.TryParse(text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out double res))
                return res;
            return null;
        }

        private void UpdatePointValues(PointResultViewModel vm, JsonElement root)
        {
            vm.T1 = ExtractParameterValue(root, "t_in", "t1", "temperatureIn", "T1", "t1Value", "T_in", "t_supply");
            vm.T2 = ExtractParameterValue(root, "t_out", "t2", "temperatureOut", "T2", "t2Value", "T_out", "t_return");
            vm.T3 = ExtractParameterValue(root, "t3", "temperature3", "T3", "t3Value");
            vm.P1 = ExtractParameterValue(root, "p_in", "p1", "pressureIn", "P1", "p1Value", "P_in", "p_supply");
            vm.P2 = ExtractParameterValue(root, "p_out", "p2", "pressureOut", "P2", "p2Value", "P_out", "p_return");
            vm.V1 = ExtractParameterValue(root, "v_in", "v1", "volume1", "V1", "V_in", "volume_supply");
            vm.V2 = ExtractParameterValue(root, "v_out", "v2", "volume2", "V2", "V_out", "volume_return");
            vm.M1 = ExtractParameterValue(root, "m_in", "m1", "mass1", "M1", "M_in", "mass_supply");
            vm.M2 = ExtractParameterValue(root, "m_out", "m2", "mass2", "M2", "M_out", "mass_return");
            vm.Q  = ExtractParameterValue(root, "q_delta", "q", "q_in", "q_out", "q_gvs", "Q_delta", "Q_in", "Q_out", "Q", "heat", "heatEnergy", "thermalEnergy", "q_total", "heatPower", "q1", "q2");
        }

        // --- МОНИТОРИНГ ТЕКУЩИХ (НАТИВНЫЙ РЕЖИМ ЛЭРС) ---
        private async void BtnStartTrend_Click(object sender, RoutedEventArgs e)
        {
            if (CmbTrendPoint.SelectedItem is not MeasurePointItem selectedPoint)
            {
                MessageBox.Show("Выберите точку для мониторинга.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int durationMinutes = CmbTrendDuration.SelectedItem is int min ? min : 3;
            var maxEndTime = DateTime.UtcNow.AddMinutes(durationMinutes);

            _activeTrendPointId = selectedPoint.Id;
            _trendCts = new CancellationTokenSource();
            var token = _trendCts.Token;

            BtnStartTrend.Visibility = Visibility.Collapsed;
            BtnStopTrend.Visibility = Visibility.Visible;
            CmbTrendPoint.IsEnabled = false;
            CmbTrendDuration.IsEnabled = false;

            _settings.PointLimits.TryGetValue(selectedPoint.Id, out var limits);

            TxtStatus.Text = $"Запуск мониторинга для {selectedPoint.Title} (макс. {durationMinutes} мин)...";

            try
            {
                try
                {
                    var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{selectedPoint.Id}/Currents");
                    pollReq.Content = new ByteArrayContent(Array.Empty<byte>());
                    pollReq.Content.Headers.ContentLength = 0;
                    await _http.SendAsync(pollReq, token);
                }
                catch { }

                DateTime? lastObservedTime = null;

                while (!token.IsCancellationRequested)
                {
                    if (DateTime.UtcNow >= maxEndTime)
                    {
                        TxtStatus.Text = $"Лимит времени ({durationMinutes} мин) исчерпан. Опрос завершён.";
                        break;
                    }

                    try
                    {
                        var res = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{selectedPoint.Id}/Consumption/Last", token);
                        if (res.IsSuccessStatusCode)
                        {
                            var json = await res.Content.ReadAsStringAsync(token);
                            if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
                            {
                                using var doc = JsonDocument.Parse(json);
                                var root = doc.RootElement;
                                var timeNow = TryGetDate(root, "dateTime", "deviceTime", "date") ?? DateTime.Now;

                                if (lastObservedTime == null || timeNow > lastObservedTime)
                                {
                                    lastObservedTime = timeNow;

                                    var row = new PointResultViewModel
                                    {
                                        Id = selectedPoint.Id,
                                        Title = selectedPoint.Title,
                                        NodeTitle = selectedPoint.NodeTitle,
                                        DeviceTime = timeNow,
                                        Limits = limits,
                                        Status = "Снято"
                                    };
                                    
                                    UpdatePointValues(row, root);

                                    _trendHistory.Insert(0, row);
                                    row.RefreshColors();
                                    
                                    var remaining = maxEndTime - DateTime.UtcNow;
                                    TxtStatus.Text = $"Данные: {timeNow:HH:mm:ss} (осталось {(int)remaining.TotalMinutes} мин {(int)remaining.Seconds} сек)";
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }

                    await Task.Delay(3000, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                StopNativeLersTrend();
                BtnStartTrend.Visibility = Visibility.Visible;
                BtnStopTrend.Visibility = Visibility.Collapsed;
                CmbTrendPoint.IsEnabled = true;
                CmbTrendDuration.IsEnabled = true;
                if (!TxtStatus.Text.StartsWith("Лимит времени"))
                    TxtStatus.Text = "Мониторинг точки остановлен.";
            }
        }

        private void StopNativeLersTrend()
        {
            _trendCts?.Cancel();
            if (_activeTrendPointId > 0 && !string.IsNullOrEmpty(_baseUrl))
            {
                try
                {
                    var stopReq = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/Poll/MeasurePoints/{_activeTrendPointId}/Currents");
                    _http.SendAsync(stopReq);
                }
                catch { }
                _activeTrendPointId = 0;
            }
        }

        private void BtnStopTrend_Click(object sender, RoutedEventArgs e)
        {
            StopNativeLersTrend();
        }

        private void BtnClearTrend_Click(object sender, RoutedEventArgs e)
        {
            _trendHistory.Clear();
        }

        private void BtnCancelMonitor_Click(object sender, RoutedEventArgs e)
        {
            _generalMonitorCts?.Cancel();
            TxtStatus.Text = "Опрос отменен пользователем.";
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

        // Вспомогательный метод для загрузки и обновления значений точки
        private async Task<(bool isNew, DateTime? timeNow)> TryFetchAndUpdateAsync(PointResultViewModel vm, DateTime? timeBefore, CancellationToken token)
        {
            try
            {
                var res = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{vm.Id}/Consumption/Last", token);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(token);
                    if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var timeNow = TryGetDate(root, "dateTime", "deviceTime", "date");

                        bool isNew = timeNow != null && (timeBefore == null || timeNow > timeBefore);

                        vm.DeviceTime = timeNow;
                        UpdatePointValues(vm, root);
                        vm.RefreshColors();

                        return (isNew, timeNow);
                    }
                }
            }
            catch { }
            return (false, null);
        }

        // --- УМНЫЙ ОБЩИЙ ОПРОС ВЫБРАННЫХ ТОЧЕК (ДИНАМИЧЕСКИЙ) ---
        private async void BtnStartMonitor_Click(object sender, RoutedEventArgs e)
        {
            var selectedPoints = ListPoints.SelectedItems.Cast<MeasurePointItem>().ToList();
            if (!selectedPoints.Any())
            {
                MessageBox.Show("Выберите точки учёта в списке слева.");
                return;
            }

            SaveUserSettings(selectedPoints.Select(p => p.Id).ToList());

            _generalMonitorCts = new CancellationTokenSource();
            var token = _generalMonitorCts.Token;

            _results.Clear();
            foreach (var p in selectedPoints)
            {
                _settings.PointLimits.TryGetValue(p.Id, out var pointLimits);
                _results.Add(new PointResultViewModel
                {
                    Id = p.Id,
                    Title = p.Title,
                    NodeTitle = p.NodeTitle,
                    Status = "Ожидание очереди...",
                    Limits = pointLimits
                });
            }

            try
            {
                BtnStartMonitor.IsEnabled = false;
                BtnCancelMonitor.Visibility = Visibility.Visible;
                TxtStatus.Text = "Фиксация времени до опроса...";

                var initialTimes = new Dictionary<int, DateTime?>();
                await Task.WhenAll(selectedPoints.Select(async p =>
                {
                    try
                    {
                        var res = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{p.Id}/Consumption/Last", token);
                        if (res.IsSuccessStatusCode)
                        {
                            var json = await res.Content.ReadAsStringAsync(token);
                            if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
                            {
                                using var doc = JsonDocument.Parse(json);
                                initialTimes[p.Id] = TryGetDate(doc.RootElement, "dateTime", "deviceTime", "date");
                            }
                        }
                    }
                    catch { }
                }));

                TxtStatus.Text = "Запуск интеллектуального опроса...";

                const int maxChecks = 100; // Увеличенный таймаут до 5 минут 
                const int checkIntervalMs = 3000;
                var rnd = new Random();

                // Опрашиваем ВСЕ точки независимыми параллельными задачами. Без группировки по имени объекта!
                var pollingTasks = selectedPoints.Select(async pt =>
                {
                    var vm = _results.FirstOrDefault(r => r.Id == pt.Id);
                    if (vm == null) return;

                    initialTimes.TryGetValue(pt.Id, out var timeBefore);
                    bool pollStarted = false;
                    bool pointFinished = false;
                    string lastError = "";

                    // Слегка размазываем старт по времени, чтобы не заДДОСить сервер тысячей TCP соединений разом
                    await Task.Delay(rnd.Next(50, 500), token);

                    for (int attempt = 1; attempt <= maxChecks; attempt++)
                    {
                        if (token.IsCancellationRequested) break;

                        // 1. Проверяем БД — вдруг данные уже обновились (опросились попутно соседним потоком/точкой)
                        var (isNew, tNow) = await TryFetchAndUpdateAsync(vm, timeBefore, token);
                        if (isNew)
                        {
                            vm.Status = pollStarted ? "Опрошено успешно" : "Опрошено попутно (успешно)";
                            pointFinished = true;
                            break;
                        }

                        // 2. Если данные старые, пробуем занять порт 
                        if (!pollStarted)
                        {
                            try
                            {
                                var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{pt.Id}/Currents?once=true");
                                pollReq.Content = new ByteArrayContent(Array.Empty<byte>());
                                var pollRes = await _http.SendAsync(pollReq, token);

                                if (pollRes.IsSuccessStatusCode)
                                {
                                    pollStarted = true;
                                    vm.Status = $"Идёт опрос прибора ({attempt}/{maxChecks})...";
                                }
                                else if ((int)pollRes.StatusCode == 422)
                                {
                                    string errStr = await pollRes.Content.ReadAsStringAsync(token);
                                    lastError = ParseLersError(errStr, 422);
                                    vm.Status = $"В очереди: {lastError} [{attempt}/{maxChecks}]";
                                    // Порт физически занят (этой же котельной или другим модемом). 
                                    // Мы не сдаёмся! Ждём 3 секунды и на следующей итерации цикла снова попытаемся.
                                }
                                else
                                {
                                    string errStr = await pollRes.Content.ReadAsStringAsync(token);
                                    lastError = ParseLersError(errStr, (int)pollRes.StatusCode);
                                    vm.Status = $"Ошибка запуска: {lastError} [{attempt}/{maxChecks}]";
                                }
                            }
                            catch (Exception ex)
                            {
                                lastError = "Сбой сети";
                                vm.Status = $"В очереди (нет связи с ЛЭРС) [{attempt}/{maxChecks}]";
                            }
                        }
                        else
                        {
                            vm.Status = $"Идёт опрос прибора ({attempt}/{maxChecks})...";
                        }

                        await Task.Delay(checkIntervalMs, token);
                    }

                    // Если 100 итераций прошло, а данных так и нет
                    if (!pointFinished && !token.IsCancellationRequested)
                    {
                        var (isNewEnd, tEnd) = await TryFetchAndUpdateAsync(vm, timeBefore, token);
                        if (isNewEnd)
                        {
                            vm.Status = "Опрошено успешно";
                        }
                        else
                        {
                            vm.Status = pollStarted ? "Таймаут (прибор не ответил)" : $"Отказ очереди: {lastError}";
                        }
                    }
                });

                await Task.WhenAll(pollingTasks);
                
                if (!token.IsCancellationRequested)
                    TxtStatus.Text = "Мониторинг завершен.";
                SaveUserSettings();
            }
            catch (OperationCanceledException)
            {
                TxtStatus.Text = "Опрос отменен.";
                foreach (var r in _results.Where(x => !x.Status.Contains("успешно")))
                    r.Status = "Отменено";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка во время опроса: {ex.Message}");
                TxtStatus.Text = "Сбой операции";
            }
            finally
            {
                BtnStartMonitor.IsEnabled = true;
                BtnCancelMonitor.Visibility = Visibility.Collapsed;
            }
        }

        private static double? ExtractParameterValue(JsonElement root, params string[] targetNames)
        {
            foreach (var name in targetNames)
            {
                if (root.TryGetProperty(name, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double val)) return val;
                    if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double sVal)) return sVal;
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

                                bool matchesName = targetNames.Any(tn => 
                                    typeName.Equals(tn, StringComparison.OrdinalIgnoreCase) ||
                                    typeName.IndexOf(tn, StringComparison.OrdinalIgnoreCase) >= 0);

                                if (matchesName)
                                {
                                    if (item.TryGetProperty("value", out var valProp) || 
                                        item.TryGetProperty("val", out valProp) || 
                                        item.TryGetProperty("currentValue", out valProp))
                                    {
                                        if (valProp.ValueKind == JsonValueKind.Number && valProp.TryGetDouble(out double v)) return v;
                                        if (valProp.ValueKind == JsonValueKind.String && double.TryParse(valProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double sv)) return sv;
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
