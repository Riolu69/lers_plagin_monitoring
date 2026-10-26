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
                TxtStatus.Text = "Запуск ручного опроса...";

                // Постановка точек в очередь опроса через PollManager
                await Task.Run(() =>
                {
                    foreach (var point in selectedPoints)
                    {
                        // Ставим опрос текущих данных по прибору точки
                        _server!.Poll.Queue(point, PollAction.Current);
                    }
                });

                TxtStatus.Text = "Опрос поставлен в очередь. Ожидание завершения (10 сек)...";

                // Небольшая задержка на время установки соединения со шлюзом/модемом
                await Task.Delay(10000);

                TxtStatus.Text = "Считывание полученных текущих данных...";
                
                await Task.Run(() =>
                {
                    foreach (var item in _results)
                    {
                        var point = selectedPoints.FirstOrDefault(p => p.Id == item.Id);
                        if (point == null) continue;

                        try
                        {
                            // Чтение последней зафиксированной записи данных
                            var lastData = point.Data.GetLastData();
                            if (lastData != null)
                            {
                                item.DeviceTime = lastData.DateTime;
                                item.T1 = lastData.T_in;
                                item.T2 = lastData.T_out;
                                item.P1 = lastData.P_in;
                                item.V1 = lastData.V_in;
                                item.Status = "Данные получены";
                            }
                            else
                            {
                                item.Status = "Нет данных";
                            }
                        }
                        catch (Exception ex)
                        {
                            item.Status = $"Ошибка: {ex.Message}";
                        }
                    }
                });

                GridResults.Items.Refresh();
                TxtStatus.Text = "Готово.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при работе с опросом: {ex.Message}");
                TxtStatus.Text = "Ошибка";
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
