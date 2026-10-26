using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Lers;
using Lers.Core;
using Lers.Data;
using Lers.Networking;
using Lers.Poll;

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
                await _server.ConnectAsync(TxtHost.Text, 10000, auth);

                var points = await _server.MeasurePoints.GetListAsync();
                ListPoints.ItemsSource = points;

                BtnStartMonitor.IsEnabled = true;
                TxtStatus.Text = $"Подключено. Загружено точек учёта: {points.Count}";
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
                TxtStatus.Text = "Запуск ручного опроса...";

                // Параметры для опроса только текущих значений
                var pollParams = new ManualPollParameters
                {
                    DataTypes = PollDataType.Current
                };

                // Добавляем выбранные точки учета в сеанс
                foreach (var point in selectedPoints)
                {
                    pollParams.AddMeasurePoint(point);
                }

                var session = await _server!.Poll.StartManualPollAsync(pollParams);

                // Ожидание завершения опроса
                while (true)
                {
                    await session.RefreshAsync();
                    TxtStatus.Text = $"Опрос: {session.State}";

                    if (session.State == PollSessionState.Completed || 
                        session.State == PollSessionState.Terminated || 
                        session.State == PollSessionState.Error)
                    {
                        break;
                    }

                    await Task.Delay(1500);
                }

                TxtStatus.Text = "Считывание текущих значений...";
                
                // Считываем обновленные текущие данные
                foreach (var item in _results)
                {
                    var point = selectedPoints.FirstOrDefault(p => p.Id == item.Id);
                    if (point == null) continue;

                    // Запрашиваем последние сохраненные текущие данные
                    var current = await point.Data.GetCurrentAsync();
                    if (current != null)
                    {
                        item.DeviceTime = current.DeviceTime;
                        item.T1 = current.T_in;
                        item.T2 = current.T_out;
                        item.P1 = current.P_in;
                        item.V1 = current.V_in;
                        item.Status = "Опрошено успешно";
                    }
                    else
                    {
                        item.Status = "Нет данных";
                    }
                }

                GridResults.Items.Refresh();
                TxtStatus.Text = "Мониторинг завершен.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка во время опроса: {ex.Message}");
                TxtStatus.Text = "Сбой опроса";
            }
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
