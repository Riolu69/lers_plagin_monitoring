// PollCoordinator.cs — единая точка опроса приборов: очередь, лимиты, кэш 3 мин, дедупликация, планировщик.
// Не зависит от WPF: UI подписывается на Updated и маршалит в Dispatcher.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LersMonitor
{
    public enum PollState { Pending, Queued, Polling, Ok, OkCached, Error, Paused }

    /// <summary>Что опрашиваем. NodeKey — ключ канала связи (по умолчанию название объекта).</summary>
    public sealed record PollTarget(int PointId, string NodeKey);

    public sealed class PointSnapshot
    {
        public int PointId { get; init; }
        public DateTime? DeviceTime { get; init; }          // время записи в ЛЭРС
        public string RawJson { get; init; } = "";          // UI разбирает существующим UpdatePointValues
        public DateTime ReadAtUtc { get; init; }            // когда мы прочитали запись
        public DateTime? PolledOkAtUtc { get; init; }       // когда прибор реально ответил на наш опрос

        /// <summary>Возраст данных: берём самый свежий из признаков (часы прибора могут отставать/спешить).</summary>
        public TimeSpan Age(DateTime nowUtc)
        {
            var best = PolledOkAtUtc ?? DateTime.MinValue;
            if (DeviceTime.HasValue)
            {
                var dev = DeviceTime.Value.Kind == DateTimeKind.Utc ? DeviceTime.Value : DeviceTime.Value.ToUniversalTime();
                if (dev > best) best = dev;
            }
            return best == DateTime.MinValue ? TimeSpan.MaxValue : nowUtc - best;
        }
    }

    public sealed record PollUpdate(int PointId, PollState State, string Message, PointSnapshot? Snapshot);

    public sealed class PollCoordinator
    {
        private readonly HttpClient _http;
        private readonly Func<string> _baseUrl;
        private readonly SemaphoreSlim _deviceSlots;   // одновременных «живых» опросов приборов
        private readonly SemaphoreSlim _httpSlots;     // одновременных лёгких GET
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _channelLocks = new();
        private readonly ConcurrentDictionary<int, Lazy<Task<PollUpdate>>> _inFlight = new();
        private readonly ConcurrentDictionary<int, PointSnapshot> _cache = new();
        private static readonly Random Rng = new();

        public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(3);
        public TimeSpan PollTimeout { get; set; } = TimeSpan.FromMinutes(3);
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Вызывается из потоков пула! Подписчик обязан маршалить в UI.</summary>
        public event Action<PollUpdate>? Updated;

        public PollCoordinator(HttpClient http, Func<string> baseUrl, int maxDevicePolls = 6, int maxHttp = 16)
        {
            _http = http; _baseUrl = baseUrl;
            _deviceSlots = new SemaphoreSlim(maxDevicePolls);
            _httpSlots = new SemaphoreSlim(maxHttp);
        }

        public bool TryGetCached(int pointId, out PointSnapshot snap) => _cache.TryGetValue(pointId, out snap!);

        // ---------- публичный API ----------

        public Task<PollUpdate> PollPointAsync(PollTarget t, bool ignoreCache, CancellationToken ct)
        {
            // Дедупликация: плановый опрос + ручной «повтор» одной точки делят один запрос.
            var lazy = _inFlight.GetOrAdd(t.PointId, _ => new Lazy<Task<PollUpdate>>(() => RunAsync(t, ignoreCache, ct)));
            return lazy.Value;
        }

        public Task<PollUpdate[]> PollManyAsync(IEnumerable<PollTarget> targets, bool ignoreCache, CancellationToken ct)
            => Task.WhenAll(targets.Select(t => PollPointAsync(t, ignoreCache, ct)));

        /// <summary>Только прочитать то, что уже лежит в ЛЭРС (без опроса прибора).</summary>
        public async Task<PollUpdate> ReadOnlyAsync(PollTarget t, CancellationToken ct)
        {
            var snap = await GetLastAsync(t.PointId, ct);
            if (snap == null) return new PollUpdate(t.PointId, PollState.Error, "Нет данных в ЛЭРС", null);
            _cache[t.PointId] = snap;
            return Publish(new PollUpdate(t.PointId, PollState.Ok, $"Снято {Fmt(snap)}", snap));
        }

        // ---------- ядро ----------

        private async Task<PollUpdate> RunAsync(PollTarget t, bool ignoreCache, CancellationToken ct)
        {
            try
            {
                Publish(new PollUpdate(t.PointId, PollState.Queued, "В очереди", null));

                if (!ignoreCache && _cache.TryGetValue(t.PointId, out var c) && c.Age(DateTime.UtcNow) <= CacheTtl)
                    return Publish(CachedUpdate(t.PointId, c));

                var chanLock = _channelLocks.GetOrAdd(t.NodeKey, _ => new SemaphoreSlim(1, 1));
                await chanLock.WaitAsync(ct).ConfigureAwait(false);        // 1 прибор/канал за раз
                try
                {
                    await _deviceSlots.WaitAsync(ct).ConfigureAwait(false); // глобальный потолок
                    try { return await PollDeviceAsync(t, ignoreCache, ct).ConfigureAwait(false); }
                    finally { _deviceSlots.Release(); }
                }
                finally { chanLock.Release(); }
            }
            catch (OperationCanceledException) { return Publish(new PollUpdate(t.PointId, PollState.Error, "Ошибка: отменено", null)); }
            catch (Exception ex) { return Publish(new PollUpdate(t.PointId, PollState.Error, "Ошибка: " + ex.Message, null)); }
            finally { _inFlight.TryRemove(t.PointId, out _); }
        }

        private async Task<PollUpdate> PollDeviceAsync(PollTarget t, bool ignoreCache, CancellationToken ct)
        {
            // Повторная проверка: пока стояли в очереди, мог опросить кто-то другой / ЛЭРС сам.
            var first = await GetLastAsync(t.PointId, ct);
            if (first != null && !ignoreCache && first.Age(DateTime.UtcNow) <= CacheTtl)
            {
                _cache[t.PointId] = first;
                return Publish(CachedUpdate(t.PointId, first));
            }

            DateTime? before = first?.DeviceTime;
            string lastError = "Нет ответа от прибора";
            int hardErrors = 0;
            var deadline = DateTime.UtcNow + PollTimeout;
            var nextPostAt = DateTime.UtcNow;
            var delay = TimeSpan.FromSeconds(2);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (DateTime.UtcNow >= nextPostAt)
                {
                    var (ok, code, err) = await PostPollAsync(t.PointId, ct);
                    if (ok)
                    {
                        nextPostAt = DateTime.UtcNow.AddSeconds(30);        // не долбим прибор повторными POST
                        Publish(new PollUpdate(t.PointId, PollState.Polling, "Идёт опрос прибора...", null));
                    }
                    else
                    {
                        lastError = err;
                        if (IsBusy(code, err))   // канал занят — ждём с jitter, это НЕ ошибка
                        {
                            nextPostAt = DateTime.UtcNow.AddSeconds(5 + Rng.Next(0, 6));
                            Publish(new PollUpdate(t.PointId, PollState.Polling, "Ожидание канала...", null));
                        }
                        else
                        {
                            if (++hardErrors >= 5) break;                   // быстрый отказ
                            nextPostAt = DateTime.UtcNow.AddSeconds(Math.Min(30, 3 * hardErrors * hardErrors));
                            Publish(new PollUpdate(t.PointId, PollState.Polling, $"Повтор ({err})", null));
                        }
                    }
                }

                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(6000, delay.TotalMilliseconds * 1.5)); // 2→3→4.5→6 c

                var snap = await GetLastAsync(t.PointId, ct);
                if (snap?.DeviceTime != null && (before == null || snap.DeviceTime > before))
                {
                    var fresh = new PointSnapshot { PointId = snap.PointId, DeviceTime = snap.DeviceTime, RawJson = snap.RawJson,
                                                    ReadAtUtc = snap.ReadAtUtc, PolledOkAtUtc = DateTime.UtcNow };
                    _cache[t.PointId] = fresh;
                    return Publish(new PollUpdate(t.PointId, PollState.Ok, $"Опрошено {Fmt(fresh)}", fresh));
                }
            }
            return Publish(new PollUpdate(t.PointId, PollState.Error, $"Таймаут: {lastError}", first));
        }

        private PollUpdate CachedUpdate(int id, PointSnapshot s)
        {
            var age = DateTime.UtcNow - (s.PolledOkAtUtc ?? s.ReadAtUtc);
            return new PollUpdate(id, PollState.OkCached,
                $"Снято {Fmt(s)} (данные {(int)Math.Max(0, age.TotalMinutes)} мин назад, повторно не опрашивали)", s);
        }

        private static string Fmt(PointSnapshot s) => (s.DeviceTime ?? s.ReadAtUtc.ToLocalTime()).ToString("HH:mm:ss");

        private PollUpdate Publish(PollUpdate u) { try { Updated?.Invoke(u); } catch { } return u; }

        // ---------- HTTP ----------

        private async Task<PointSnapshot?> GetLastAsync(int id, CancellationToken ct)
        {
            await _httpSlots.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);
                using var res = await _http.GetAsync($"{_baseUrl()}/Data/MeasurePoints/{id}/Consumption/Last", cts.Token);
                if (!res.IsSuccessStatusCode) return null;
                var json = await res.Content.ReadAsStringAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;
                using var doc = JsonDocument.Parse(json);
                return new PointSnapshot { PointId = id, RawJson = json, ReadAtUtc = DateTime.UtcNow,
                                           DeviceTime = TryGetDate(doc.RootElement, "dateTime", "deviceTime", "date") };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // таймаут запроса
            catch (HttpRequestException) { return null; }
            finally { _httpSlots.Release(); }
        }

        private async Task<(bool ok, int code, string err)> PostPollAsync(int id, CancellationToken ct)
        {
            await _httpSlots.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl()}/Poll/MeasurePoints/{id}/Currents?once=true")
                { Content = new ByteArrayContent(Array.Empty<byte>()) };
                using var res = await _http.SendAsync(req, cts.Token);
                if (res.IsSuccessStatusCode) return (true, (int)res.StatusCode, "");
                var body = await res.Content.ReadAsStringAsync(cts.Token);
                return (false, (int)res.StatusCode, ParseLersError(body, (int)res.StatusCode));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, 0, "Сбой сети (таймаут запроса)"); }
            catch (HttpRequestException) { return (false, 0, "Сбой сети"); }
            finally { _httpSlots.Release(); }
        }

        private static bool IsBusy(int code, string msg) =>
            code == 409 || code == 423 ||
            msg.Contains("занят", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("already", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("используется", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("ожидани", StringComparison.OrdinalIgnoreCase);

        private static string ParseLersError(string json, int status)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return $"Отказ ({status})";
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.TryGetProperty("errorDescription", out var a)) return a.GetString() ?? "";
                if (d.RootElement.TryGetProperty("errorMessage", out var b)) return b.GetString() ?? "";
            }
            catch { }
            return $"Отказ ({status})";
        }

        private static DateTime? TryGetDate(JsonElement e, params string[] names)
        {
            foreach (var n in names)
                if (e.TryGetProperty(n, out var p))
                {
                    if (p.TryGetDateTime(out var dt)) return dt;
                    if (p.ValueKind == JsonValueKind.String && DateTime.TryParse(p.GetString(), out var s)) return s;
                }
            return null;
        }
    }

    /// <summary>Планировщик без дрейфа: тик каждые N минут независимо от длительности цикла.</summary>
    public sealed class PollScheduler
    {
        private readonly PollCoordinator _coord;
        private readonly Func<IReadOnlyList<PollTarget>> _targets;   // только НЕ приостановленные
        public DateTime NextRunLocal { get; private set; }

        public PollScheduler(PollCoordinator coord, Func<IReadOnlyList<PollTarget>> targets) { _coord = coord; _targets = targets; }

        public async Task RunAsync(TimeSpan interval, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(interval);
            _ = _coord.PollManyAsync(_targets(), false, ct);               // первый цикл сразу (с учётом кэша)
            NextRunLocal = DateTime.Now + interval;
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    NextRunLocal = DateTime.Now + interval;
                    _ = _coord.PollManyAsync(_targets(), false, ct);       // не ждём: пересечения гасит дедупликация
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>Кнопка «Принудительный опрос». Кэш &lt; 3 мин уважается — такие точки не дёргаются.</summary>
        public Task ForceNowAsync(CancellationToken ct) => _coord.PollManyAsync(_targets(), false, ct);
    }
}
