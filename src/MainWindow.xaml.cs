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
using System.Windows.Threading;

namespace LersMonitor
{
    public partial class App : Application { }

    public class DashSettings : INotifyPropertyChanged
    {
        public static DashSettings Instance { get; } = new DashSettings();
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        private bool _showT = true; public bool ShowT { get => _showT; set { _showT = value; OnPropertyChanged(); } }
        private bool _showP = true; public bool ShowP { get => _showP; set { _showP = value; OnPropertyChanged(); } }
        private bool _showV = true; public bool ShowV { get => _showV; set { _showV = value; OnPropertyChanged(); } }
        private bool _showM = true; public bool ShowM { get => _showM; set { _showM = value; OnPropertyChanged(); } }
        private bool _showQ = true; public bool ShowQ { get => _showQ; set { _showQ = value; OnPropertyChanged(); } }
    }

    public partial class MainWindow : Window
    {
        private readonly HttpClient _http = new();
        private string? _token;
        private string _baseUrl = "";
        
        public static double? CurrentGlobalTnv { get; set; }

        private List<MeasurePointItem> _allPoints = new();
        private readonly ObservableCollection<PointResultViewModel> _results = new();
        private readonly ObservableCollection<PointResultViewModel> _trendHistory = new();
        private readonly ObservableCollection<DashboardNodeViewModel> _dashboardNodes = new();
        private readonly List<PointResultViewModel> _dashboardFlatPoints = new();
        
        private ObservableCollection<TempSchedulePointViewModel> _currentScheduleEdit = new();

        private UserSettings _settings = new();
        private List<MeasurePointItem> _editingPoints = new();
        
        private CancellationTokenSource? _generalMonitorCts;
        private CancellationTokenSource? _trendCts;
        private CancellationTokenSource? _dashboardCts;
        private int _activeTrendPointId = 0;
        
        private DispatcherTimer _themeTimer;
        private DispatcherTimer _globalTnvTimer;

        private static string SettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LersMonitor", "settings.json");

        public MainWindow()
        {
            InitializeComponent();
            GridResults.ItemsSource = _results;
            GridTrend.ItemsSource = _trendHistory;
            ItemsDashboard.ItemsSource = _dashboardNodes;
            GridTempSchedule.ItemsSource = _currentScheduleEdit;
            
            CmbTrendDuration.ItemsSource = Enumerable.Range(1, 10).ToList();
            CmbTrendDuration.SelectedItem = 3;

            CmbDashboardInterval.ItemsSource = new List<int> { 1, 2, 3, 5, 10, 15, 20, 30, 60 };
            CmbDashboardInterval.SelectedItem = 10;

            _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _themeTimer.Tick += ThemeTimer_Tick;
            
            _globalTnvTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _globalTnvTimer.Tick += async (s, e) => await UpdateGlobalTnvAsync();

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
                        
                        TxtThemeLight.Text = _settings.TimeLightStr;
                        TxtThemeDark.Text = _settings.TimeDarkStr;
                        SldUiScale.Value = _settings.UiScale <= 0 ? 1.0 : _settings.UiScale;

                        if (!string.IsNullOrEmpty(_settings.PasswordBase64))
                        {
                            try {
                                var bytes = Convert.FromBase64String(_settings.PasswordBase64);
                                TxtPassword.Password = Encoding.UTF8.GetString(bytes);
                            } catch {}
                        }
                        
                        CmbTheme.SelectedIndex = _settings.ThemeMode;
                        
                        if (_settings.PointLimits == null) _settings.PointLimits = new Dictionary<int, PointThresholds>();

                        if (_settings.LastResults != null && _settings.LastResults.Any())
                        {
                            foreach (var r in _settings.LastResults)
                            {
                                if (_settings.PointLimits.TryGetValue(r.Id, out var limits)) r.Limits = limits;
                                _results.Add(r);
                                r.RefreshColors();
                            }
                        }
                    }
                }
            }
            catch { }
            
            _themeTimer.Start();
            ApplyThemeLogic();
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

                _settings.ThemeMode = CmbTheme.SelectedIndex;
                _settings.TimeLightStr = TxtThemeLight.Text;
                _settings.TimeDarkStr = TxtThemeDark.Text;
                _settings.UiScale = SldUiScale.Value;

                if (selectedPointIds != null) _settings.SelectedPointIds = selectedPointIds;
                _settings.LastResults = _results.ToList();

                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _generalMonitorCts?.Cancel();
            _dashboardCts?.Cancel();
            _themeTimer.Stop();
            _globalTnvTimer.Stop();
            StopNativeLersTrend();
            SaveUserSettings();
            base.OnClosed(e);
        }

        private void TabMode_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewGeneral == null || ViewTrend == null || ViewDashboard == null) return;
            ViewGeneral.Visibility = Visibility.Collapsed;
            ViewTrend.Visibility = Visibility.Collapsed;
            ViewDashboard.Visibility = Visibility.Collapsed;
            if (TabAll.IsChecked == true) ViewGeneral.Visibility = Visibility.Visible;
            else if (TabSingle.IsChecked == true) ViewTrend.Visibility = Visibility.Visible;
            else if (TabDashboard.IsChecked == true) ViewDashboard.Visibility = Visibility.Visible;
        }

        private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PanelAutoTheme == null) return;
            PanelAutoTheme.Visibility = CmbTheme.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            ApplyThemeLogic();
            SaveUserSettings();
        }

        private void ThemeTimer_Tick(object? sender, EventArgs e) => ApplyThemeLogic();

        private void ApplyThemeLogic()
        {
            if (CmbTheme == null) return;
            bool isDark = false;
            if (CmbTheme.SelectedIndex == 0) isDark = false;
            else if (CmbTheme.SelectedIndex == 1) isDark = true;
            else if (CmbTheme.SelectedIndex == 2)
            {
                if (TimeSpan.TryParse(TxtThemeLight.Text, out var tLight) && TimeSpan.TryParse(TxtThemeDark.Text, out var tDark))
                {
                    var now = DateTime.Now.TimeOfDay;
                    bool isLightTime = now >= tLight && now < tDark;
                    isDark = !isLightTime;
                }
            }

            ApplyThemeColors(isDark);
        }

        private void ApplyThemeColors(bool isDark)
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

            foreach (var r in _results) r.RefreshColors();
            foreach (var r in _trendHistory) r.RefreshColors();
            foreach (var r in _dashboardFlatPoints) r.RefreshColors();
            foreach (var node in _dashboardNodes) node.Refresh();
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

                if (string.IsNullOrWhiteSpace(host) || !ushort.TryParse(portStr, out ushort port)) {
                    MessageBox.Show("Укажите корректный IP/хост и порт.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _baseUrl = $"http://{host}:{port}/api/v1";

                var loginPayload = JsonSerializer.Serialize(new { login, password, application = "LersMonitor WPF" });
                var loginContent = new StringContent(loginPayload, Encoding.UTF8, "application/json");
                _http.DefaultRequestHeaders.Accept.Clear();
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _http.PostAsync($"{_baseUrl}/Login", loginContent);
                var loginResponseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) throw new Exception($"Код {(int)response.StatusCode}: {loginResponseJson}");

                using (var doc = JsonDocument.Parse(loginResponseJson))
                {
                    if (doc.RootElement.TryGetProperty("token", out var tokenProp)) _token = tokenProp.GetString();
                    else _token = loginResponseJson.Trim('"');
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
                        JsonElement nodesArr = nDoc.RootElement.ValueKind == JsonValueKind.Array ? nDoc.RootElement : (nDoc.RootElement.TryGetProperty("nodes", out var nProp) ? nProp : nDoc.RootElement);
                        foreach (var node in nodesArr.EnumerateArray())
                        {
                            int id = node.TryGetProperty("id", out var idElem) ? idElem.GetInt32() : 0;
                            string title = node.TryGetProperty("title", out var titleElem) ? titleElem.GetString() ?? "" : "";
                            if (id > 0 && !string.IsNullOrEmpty(title)) nodeTitlesById[id] = title;
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
                    JsonElement arrayElement = pDoc.RootElement.ValueKind == JsonValueKind.Array ? pDoc.RootElement : (pDoc.RootElement.TryGetProperty("measurePoints", out var mProp) ? mProp : pDoc.RootElement);
                    foreach (var item in arrayElement.EnumerateArray())
                    {
                        int id = item.TryGetProperty("id", out var idElem) ? idElem.GetInt32() : 0;
                        string pointTitle = item.TryGetProperty("title", out var titleElem) ? titleElem.GetString() ?? "" : "Точка";
                        int nodeId = item.TryGetProperty("nodeId", out var nodeIdElem) ? nodeIdElem.GetInt32() : 0;
                        string nodeTitle = item.TryGetProperty("nodeTitle", out var nodeTitleElem) ? nodeTitleElem.GetString() ?? "" : "";

                        if (string.IsNullOrEmpty(nodeTitle) && nodeId > 0 && nodeTitlesById.ContainsKey(nodeId)) nodeTitle = nodeTitlesById[nodeId];
                        if (string.IsNullOrEmpty(nodeTitle)) nodeTitle = "Объект";

                        if (id > 0) _allPoints.Add(new MeasurePointItem { Id = id, Title = pointTitle, NodeTitle = nodeTitle });
                    }
                }

                _allPoints = _allPoints.OrderBy(p => p.NodeTitle).ThenBy(p => p.Title).ToList();
                
                var uniqueNodes = _allPoints.Select(p => p.NodeTitle).Distinct().OrderBy(n => n).ToList();
                uniqueNodes.Insert(0, "Все объекты");
                CmbNode.ItemsSource = uniqueNodes; CmbNode.SelectedIndex = 0;
                CmbNodeDashboard.ItemsSource = uniqueNodes; CmbNodeDashboard.SelectedIndex = 0;
                
                CmbSystemType.ItemsSource = new List<string> { "Все системы", "Теплоснабжение", "ГВС", "Подпитка", "ХВС" }; CmbSystemType.SelectedIndex = 0;
                CmbSystemTypeDashboard.ItemsSource = new List<string> { "Все системы", "Теплоснабжение", "ГВС", "Подпитка", "ХВС" }; CmbSystemTypeDashboard.SelectedIndex = 0;

                CmbTrendPoint.ItemsSource = _allPoints;
                if (_allPoints.Any()) CmbTrendPoint.SelectedIndex = 0;
                
                CmbTnvSource.ItemsSource = _allPoints;
                if (_settings.TnvSourcePointId.HasValue) 
                {
                    CmbTnvSource.SelectedItem = _allPoints.FirstOrDefault(p => p.Id == _settings.TnvSourcePointId.Value);
                }
                _globalTnvTimer.Start();

                ApplyFilters(); ApplyFiltersDashboard();

                if (_settings.SelectedPointIds != null && _settings.SelectedPointIds.Any())
                {
                    foreach (var p in _allPoints.Where(p => _settings.SelectedPointIds.Contains(p.Id))) ListPoints.SelectedItems.Add(p);
                }

                BtnStartMonitor.IsEnabled = true; BtnStartDashboard.IsEnabled = true;
                TxtStatus.Text = $"Подключено к {host}:{port}. Загружено точек: {_allPoints.Count}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка подключения:\n\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ошибка подключения";
            }
            finally { BtnConnect.IsEnabled = true; }
        }

        private async void CmbTnvSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbTnvSource.SelectedItem is MeasurePointItem pt)
            {
                _settings.TnvSourcePointId = pt.Id;
                SaveUserSettings();
                TxtGlobalTnv.Text = "...";
                await UpdateGlobalTnvAsync();
            }
        }
        
        private async Task UpdateGlobalTnvAsync()
        {
            if (CmbTnvSource.SelectedItem is MeasurePointItem pt)
            {
                try
                {
                    var res = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{pt.Id}/Consumption/Last");
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
                        {
                            using var doc = JsonDocument.Parse(json);
                            var tnv = ExtractParameterValue(doc.RootElement, "t_out_env", "t_nv", "T_nv", "outdoorTemp", "t_outdoor", "t_env");
                            if (tnv.HasValue)
                            {
                                CurrentGlobalTnv = tnv;
                                TxtGlobalTnv.Text = tnv.Value.ToString("F1");
                                foreach (var p in _dashboardFlatPoints) p.RefreshColors();
                                foreach (var p in _results) p.RefreshColors();
                                foreach (var node in _dashboardNodes) node.Refresh();
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void BtnNodeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DashboardNodeViewModel node)
            {
                node.IsPaused = !node.IsPaused;
                
                if (node.IsPaused)
                {
                    foreach (var p in node.Points) 
                    {
                        p.Status = "Опрос приостановлен";
                        p.RefreshColors();
                    }
                    node.Refresh();
                }
                else
                {
                    foreach (var p in node.Points) 
                    {
                        p.Status = "Внеочередной запуск...";
                        p.RefreshColors();
                    }
                    node.Refresh();

                    var pointsToPoll = _allPoints.Where(p => node.Points.Any(np => np.Id == p.Id)).ToList();
                    CancellationToken token = _dashboardCts?.Token ?? new CancellationTokenSource().Token;
                    
                    _ = Task.Run(async () => {
                        try {
                            var progress = new Progress<string>();
                            await RunSmartPollingIterationAsync(pointsToPoll, _dashboardFlatPoints, progress, token);
                        } catch {}
                    });
                }
            }
        }

        private void BtnRetryPoint_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PointResultViewModel vm)
            {
                var pt = _allPoints.FirstOrDefault(p => p.Id == vm.Id);
                if (pt == null) return;
                
                vm.Status = "Внеочередной опрос...";
                vm.RefreshColors();

                var pointsToPoll = new List<MeasurePointItem> { pt };
                CancellationToken token = _dashboardCts?.Token ?? new CancellationTokenSource().Token;
                
                _ = Task.Run(async () => {
                    try {
                        var progress = new Progress<string>();
                        await RunSmartPollingIterationAsync(pointsToPoll, _dashboardFlatPoints, progress, token);
                    } catch {}
                });
            }
        }

        private void BtnToggleGeneral_Click(object sender, RoutedEventArgs e)
        {
            bool isVis = PanelGeneralLeft.Visibility == Visibility.Visible;
            PanelGeneralLeft.Visibility = isVis ? Visibility.Collapsed : Visibility.Visible;
            if (BtnToggleGenPanel != null) BtnToggleGenPanel.Content = isVis ? "▶" : "◀";
        }

        private void BtnToggleDashboard_Click(object sender, RoutedEventArgs e)
        {
            bool isVis = PanelDashboardLeft.Visibility == Visibility.Visible;
            PanelDashboardLeft.Visibility = isVis ? Visibility.Collapsed : Visibility.Visible;
            if (BtnToggleDashPanel != null) BtnToggleDashPanel.Content = isVis ? "▶" : "◀";
        }

        private void ApplyFilters_Changed(object sender, RoutedEventArgs e) => ApplyFilters();

        private void ApplyFiltersDashboard_Changed(object sender, RoutedEventArgs e) => ApplyFiltersDashboard();

        private void ApplyFilters()
        {
            if (_allPoints == null || !_allPoints.Any()) return;
            string query = TxtSearch.Text.Trim().ToLower();
            string? selectedNode = CmbNode.SelectedItem as string;
            string? selectedType = CmbSystemType.SelectedItem as string;

            var filtered = _allPoints.AsEnumerable();
            if (!string.IsNullOrEmpty(query)) filtered = filtered.Where(p => p.DisplayName.ToLower().Contains(query));
            if (!string.IsNullOrEmpty(selectedNode) && selectedNode != "Все объекты") filtered = filtered.Where(p => p.NodeTitle == selectedNode);
            if (!string.IsNullOrEmpty(selectedType) && selectedType != "Все системы") filtered = filtered.Where(p => p.Title.ToLower().Contains(selectedType.ToLower()));
            ListPoints.ItemsSource = filtered.ToList();
        }

        private void ApplyFiltersDashboard()
        {
            if (_allPoints == null || !_allPoints.Any()) return;
            string query = TxtSearchDashboard.Text.Trim().ToLower();
            string? selectedNode = CmbNodeDashboard.SelectedItem as string;
            string? selectedType = CmbSystemTypeDashboard.SelectedItem as string;

            var filtered = _allPoints.AsEnumerable();
            if (!string.IsNullOrEmpty(query)) filtered = filtered.Where(p => p.DisplayName.ToLower().Contains(query));
            if (!string.IsNullOrEmpty(selectedNode) && selectedNode != "Все объекты") filtered = filtered.Where(p => p.NodeTitle == selectedNode);
            if (!string.IsNullOrEmpty(selectedType) && selectedType != "Все системы") filtered = filtered.Where(p => p.Title.ToLower().Contains(selectedType.ToLower()));
            ListPointsDashboard.ItemsSource = filtered.ToList();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => ListPoints.SelectAll();
        private void BtnClearSelection_Click(object sender, RoutedEventArgs e) => ListPoints.UnselectAll();
        private void BtnSelectAllDashboard_Click(object sender, RoutedEventArgs e) => ListPointsDashboard.SelectAll();
        private void BtnClearSelectionDashboard_Click(object sender, RoutedEventArgs e) => ListPointsDashboard.UnselectAll();

        private void ColumnToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox chk && GridResults != null)
            {
                string headerPrefix = chk.Content?.ToString() + " ";
                foreach (var col in GridResults.Columns)
                {
                    if (col.Header != null && (col.Header.ToString()!.StartsWith(headerPrefix) || col.Header.ToString()!.StartsWith(chk.Content?.ToString() + " (")))
                    {
                        col.Visibility = chk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    }
                }
            }
        }

        private void DashFilter_Click(object sender, RoutedEventArgs e)
        {
            DashSettings.Instance.ShowT = ChkDashT.IsChecked == true;
            DashSettings.Instance.ShowP = ChkDashP.IsChecked == true;
            DashSettings.Instance.ShowV = ChkDashV.IsChecked == true;
            DashSettings.Instance.ShowM = ChkDashM.IsChecked == true;
            DashSettings.Instance.ShowQ = ChkDashQ.IsChecked == true;
        }

        private void BtnSettingsShared_Click(object sender, RoutedEventArgs e)
        {
            var activeList = TabDashboard.IsChecked == true ? ListPointsDashboard : ListPoints;
            var selected = activeList.SelectedItems.Cast<MeasurePointItem>().ToList();
            if (!selected.Any()) { MessageBox.Show("Выберите хотя бы одну точку для настройки границ."); return; }
            _editingPoints = selected;

            if (_editingPoints.Count == 1)
            {
                var point = _editingPoints.First();
                TxtLimitsTitle.Text = $"Границы: {point.Title}";
                TxtLimitsSubtitle.Text = point.NodeTitle;

                if (_settings.PointLimits.TryGetValue(point.Id, out var limits))
                {
                    TxtT1Min.Text = limits.T1Min?.ToString(); TxtT1Max.Text = limits.T1Max?.ToString();
                    TxtT2Min.Text = limits.T2Min?.ToString(); TxtT2Max.Text = limits.T2Max?.ToString();
                    TxtP1Min.Text = limits.P1Min?.ToString(); TxtP1Max.Text = limits.P1Max?.ToString();
                    TxtP2Min.Text = limits.P2Min?.ToString(); TxtP2Max.Text = limits.P2Max?.ToString();
                    TxtV1Min.Text = limits.V1Min?.ToString(); TxtV1Max.Text = limits.V1Max?.ToString();
                    TxtV2Min.Text = limits.V2Min?.ToString(); TxtV2Max.Text = limits.V2Max?.ToString();
                    TxtM1Min.Text = limits.M1Min?.ToString(); TxtM1Max.Text = limits.M1Max?.ToString();
                    TxtM2Min.Text = limits.M2Min?.ToString(); TxtM2Max.Text = limits.M2Max?.ToString();
                    TxtQMin.Text = limits.QMin?.ToString(); TxtQMax.Text = limits.QMax?.ToString();
                }
                else ClearLimitsFields();
            }
            else
            {
                TxtLimitsTitle.Text = $"Групповая настройка ({_editingPoints.Count} точек)";
                TxtLimitsSubtitle.Text = "Заполните только те поля, которые хотите применить";
                ClearLimitsFields();
            }
            OverlayLimits.Visibility = Visibility.Visible;
        }
        
        private void BtnEditTempSchedule_Click(object sender, RoutedEventArgs e)
        {
            _currentScheduleEdit.Clear();
            var point = _editingPoints.FirstOrDefault();
            if (point != null && _settings.PointLimits.TryGetValue(point.Id, out var limits) && limits.Schedule != null)
            {
                foreach (var sp in limits.Schedule) 
                    _currentScheduleEdit.Add(new TempSchedulePointViewModel { Tnv = sp.Tnv, T1 = sp.T1, T2 = sp.T2 });
            }
            OverlayTempSchedule.Visibility = Visibility.Visible;
        }

        private void BtnAddSchedulePoint_Click(object sender, RoutedEventArgs e)
        {
            _currentScheduleEdit.Add(new TempSchedulePointViewModel { Tnv = 0, T1 = 70, T2 = 50 });
        }

        private void BtnRemoveSchedulePoint_Click(object sender, RoutedEventArgs e)
        {
            if (GridTempSchedule.SelectedItem is TempSchedulePointViewModel item)
                _currentScheduleEdit.Remove(item);
        }

        private void BtnCancelSchedule_Click(object sender, RoutedEventArgs e)
        {
            OverlayTempSchedule.Visibility = Visibility.Collapsed;
        }

        private void BtnSaveSchedule_Click(object sender, RoutedEventArgs e)
        {
            var newSchedule = _currentScheduleEdit
                .Select(x => new TempSchedulePoint { Tnv = x.Tnv, T1 = x.T1, T2 = x.T2 })
                .OrderBy(x => x.Tnv)
                .ToList();

            foreach (var point in _editingPoints)
            {
                if (!_settings.PointLimits.TryGetValue(point.Id, out var limits)) { limits = new PointThresholds(); _settings.PointLimits[point.Id] = limits; }
                limits.Schedule = newSchedule;
                
                var vm = _results.FirstOrDefault(r => r.Id == point.Id); if (vm != null) { vm.Limits = limits; vm.RefreshColors(); }
                var vmDash = _dashboardFlatPoints.FirstOrDefault(r => r.Id == point.Id); if (vmDash != null) { vmDash.Limits = limits; vmDash.RefreshColors(); }
            }
            
            foreach (var node in _dashboardNodes) node.Refresh();
            SaveUserSettings();
            OverlayTempSchedule.Visibility = Visibility.Collapsed;
        }

        private void ClearLimitsFields()
        {
            TxtT1Min.Text = ""; TxtT1Max.Text = ""; TxtT2Min.Text = ""; TxtT2Max.Text = "";
            TxtP1Min.Text = ""; TxtP1Max.Text = ""; TxtP2Min.Text = ""; TxtP2Max.Text = "";
            TxtV1Min.Text = ""; TxtV1Max.Text = ""; TxtV2Min.Text = ""; TxtV2Max.Text = "";
            TxtM1Min.Text = ""; TxtM1Max.Text = ""; TxtM2Min.Text = ""; TxtM2Max.Text = "";
            TxtQMin.Text = ""; TxtQMax.Text = "";
        }

        private void BtnCancelLimits_Click(object sender, RoutedEventArgs e)
        {
            OverlayLimits.Visibility = Visibility.Collapsed; _editingPoints.Clear();
        }

        private void BtnSaveLimits_Click(object sender, RoutedEventArgs e)
        {
            if (!_editingPoints.Any()) return;
            var t1Min = ParseDouble(TxtT1Min.Text); var t1Max = ParseDouble(TxtT1Max.Text);
            var t2Min = ParseDouble(TxtT2Min.Text); var t2Max = ParseDouble(TxtT2Max.Text);
            var p1Min = ParseDouble(TxtP1Min.Text); var p1Max = ParseDouble(TxtP1Max.Text);
            var p2Min = ParseDouble(TxtP2Min.Text); var p2Max = ParseDouble(TxtP2Max.Text);
            var v1Min = ParseDouble(TxtV1Min.Text); var v1Max = ParseDouble(TxtV1Max.Text);
            var v2Min = ParseDouble(TxtV2Min.Text); var v2Max = ParseDouble(TxtV2Max.Text);
            var m1Min = ParseDouble(TxtM1Min.Text); var m1Max = ParseDouble(TxtM1Max.Text);
            var m2Min = ParseDouble(TxtM2Min.Text); var m2Max = ParseDouble(TxtM2Max.Text);
            var qMin = ParseDouble(TxtQMin.Text); var qMax = ParseDouble(TxtQMax.Text);

            foreach (var point in _editingPoints)
            {
                if (!_settings.PointLimits.TryGetValue(point.Id, out var limits)) { limits = new PointThresholds(); _settings.PointLimits[point.Id] = limits; }

                if (!string.IsNullOrWhiteSpace(TxtT1Min.Text)) limits.T1Min = t1Min; if (!string.IsNullOrWhiteSpace(TxtT1Max.Text)) limits.T1Max = t1Max;
                if (!string.IsNullOrWhiteSpace(TxtT2Min.Text)) limits.T2Min = t2Min; if (!string.IsNullOrWhiteSpace(TxtT2Max.Text)) limits.T2Max = t2Max;
                if (!string.IsNullOrWhiteSpace(TxtP1Min.Text)) limits.P1Min = p1Min; if (!string.IsNullOrWhiteSpace(TxtP1Max.Text)) limits.P1Max = p1Max;
                if (!string.IsNullOrWhiteSpace(TxtP2Min.Text)) limits.P2Min = p2Min; if (!string.IsNullOrWhiteSpace(TxtP2Max.Text)) limits.P2Max = p2Max;
                if (!string.IsNullOrWhiteSpace(TxtV1Min.Text)) limits.V1Min = v1Min; if (!string.IsNullOrWhiteSpace(TxtV1Max.Text)) limits.V1Max = v1Max;
                if (!string.IsNullOrWhiteSpace(TxtV2Min.Text)) limits.V2Min = v2Min; if (!string.IsNullOrWhiteSpace(TxtV2Max.Text)) limits.V2Max = v2Max;
                if (!string.IsNullOrWhiteSpace(TxtM1Min.Text)) limits.M1Min = m1Min; if (!string.IsNullOrWhiteSpace(TxtM1Max.Text)) limits.M1Max = m1Max;
                if (!string.IsNullOrWhiteSpace(TxtM2Min.Text)) limits.M2Min = m2Min; if (!string.IsNullOrWhiteSpace(TxtM2Max.Text)) limits.M2Max = m2Max;
                if (!string.IsNullOrWhiteSpace(TxtQMin.Text)) limits.QMin = qMin; if (!string.IsNullOrWhiteSpace(TxtQMax.Text)) limits.QMax = qMax;

                var vm = _results.FirstOrDefault(r => r.Id == point.Id); if (vm != null) { vm.Limits = limits; vm.RefreshColors(); }
                var vmDash = _dashboardFlatPoints.FirstOrDefault(r => r.Id == point.Id); if (vmDash != null) { vmDash.Limits = limits; vmDash.RefreshColors(); }
            }
            
            foreach (var node in _dashboardNodes) node.Refresh();
            SaveUserSettings();
            OverlayLimits.Visibility = Visibility.Collapsed; _editingPoints.Clear();
        }

        private double? ParseDouble(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (double.TryParse(text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out double res)) return res;
            return null;
        }

        private void UpdatePointValues(PointResultViewModel vm, JsonElement root)
        {
            vm.T1 = ExtractParameterValue(root, "t_in", "t1", "temperatureIn", "T1", "t1Value", "T_in", "t_supply");
            vm.T2 = ExtractParameterValue(root, "t_out", "t2", "temperatureOut", "T2", "t2Value", "T_out", "t_return");
            vm.P1 = ExtractParameterValue(root, "p_in", "p1", "pressureIn", "P1", "p1Value", "P_in", "p_supply");
            vm.P2 = ExtractParameterValue(root, "p_out", "p2", "pressureOut", "P2", "p2Value", "P_out", "p_return");
            vm.V1 = ExtractParameterValue(root, "v_in", "v1", "volume1", "V1", "V_in", "volume_supply");
            vm.V2 = ExtractParameterValue(root, "v_out", "v2", "volume2", "V2", "V_out", "volume_return");
            vm.M1 = ExtractParameterValue(root, "m_in", "m1", "mass1", "M1", "M_in", "mass_supply");
            vm.M2 = ExtractParameterValue(root, "m_out", "m2", "mass2", "M2", "M_out", "mass_return");
            vm.Q  = ExtractParameterValue(root, "q_delta", "q", "q_in", "q_out", "q_gvs", "Q_delta", "Q", "heat", "thermalEnergy");
        }

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

        private static string ParseLersError(string jsonResponse, int statusCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonResponse)) return $"Отказ ({statusCode})";
                using var doc = JsonDocument.Parse(jsonResponse);
                if (doc.RootElement.TryGetProperty("errorDescription", out var desc)) return desc.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("errorMessage", out var msg)) return msg.GetString() ?? "";
            }
            catch { }
            return $"Отказ ({statusCode})";
        }

        private async Task RunSmartPollingIterationAsync(List<MeasurePointItem> selectedPoints, IEnumerable<PointResultViewModel> targetCollection, IProgress<string> progress, CancellationToken token)
        {
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

            progress.Report("Запуск опроса оборудования...");
            var groups = selectedPoints.GroupBy(p => p.NodeTitle).ToList();

            var pollingTasks = groups.Select(async grp =>
            {
                if (_dashboardNodes.Any()) 
                {
                    var nodeVm = _dashboardNodes.FirstOrDefault(n => n.NodeTitle == grp.Key);
                    if (nodeVm != null && nodeVm.IsPaused) return; 
                }

                foreach (var pt in grp)
                {
                    if (token.IsCancellationRequested) break;
                    var vm = targetCollection.FirstOrDefault(r => r.Id == pt.Id);
                    if (vm == null) continue;

                    initialTimes.TryGetValue(pt.Id, out var timeBefore);
                    
                    var (isNew, _) = await TryFetchAndUpdateAsync(vm, timeBefore, token);
                    if (isNew) { vm.Status = "Опрошено успешно"; continue; }

                    int postCooldown = 0; 
                    bool pointFinished = false; 
                    string lastError = "Нет ответа от прибора";

                    for (int attempt = 1; attempt <= 60; attempt++) 
                    {
                        if (token.IsCancellationRequested) break;
                        
                        var (isNewWait, _) = await TryFetchAndUpdateAsync(vm, timeBefore, token);
                        if (isNewWait) 
                        { 
                            vm.Status = "Опрошено успешно"; 
                            pointFinished = true; 
                            break; 
                        }

                        if (postCooldown <= 0)
                        {
                            try
                            {
                                var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{pt.Id}/Currents?once=true");
                                pollReq.Content = new ByteArrayContent(Array.Empty<byte>());
                                var pollRes = await _http.SendAsync(pollReq, token);
                                
                                if (pollRes.IsSuccessStatusCode) 
                                { 
                                    // Повторный пинок через 10 циклов (30 сек), если прибор на GPRS/Ethernet не отдал данные с первого раза
                                    postCooldown = 10;
                                    vm.Status = $"Идёт опрос прибора... [{attempt}/60]";
                                }
                                else 
                                {
                                    string errStr = await pollRes.Content.ReadAsStringAsync(token);
                                    int code = (int)pollRes.StatusCode;
                                    lastError = ParseLersError(errStr, code);
                                    
                                    if (code == 409 || code == 423 || 
                                        lastError.IndexOf("занят", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        lastError.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        lastError.IndexOf("используется", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        lastError.IndexOf("ожидани", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        postCooldown = 3; 
                                        vm.Status = $"Ожидание канала... [{attempt}/60]";
                                    }
                                    else 
                                    {
                                        postCooldown = 2; 
                                        vm.Status = $"Повтор ({lastError}) [{attempt}/60]";
                                    }
                                }
                            }
                            catch { 
                                lastError = "Сбой сети"; 
                                postCooldown = 2; 
                                vm.Status = $"Сбой сети, повтор... [{attempt}/60]";
                            }
                        }
                        else
                        {
                            postCooldown--;
                            if (vm.Status.Contains("Идёт опрос")) vm.Status = $"Идёт опрос прибора... [{attempt}/60]";
                            else if (vm.Status.Contains("Ожидание канала")) vm.Status = $"Ожидание канала... [{attempt}/60]";
                        }
                        
                        await Task.Delay(3000, token);
                    }

                    if (!pointFinished && !token.IsCancellationRequested)
                    {
                        var (finalCheck, _) = await TryFetchAndUpdateAsync(vm, timeBefore, token);
                        vm.Status = finalCheck ? "Опрошено успешно" : $"Таймаут: {lastError}";
                    }
                }
            });
            await Task.WhenAll(pollingTasks);
        }

        private async void BtnStartMonitor_Click(object sender, RoutedEventArgs e)
        {
            var selectedPoints = ListPoints.SelectedItems.Cast<MeasurePointItem>().ToList();
            if (!selectedPoints.Any()) { MessageBox.Show("Выберите точки учёта в списке слева."); return; }
            SaveUserSettings(selectedPoints.Select(p => p.Id).ToList());

            _generalMonitorCts = new CancellationTokenSource();
            var token = _generalMonitorCts.Token;
            _results.Clear();
            foreach (var p in selectedPoints)
            {
                _settings.PointLimits.TryGetValue(p.Id, out var pointLimits);
                _results.Add(new PointResultViewModel { Id = p.Id, Title = p.Title, NodeTitle = p.NodeTitle, Status = "Ожидание...", Limits = pointLimits });
            }

            try
            {
                BtnStartMonitor.IsEnabled = false; BtnCancelMonitor.Visibility = Visibility.Visible;
                TxtStatus.Text = "Фиксация времени до опроса...";
                var progress = new Progress<string>(s => TxtStatus.Text = s);
                await RunSmartPollingIterationAsync(selectedPoints, _results, progress, token);
                if (!token.IsCancellationRequested) TxtStatus.Text = "Мониторинг завершен.";
                SaveUserSettings();
            }
            catch (OperationCanceledException) { TxtStatus.Text = "Опрос отменен."; foreach (var r in _results.Where(x => !x.Status.Contains("успешно"))) r.Status = "Отменено"; }
            catch (Exception) { MessageBox.Show("Ошибка во время опроса."); TxtStatus.Text = "Сбой операции"; }
            finally { BtnStartMonitor.IsEnabled = true; BtnCancelMonitor.Visibility = Visibility.Collapsed; }
        }

        private void BtnCancelMonitor_Click(object sender, RoutedEventArgs e)
        {
            _generalMonitorCts?.Cancel(); TxtStatus.Text = "Опрос отменен пользователем.";
        }

        private async void BtnStartTrend_Click(object sender, RoutedEventArgs e)
        {
            if (CmbTrendPoint.SelectedItem is not MeasurePointItem selectedPoint) return;
            int durationMinutes = CmbTrendDuration.SelectedItem is int min ? min : 3;
            var maxEndTime = DateTime.UtcNow.AddMinutes(durationMinutes);
            _activeTrendPointId = selectedPoint.Id;
            _trendCts = new CancellationTokenSource(); var token = _trendCts.Token;

            BtnStartTrend.Visibility = Visibility.Collapsed; BtnStopTrend.Visibility = Visibility.Visible;
            CmbTrendPoint.IsEnabled = false; CmbTrendDuration.IsEnabled = false;

            _settings.PointLimits.TryGetValue(selectedPoint.Id, out var limits);
            TxtStatus.Text = $"Запуск мониторинга для {selectedPoint.Title} (макс. {durationMinutes} мин)...";

            try
            {
                try {
                    var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{selectedPoint.Id}/Currents");
                    pollReq.Content = new ByteArrayContent(Array.Empty<byte>()); pollReq.Content.Headers.ContentLength = 0;
                    await _http.SendAsync(pollReq, token);
                } catch { }

                DateTime? lastObservedTime = null;
                while (!token.IsCancellationRequested)
                {
                    if (DateTime.UtcNow >= maxEndTime) { TxtStatus.Text = $"Лимит времени ({durationMinutes} мин) исчерпан."; break; }
                    try
                    {
                        var res = await _http.GetAsync($"{_baseUrl}/Data/MeasurePoints/{selectedPoint.Id}/Consumption/Last", token);
                        if (res.IsSuccessStatusCode)
                        {
                            var json = await res.Content.ReadAsStringAsync(token);
                            if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "null")
                            {
                                using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
                                var timeNow = TryGetDate(root, "dateTime", "deviceTime", "date") ?? DateTime.Now;
                                if (lastObservedTime == null || timeNow > lastObservedTime)
                                {
                                    lastObservedTime = timeNow;
                                    var row = new PointResultViewModel { Id = selectedPoint.Id, Title = selectedPoint.Title, NodeTitle = selectedPoint.NodeTitle, DeviceTime = timeNow, Limits = limits, Status = "Снято" };
                                    UpdatePointValues(row, root); _trendHistory.Insert(0, row); row.RefreshColors();
                                    var remain = maxEndTime - DateTime.UtcNow;
                                    TxtStatus.Text = $"Данные: {timeNow:HH:mm:ss} (осталось {(int)remain.TotalMinutes} мин {(int)remain.Seconds} сек)";
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; } catch { }
                    await Task.Delay(3000, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                StopNativeLersTrend();
                BtnStartTrend.Visibility = Visibility.Visible; BtnStopTrend.Visibility = Visibility.Collapsed;
                CmbTrendPoint.IsEnabled = true; CmbTrendDuration.IsEnabled = true;
                if (!TxtStatus.Text.StartsWith("Лимит времени")) TxtStatus.Text = "Мониторинг точки остановлен.";
            }
        }

        private void StopNativeLersTrend()
        {
            _trendCts?.Cancel();
            if (_activeTrendPointId > 0 && !string.IsNullOrEmpty(_baseUrl)) {
                try { var stopReq = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/Poll/MeasurePoints/{_activeTrendPointId}/Currents"); _http.SendAsync(stopReq); } catch { }
                _activeTrendPointId = 0;
            }
        }

        private void BtnStopTrend_Click(object sender, RoutedEventArgs e) => StopNativeLersTrend();
        private void BtnClearTrend_Click(object sender, RoutedEventArgs e) => _trendHistory.Clear();

        private async void BtnStartDashboard_Click(object sender, RoutedEventArgs e)
        {
            var selectedPoints = ListPointsDashboard.SelectedItems.Cast<MeasurePointItem>().ToList();
            if (!selectedPoints.Any()) { MessageBox.Show("Выберите точки учёта для дашборда."); return; }

            int intervalMin = CmbDashboardInterval.SelectedItem is int min ? min : 10;
            _dashboardCts = new CancellationTokenSource(); var token = _dashboardCts.Token;

            BtnStartDashboard.Visibility = Visibility.Collapsed; BtnStopDashboard.Visibility = Visibility.Visible;
            CmbDashboardInterval.IsEnabled = false;

            _dashboardNodes.Clear(); _dashboardFlatPoints.Clear();

            var groups = selectedPoints.GroupBy(p => p.NodeTitle);
            foreach (var g in groups)
            {
                var nodeVm = new DashboardNodeViewModel { NodeTitle = g.Key };
                foreach (var p in g)
                {
                    _settings.PointLimits.TryGetValue(p.Id, out var pointLimits);
                    var vm = new PointResultViewModel { Id = p.Id, Title = p.Title, NodeTitle = p.NodeTitle, Status = "Ожидание автоопроса...", Limits = pointLimits };
                    vm.PropertyChanged += (s, ev) => nodeVm.Refresh();
                    nodeVm.Points.Add(vm); _dashboardFlatPoints.Add(vm);
                }
                _dashboardNodes.Add(nodeVm);
            }

            foreach(var vm in _dashboardFlatPoints) await TryFetchAndUpdateAsync(vm, null, token);

            if (selectedPoints.Count == 1) { PanelSinglePoint.Visibility = Visibility.Visible; PanelMultiPoint.Visibility = Visibility.Collapsed; PanelSinglePoint.DataContext = _dashboardFlatPoints.First(); }
            else { PanelSinglePoint.Visibility = Visibility.Collapsed; PanelMultiPoint.Visibility = Visibility.Visible; }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var progress = new Progress<string>(s => TxtStatus.Text = $"Дашборд: {s}");
                    // Активный пуш опроса
                    await RunSmartPollingIterationAsync(selectedPoints, _dashboardFlatPoints, progress, token);
                    if (token.IsCancellationRequested) break;
                    
                    DateTime nextRun = DateTime.Now.AddMinutes(intervalMin);
                    DateTime nextSilentCheck = DateTime.Now.AddSeconds(10); // Таймер фоновой проверки

                    // Режим ожидания
                    while (DateTime.Now < nextRun)
                    {
                        if (token.IsCancellationRequested) break;
                        
                        // Каждые 10 секунд тихо дёргаем базу ЛЭРСа. Вдруг данные обновились сами (автоопросом сервера)
                        if (DateTime.Now >= nextSilentCheck)
                        {
                            nextSilentCheck = DateTime.Now.AddSeconds(10);
                            var fetchTasks = _dashboardFlatPoints.Select(vm => TryFetchAndUpdateAsync(vm, vm.DeviceTime, token));
                            await Task.WhenAll(fetchTasks);
                        }

                        var remain = nextRun - DateTime.Now;
                        TxtStatus.Text = $"Дашборд обновлён. Следующий активный запуск через {(int)remain.TotalMinutes} мин {remain.Seconds:D2} сек";
                        await Task.Delay(1000, token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { BtnStartDashboard.Visibility = Visibility.Visible; BtnStopDashboard.Visibility = Visibility.Collapsed; CmbDashboardInterval.IsEnabled = true; TxtStatus.Text = "Автоопрос дашборда остановлен."; }
        }

        private void BtnStopDashboard_Click(object sender, RoutedEventArgs e) => _dashboardCts?.Cancel();

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
                                
                                bool matchesName = targetNames.Any(tn => typeName.Equals(tn, StringComparison.OrdinalIgnoreCase));
                                if (matchesName && (item.TryGetProperty("value", out var valProp) || item.TryGetProperty("val", out valProp)))
                                {
                                    if (valProp.ValueKind == JsonValueKind.Number && valProp.TryGetDouble(out double v)) return v;
                                    if (valProp.ValueKind == JsonValueKind.String && double.TryParse(valProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double sv)) return sv;
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
                    if (prop.TryGetDateTime(out var dt)) return dt;
                    if (prop.ValueKind == JsonValueKind.String && DateTime.TryParse(prop.GetString(), out var sDt)) return sDt;
                }
            }
            return null;
        }
    }

    public class TempSchedulePoint
    {
        public double Tnv { get; set; }
        public double T1 { get; set; }
        public double T2 { get; set; }
    }

    public class TempSchedulePointViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        private double _tnv;
        public double Tnv { get => _tnv; set { _tnv = value; OnPropertyChanged(); } }

        private double _t1;
        public double T1 { get => _t1; set { _t1 = value; OnPropertyChanged(); } }

        private double _t2;
        public double T2 { get => _t2; set { _t2 = value; OnPropertyChanged(); } }
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
        public double? T1Min { get; set; } public double? T1Max { get; set; }
        public double? T2Min { get; set; } public double? T2Max { get; set; }
        public double? P1Min { get; set; } public double? P1Max { get; set; }
        public double? P2Min { get; set; } public double? P2Max { get; set; }
        public double? V1Min { get; set; } public double? V1Max { get; set; }
        public double? V2Min { get; set; } public double? V2Max { get; set; }
        public double? M1Min { get; set; } public double? M1Max { get; set; }
        public double? M2Min { get; set; } public double? M2Max { get; set; }
        public double? QMin { get; set; } public double? QMax { get; set; }
        public List<TempSchedulePoint> Schedule { get; set; } = new();
    }

    public class UserSettings
    {
        public string Host { get; set; } = "localhost";
        public ushort Port { get; set; } = 10000;
        public string Login { get; set; } = "";
        public string PasswordBase64 { get; set; } = "";
        public bool RememberCredentials { get; set; } = true;
        public int ThemeMode { get; set; } = 0; // 0=Светлая, 1=Тёмная, 2=Авто
        public string TimeLightStr { get; set; } = "08:00";
        public string TimeDarkStr { get; set; } = "20:00";
        public double UiScale { get; set; } = 1.0;
        public List<int> SelectedPointIds { get; set; } = new();
        public int? TnvSourcePointId { get; set; }
        public Dictionary<int, PointThresholds> PointLimits { get; set; } = new();
        public List<PointResultViewModel> LastResults { get; set; } = new();
    }

    public class DashboardNodeViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string NodeTitle { get; set; } = string.Empty;
        public ObservableCollection<PointResultViewModel> Points { get; } = new();
        
        private bool _isPaused;
        public bool IsPaused { get => _isPaused; set { _isPaused = value; Refresh(); } }
        
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

        public string ToggleIcon => IsPaused ? "▶" : "✕";
        public Brush ToggleBg => IsPaused 
            ? new SolidColorBrush(Color.FromArgb(180, 40, 167, 69))
            : new SolidColorBrush(Color.FromArgb(210, 255, 59, 48));
        public string ToggleTooltip => IsPaused 
            ? "Опрос объекта остановлен (нажмите, чтобы включить)" 
            : "Объект опрашивается (нажмите, чтобы остановить)";

        public Brush TileBackground
        {
            get
            {
                if (IsPaused) return (Brush)Application.Current.MainWindow.FindResource("AccentGray");
                if (Points.Any(p => p.HasBreach)) return (Brush)Application.Current.MainWindow.FindResource("AccentRed");
                if (Points.Any(p => p.HasError)) return (Brush)Application.Current.MainWindow.FindResource("AccentOrange");
                if (Points.Any(p => p.IsWaiting)) return (Brush)Application.Current.MainWindow.FindResource("AccentGray");
                return (Brush)Application.Current.MainWindow.FindResource("AccentGreen");
            }
        }
        public Brush TileForeground => Brushes.White;
        public DateTime? MaxDeviceTime => Points.Max(p => p.DeviceTime);
        public string GeneralStatus
        {
            get
            {
                if (IsPaused) return "Опрос приостановлен";
                var errPoint = Points.FirstOrDefault(p => p.HasError); if (errPoint != null) return errPoint.Status;
                var waitPoint = Points.FirstOrDefault(p => p.IsWaiting); if (waitPoint != null) return waitPoint.Status;
                return "Опрошено успешно";
            }
        }
    }

    public class PointResultViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        [System.Text.Json.Serialization.JsonIgnore]
        public PointThresholds? Limits { get; set; }

        public void RefreshColors()
        {
            OnPropertyChanged(nameof(T1Color)); OnPropertyChanged(nameof(T2Color));
            OnPropertyChanged(nameof(P1Color)); OnPropertyChanged(nameof(P2Color));
            OnPropertyChanged(nameof(V1Color)); OnPropertyChanged(nameof(V2Color));
            OnPropertyChanged(nameof(M1Color)); OnPropertyChanged(nameof(M2Color)); OnPropertyChanged(nameof(QColor));
            
            OnPropertyChanged(nameof(T1DashBg)); OnPropertyChanged(nameof(T2DashBg));
            OnPropertyChanged(nameof(P1DashBg)); OnPropertyChanged(nameof(P2DashBg));
            OnPropertyChanged(nameof(V1DashBg)); OnPropertyChanged(nameof(V2DashBg));
            OnPropertyChanged(nameof(M1DashBg)); OnPropertyChanged(nameof(M2DashBg)); OnPropertyChanged(nameof(QDashBg));

            OnPropertyChanged(nameof(T1DashForeground)); OnPropertyChanged(nameof(T2DashForeground));
            OnPropertyChanged(nameof(P1DashForeground)); OnPropertyChanged(nameof(P2DashForeground));
            OnPropertyChanged(nameof(V1DashForeground)); OnPropertyChanged(nameof(V2DashForeground));
            OnPropertyChanged(nameof(M1DashForeground)); OnPropertyChanged(nameof(M2DashForeground)); OnPropertyChanged(nameof(QDashForeground));
            OnPropertyChanged(nameof(SingleStatusColor));
            OnPropertyChanged(nameof(PointTitleColor));
            OnPropertyChanged(nameof(RetryVisibility));
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasBreach => IsBreached(T1, Limits?.T1Min, Limits?.T1Max) || IsBreached(T2, Limits?.T2Min, Limits?.T2Max) ||
                                 IsT1ScheduleBreached(T1, Limits?.Schedule) || IsT2ScheduleBreached(T2, Limits?.Schedule) ||
                                 IsBreached(P1, Limits?.P1Min, Limits?.P1Max) || IsBreached(P2, Limits?.P2Min, Limits?.P2Max) ||
                                 IsBreached(V1, Limits?.V1Min, Limits?.V1Max) || IsBreached(V2, Limits?.V2Min, Limits?.V2Max) ||
                                 IsBreached(M1, Limits?.M1Min, Limits?.M1Max) || IsBreached(M2, Limits?.M2Min, Limits?.M2Max) ||
                                 IsBreached(Q, Limits?.QMin, Limits?.QMax);

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasError => !string.IsNullOrEmpty(Status) && (Status.Contains("Таймаут") || Status.Contains("Отказ") || Status.Contains("Сбой") || Status.Contains("Ошибка"));

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWaiting => string.IsNullOrEmpty(Status) || Status.Contains("Ожидание") || Status.Contains("В очереди") || Status.Contains("Идёт");

        [System.Text.Json.Serialization.JsonIgnore]
        public Visibility RetryVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

        private Brush GetColor(double? val, double? min, double? max)
        {
            var defaultColor = Application.Current.MainWindow?.TryFindResource("LabelPrimary") as Brush ?? Brushes.Black;
            if (val == null) return defaultColor;
            return IsBreached(val, min, max) ? Brushes.Red : defaultColor;
        }

        private bool IsBreached(double? val, double? min, double? max)
        {
            if (!val.HasValue) return false;
            if (min.HasValue && val.Value < min.Value) return true;
            if (max.HasValue && val.Value > max.Value) return true;
            return false;
        }

        private (double? expT1, double? expT2) GetExpectedTemps(List<TempSchedulePoint>? schedule)
        {
            var tnv = MainWindow.CurrentGlobalTnv;
            if (!tnv.HasValue || schedule == null || !schedule.Any()) return (null, null);

            try
            {
                var points = schedule.OrderBy(p => p.Tnv).ToList();
                
                var exact = points.FirstOrDefault(p => Math.Abs(p.Tnv - tnv.Value) < 0.2);
                if (exact != null) return (exact.T1, exact.T2);

                if (tnv.Value <= points.First().Tnv) return (points.First().T1, points.First().T2);
                if (tnv.Value >= points.Last().Tnv) return (points.Last().T1, points.Last().T2);

                for (int i = 0; i < points.Count - 1; i++)
                {
                    if (tnv.Value >= points[i].Tnv && tnv.Value <= points[i+1].Tnv)
                    {
                        double x1 = points[i].Tnv, y1 = points[i].T1, z1 = points[i].T2;
                        double x2 = points[i+1].Tnv, y2 = points[i+1].T1, z2 = points[i+1].T2;
                        if (Math.Abs(x2 - x1) < 0.001) return (y1, z1);
                        double ratio = (tnv.Value - x1) / (x2 - x1);
                        return (y1 + ratio * (y2 - y1), z1 + ratio * (z2 - z1));
                    }
                }
            }
            catch { }
            return (null, null);
        }

        private bool IsT1ScheduleBreached(double? t1, List<TempSchedulePoint>? schedule)
        {
            if (!t1.HasValue) return false;
            var exp = GetExpectedTemps(schedule).expT1;
            return exp.HasValue && Math.Abs(t1.Value - exp.Value) > 3.0;
        }

        private bool IsT2ScheduleBreached(double? t2, List<TempSchedulePoint>? schedule)
        {
            if (!t2.HasValue) return false;
            var exp = GetExpectedTemps(schedule).expT2;
            return exp.HasValue && Math.Abs(t2.Value - exp.Value) > 3.0;
        }

        private Brush GetDashBg(double? val, double? min, double? max, bool isT1 = false, bool isT2 = false)
        {
            if (IsBreached(val, min, max) || 
               (isT1 && IsT1ScheduleBreached(T1, Limits?.Schedule)) || 
               (isT2 && IsT2ScheduleBreached(T2, Limits?.Schedule))) 
                return (Brush)Application.Current.MainWindow.FindResource("AccentRed");
                
            if (IsWaiting) return (Brush)Application.Current.MainWindow.FindResource("AccentGray");
            if (HasError) return (Brush)Application.Current.MainWindow.FindResource("AccentOrange");
            return (Brush)Application.Current.MainWindow.FindResource("AccentGreen");
        }

        [System.Text.Json.Serialization.JsonIgnore] public Brush T1DashBg => GetDashBg(T1, Limits?.T1Min, Limits?.T1Max, true, false);
        [System.Text.Json.Serialization.JsonIgnore] public Brush T2DashBg => GetDashBg(T2, Limits?.T2Min, Limits?.T2Max, false, true);
        [System.Text.Json.Serialization.JsonIgnore] public Brush P1DashBg => GetDashBg(P1, Limits?.P1Min, Limits?.P1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush P2DashBg => GetDashBg(P2, Limits?.P2Min, Limits?.P2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush V1DashBg => GetDashBg(V1, Limits?.V1Min, Limits?.V1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush V2DashBg => GetDashBg(V2, Limits?.V2Min, Limits?.V2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush M1DashBg => GetDashBg(M1, Limits?.M1Min, Limits?.M1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush M2DashBg => GetDashBg(M2, Limits?.M2Min, Limits?.M2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush QDashBg => GetDashBg(Q, Limits?.QMin, Limits?.QMax);

        private Brush DashFore(double? val, double? min, double? max, bool isT1 = false, bool isT2 = false) => 
            (IsBreached(val, min, max) || (isT1 && IsT1ScheduleBreached(T1, Limits?.Schedule)) || (isT2 && IsT2ScheduleBreached(T2, Limits?.Schedule))) 
            ? Brushes.Yellow : Brushes.White;

        [System.Text.Json.Serialization.JsonIgnore] public Brush T1DashForeground => DashFore(T1, Limits?.T1Min, Limits?.T1Max, true, false);
        [System.Text.Json.Serialization.JsonIgnore] public Brush T2DashForeground => DashFore(T2, Limits?.T2Min, Limits?.T2Max, false, true);
        [System.Text.Json.Serialization.JsonIgnore] public Brush P1DashForeground => DashFore(P1, Limits?.P1Min, Limits?.P1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush P2DashForeground => DashFore(P2, Limits?.P2Min, Limits?.P2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush V1DashForeground => DashFore(V1, Limits?.V1Min, Limits?.V1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush V2DashForeground => DashFore(V2, Limits?.V2Min, Limits?.V2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush M1DashForeground => DashFore(M1, Limits?.M1Min, Limits?.M1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush M2DashForeground => DashFore(M2, Limits?.M2Min, Limits?.M2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush QDashForeground => DashFore(Q, Limits?.QMin, Limits?.QMax);

        [System.Text.Json.Serialization.JsonIgnore]
        public Brush SingleStatusColor
        {
            get
            {
                if (HasBreach) return (Brush)Application.Current.MainWindow.FindResource("AccentRed");
                if (HasError) return (Brush)Application.Current.MainWindow.FindResource("AccentOrange");
                if (IsWaiting) return (Brush)Application.Current.MainWindow.FindResource("AccentGray");
                return (Brush)Application.Current.MainWindow.FindResource("AccentGreen");
            }
        }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush PointTitleColor
        {
            get
            {
                if (HasBreach) return (Brush)Application.Current.MainWindow.FindResource("AccentRed");
                if (HasError) return (Brush)Application.Current.MainWindow.FindResource("AccentOrange");
                if (IsWaiting) return (Brush)Application.Current.MainWindow.FindResource("AccentGray");
                return Brushes.White;
            }
        }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush TileForeground => Brushes.White;

        private int _id; public int Id { get => _id; set { _id = value; OnPropertyChanged(); } }
        private string _nodeTitle = string.Empty; public string NodeTitle { get => _nodeTitle; set { _nodeTitle = value; OnPropertyChanged(); } }
        private string _title = string.Empty; public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        private DateTime? _deviceTime; public DateTime? DeviceTime { get => _deviceTime; set { _deviceTime = value; OnPropertyChanged(); } }
        
        private double? _t1; public double? T1 { get => _t1; set { _t1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(T1Color)); OnPropertyChanged(nameof(T1DashBg)); OnPropertyChanged(nameof(T1DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush T1Color => (IsBreached(T1, Limits?.T1Min, Limits?.T1Max) || IsT1ScheduleBreached(T1, Limits?.Schedule)) ? Brushes.Red : ((Brush?)Application.Current.MainWindow?.TryFindResource("LabelPrimary") ?? Brushes.Black);
        
        private double? _t2; public double? T2 { get => _t2; set { _t2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(T2Color)); OnPropertyChanged(nameof(T2DashBg)); OnPropertyChanged(nameof(T2DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush T2Color => (IsBreached(T2, Limits?.T2Min, Limits?.T2Max) || IsT2ScheduleBreached(T2, Limits?.Schedule)) ? Brushes.Red : ((Brush?)Application.Current.MainWindow?.TryFindResource("LabelPrimary") ?? Brushes.Black);
        
        private double? _p1; public double? P1 { get => _p1; set { _p1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(P1Color)); OnPropertyChanged(nameof(P1DashBg)); OnPropertyChanged(nameof(P1DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush P1Color => GetColor(P1, Limits?.P1Min, Limits?.P1Max);
        
        private double? _p2; public double? P2 { get => _p2; set { _p2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(P2Color)); OnPropertyChanged(nameof(P2DashBg)); OnPropertyChanged(nameof(P2DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush P2Color => GetColor(P2, Limits?.P2Min, Limits?.P2Max);
        
        private double? _v1; public double? V1 { get => _v1; set { _v1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(V1Color)); OnPropertyChanged(nameof(V1DashBg)); OnPropertyChanged(nameof(V1DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush V1Color => GetColor(V1, Limits?.V1Min, Limits?.V1Max);
        
        private double? _v2; public double? V2 { get => _v2; set { _v2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(V2Color)); OnPropertyChanged(nameof(V2DashBg)); OnPropertyChanged(nameof(V2DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush V2Color => GetColor(V2, Limits?.V2Min, Limits?.V2Max);
        
        private double? _m1; public double? M1 { get => _m1; set { _m1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(M1Color)); OnPropertyChanged(nameof(M1DashBg)); OnPropertyChanged(nameof(M1DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush M1Color => GetColor(M1, Limits?.M1Min, Limits?.M1Max);
        
        private double? _m2; public double? M2 { get => _m2; set { _m2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(M2Color)); OnPropertyChanged(nameof(M2DashBg)); OnPropertyChanged(nameof(M2DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush M2Color => GetColor(M2, Limits?.M2Min, Limits?.M2Max);
        
        private double? _q; public double? Q { get => _q; set { _q = value; OnPropertyChanged(); OnPropertyChanged(nameof(QColor)); OnPropertyChanged(nameof(QDashBg)); OnPropertyChanged(nameof(QDashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush QColor => GetColor(Q, Limits?.QMin, Limits?.QMax);
        
        private string _status = string.Empty; public string Status { get => _status; set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(SingleStatusColor)); OnPropertyChanged(nameof(PointTitleColor)); OnPropertyChanged(nameof(RetryVisibility)); } }
    }
}
