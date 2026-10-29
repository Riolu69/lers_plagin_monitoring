# LersMonitor — ревизия опроса и план доработок

## 1. Аудит текущей логики опроса

| # | Проблема | Где | Последствие | Исправление |
|---|---|---|---|---|
| 1 | Опрос запускается из `Task.Run` (`BtnNodeToggle_Click`, `BtnRetryPoint_Click`), а внутри меняются VM, `Brushes`, `Application.Current.MainWindow.FindResource` | `RunSmartPollingIterationAsync` | `FindResource` из пула бросает `InvalidOperationException`, `catch {}` его глотает: UI не обновляется, ошибки не видно. `Dictionary initialTimes` пишется из нескольких потоков | `PollCoordinator` не трогает UI, шлёт `Updated`; UI маршалит в `Dispatcher` |
| 2 | Нет лимита параллельности: `Task.WhenAll` по всем точкам, у каждой цикл до 60 × (GET + POST) каждые 3 с | там же | 100 точек ≈ 33 GET/с к ЛЭРС и шквал POST. Перегрузка каналов — ровно то, чего вы хотите избежать | `SemaphoreSlim` на живые опросы (6) и на HTTP (16); `SemaphoreSlim(1)` на канал/объект |
| 3 | Нет дедупликации: плановый цикл и ручной «повтор» одной точки шлют параллельные POST | `BtnRetryPoint_Click` + цикл дашборда | Двойной опрос прибора, гонка статусов | `_inFlight`: один `Task` на точку |
| 4 | Фиксированный шаг 3 с и повтор POST каждые ~10 итераций | цикл `for attempt ≤ 60` | Лишние запросы; при «занят» опрос идёт каждые 9 с без jitter | Экспоненциальный шаг 2→6 с, POST не чаще раза в 30 с, jitter при «занят», fail-fast после 5 жёстких ошибок |
| 5 | Дрейф расписания: интервал отсчитывается после окончания цикла, а цикл может идти до 3 мин | `BtnStartDashboard_Click` | Реальный период 13 мин вместо 10 | `PeriodicTimer` + дедупликация |
| 6 | «Тихая» проверка каждые 10 с по всем точкам | цикл ожидания | Постоянный фоновый трафик | Убрана; при необходимости только GET раз в 60 с (`ReadOnlyAsync`) |
| 7 | Старт дашборда: `foreach … await TryFetchAndUpdateAsync` | `BtnStartDashboard_Click` | Последовательная загрузка, UI «висит» | Параллельно через `PollManyAsync` |
| 8 | Статус-строки используются как логика (`Status.Contains("Таймаут")`) | `HasError`, `IsWaiting` | Любая правка текста ломает цвета | `enum PollState` |
| 9 | Токен `new CancellationTokenSource().Token` для ручных запусков, никогда не отменяется и не освобождается | оба обработчика | Опрос идёт после остановки дашборда | Один токен сессии |
| 10 | `HttpResponseMessage`/`HttpRequestMessage` не освобождаются, `HttpClient.Timeout = 10 мин` | везде | Утечки соединений, зависшие запросы | `using`, таймаут 30 с на запрос через linked CTS |
| 11 | `StopNativeLersTrend`: `SendAsync` без `await`, без токена | вкладка «текущие» | «Забытые» запросы и необработанные исключения | `await` + `using` |
| 12 | Карточка объекта красная при `HasBreach` даже пока часть точек не опрошена | `TileBackground` | Нарушает п. 3 ТЗ | Серая, пока `Points.Any(!IsDone)` |
| 13 | `catch { }` повсюду | везде | Ошибки не диагностируются | Ошибки превращаются в `PollState.Error` с текстом; добавьте лог (Serilog/`Debug`) |

## 2. Архитектура

```
UI (XAML)  ──binding──►  ViewModels (PointResultViewModel, DashboardNodeViewModel, PodpitkaNode/PointViewModel)
   ▲                         ▲  Apply(PollUpdate)  (через Dispatcher)
   │ команды                 │
MainWindow (тонкий)  ──►  PollScheduler (PeriodicTimer, ForceNow)
                              │
                          PollCoordinator  ← очередь, лимиты, кэш 3 мин, дедупликация, backoff
                              │
                          HttpClient → ЛЭРС REST
PodpitkaVolumeService ── GET Consumption (V1 по срезам) ──► ЛЭРС
```

Файлы: `PollCoordinator.cs`, `MasonryPanel.cs`, `PodpitkaModule.cs`. Добавьте в проект рядом с `MainWindow.xaml.cs`.

### Кэш (п. 2 ТЗ)
`PollCoordinator.CacheTtl = 3 мин`. Точка не опрашивается повторно, если возраст данных ≤ 3 мин. Возраст берётся по более свежему из двух признаков: время записи прибора и момент нашего успешного опроса. Так сбитые часы прибора не вызывают бесконечных повторов. Статус такой точки — `OkCached` («Снято 14:02:11 (данные 1 мин назад, повторно не опрашивали)»), цвет как у «Ок». Проверка выполняется дважды: до очереди и после ожидания, потому что пока точка стояла в очереди, её мог опросить кто-то другой. Кэш живёт в памяти приложения. Если нужен общий кэш для нескольких клиентов, придётся выносить `PollCoordinator` в отдельный сервис.

## 3. Патчи в MainWindow.xaml.cs

### 3.1 Инициализация (после подключения в `BtnConnect_Click`, рядом с `_allPoints = …`)
```csharp
private PollCoordinator _coord = null!;
private PollScheduler? _dashScheduler;
private PodpitkaVolumeService _volumes = null!;
private static readonly SemaphoreSlim _uiGate = new(1);

private void InitPolling()
{
    _http.Timeout = Timeout.InfiniteTimeSpan;                 // таймауты на уровне запроса
    _coord = new PollCoordinator(_http, () => _baseUrl, maxDevicePolls: 6, maxHttp: 16);
    _coord.Updated += u => Dispatcher.InvokeAsync(() => ApplyUpdate(u));
    _volumes = new PodpitkaVolumeService(_http, () => _baseUrl);
}

private void ApplyUpdate(PollUpdate u)
{
    // дашборд / общий мониторинг
    foreach (var vm in _dashboardFlatPoints.Concat(_results).Where(v => v.Id == u.PointId))
    {
        if (u.Snapshot != null && u.State is PollState.Ok or PollState.OkCached)
        {
            using var doc = JsonDocument.Parse(u.Snapshot.RawJson);
            vm.DeviceTime = u.Snapshot.DeviceTime;
            UpdatePointValues(vm, doc.RootElement);
        }
        vm.State = u.State; vm.Status = u.Message; vm.RefreshColors();
    }
    foreach (var n in _dashboardNodes) n.Refresh();
    // подпитка
    foreach (var p in _podpitkaNodes.SelectMany(n => n.Points).Where(p => p.Id == u.PointId))
        { p.State = u.State; p.Message = u.Message; }
    foreach (var n in _podpitkaNodes) n.Refresh();
}
```
`InitPolling()` вызвать в `BtnConnect_Click` после установки `_baseUrl` и токена.

### 3.2 PointResultViewModel
```csharp
private PollState _state = PollState.Pending;
[JsonIgnore] public PollState State { get => _state; set { _state = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(IsWaiting)); OnPropertyChanged(nameof(RetryVisibility)); } }
[JsonIgnore] public bool HasError => State == PollState.Error;
[JsonIgnore] public bool IsWaiting => State is PollState.Pending or PollState.Queued or PollState.Polling;
[JsonIgnore] public bool IsDone => !IsWaiting && State != PollState.Paused;
```
В `DashboardNodeViewModel.TileBackground` порядок такой: `IsPaused` → серая; `Points.Any(!p.IsDone)` → серая (п. 3 ТЗ); `HasBreach` → красная; `HasError` → оранжевая; иначе зелёная.

### 3.3 Дашборд: замена цикла в `BtnStartDashboard_Click`
Удалите `RunSmartPollingIterationAsync`, внутренний `while` и «тихую проверку». Вместо них:
```csharp
IReadOnlyList<PollTarget> Targets() => _dashboardNodes.Where(n => !n.IsPaused)
    .SelectMany(n => n.Points.Select(p => new PollTarget(p.Id, n.NodeTitle))).ToList();

_dashScheduler = new PollScheduler(_coord, Targets);
_ = _dashScheduler.RunAsync(TimeSpan.FromMinutes(intervalMin), token);

// таймер обратного отсчёта (DispatcherTimer 1 с) для TxtStatus:
// $"Следующий опрос через {(_dashScheduler.NextRunLocal - DateTime.Now):mm\\:ss}"
```

### 3.4 Кнопка «Принудительный опрос»
```xml
<Button x:Name="BtnForcePoll" Content="⟳ Принудительный опрос" Style="{StaticResource AppleGreenButton}"
        Margin="0,0,10,0" Click="BtnForcePoll_Click" IsEnabled="False"/>
```
```csharp
private async void BtnForcePoll_Click(object s, RoutedEventArgs e)
{
    if (_dashScheduler == null || _dashboardCts == null) return;
    BtnForcePoll.IsEnabled = false;
    try { await _dashScheduler.ForceNowAsync(_dashboardCts.Token); }   // кэш < 3 мин уважается
    finally { BtnForcePoll.IsEnabled = true; }
}
```
Включать кнопку после старта автоопроса.

### 3.5 Перезапуск одной точки и пауза объекта
```csharp
private async void BtnRetryPoint_Click(object s, RoutedEventArgs e)
{
    if (s is not Button { Tag: PointResultViewModel vm } || _dashboardCts == null) return;
    await _coord.PollPointAsync(new PollTarget(vm.Id, vm.NodeTitle), ignoreCache: true, _dashboardCts.Token);
}

private async void BtnNodeToggle_Click(object s, RoutedEventArgs e)
{
    if (s is not Button { Tag: DashboardNodeViewModel node } || _dashboardCts == null) return;
    node.IsPaused = !node.IsPaused;
    foreach (var p in node.Points) { p.State = node.IsPaused ? PollState.Paused : PollState.Pending; p.Status = node.IsPaused ? "Опрос приостановлен" : "Ожидание..."; }
    node.Refresh();
    if (!node.IsPaused)
        await _coord.PollManyAsync(node.Points.Select(p => new PollTarget(p.Id, node.NodeTitle)), false, _dashboardCts.Token);
}
```
Кнопка ↻ в XAML уже есть. Теперь она видна только у точки с `State == Error` и перезапускает только эту точку, а не объект целиком (п. 3 ТЗ). Для `BtnStartMonitor_Click` («Общий мониторинг») замените вызов `RunSmartPollingIterationAsync` на `_coord.PollManyAsync(targets, false, token)`.

## 4. Плотная вёрстка (п. 1 ТЗ)

В XAML вместо `WrapPanel` в `ItemsDashboard`:
```xml
<ItemsControl.ItemsPanel>
  <ItemsPanelTemplate><local:MasonryPanel Spacing="6" MaxCardWidth="360"/></ItemsPanelTemplate>
</ItemsControl.ItemsPanel>
```
Внутри карточки замените `UniformGrid Columns="{Binding ColumnsCount}"` на `WrapPanel`, уберите `MinWidth="150"` у блока точки (он создаёт пустоты) и добавьте `MinWidth="120"` только как нижнюю границу читаемости. Тогда блоки точек занимают ровно столько места, сколько нужно, а `MasonryPanel` упаковывает карточки по принципу «самая верхняя-левая свободная позиция». Свойство `ColumnsCount` можно удалить. Для максимальной плотности можно также уменьшить `Padding` карточки до 6 и `FontSize` подписей до 9–10. В скроллируемом `ScrollViewer` включите `CanContentScroll="False"`, чтобы прокрутка была плавной.

## 5. Вкладка «Подпитка» (п. 4 ТЗ)

### 5.1 Вкладка и экран
```xml
<RadioButton x:Name="TabPodpitka" Content="Подпитка" Style="{StaticResource TabNavButton}" Checked="TabMode_Checked" Margin="0,0,6,6"/>
```
В `TabMode_Checked` добавьте `ViewPodpitka` в список скрываемых и показывайте при `TabPodpitka.IsChecked`.

```xml
<Grid x:Name="ViewPodpitka" Grid.Row="2" Visibility="Collapsed">
  <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
  <Border Background="{DynamicResource CardBackground}" CornerRadius="14" Padding="14" Margin="0,0,0,12">
    <WrapPanel>
      <Button Content="⚙ Выбрать точки" Style="{StaticResource ApplePrimaryButton}" Background="{DynamicResource AccentGray}" Margin="0,0,10,0" Click="BtnPodpitkaPoints_Click"/>
      <Button Content="⚙ Допуски V1" Style="{StaticResource ApplePrimaryButton}" Background="{DynamicResource AccentGray}" Margin="0,0,10,0" Click="BtnPodpitkaLimits_Click"/>
      <TextBlock Text="Опрос каждые (мин):" VerticalAlignment="Center" Margin="0,0,8,0" Foreground="{DynamicResource LabelSecondary}"/>
      <ComboBox x:Name="CmbPodpitkaInterval" Width="70" Margin="0,0,14,0" SelectionChanged="CmbPodpitkaInterval_Changed"/>
      <Button x:Name="BtnPodpitkaForce" Content="⟳ Принудительный опрос" Style="{StaticResource AppleGreenButton}" Click="BtnPodpitkaForce_Click"/>
    </WrapPanel>
  </Border>
  <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
    <ItemsControl x:Name="ItemsPodpitka">
      <ItemsControl.ItemsPanel><ItemsPanelTemplate><local:MasonryPanel Spacing="6" MaxCardWidth="340"/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
      <ItemsControl.ItemTemplate>
        <DataTemplate>
          <Border Background="{Binding TileBackground}" CornerRadius="8" Padding="8" VerticalAlignment="Top" HorizontalAlignment="Left">
            <StackPanel>
              <TextBlock Text="{Binding NodeTitle}" Foreground="White" FontWeight="Bold" FontSize="13" TextWrapping="Wrap" MaxWidth="300"/>
              <TextBlock Text="{Binding GeneralStatus}" Foreground="White" FontSize="10" Opacity="0.85" Margin="0,1,0,6"/>
              <ItemsControl ItemsSource="{Binding Points}">
                <ItemsControl.ItemsPanel><ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                  <DataTemplate>
                    <Border Background="#26000000" CornerRadius="4" Padding="4" Margin="2">
                      <StackPanel>
                        <Grid>
                          <TextBlock Text="{Binding Title}" Foreground="{Binding PointTitleColor}" FontSize="11" FontWeight="Bold" HorizontalAlignment="Left"/>
                          <Button HorizontalAlignment="Right" Tag="{Binding}" Click="BtnPodpitkaRetry_Click" Visibility="{Binding RetryVisibility}"
                                  Background="Transparent" BorderThickness="0" Content="↻" Foreground="#FF9500" ToolTip="Повторить опрос точки"/>
                        </Grid>
                        <UniformGrid Columns="2">
                          <!-- 4 среза V1; фон/цвет текста при выходе за допуск — как на основном дашборде -->
                          <Border Background="{Binding ReportBg}" CornerRadius="3" Padding="4,2" Margin="1">
                            <StackPanel><TextBlock Text="Отчётный период V1" FontSize="9" Foreground="White" Opacity="0.8"/>
                              <TextBlock Text="{Binding Report, StringFormat={}{0:F1} м³, TargetNullValue='—'}" FontSize="13" FontWeight="Bold" Foreground="{Binding ReportFg}"/></StackPanel></Border>
                          <Border Background="{Binding DayBg}" CornerRadius="3" Padding="4,2" Margin="1">
                            <StackPanel><TextBlock Text="Вчера V1" FontSize="9" Foreground="White" Opacity="0.8"/>
                              <TextBlock Text="{Binding Day, StringFormat={}{0:F1} м³, TargetNullValue='—'}" FontSize="13" FontWeight="Bold" Foreground="{Binding DayFg}"/></StackPanel></Border>
                          <Border Background="{Binding TodayBg}" CornerRadius="3" Padding="4,2" Margin="1">
                            <StackPanel><TextBlock Text="Сегодня V1" FontSize="9" Foreground="White" Opacity="0.8"/>
                              <TextBlock Text="{Binding Today, StringFormat={}{0:F1} м³, TargetNullValue='—'}" FontSize="13" FontWeight="Bold" Foreground="{Binding TodayFg}"/></StackPanel></Border>
                          <Border Background="{Binding HourBg}" CornerRadius="3" Padding="4,2" Margin="1">
                            <StackPanel><TextBlock Text="Прошлый час V1" FontSize="9" Foreground="White" Opacity="0.8"/>
                              <TextBlock Text="{Binding Hour, StringFormat={}{0:F2} м³, TargetNullValue='—'}" FontSize="13" FontWeight="Bold" Foreground="{Binding HourFg}"/></StackPanel></Border>
                        </UniformGrid>
                      </StackPanel>
                    </Border>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>
            </StackPanel>
          </Border>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
  </ScrollViewer>
</Grid>
```

### 5.2 Выбор точек и допуски
- «Выбрать точки» открывает оверлей, по образцу `OverlayLimits`, со списком `ListPodpitka` (`SelectionMode=Multiple`, `DisplayMemberPath=DisplayName`, поиск как в `ApplyFiltersDashboard`). Результат сохраняется в `_settings.PodpitkaPointIds`, после чего `_podpitkaNodes` пересобирается: `GroupBy(NodeTitle)` → `PodpitkaNodeViewModel`.
- «Допуски V1» работает как `BtnSettingsShared_Click`: 8 полей (Мин/Макс × отчётный период, вчера, сегодня, прошлый час), поддерживается групповая правка (пустое поле не затирает значение). Результат сохраняется в `_settings.PodpitkaLimits[pointId]`. После сохранения для каждого `PodpitkaPointViewModel` выставьте `Limits` и вызовите `RefreshColors()` и `node.Refresh()`.
- Интервал: `CmbPodpitkaInterval.ItemsSource = PodpitkaPeriods.IntervalsMin` (10, 20, 30, 40, 50, 60, 120), значение хранится в `_settings.PodpitkaIntervalMin`. При смене перезапустите планировщик подпитки.

### 5.3 Цикл опроса подпитки
```csharp
private readonly ObservableCollection<PodpitkaNodeViewModel> _podpitkaNodes = new();
private CancellationTokenSource? _podCts;

private async Task PodpitkaCycleAsync(bool ignoreCache, CancellationToken ct)
{
    var pts = _podpitkaNodes.SelectMany(n => n.Points.Select(p => (p, n))).ToList();
    foreach (var (p, _) in pts) { p.State = PollState.Queued; }
    await Task.WhenAll(pts.Select(async x =>
    {
        // 1) свежие данные из прибора (кэш 3 мин, очередь и лимиты те же)
        var u = await _coord.PollPointAsync(new PollTarget(x.p.Id, x.n.NodeTitle), ignoreCache, ct);
        // 2) объёмы из архива ЛЭРС
        if (u.State != PollState.Error)
            try { await _volumes.RefreshAsync(x.p, DateTime.Now, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { x.p.State = PollState.Error; x.p.Message = "Ошибка: " + ex.Message; }
    }));
    foreach (var n in _podpitkaNodes) n.Refresh();
}
```
Планировщик: `PeriodicTimer(TimeSpan.FromMinutes(_settings.PodpitkaIntervalMin))` вызывает `PodpitkaCycleAsync(false, ct)`. Кнопка «Принудительный опрос» вызывает то же самое немедленно, кэш при этом соблюдается.

## 6. Что обязательно проверить у себя
1. **Эндпоинт объёмов.** Путь `Data/MeasurePoints/{id}/Consumption?start&end&dataAggregation=Hour|Day` и имя поля V1 в записях сверьте в Swagger вашего ЛЭРС. Я их не проверял, они вынесены в `PodpitkaVolumeService.SumV1Async` и `ReadV1`.
2. **Опрос архивов.** Часовые и суточные данные приходят из архива. Если нужны самые свежие, а ЛЭРС сам их не подтягивает, нужен отдельный вызов опроса архивов (в API ЛЭРС есть ветка `Poll/…`). Текущий `PollPointAsync` опрашивает только текущие параметры.
3. **Канал связи.** Ключом канала сейчас служит название объекта. Если у вас несколько приборов на один модем или порт, передавайте в `PollTarget.NodeKey` идентификатор устройства или порта.
4. **Лимиты.** `maxDevicePolls=6` и `maxHttp=16` подберите под ваш ЛЭРС-сервер.
5. **Отчётный период.** Реализован как последний закрытый период 25-е → 24-е: 13.10 → 25.08–24.09 (как в вашем примере), 29.10 → 25.09–24.10. Если нужен текущий незакрытый период, измените `PodpitkaPeriods.Report`.
6. **`UserSettings.LastResults`.** Сериализует `PointResultViewModel`. Новое свойство `State` помечено `[JsonIgnore]`, поэтому после запуска восстановленные из файла точки показывают «Снято …» с состоянием `Pending`. Если нужно, при загрузке выставляйте им `OkCached`.
