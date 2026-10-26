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
                TxtStatus.Text = "Запуск опроса точек...";

                // Запуск опроса для каждой выбранной точки учета
                await Task.Run(() =>
                {
                    foreach (var point in selectedPoints)
                    {
                        // В Lers.Framework опрос вызывается непосредственно у точки
                        point.Poll(PollActionType.Current);
                    }
                });

                TxtStatus.Text = "Опрос запущен. Ожидание ответа приборов (8 сек)...";
                await Task.Delay(8000);

                TxtStatus.Text = "Считывание текущих значений...";
                
                await Task.Run(() =>
                {
                    foreach (var item in _results)
                    {
                        var point = selectedPoints.FirstOrDefault(p => p.Id == item.Id);
                        if (point == null) continue;

                        try
                        {
                            // Чтение последней записи текущих данных из архива текущих
                            var archive = point.Data.Get(ArchiveType.Current, DateTime.Today.AddDays(-1), DateTime.Now);
                            var lastRecord = archive.OrderByDescending(r => r.DateTime).FirstOrDefault();

                            if (lastRecord != null)
                            {
                                item.DeviceTime = lastRecord.DateTime;
                                
                                // Извлечение основных параметров
                                if (lastRecord.GetValue(DataParameterType.T_in) is double t1) item.T1 = t1;
                                if (lastRecord.GetValue(DataParameterType.T_out) is double t2) item.T2 = t2;
                                if (lastRecord.GetValue(DataParameterType.P_in) is double p1) item.P1 = p1;
                                if (lastRecord.GetValue(DataParameterType.V_in) is double v1) item.V1 = v1;

                                item.Status = "Успешно";
                            }
                            else
                            {
                                item.Status = "Нет данных в архиве";
                            }
                        }
                        catch (Exception ex)
                        {
                            item.Status = $"Ошибка: {ex.Message}";
                        }
                    }
                });

                GridResults.Items.Refresh();
                TxtStatus.Text = "Мониторинг завершен.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка во время запуска опроса: {ex.Message}");
                TxtStatus.Text = "Сбой операции";
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
