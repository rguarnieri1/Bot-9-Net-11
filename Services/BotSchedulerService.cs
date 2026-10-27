using System.Collections.Concurrent;
using BotCripto.Models;
using BotCripto.Strategies;

namespace BotCripto.Services;

public class BotSchedulerService
{
    private const int CandlesPerAnalysis = 300;  // stessa finestra del backtest
    private const int MaxConcurrentFetches = 4;
    private const string BenchmarkSymbol = "BTC";
    private const int MinCandlesForAnalysis = RelativeStrengthBreakoutStrategy.MinCandles; // minimo richiesto dalla strategia
    // Un segnale viene valutato solo entro questo tempo dalla chiusura della candela (niente ingressi in ritardo)
    private static readonly TimeSpan SignalFreshness = TimeSpan.FromMinutes(30);

    private readonly CryptoDataService _dataService;
    private readonly NotificationService _notificationService;
    private readonly ReportingService _reportingService;
    private readonly RelativeStrengthBreakoutStrategy _strategy;
    private readonly RiskManager _riskManager;
    private Timer? _marketCheckTimer;
    private Timer? _weeklyReportTimer;
    private bool _isRunning = false;
    private int _cycleRunning = 0;
    private DateTime? _lastScannedCandle;
    // Il controllo gira spesso rispetto alla chiusura delle candele: la finestra di freschezza (30 min) contiene più tick,
    // così un tick in ritardo/saltato non fa perdere il segnale. La scansione dei simboli avviene una sola volta per candela.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly string _timeframe;
    private readonly TimeSpan _timeframeSpan;
    private decimal _realizedPnl;
    private int _checksPerformed = 0;
    private int _signalsGenerated = 0;
    private int _tradesRecorded = 0;
    private readonly Dictionary<string, int> _cumulativeRejectionCounts = new();

    // Capitale investibile illimitato: nessun importo totale, solo un importo fisso per trade (capitalPerTrade)
    public BotSchedulerService(string timeframe, decimal rewardRiskRatio, decimal capitalPerTrade)
    {
        _timeframe = timeframe;
        _timeframeSpan = BacktestService.TimeframeToTimeSpan(timeframe);
        _dataService = new CryptoDataService();
        _notificationService = new NotificationService();
        _reportingService = new ReportingService();
        _strategy = new RelativeStrengthBreakoutStrategy();
        _riskManager = new RiskManager(
            positionValue: capitalPerTrade,   // importo fisso per trade (€)
            rewardRiskRatio: rewardRiskRatio, // take profit = R:R × distanza dello stop
            commissionsPercent: 0.25m,        // 0.25% commissioni per lato
            taxRate: 0.26m                    // 26% tasse
        );
    }

    public async Task StartAsync()
    {
        if (_isRunning)
        {
            Console.WriteLine("Bot è già in esecuzione.");
            return;
        }

        _isRunning = true;
        Console.WriteLine("\n🤖 Bot 8 avviato! (paper trading: nessun ordine reale)");
        Console.WriteLine($"📊 Monitoraggio ogni {CheckInterval.TotalMinutes:F0} minuti, candele {_timeframe}...\n");

        // Esegui il primo controllo immediatamente
        await CheckMarketAsync();

        // Timer di controllo periodico (stop/target dei trade aperti + ricerca segnali a candela chiusa)
        _marketCheckTimer = new Timer(
            async _ => await CheckMarketAsync(),
            null,
            CheckInterval,
            CheckInterval);

        // Timer per il report settimanale (ogni lunedì alle 00:00)
        var now = DateTime.UtcNow;
        var daysToMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        var nextMonday = now.Date.AddDays(daysToMonday); // lunedì alle 00:00 UTC
        if (nextMonday <= now)
            nextMonday = nextMonday.AddDays(7);

        var timeUntilMonday = nextMonday - now;
        _weeklyReportTimer = new Timer(
            _ =>
            {
                try { _reportingService.GenerateWeeklyReport(); }
                catch (Exception ex) { Console.WriteLine($"❌ Errore nel report settimanale: {ex.Message}"); }
            },
            null,
            timeUntilMonday,
            TimeSpan.FromDays(7));

        Console.WriteLine($"⏰ Prossimo report settimanale: {nextMonday:yyyy-MM-dd HH:mm:ss} UTC");

        await Task.Delay(-1);
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _marketCheckTimer?.Dispose();
        _weeklyReportTimer?.Dispose();
        Console.WriteLine("\n🛑 Bot 8 fermato.");
    }

    private async Task CheckMarketAsync()
    {
        // Il timer scatta ogni 30 minuti anche se il ciclo precedente non è finito: mai due cicli insieme,
        // altrimenti lo stesso segnale verrebbe registrato due volte.
        if (Interlocked.Exchange(ref _cycleRunning, 1) == 1)
        {
            Console.WriteLine("⏭️  Ciclo precedente ancora in corso: questo ciclo viene saltato.");
            return;
        }

        try
        {
            _checksPerformed++;
            var now = DateTime.UtcNow;
            Console.WriteLine($"\n⏱️  Ciclo #{_checksPerformed} - {now:yyyy-MM-dd HH:mm:ss} UTC");

            // Cache per ciclo; il download è limitato a poche richieste concorrenti
            var candleCache = new ConcurrentDictionary<string, Task<List<Candle>>>();
            using var fetchGate = new SemaphoreSlim(MaxConcurrentFetches);
            async Task<List<Candle>> FetchCandlesAsync(string symbol)
            {
                await fetchGate.WaitAsync();
                try
                {
                    return (await _dataService.GetCandlesAsync(symbol, _timeframe, CandlesPerAnalysis)).OrderBy(c => c.Time).ToList();
                }
                finally
                {
                    fetchGate.Release();
                }
            }
            Task<List<Candle>> GetCandlesCachedAsync(string symbol) => candleCache.GetOrAdd(symbol, FetchCandlesAsync);

            // 1) Paper trading: chiudi i trade aperti che hanno toccato stop loss o target
            int tradesClosed = await CloseTradesAtStopOrTargetAsync(GetCandlesCachedAsync);

            // Trade gestiti dal paper trading (quelli registrati prima non hanno stop/target e vengono ignorati)
            var managedTrades = _reportingService.GetAllTrades().Where(t => t.IsPaperManaged).ToList();
            var openTrades = managedTrades.Where(t => t.Status == "Open").ToList();
            _realizedPnl = managedTrades.Where(t => t.Status == "Closed").Sum(t => t.Profit ?? 0);

            int signalsFound = 0;
            int signalsFiltered = 0;
            var rejectionCounts = new Dictionary<string, int>();

            void CountRejection(string category)
            {
                rejectionCounts.TryGetValue(category, out var c);
                rejectionCounts[category] = c + 1;

                _cumulativeRejectionCounts.TryGetValue(category, out var cc);
                _cumulativeRejectionCounts[category] = cc + 1;
            }

            // 2) I segnali si valutano solo su candele chiuse (come nel backtest) e solo subito dopo la chiusura
            // L'ultima candela chiusa si ricava dal calendario (candele allineate all'epoca UTC)
            var lastCloseEnd = (DateTime?)new DateTime(now.Ticks / _timeframeSpan.Ticks * _timeframeSpan.Ticks, DateTimeKind.Utc);
            var lastClosedTime = lastCloseEnd - _timeframeSpan;
            bool newCandle = lastCloseEnd != null && now - lastCloseEnd.Value <= SignalFreshness && _lastScannedCandle != lastClosedTime;

            if (!newCandle)
            {
                var nextClose = lastCloseEnd?.Add(_timeframeSpan);
                Console.WriteLine($"🕒 Nessuna candela {_timeframe} appena chiusa: ricerca segnali alla prossima chiusura ({nextClose:yyyy-MM-dd HH:mm} UTC).");
            }
            else
            {
                Console.WriteLine($"🕯️  Candela {_timeframe} chiusa alle {lastCloseEnd:yyyy-MM-dd HH:mm} UTC: ricerca segnali");

                _lastScannedCandle = lastClosedTime;
                var cryptos = await _dataService.GetLargeCapCryptocurrenciesAsync();
                Console.WriteLine($"📈 Analizzando {cryptos.Count} criptovalute (max 500 per performance)...");

                var allTrades = _reportingService.GetAllTrades();
                var openSymbols = openTrades.Select(t => t.Symbol).ToHashSet();
                var registeredSignals = allTrades.Where(t => t.SignalCandleTime == lastClosedTime).Select(t => t.Symbol).ToHashSet();

                // Scarica in parallelo (con limite) solo i simboli che verranno analizzati; gli errori emergono nel loop per simbolo
                var toFetch = cryptos.Where(c => !openSymbols.Contains(c.Symbol) && !registeredSignals.Contains(c.Symbol)).Select(c => GetCandlesCachedAsync(c.Symbol)).ToList();
                try { await Task.WhenAll(toFetch); } catch { /* gestito per simbolo più sotto */ }
                var candidates = new List<(Cryptocurrency crypto, AnalysisResult analysis, List<Candle> candles)>();

                // Benchmark della forza relativa: se non disponibile non si può valutare alcun segnale
                List<Candle> benchmarkCandles;
                try { benchmarkCandles = ClosedCandles(await GetCandlesCachedAsync(BenchmarkSymbol), now); }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️  Benchmark {BenchmarkSymbol} non disponibile: {ex.Message}");
                    benchmarkCandles = new List<Candle>();
                }

                foreach (var crypto in cryptos)
                {
                    try
                    {
                        if (openSymbols.Contains(crypto.Symbol))
                        {
                            CountRejection("Posizione già aperta sul simbolo");
                            continue;
                        }

                        if (registeredSignals.Contains(crypto.Symbol))
                        {
                            CountRejection("Segnale di questa candela già registrato");
                            continue;
                        }

                        var candles = ClosedCandles(await GetCandlesCachedAsync(crypto.Symbol), now);

                        if (candles.Count < MinCandlesForAnalysis)
                        {
                            CountRejection($"Dati insufficienti (candele < {MinCandlesForAnalysis})");
                            continue;
                        }

                        if (candles.Last().Time != lastClosedTime)
                        {
                            CountRejection("Ultima candela non aggiornata");
                            continue;
                        }

                        // STRATEGIA: Breakout su forza relativa (benchmark BTC)
                        var signalResult = _strategy.Analyze(crypto.Symbol, candles, benchmarkCandles);

                        if (signalResult.IsSignal)
                            candidates.Add((crypto, signalResult, candles));
                        else
                            CountRejection(RejectionLabel(signalResult.Rejection));

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ⚠️  {crypto.Symbol}: {ex.Message}");
                    }
                }

                // Dal segnale più forte al più debole (capitale illimitato: entrano tutti i segnali validi)
                foreach (var (crypto, signalResult, candles) in candidates.OrderByDescending(c => c.analysis.Indicators.GetValueOrDefault("SignalStrength")))
                {
                    var isLong = signalResult.Signal.Contains("BUY");
                    // Ingresso alla chiusura della candela del segnale (come nel backtest), non al prezzo live:
                    // lo stop della strategia è calcolato su quella candela e i controlli stop/target partono dalla successiva.
                    var entryPrice = candles.Last().Close;
                    var positionResult = _riskManager.CalculatePosition(
                        crypto.Symbol,
                        entryPrice,
                        CalculateVolatility(candles),
                        isLong,
                        signalResult.Indicators.GetValueOrDefault("StopLoss")
                    );

                    if (!positionResult.IsValid)
                    {
                        signalsFiltered++;
                        CountRejection(RejectionLabel(positionResult.Rejection));
                        continue;
                    }

                    signalResult.Indicators["StopLoss"] = positionResult.StopLossPrice;
                    signalResult.Indicators["Target"] = positionResult.TargetPrice;
                    signalResult.Indicators["PositionSize"] = positionResult.PositionSize;
                    signalResult.Indicators["RiskRewardRatio"] = positionResult.RiskRewardRatio;
                    signalResult.Indicators["ExpectedProfit"] = positionResult.ExpectedProfit;

                    await _notificationService.SendNotificationAsync(signalResult);
                    openTrades.Add(_reportingService.RecordTrade(
                        crypto.Symbol,
                        entryPrice,
                        RelativeStrengthBreakoutStrategy.Name,
                        isLong,
                        positionResult.StopLossPrice,
                        positionResult.TargetPrice,
                        positionResult.PositionSize,
                        lastClosedTime!.Value));

                    _signalsGenerated++;
                    _tradesRecorded++;
                    signalsFound++;
                }
            }

            // Report del ciclo
            Console.WriteLine($"\n✅ Ciclo completato:");
            Console.WriteLine($"   • Trade chiusi (stop/target): {tradesClosed}");
            Console.WriteLine($"   • Segnali trovati: {signalsFound}");
            Console.WriteLine($"   • Segnali filtrati (rischio): {signalsFiltered}");
            Console.WriteLine($"   • Trade aperti: {openTrades.Count} (capitale impegnato €{openTrades.Sum(t => t.EntryPrice * t.PositionSize):F2})");
            Console.WriteLine($"   • P&L realizzato: €{_realizedPnl:F2}");

            if (rejectionCounts.Count > 0)
            {
                Console.WriteLine("\n📊 Motivi di scarto in questo ciclo:");
                foreach (var kv in rejectionCounts.OrderByDescending(k => k.Value))
                {
                    Console.WriteLine($"   • {kv.Key}: {kv.Value}");
                }
            }

            // Calcola e mostra metriche giornaliere ogni 10 cicli
            if (_checksPerformed % 10 == 0)
            {
                var metrics = _riskManager.CalculatePerformanceMetrics(managedTrades);
                if (metrics.TotalTrades > 0)
                {
                    Console.WriteLine($"\n📊 Metriche cumulative paper trading (dopo {_checksPerformed} cicli):");
                    Console.WriteLine($"   • Trade chiusi: {metrics.TotalTrades}");
                    Console.WriteLine($"   • Win Rate: {metrics.WinRate:P}");
                    Console.WriteLine($"   • Profit Factor: {metrics.ProfitFactor:F2}");
                    Console.WriteLine($"   • Total P&L (netto commissioni): €{metrics.TotalProfit:F2}");
                    Console.WriteLine($"   • Expectancy: €{metrics.Expectancy:F2}");
                }

                if (_cumulativeRejectionCounts.Count > 0)
                {
                    Console.WriteLine($"\n📊 Motivi di scarto cumulativi (dopo {_checksPerformed} cicli):");
                    foreach (var kv in _cumulativeRejectionCounts.OrderByDescending(k => k.Value))
                    {
                        Console.WriteLine($"   • {kv.Key}: {kv.Value}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Errore nel controllo del mercato: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _cycleRunning, 0);
        }
    }

    // Candele il cui periodo è già terminato (Time è l'inizio della candela): esclude quella ancora in formazione.
    private List<Candle> ClosedCandles(List<Candle> candles, DateTime now) =>
        candles.Where(c => c.Time + _timeframeSpan <= now).ToList();

    // Paper trading: stessa regola del backtest. Un trade si chiude quando una candela successiva a quella
    // del segnale tocca lo stop loss o il target; se li tocca entrambi si assume lo stop (ipotesi conservativa).
    private async Task<int> CloseTradesAtStopOrTargetAsync(Func<string, Task<List<Candle>>> getCandlesAsync)
    {
        int closed = 0;
        var openTrades = _reportingService.GetAllTrades().Where(t => t.Status == "Open" && t.IsPaperManaged).ToList();

        foreach (var trade in openTrades)
        {
            try
            {
                var candles = await getCandlesAsync(trade.Symbol);
                foreach (var candle in candles.Where(c => c.Time > trade.SignalCandleTime))
                {
                    bool hitStop = trade.IsLong ? candle.Low <= trade.StopLoss : candle.High >= trade.StopLoss;
                    bool hitTarget = trade.IsLong ? candle.High >= trade.TargetPrice : candle.Low <= trade.TargetPrice;
                    if (!hitStop && !hitTarget)
                        continue;

                    // Gap oltre lo stop: l'uscita avviene all'apertura della candela, non al prezzo di stop
                    var exitPrice = hitStop
                        ? (trade.IsLong ? Math.Min(candle.Open, trade.StopLoss) : Math.Max(candle.Open, trade.StopLoss))
                        : trade.TargetPrice;
                    var profitCalc = _riskManager.CalculateProfitAndTaxes(
                        trade.EntryPrice, exitPrice, trade.PositionSize, trade.IsLong);
                    var profit = profitCalc.NetProfitBeforeTax; // tasse sul saldo netto del periodo, non per trade
                    var profitPercent = profit / (trade.EntryPrice * trade.PositionSize) * 100;

                    _reportingService.ClosePaperTrade(trade, exitPrice, profit, profitPercent, hitStop ? "Stop loss" : "Target");
                    Console.WriteLine($"   {(profit > 0 ? "✅" : "❌")} Chiuso {trade.Symbol} {(trade.IsLong ? "LONG" : "SHORT")} per {(hitStop ? "stop loss" : "target")}: €{profit:F2} ({profitPercent:F2}%)");
                    closed++;
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️  {trade.Symbol} (gestione trade aperto): {ex.Message}");
            }
        }

        return closed;
    }

    // Etichette leggibili per la diagnostica dei motivi di scarto.
    private static string RejectionLabel(RejectionReason reason) => reason switch
    {
        RejectionReason.InsufficientData => "Strategia: dati insufficienti per il calcolo",
        RejectionReason.TrendNotAligned => "Strategia: trend non rialzista (Close>EMA50, EMA20>EMA50)",
        RejectionReason.NoVolumeData => "Strategia: volume assente",
        RejectionReason.VolumeInsufficient => "Strategia: volume insufficiente",
        RejectionReason.CandleNotStrong => "Strategia: candela non abbastanza forte",
        RejectionReason.PriceVsEma10 => "Strategia: forza relativa non confermata vs BTC",
        RejectionReason.RsiExtreme => "Strategia: RSI ipercomprato",
        RejectionReason.NoBreakout => "Strategia: nessun breakout del massimo recente",
        RejectionReason.MaxConcurrentTrades => "Risk Manager: troppi trade aperti simultaneamente",
        RejectionReason.ProfitBelowCommissions => "Risk Manager: profitto non copre commissioni",
        RejectionReason.RiskRewardTooLow => "Risk Manager: rapporto rischio/rendimento insufficiente",
        RejectionReason.StopLossError => "Stop non valido o troppo largo",
        _ => "Scarto non classificato"
    };

    private decimal CalculateVolatility(List<Candle> candles)
    {
        if (candles.Count < 20)
            return 0.02m;

        var closes = candles.TakeLast(20).Select(c => c.Close).ToList();
        var returns = new List<decimal>();

        for (int i = 1; i < closes.Count; i++)
        {
            returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
        }

        var avg = returns.Average();
        var variance = returns.Sum(r => (r - avg) * (r - avg)) / returns.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        return Math.Max(0.01m, Math.Min(stdDev, 0.08m));
    }
}
