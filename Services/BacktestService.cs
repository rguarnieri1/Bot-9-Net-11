using BotCripto.Models;
using BotCripto.Strategies;
using System.Text;

namespace BotCripto.Services;

public class BacktestService
{
    private const int WarmupCandles = 60; // candele minime richieste dalla strategia prima di poter generare segnali
    private const int MinWarmupDays = 20; // storico minimo scaricato prima di startDate per il calcolo degli indicatori
    private const int LookbackCandles = 300; // candele passate alla strategia a ogni passo (come nel download standard)

    public static TimeSpan TimeframeToTimeSpan(string timeframe) => timeframe switch
    {
        "1h" => TimeSpan.FromHours(1),
        "2h" => TimeSpan.FromHours(2),
        "6h" => TimeSpan.FromHours(6),
        "12h" => TimeSpan.FromHours(12),
        "1d" => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(4)
    };

    // Ultime LookbackCandles candele fino a idx incluso
    private static List<Candle> Window(List<Candle> candles, int idx)
    {
        var start = Math.Max(0, idx + 1 - LookbackCandles);
        return candles.GetRange(start, idx + 1 - start);
    }

    private const string BenchmarkSymbol = "BTC";

    // Indice dell'ultima candela del benchmark con Time <= time (le candele sono ordinate)
    private static int BenchmarkIndex(List<Candle> benchmark, DateTime time)
    {
        int lo = 0, hi = benchmark.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (benchmark[mid].Time <= time) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return Math.Max(found, 0);
    }

    private readonly CryptoDataService _dataService;
    private readonly RiskManager _riskManager;
    private readonly RelativeStrengthBreakoutStrategy _strategy;

    // Capitale investibile illimitato: ogni trade investe un importo fisso (capitalPerTrade), senza tetto totale
    public BacktestService(decimal rewardRiskRatio, decimal capitalPerTrade)
    {
        _dataService = new CryptoDataService();
        _strategy = new RelativeStrengthBreakoutStrategy();
        _riskManager = new RiskManager(
            positionValue: capitalPerTrade,
            rewardRiskRatio: rewardRiskRatio,
            commissionsPercent: 0.25m,
            taxRate: TaxRate
        );
    }

    // Imposta sulle plusvalenze crypto: si applica al saldo netto del periodo (le perdite compensano i guadagni)
    private const decimal TaxRate = 0.26m;

    // Esegue davvero la strategia Breakout su forza relativa + RiskManager candela per candela su dati storici reali
    // (Crypto.com/Bybit), aprendo e chiudendo posizioni quando stop loss o target vengono toccati.
    public async Task<BacktestResult> RunBacktestAsync(
        List<string> symbols,
        DateTime startDate,
        DateTime endDate,
        string timeframe = "4h")
    {
        Console.WriteLine($"\n🔍 INIZIO BACKTEST (dati storici reali)");
        Console.WriteLine($"📅 Periodo richiesto: {startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}");
        Console.WriteLine($"⏱️  Timeframe: {timeframe}");
        Console.WriteLine($"🪙 Simboli: {symbols.Count}\n");

        var backtestResult = new BacktestResult
        {
            StartDate = startDate,
            EndDate = endDate,
            Timeframe = timeframe,
            SymbolsAnalyzed = symbols.Count,
            Trades = new List<Trade>(),
            AnalysisResults = new List<AnalysisResult>()
        };

        int totalSignals = 0;
        int totalFiltered = 0;
        int candleSetsAnalyzed = 0;
        int closedAtEnd = 0;
        int maxConcurrent = 0;
        DateTime? earliestTestedCandle = null;
        DateTime? latestTestedCandle = null;

        // Riscaldamento: almeno WarmupCandles + 60 candele del timeframe (es. 1d → 120 giorni), minimo 20 giorni
        var warmup = TimeSpan.FromTicks(Math.Max(TimeSpan.FromDays(MinWarmupDays).Ticks, TimeframeToTimeSpan(timeframe).Ticks * (WarmupCandles + 60)));
        var historyFrom = startDate - warmup;

        // 1) Scarica tutte le candele prima di simulare: il portafoglio va simulato in ordine cronologico
        //    su tutti i simboli insieme, per sapere in ogni momento quanto capitale è impegnato.
        var candlesBySymbol = new Dictionary<string, List<Candle>>();
        var indexBySymbol = new Dictionary<string, Dictionary<DateTime, int>>();
        // Il benchmark (BTC) serve alla forza relativa anche se non è tra i simboli da testare
        var benchmarkCandles = (await _dataService.GetHistoricalCandlesAsync(BenchmarkSymbol, timeframe, historyFrom, endDate))
            .OrderBy(c => c.Time).ToList();
        foreach (var symbol in symbols)
        {
            try
            {
                var candles = await _dataService.GetHistoricalCandlesAsync(symbol, timeframe, historyFrom, endDate);
                if (candles.Count < WarmupCandles + 20)
                    continue; // troppo pochi dati per un warmup + una finestra di test minima

                candles = candles.OrderBy(c => c.Time).ToList();
                candlesBySymbol[symbol] = candles;
                indexBySymbol[symbol] = candles.Select((c, i) => (c.Time, i)).ToDictionary(x => x.Time, x => x.i);
                candleSetsAnalyzed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️  {symbol}: {ex.Message}");
            }
        }

        // 2) Simulazione di portafoglio con capitale illimitato: ogni segnale valido apre una posizione
        //    di importo fisso; il drawdown è misurato sulla curva del P&L cumulato (realizzato + non realizzato).
        var openPositions = new Dictionary<string, OpenPosition>();
        decimal realizedPnl = 0m;
        decimal peakPnl = 0m;
        decimal maxDrawdownAmount = 0m;
        decimal maxCommitted = 0m;

        void ClosePosition(string symbol, decimal exitPrice, DateTime time, bool isWin)
        {
            var pos = openPositions[symbol];
            var profitCalc = _riskManager.CalculateProfitAndTaxes(
                pos.Trade.EntryPrice, exitPrice, pos.Size, pos.IsLong);

            pos.Trade.ExitPrice = exitPrice;
            pos.Trade.CloseTime = time;
            pos.Trade.Status = "Closed";
            pos.Trade.Profit = profitCalc.NetProfitBeforeTax; // tasse calcolate sul saldo netto del periodo, non per trade
            pos.Trade.ProfitPercentage = profitCalc.ProfitPercent;

            backtestResult.Trades.Add(pos.Trade);
            realizedPnl += profitCalc.NetProfitBeforeTax;
            openPositions.Remove(symbol);
        }

        var timeline = candlesBySymbol.Values
            .SelectMany(c => c.Skip(WarmupCandles).Select(x => x.Time))
            .Where(t => t <= endDate)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        foreach (var time in timeline)
        {
            // a) Posizioni aperte: verifica se stop/target sono stati toccati in questa candela
            foreach (var symbol in openPositions.Keys.ToList())
            {
                if (!indexBySymbol[symbol].TryGetValue(time, out var idx))
                    continue;

                var pos = openPositions[symbol];
                var candle = candlesBySymbol[symbol][idx];
                bool hitStop = pos.IsLong ? candle.Low <= pos.StopLoss : candle.High >= pos.StopLoss;
                bool hitTarget = pos.IsLong ? candle.High >= pos.Target : candle.Low <= pos.Target;

                // Se in una stessa candela vengono toccati entrambi, assumiamo lo stop loss (ipotesi conservativa)
                if (hitStop || hitTarget)
                    ClosePosition(symbol, hitStop ? pos.StopLoss : pos.Target, time, !hitStop);
            }

            // b) Nuovi segnali (solo dentro la finestra richiesta), dal più forte al più debole:
            if (time >= startDate)
            {
                var candidates = new List<(string symbol, Candle candle, AnalysisResult analysis)>();
                foreach (var (symbol, candles) in candlesBySymbol)
                {
                    if (openPositions.ContainsKey(symbol) || !indexBySymbol[symbol].TryGetValue(time, out var idx) || idx < WarmupCandles)
                        continue;

                    var analysis = _strategy.Analyze(symbol, Window(candles, idx), Window(benchmarkCandles, BenchmarkIndex(benchmarkCandles, candles[idx].Time)));
                    if (analysis.IsSignal)
                        candidates.Add((symbol, candles[idx], analysis));
                }

                foreach (var (symbol, candle, analysis) in candidates.OrderByDescending(c => c.analysis.Indicators.GetValueOrDefault("SignalStrength")))
                {
                    totalSignals++;

                    var isLong = analysis.Signal.Contains("BUY");
                    var sizing = _riskManager.CalculatePosition(
                        symbol,
                        candle.Close,
                        0m,
                        isLong,
                        analysis.Indicators.GetValueOrDefault("StopLoss"));

                    if (!sizing.IsValid)
                    {
                        totalFiltered++;
                        continue;
                    }

                    if (earliestTestedCandle == null || time < earliestTestedCandle)
                        earliestTestedCandle = time;
                    if (latestTestedCandle == null || time > latestTestedCandle)
                        latestTestedCandle = time;

                    openPositions[symbol] = new OpenPosition
                    {
                        IsLong = isLong,
                        StopLoss = sizing.StopLossPrice,
                        Target = sizing.TargetPrice,
                        Size = sizing.PositionSize,
                        Trade = new Trade
                        {
                            Symbol = symbol,
                            OpenTime = time,
                            EntryPrice = candle.Close,
                            PositionSize = sizing.PositionSize,
                            Strategy = analysis.StrategyName,
                            Status = "Open"
                        }
                    };
                }

                maxConcurrent = Math.Max(maxConcurrent, openPositions.Count);
                maxCommitted = Math.Max(maxCommitted, openPositions.Values.Sum(p => p.Trade.EntryPrice * p.Size));
            }

            // c) Equity a valore di mercato (realizzato + non realizzato) per il drawdown massimo
            decimal unrealized = 0m;
            foreach (var (symbol, pos) in openPositions)
            {
                var candles = candlesBySymbol[symbol];
                var lastClose = candles.LastOrDefault(c => c.Time <= time)?.Close ?? pos.Trade.EntryPrice;
                unrealized += _riskManager.CalculateProfitAndTaxes(pos.Trade.EntryPrice, lastClose, pos.Size, pos.IsLong).NetProfitBeforeTax;
            }
            var markedPnl = realizedPnl + unrealized;
            peakPnl = Math.Max(peakPnl, markedPnl);
            maxDrawdownAmount = Math.Max(maxDrawdownAmount, peakPnl - markedPnl);
        }

        // Una posizione ancora aperta a fine periodo viene chiusa al prezzo dell'ultima candela
        // testata (mark-to-market), altrimenti le perdite/guadagni non realizzati sparirebbero dalle metriche.
        foreach (var symbol in openPositions.Keys.ToList())
        {
            var lastCandle = candlesBySymbol[symbol].Last(c => c.Time <= endDate);
            ClosePosition(symbol, lastCandle.Close, lastCandle.Time, false);
            closedAtEnd++;
        }

        backtestResult.Metrics = _riskManager.CalculatePerformanceMetrics(backtestResult.Trades);

        backtestResult.TotalSignalsGenerated = totalSignals;
        backtestResult.TotalSignalsFiltered = totalFiltered;
        backtestResult.CandleSetsAnalyzed = candleSetsAnalyzed;
        backtestResult.ActualStartDate = earliestTestedCandle;
        backtestResult.ActualEndDate = latestTestedCandle;
        backtestResult.MaxConcurrentPositions = maxConcurrent;
        backtestResult.MaxCapitalCommitted = maxCommitted;
        backtestResult.TotalCapitalInvested = backtestResult.Trades.Sum(t => t.EntryPrice * t.PositionSize);
        backtestResult.MaxDrawdownAmount = maxDrawdownAmount;

        Console.WriteLine($"\n✅ BACKTEST COMPLETATO ({backtestResult.Trades.Count} trade chiusi da {candleSetsAnalyzed} simboli con dati sufficienti, di cui {closedAtEnd} chiusi a fine periodo al prezzo di mercato)");

        return backtestResult;
    }

    public void PrintBacktestReport(BacktestResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine("\n" + new string('═', 80));
        sb.AppendLine("📊 BACKTEST REPORT - BOT CRIPTO V1.1 (dati storici reali)");
        sb.AppendLine(new string('═', 80));

        sb.AppendLine($"\n📅 PERIODO TEST:");
        sb.AppendLine($"   • Richiesto: {result.StartDate:yyyy-MM-dd} → {result.EndDate:yyyy-MM-dd}");
        if (result.ActualStartDate.HasValue && result.ActualEndDate.HasValue)
        {
            sb.AppendLine($"   • Trade effettivamente aperti tra: {result.ActualStartDate:yyyy-MM-dd HH:mm} → {result.ActualEndDate:yyyy-MM-dd HH:mm}");
        }
        sb.AppendLine($"   • Timeframe: {result.Timeframe}");

        sb.AppendLine($"\n📈 COPERTURA ANALISI:");
        sb.AppendLine($"   • Simboli richiesti: {result.SymbolsAnalyzed}");
        sb.AppendLine($"   • Simboli con dati storici sufficienti: {result.CandleSetsAnalyzed}");
        sb.AppendLine($"   • Segnali generati dalla strategia: {result.TotalSignalsGenerated}");
        sb.AppendLine($"   • Segnali scartati dal RiskManager: {result.TotalSignalsFiltered}");
        var totalCandidati = result.TotalSignalsGenerated;
        if (totalCandidati > 0)
            sb.AppendLine($"   • Tasso di scarto RiskManager: {(decimal)result.TotalSignalsFiltered / totalCandidati * 100:F1}%");
        sb.AppendLine($"   • Posizioni aperte contemporaneamente (max): {result.MaxConcurrentPositions}");

        var pnlBeforeTax = result.Metrics.TotalProfit;
        var taxes = pnlBeforeTax > 0 ? pnlBeforeTax * TaxRate : 0m;
        var netPnl = pnlBeforeTax - taxes;

        sb.AppendLine($"\n💼 ACCOUNT METRICS:");
        sb.AppendLine($"   • Capitale investibile: illimitato");
        sb.AppendLine($"   • Capitale massimo impegnato contemporaneamente: €{result.MaxCapitalCommitted:F2}");
        sb.AppendLine($"   • Totale investito (somma dei trade): €{result.TotalCapitalInvested:F2}");
        sb.AppendLine($"   • P&L al netto delle commissioni (prima delle tasse): €{pnlBeforeTax:F2}");
        sb.AppendLine($"   • Tasse ({TaxRate:P0} sul saldo netto positivo del periodo): €{-taxes:F2}");
        sb.AppendLine($"   • P&L netto totale: €{netPnl:F2}");
        if (result.MaxCapitalCommitted > 0)
            sb.AppendLine($"   • ROI netto sul capitale massimo impegnato: {netPnl / result.MaxCapitalCommitted * 100:F2}%");
        if (result.TotalCapitalInvested > 0)
            sb.AppendLine($"   • Rendimento netto sul totale investito: {netPnl / result.TotalCapitalInvested * 100:F2}%");
        sb.AppendLine($"   • Drawdown massimo del P&L (a valore di mercato): €{result.MaxDrawdownAmount:F2}");

        sb.AppendLine($"\n🎯 PERFORMANCE METRICS:");
        sb.AppendLine($"   • Trade chiusi: {result.Metrics.TotalTrades}");
        sb.AppendLine($"   • Vincenti: {result.Metrics.WinningTrades}");
        sb.AppendLine($"   • Perdenti: {result.Metrics.LosingTrades}");
        sb.AppendLine($"   • Win Rate: {result.Metrics.WinRate:P2}");
        sb.AppendLine($"   • Profit Factor: {result.Metrics.ProfitFactor:F2}");
        sb.AppendLine($"   • Expectancy: €{result.Metrics.Expectancy:F2}/trade (al netto delle commissioni, prima delle tasse)");

        sb.AppendLine($"\n💰 WIN/LOSS ANALYSIS (al netto delle commissioni, prima delle tasse):");
        sb.AppendLine($"   • Vincita media: €{result.Metrics.AverageWin:F2}");
        sb.AppendLine($"   • Perdita media: €{result.Metrics.AverageLoss:F2}");
        sb.AppendLine($"   • Serie massima di vincite consecutive: {result.Metrics.MaxConsecutiveWins}");
        sb.AppendLine($"   • Serie massima di perdite consecutive: {result.Metrics.MaxConsecutiveLosses}");

        var byMonth = result.Trades
            .Where(t => t.CloseTime.HasValue)
            .GroupBy(t => new DateTime(t.CloseTime!.Value.Year, t.CloseTime.Value.Month, 1))
            .OrderBy(g => g.Key)
            .ToList();
        if (byMonth.Count > 1)
        {
            sb.AppendLine($"\n📆 DETTAGLIO MENSILE (per data di chiusura, prima delle tasse):");
            foreach (var g in byMonth)
            {
                var wins = g.Count(t => t.Profit > 0);
                sb.AppendLine($"   • {g.Key:yyyy-MM}: {g.Count(),4} trade | win rate {(decimal)wins / g.Count():P1} | P&L €{g.Sum(t => t.Profit ?? 0):F2}");
            }
        }

        sb.AppendLine("\n" + new string('═', 80) + "\n");

        var report = sb.ToString();
        Console.WriteLine(report);

        SaveBacktestReport(report);
    }

    private void SaveBacktestReport(string report)
    {
        try
        {
            var reportsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Reports");
            Directory.CreateDirectory(reportsDir);

            var fileName = $"Backtest_Report_{DateTime.UtcNow:yyyy-MM-dd_HHmmss}.txt";
            var filePath = Path.Combine(reportsDir, fileName);

            File.WriteAllText(filePath, report);
            Console.WriteLine($"💾 Report salvato: {filePath}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Errore nel salvataggio report: {ex.Message}");
        }
    }
}

internal class OpenPosition
{
    public bool IsLong { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Target { get; set; }
    public decimal Size { get; set; }
    public Trade Trade { get; set; } = new();
}

public class BacktestResult
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime? ActualStartDate { get; set; }
    public DateTime? ActualEndDate { get; set; }
    public string? Timeframe { get; set; }
    public int SymbolsAnalyzed { get; set; }
    public int CandleSetsAnalyzed { get; set; }
    public int TotalSignalsGenerated { get; set; }
    public int TotalSignalsFiltered { get; set; }
    public int MaxConcurrentPositions { get; set; }
    public decimal MaxCapitalCommitted { get; set; }
    public decimal TotalCapitalInvested { get; set; }
    public decimal MaxDrawdownAmount { get; set; }
    public List<Trade> Trades { get; set; } = new();
    public List<AnalysisResult> AnalysisResults { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
}
