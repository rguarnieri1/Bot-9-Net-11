using BotCripto.Services;
using Newtonsoft.Json.Linq;

Console.WriteLine(@"
╔════════════════════════════════════════════════════════╗
║       🤖 BOT 1 - ERTF-Crypto - VERSIONE 1.1            ║
║                 Backtest + Live Trading                ║
╚════════════════════════════════════════════════════════╝
");

// Capitale, timeframe e R:R sono letti da appsettings.json (TradingSettings / MarketMonitoring)
var appSettings = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
var tradingSettings = appSettings["TradingSettings"] ?? throw new InvalidOperationException("appsettings.json: sezione TradingSettings mancante");
var marketSettings = appSettings["MarketMonitoring"] ?? throw new InvalidOperationException("appsettings.json: sezione MarketMonitoring mancante");
decimal InitialCapital = tradingSettings.Value<decimal?>("InitialCapital") ?? throw new InvalidOperationException("appsettings.json: InitialCapital mancante");
decimal CapitalPerTrade = tradingSettings.Value<decimal?>("CapitalPerTrade") ?? decimal.MaxValue;
decimal RewardRiskRatio =tradingSettings.Value<decimal?>("RewardRiskRatio") ?? throw new InvalidOperationException("appsettings.json: RewardRiskRatio mancante");
string Timeframe = marketSettings.Value<string>("TimeframeForCandlesticks") ?? throw new InvalidOperationException("appsettings.json: TimeframeForCandlesticks mancante");

// Check se è backtest mode
var cmdArgs = Environment.GetCommandLineArgs();
bool isBacktestMode = cmdArgs.Length > 1 && cmdArgs[1].ToLower() == "--backtest";
bool isRealBacktestMode = cmdArgs.Length > 1 && cmdArgs[1].ToLower() == "--backtest-real";

if (isBacktestMode)
{
    await RunBacktestAsync();
}
else if (isRealBacktestMode)
{
    // --backtest-real [giorni | yyyy-MM-dd yyyy-MM-dd] [--timeframe 1h|2h|4h|6h|12h|1d] [--capital €] [--symbols file] [--rr rapporto]
    // --rr: take profit come multiplo della distanza dello stop (default RewardRiskRatio)
    // --symbols: elenco di simboli da usare (uno per riga); se il file non esiste viene creato con
    //            l'elenco scaricato, così più backtest possono usare esattamente lo stesso paniere.
    var positional = new List<string>();
    var timeframe = Timeframe;
    var capital = InitialCapital;
    string? symbolsFile = null;
    var rewardRisk = RewardRiskRatio;
    for (int a = 2; a < cmdArgs.Length; a++)
    {
        if (cmdArgs[a] == "--timeframe" && a + 1 < cmdArgs.Length)
            timeframe = cmdArgs[++a];
        else if (cmdArgs[a] == "--capital" && a + 1 < cmdArgs.Length)
            capital = decimal.Parse(cmdArgs[++a], System.Globalization.CultureInfo.InvariantCulture);
        else if (cmdArgs[a] == "--symbols" && a + 1 < cmdArgs.Length)
            symbolsFile = cmdArgs[++a];
        else if (cmdArgs[a] == "--rr" && a + 1 < cmdArgs.Length)
            rewardRisk = decimal.Parse(cmdArgs[++a], System.Globalization.CultureInfo.InvariantCulture);
        else
            positional.Add(cmdArgs[a]);
    }

    var endDate = DateTime.UtcNow;
    var startDate = endDate.AddDays(-30);
    var dateStyle = System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal;
    if (positional.Count >= 2
        && DateTime.TryParseExact(positional[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, dateStyle, out var fromDate)
        && DateTime.TryParseExact(positional[1], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, dateStyle, out var toDate))
    {
        startDate = fromDate;
        endDate = toDate.AddDays(1).AddTicks(-1); // data finale inclusa
    }
    else if (positional.Count >= 1 && int.TryParse(positional[0], out var parsedDays))
    {
        startDate = endDate.AddDays(-parsedDays);
    }
    await RunRealBacktestAsync(startDate, endDate, timeframe, capital, symbolsFile, rewardRisk);
}
else
{
    await RunLiveAsync();
}

async Task RunLiveAsync()
{
    var scheduler = new BotSchedulerService(InitialCapital, Timeframe, RewardRiskRatio, CapitalPerTrade);

    Console.WriteLine("Strategia Principale Attiva:");
    Console.WriteLine("  ⭐ Breakout trading su forza relativa (vs BTC)");
    Console.WriteLine($"     • Configurazione: Breakout su forza relativa (long) su candele {Timeframe}");
    Console.WriteLine("     • Filtri: Volume, Candle Body, RSI, Breakout Confirmation");
    Console.WriteLine("     • Modalità: paper trading (segnali e trade simulati, nessun ordine reale)");
    Console.WriteLine("\nImpostazioni:");
    Console.WriteLine($"  • Capitale Iniziale: €{InitialCapital:F2}");
    Console.WriteLine("  • Intervallo Monitoraggio: 30 minuti");
    Console.WriteLine("  • Max Crypto: 500");
    Console.WriteLine($"  • Risk per Trade: 2% (€{InitialCapital * 0.02m:F2})");
    Console.WriteLine($"  • Importo massimo per trade: €{Math.Min(InitialCapital * 0.10m, CapitalPerTrade):F2}");
    Console.WriteLine($"  • Take profit: {RewardRiskRatio}x la distanza dello stop");
    Console.WriteLine("  • Report Settimanale: Lunedì 00:00");
    Console.WriteLine("  • Tracking Metriche: Ogni 10 cicli");

    try
    {
        await scheduler.StartAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore: {ex.Message}");
    }
    finally
    {
        scheduler.Stop();
    }
}

async Task RunRealBacktestAsync(DateTime startDate, DateTime endDate, string timeframe, decimal capital, string? symbolsFile, decimal rewardRisk)
{
    Console.WriteLine($"\n🔍 MODALITA' BACKTEST SU DATI STORICI REALI ATTIVATA ({startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}, {timeframe}, capitale €{capital:F2}, R:R {rewardRisk})\n");

    var backtestService = new BacktestService(capital, rewardRisk, CapitalPerTrade);

    try
    {
        List<string> symbols;
        if (symbolsFile != null && File.Exists(symbolsFile))
        {
            symbols = File.ReadAllLines(symbolsFile).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
            Console.WriteLine($"📄 Simboli letti da {symbolsFile}: {symbols.Count}");
        }
        else
        {
            var dataService = new CryptoDataService();
            var cryptos = await dataService.GetLargeCapCryptocurrenciesAsync();
            symbols = cryptos.Select(c => c.Symbol).ToList();
            if (symbolsFile != null)
            {
                File.WriteAllLines(symbolsFile, symbols);
                Console.WriteLine($"📄 Elenco simboli salvato in {symbolsFile}");
            }
        }

        var result = await backtestService.RunBacktestAsync(symbols, startDate, endDate, timeframe);
        backtestService.PrintBacktestReport(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore nel backtest: {ex.Message}\n{ex.StackTrace}");
    }
}

async Task RunBacktestAsync()
{
    Console.WriteLine("\n🔍 MODALITA' BACKTEST SINTETICO ATTIVATA\n");

    var backtest = new SyntheticBacktest(initialCapital: 100m);

    try
    {
        // Esegui backtest su 365 giorni (1 anno)
        // con 20 simboli, 55% win-rate atteso, 8 trade per simbolo
        var result = backtest.RunBacktest(
            numSymbols: 20,
            daysOfData: 365,
            expectedWinRate: 0.55m,
            tradesPerSymbol: 8
        );

        // Stampa report dettagliato
        backtest.PrintBacktestReport(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore nel backtest: {ex.Message}\n{ex.StackTrace}");
    }
}

