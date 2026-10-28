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
        private bool _showTnv = true; public bool ShowTnv { get => _showTnv; set { _showTnv = value; OnPropertyChanged(); } }
    }

    public partial class MainWindow : Window
    {
        private readonly HttpClient _http = new();
        private string? _token;
        private string _baseUrl = "";

        private List<MeasurePointItem> _allPoints = new();
        private readonly ObservableCollection<PointResultViewModel> _results = new();
        private readonly ObservableCollection<PointResultViewModel> _trendHistory = new();
        private readonly ObservableCollection<DashboardNodeViewModel> _dashboardNodes = new();
        private readonly List<PointResultViewModel> _dashboardFlatPoints = new();

        private UserSettings _settings = new();
        private List<MeasurePointItem> _editingPoints = new();
        
        private CancellationTokenSource? _generalMonitorCts;
        private CancellationTokenSource? _trendCts;
        private CancellationTokenSource? _dashboardCts;
        private int _activeTrendPointId = 0;
        
        private DispatcherTimer _themeTimer;

        private static string SettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LersMonitor", "settings.json");

        public MainWindow()
        {
            InitializeComponent();
            GridResults.ItemsSource = _results;
            GridTrend.ItemsSource = _trendHistory;
            ItemsDashboard.ItemsSource = _dashboardNodes;
            
            CmbTrendDuration.ItemsSource = Enumerable.Range(1, 10).ToList();
            CmbTrendDuration.SelectedItem = 3;

            CmbDashboardInterval.ItemsSource = new List<int> { 1, 2, 3, 5, 10, 15, 20, 30, 60 };
            CmbDashboardInterval.SelectedItem = 10;

            _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _themeTimer.Tick += ThemeTimer_Tick;

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
                        
                        // Меняем тему в самом конце, так как она триггерит SaveUserSettings
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
            DashSettings.Instance.ShowTnv = ChkDashTnv.IsChecked == true;
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
                    TxtTnvMin.Text = limits.TnvMin?.ToString(); TxtTnvMax.Text = limits.TnvMax?.ToString();
                    TxtP1Min.Text = limits.P1Min?.ToString(); TxtP1Max.Text = limits.P1Max?.ToString();
                    TxtP2Min.Text = limits.P2Min?.ToString(); TxtP2Max.Text = limits.P2Max?.ToString();
                    TxtV1Min.Text = limits.V1Min?.ToString(); TxtV1Max.Text = limits.V1Max?.ToString();
                    TxtV2Min.Text = limits.V2Min?.ToString(); TxtV2Max.Text = limits.V2Max?.ToString();
                    TxtM1Min.Text = limits.M1Min?.ToString(); TxtM1Max.Text = limits.M1Max?.ToString();
                    TxtM2Min.Text = limits.M2Min?.ToString(); TxtM2Max.Text = limits.M2Max?.ToString();
                    TxtQMin.Text = limits.QMin?.ToString(); TxtQMax.Text = limits.QMax?.ToString();
                    TxtTempSchedule.Text = limits.TempSchedule;
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

        private void ClearLimitsFields()
        {
            TxtT1Min.Text = ""; TxtT1Max.Text = ""; TxtT2Min.Text = ""; TxtT2Max.Text = "";
            TxtTnvMin.Text = ""; TxtTnvMax.Text = "";
            TxtP1Min.Text = ""; TxtP1Max.Text = ""; TxtP2Min.Text = ""; TxtP2Max.Text = "";
            TxtV1Min.Text = ""; TxtV1Max.Text = ""; TxtV2Min.Text = ""; TxtV2Max.Text = "";
            TxtM1Min.Text = ""; TxtM1Max.Text = ""; TxtM2Min.Text = ""; TxtM2Max.Text = "";
            TxtQMin.Text = ""; TxtQMax.Text = ""; TxtTempSchedule.Text = "";
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
            var tnvMin = ParseDouble(TxtTnvMin.Text); var tnvMax = ParseDouble(TxtTnvMax.Text);
            var p1Min = ParseDouble(TxtP1Min.Text); var p1Max = ParseDouble(TxtP1Max.Text);
            var p2Min = ParseDouble(TxtP2Min.Text); var p2Max = ParseDouble(TxtP2Max.Text);
            var v1Min = ParseDouble(TxtV1Min.Text); var v1Max = ParseDouble(TxtV1Max.Text);
            var v2Min = ParseDouble(TxtV2Min.Text); var v2Max = ParseDouble(TxtV2Max.Text);
            var m1Min = ParseDouble(TxtM1Min.Text); var m1Max = ParseDouble(TxtM1Max.Text);
            var m2Min = ParseDouble(TxtM2Min.Text); var m2Max = ParseDouble(TxtM2Max.Text);
            var qMin = ParseDouble(TxtQMin.Text); var qMax = ParseDouble(TxtQMax.Text);
            var sched = TxtTempSchedule.Text.Trim();

            foreach (var point in _editingPoints)
            {
                if (!_settings.PointLimits.TryGetValue(point.Id, out var limits)) { limits = new PointThresholds(); _settings.PointLimits[point.Id] = limits; }

                if (!string.IsNullOrWhiteSpace(TxtT1Min.Text)) limits.T1Min = t1Min; if (!string.IsNullOrWhiteSpace(TxtT1Max.Text)) limits.T1Max = t1Max;
                if (!string.IsNullOrWhiteSpace(TxtT2Min.Text)) limits.T2Min = t2Min; if (!string.IsNullOrWhiteSpace(TxtT2Max.Text)) limits.T2Max = t2Max;
                if (!string.IsNullOrWhiteSpace(TxtTnvMin.Text)) limits.TnvMin = tnvMin; if (!string.IsNullOrWhiteSpace(TxtTnvMax.Text)) limits.TnvMax = tnvMax;
                if (!string.IsNullOrWhiteSpace(TxtP1Min.Text)) limits.P1Min = p1Min; if (!string.IsNullOrWhiteSpace(TxtP1Max.Text)) limits.P1Max = p1Max;
                if (!string.IsNullOrWhiteSpace(TxtP2Min.Text)) limits.P2Min = p2Min; if (!string.IsNullOrWhiteSpace(TxtP2Max.Text)) limits.P2Max = p2Max;
                if (!string.IsNullOrWhiteSpace(TxtV1Min.Text)) limits.V1Min = v1Min; if (!string.IsNullOrWhiteSpace(TxtV1Max.Text)) limits.V1Max = v1Max;
                if (!string.IsNullOrWhiteSpace(TxtV2Min.Text)) limits.V2Min = v2Min; if (!string.IsNullOrWhiteSpace(TxtV2Max.Text)) limits.V2Max = v2Max;
                if (!string.IsNullOrWhiteSpace(TxtM1Min.Text)) limits.M1Min = m1Min; if (!string.IsNullOrWhiteSpace(TxtM1Max.Text)) limits.M1Max = m1Max;
                if (!string.IsNullOrWhiteSpace(TxtM2Min.Text)) limits.M2Min = m2Min; if (!string.IsNullOrWhiteSpace(TxtM2Max.Text)) limits.M2Max = m2Max;
                if (!string.IsNullOrWhiteSpace(TxtQMin.Text)) limits.QMin = qMin; if (!string.IsNullOrWhiteSpace(TxtQMax.Text)) limits.QMax = qMax;
                if (!string.IsNullOrWhiteSpace(sched)) limits.TempSchedule = sched;

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
            vm.Tnv = ExtractParameterValue(root, "t_out_env", "t_nv", "T_nv", "outdoorTemp", "t_outdoor", "t_env");
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
                bool nodeHadAnySuccess = false;
                foreach (var pt in grp)
                {
                    if (token.IsCancellationRequested) break;
                    var vm = targetCollection.FirstOrDefault(r => r.Id == pt.Id);
                    if (vm == null) continue;

                    initialTimes.TryGetValue(pt.Id, out var timeBefore);
                    
                    var (isNew, tNow) = await TryFetchAndUpdateAsync(vm, timeBefore, token);
                    if (isNew) { vm.Status = "Опрошено успешно"; nodeHadAnySuccess = true; continue; }

                    bool pollStarted = false; 
                    bool pointFinished = false; 
                    string lastError = "";

                    // ФАЗА 1: ОЧЕРЕДЬ И ЗАПУСК ОПРОСА
                    for (int r = 1; r <= 15; r++) 
                    {
                        if (token.IsCancellationRequested) break;
                        
                        var (isNewWait, _) = await TryFetchAndUpdateAsync(vm, timeBefore, token);
                        if (isNewWait) 
                        { 
                            vm.Status = "Опрошено успешно"; 
                            pollStarted = true; 
                            pointFinished = true; 
                            nodeHadAnySuccess = true; 
                            break; 
                        }

                        try
                        {
                            var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/Poll/MeasurePoints/{pt.Id}/Currents?once=true");
                            pollReq.Content = new ByteArrayContent(Array.Empty<byte>());
                            var pollRes = await _http.SendAsync(pollReq, token);
                            
                            if (pollRes.IsSuccessStatusCode) 
                            { 
                                pollStarted = true; 
                                break; 
                            }
                            else 
                            {
                                string errStr = await pollRes.Content.ReadAsStringAsync(token);
                                int code = (int)pollRes.StatusCode;
                                lastError = ParseLersError(errStr, code);
                                
                                // Строгая проверка на занятость через коды HTTP (409 Conflict, 423 Locked) и текст
                                if (code == 409 || code == 423 || 
                                    lastError.IndexOf("занят", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    lastError.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    lastError.IndexOf("используется", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    lastError.IndexOf("ожидани", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    pollStarted = true; 
                                    break; 
                                }

                                if (nodeHadAnySuccess && code == 422) break; 
                                vm.Status = $"В очереди: {lastError} [{r}/15]";
                            }
                        }
                        catch { lastError = "Сбой сети"; }
                        
                        if (!pollStarted) await Task.Delay(3000, token);
                    }

                    if (!pollStarted && !token.IsCancellationRequested && !pointFinished)
                    {
                        await TryFetchAndUpdateAsync(vm, timeBefore, token); 
                        vm.Status = nodeHadAnySuccess ? "Опрошено успешно" : $"Отказ: {lastError}";
                        continue;
                    }

                    // ФАЗА 2: ОЖИДАНИЕ НОВЫХ ДАННЫХ ОТ ПРИБОРА
                    if (pollStarted && !pointFinished)
                    {
                        // УВЕЛИЧЕНО до 60 попыток (3 минуты), так как котельные часто сидят на медленных CSD модемах
                        for (int attempt = 1; attempt <= 60; attempt++)
                        {
                            if (token.IsCancellationRequested) break;
                            vm.Status = $"Идёт опрос прибора ({attempt}/60)...";
                            await Task.Delay(3000, token);
                            
                            var (isNew2, tCheck) = await TryFetchAndUpdateAsync(vm, timeBefore, token);
                            if (isNew2) 
                            { 
                                vm.Status = "Опрошено успешно"; 
                                pointFinished = true; 
                                nodeHadAnySuccess = true; 
                                break; 
                            }
                        }
                        if (!pointFinished && !token.IsCancellationRequested) 
                            vm.Status = "Таймаут (данные не обновились)";
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
        public double? TnvMin { get; set; } public double? TnvMax { get; set; }
        public double? P1Min { get; set; } public double? P1Max { get; set; }
        public double? P2Min { get; set; } public double? P2Max { get; set; }
        public double? V1Min { get; set; } public double? V1Max { get; set; }
        public double? V2Min { get; set; } public double? V2Max { get; set; }
        public double? M1Min { get; set; } public double? M1Max { get; set; }
        public double? M2Min { get; set; } public double? M2Max { get; set; }
        public double? QMin { get; set; } public double? QMax { get; set; }
        public string TempSchedule { get; set; } = "";
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
        public Dictionary<int, PointThresholds> PointLimits { get; set; } = new();
        public List<PointResultViewModel> LastResults { get; set; } = new();
    }

    public class DashboardNodeViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string NodeTitle { get; set; } = string.Empty;
        public ObservableCollection<PointResultViewModel> Points { get; } = new();
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        public Brush TileBackground
        {
            get
            {
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
            OnPropertyChanged(nameof(T1Color)); OnPropertyChanged(nameof(T2Color)); OnPropertyChanged(nameof(TnvColor));
            OnPropertyChanged(nameof(P1Color)); OnPropertyChanged(nameof(P2Color));
            OnPropertyChanged(nameof(V1Color)); OnPropertyChanged(nameof(V2Color));
            OnPropertyChanged(nameof(M1Color)); OnPropertyChanged(nameof(M2Color)); OnPropertyChanged(nameof(QColor));
            
            OnPropertyChanged(nameof(T1DashBg)); OnPropertyChanged(nameof(T2DashBg)); OnPropertyChanged(nameof(TnvDashBg));
            OnPropertyChanged(nameof(P1DashBg)); OnPropertyChanged(nameof(P2DashBg));
            OnPropertyChanged(nameof(V1DashBg)); OnPropertyChanged(nameof(V2DashBg));
            OnPropertyChanged(nameof(M1DashBg)); OnPropertyChanged(nameof(M2DashBg)); OnPropertyChanged(nameof(QDashBg));

            OnPropertyChanged(nameof(T1DashForeground)); OnPropertyChanged(nameof(T2DashForeground)); OnPropertyChanged(nameof(TnvDashForeground));
            OnPropertyChanged(nameof(P1DashForeground)); OnPropertyChanged(nameof(P2DashForeground));
            OnPropertyChanged(nameof(V1DashForeground)); OnPropertyChanged(nameof(V2DashForeground));
            OnPropertyChanged(nameof(M1DashForeground)); OnPropertyChanged(nameof(M2DashForeground)); OnPropertyChanged(nameof(QDashForeground));
            OnPropertyChanged(nameof(SingleStatusColor));
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasBreach => IsBreached(T1, Limits?.T1Min, Limits?.T1Max) || IsBreached(T2, Limits?.T2Min, Limits?.T2Max) ||
                                 IsBreached(Tnv, Limits?.TnvMin, Limits?.TnvMax) || IsTempScheduleBreached(T1, Tnv, Limits?.TempSchedule) ||
                                 IsBreached(P1, Limits?.P1Min, Limits?.P1Max) || IsBreached(P2, Limits?.P2Min, Limits?.P2Max) ||
                                 IsBreached(V1, Limits?.V1Min, Limits?.V1Max) || IsBreached(V2, Limits?.V2Min, Limits?.V2Max) ||
                                 IsBreached(M1, Limits?.M1Min, Limits?.M1Max) || IsBreached(M2, Limits?.M2Min, Limits?.M2Max) ||
                                 IsBreached(Q, Limits?.QMin, Limits?.QMax);

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasError => !string.IsNullOrEmpty(Status) && (Status.Contains("Таймаут") || Status.Contains("Отказ") || Status.Contains("Сбой") || Status.Contains("Ошибка"));

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWaiting => string.IsNullOrEmpty(Status) || Status.Contains("Ожидание") || Status.Contains("В очереди") || Status.Contains("Идёт");

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

        private bool IsTempScheduleBreached(double? t1, double? tnv, string? scheduleStr)
        {
            if (!t1.HasValue || !tnv.HasValue || string.IsNullOrWhiteSpace(scheduleStr)) return false;
            try
            {
                var points = scheduleStr.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split(':')).Where(p => p.Length == 2)
                    .Select(p => new { Tnv = double.Parse(p[0].Trim(), CultureInfo.InvariantCulture), T1 = double.Parse(p[1].Trim(), CultureInfo.InvariantCulture) })
                    .OrderBy(p => p.Tnv).ToList();

                if (!points.Any()) return false;
                double expectedT1 = points.First().T1;
                
                if (tnv.Value <= points.First().Tnv) expectedT1 = points.First().T1;
                else if (tnv.Value >= points.Last().Tnv) expectedT1 = points.Last().T1;
                else
                {
                    for (int i = 0; i < points.Count - 1; i++)
                    {
                        if (tnv.Value >= points[i].Tnv && tnv.Value <= points[i+1].Tnv)
                        {
                            double x1 = points[i].Tnv, y1 = points[i].T1;
                            double x2 = points[i+1].Tnv, y2 = points[i+1].T1;
                            expectedT1 = y1 + (tnv.Value - x1) * (y2 - y1) / (x2 - x1);
                            break;
                        }
                    }
                }
                return Math.Abs(t1.Value - expectedT1) > 3.0;
            }
            catch { return false; }
        }

        private Brush GetDashBg(double? val, double? min, double? max, bool isT1 = false)
        {
            if (IsBreached(val, min, max) || (isT1 && IsTempScheduleBreached(T1, Tnv, Limits?.TempSchedule))) return (Brush)Application.Current.MainWindow.FindResource("AccentRed");
            if (IsWaiting) return (Brush)Application.Current.MainWindow.FindResource("AccentGray");
            if (HasError) return (Brush)Application.Current.MainWindow.FindResource("AccentOrange");
            return (Brush)Application.Current.MainWindow.FindResource("AccentGreen");
        }

        [System.Text.Json.Serialization.JsonIgnore] public Brush T1DashBg => GetDashBg(T1, Limits?.T1Min, Limits?.T1Max, true);
        [System.Text.Json.Serialization.JsonIgnore] public Brush T2DashBg => GetDashBg(T2, Limits?.T2Min, Limits?.T2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush TnvDashBg => GetDashBg(Tnv, Limits?.TnvMin, Limits?.TnvMax);
        [System.Text.Json.Serialization.JsonIgnore] public Brush P1DashBg => GetDashBg(P1, Limits?.P1Min, Limits?.P1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush P2DashBg => GetDashBg(P2, Limits?.P2Min, Limits?.P2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush V1DashBg => GetDashBg(V1, Limits?.V1Min, Limits?.V1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush V2DashBg => GetDashBg(V2, Limits?.V2Min, Limits?.V2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush M1DashBg => GetDashBg(M1, Limits?.M1Min, Limits?.M1Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush M2DashBg => GetDashBg(M2, Limits?.M2Min, Limits?.M2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush QDashBg => GetDashBg(Q, Limits?.QMin, Limits?.QMax);

        private Brush DashFore(double? val, double? min, double? max, bool isT1 = false) => (IsBreached(val, min, max) || (isT1 && IsTempScheduleBreached(T1, Tnv, Limits?.TempSchedule))) ? Brushes.Yellow : Brushes.White;

        [System.Text.Json.Serialization.JsonIgnore] public Brush T1DashForeground => DashFore(T1, Limits?.T1Min, Limits?.T1Max, true);
        [System.Text.Json.Serialization.JsonIgnore] public Brush T2DashForeground => DashFore(T2, Limits?.T2Min, Limits?.T2Max);
        [System.Text.Json.Serialization.JsonIgnore] public Brush TnvDashForeground => DashFore(Tnv, Limits?.TnvMin, Limits?.TnvMax);
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
        public Brush TileForeground => Brushes.White;

        private int _id; public int Id { get => _id; set { _id = value; OnPropertyChanged(); } }
        private string _nodeTitle = string.Empty; public string NodeTitle { get => _nodeTitle; set { _nodeTitle = value; OnPropertyChanged(); } }
        private string _title = string.Empty; public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        private DateTime? _deviceTime; public DateTime? DeviceTime { get => _deviceTime; set { _deviceTime = value; OnPropertyChanged(); } }
        
        private double? _t1; public double? T1 { get => _t1; set { _t1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(T1Color)); OnPropertyChanged(nameof(T1DashBg)); OnPropertyChanged(nameof(T1DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush T1Color => (IsBreached(T1, Limits?.T1Min, Limits?.T1Max) || IsTempScheduleBreached(T1, Tnv, Limits?.TempSchedule)) ? Brushes.Red : ((Brush?)Application.Current.MainWindow?.TryFindResource("LabelPrimary") ?? Brushes.Black);
        
        private double? _t2; public double? T2 { get => _t2; set { _t2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(T2Color)); OnPropertyChanged(nameof(T2DashBg)); OnPropertyChanged(nameof(T2DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush T2Color => GetColor(T2, Limits?.T2Min, Limits?.T2Max);
        
        private double? _tnv; public double? Tnv { get => _tnv; set { _tnv = value; OnPropertyChanged(); OnPropertyChanged(nameof(TnvColor)); OnPropertyChanged(nameof(TnvDashBg)); OnPropertyChanged(nameof(TnvDashForeground)); OnPropertyChanged(nameof(T1Color)); OnPropertyChanged(nameof(SingleStatusColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush TnvColor => GetColor(Tnv, Limits?.TnvMin, Limits?.TnvMax);
        
        private double? _p1; public double? P1 { get => _p1; set { _p1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(P1Color)); OnPropertyChanged(nameof(P1DashBg)); OnPropertyChanged(nameof(P1DashForeground)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush P1Color => GetColor(P1, Limits?.P1Min, Limits?.P1Max);
        
        private double? _p2; public double? P2 { get => _p2; set { _p2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(P2Color)); OnPropertyChanged(nameof(P2DashBg)); OnPropertyChanged(nameof(P2DashForeground)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush P2Color => GetColor(P2, Limits?.P2Min, Limits?.P2Max);
        
        private double? _v1; public double? V1 { get => _v1; set { _v1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(V1Color)); OnPropertyChanged(nameof(V1DashBg)); OnPropertyChanged(nameof(V1DashForeground)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush V1Color => GetColor(V1, Limits?.V1Min, Limits?.V1Max);
        
        private double? _v2; public double? V2 { get => _v2; set { _v2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(V2Color)); OnPropertyChanged(nameof(V2DashBg)); OnPropertyChanged(nameof(V2DashForeground)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush V2Color => GetColor(V2, Limits?.V2Min, Limits?.V2Max);
        
        private double? _m1; public double? M1 { get => _m1; set { _m1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(M1Color)); OnPropertyChanged(nameof(M1DashBg)); OnPropertyChanged(nameof(M1DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush M1Color => GetColor(M1, Limits?.M1Min, Limits?.M1Max);
        
        private double? _m2; public double? M2 { get => _m2; set { _m2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(M2Color)); OnPropertyChanged(nameof(M2DashBg)); OnPropertyChanged(nameof(M2DashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush M2Color => GetColor(M2, Limits?.M2Min, Limits?.M2Max);
        
        private double? _q; public double? Q { get => _q; set { _q = value; OnPropertyChanged(); OnPropertyChanged(nameof(QColor)); OnPropertyChanged(nameof(QDashBg)); OnPropertyChanged(nameof(QDashForeground)); OnPropertyChanged(nameof(SingleStatusColor)); } }
        [System.Text.Json.Serialization.JsonIgnore] public Brush QColor => GetColor(Q, Limits?.QMin, Limits?.QMax);
        
        private string _status = string.Empty; public string Status { get => _status; set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(SingleStatusColor)); } }
    }
}