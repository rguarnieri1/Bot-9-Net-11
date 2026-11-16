using Newtonsoft.Json;
using BotCripto.Data;
using Microsoft.EntityFrameworkCore;
using BotCripto.Models;
using System.Text;

namespace BotCripto.Services;

public class ReportingService
{
    private readonly string _dataDirectory;
    private readonly string _tradesFile;
    private readonly bool _useDatabase;
    private List<Trade> _trades = new();

    public ReportingService()
    {
        _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        Directory.CreateDirectory(_dataDirectory);
        _tradesFile = Path.Combine(_dataDirectory, "trades.json");
        _useDatabase = BotDatabase.Initialize();
        LoadTrades();
    }

    // Registra un trade simulato (paper trading) con i livelli di uscita, così può essere chiuso su stop/target.
    public Trade RecordTrade(string symbol, decimal entryPrice, string strategy,
        bool isLong, decimal stopLoss, decimal targetPrice, decimal positionSize, DateTime signalCandleTime)
    {
        var trade = new Trade
        {
            Symbol = symbol,
            EntryPrice = entryPrice,
            OpenTime = DateTime.UtcNow,
            Strategy = strategy,
            Status = "Open",
            IsLong = isLong,
            StopLoss = stopLoss,
            TargetPrice = targetPrice,
            PositionSize = positionSize,
            SignalCandleTime = signalCandleTime
        };

        _trades.Add(trade);
        SaveTrades(trade, isNew: true);
        return trade;
    }

    // Chiude un trade simulato. profit è in € al netto delle commissioni (prima delle tasse).
    public void ClosePaperTrade(Trade trade, decimal exitPrice, decimal profit, decimal profitPercentage, string exitReason)
    {
        trade.ExitPrice = exitPrice;
        trade.CloseTime = DateTime.UtcNow;
        trade.Status = "Closed";
        trade.Profit = profit;
        trade.ProfitPercentage = profitPercentage;
        trade.ExitReason = exitReason;
        SaveTrades(trade);
        LogClosedTrade(trade);
    }

    public Task CloseTradeAsync(string symbol, decimal exitPrice)
    {
        var trade = _trades.FirstOrDefault(t => t.Symbol == symbol && t.Status == "Open");
        if (trade != null)
        {
            trade.ExitPrice = exitPrice;
            trade.CloseTime = DateTime.UtcNow;
            trade.Status = "Closed";
            trade.Profit = exitPrice - trade.EntryPrice;
            trade.ProfitPercentage = (trade.Profit.Value / trade.EntryPrice) * 100;
            SaveTrades(trade);
            LogClosedTrade(trade);
        }

        return Task.CompletedTask;
    }

    // Registra l'operazione chiusa nella tabella bot9.bot9_operazioni_chiuse (solo con il database attivo).
    // Il file Logs/Operazioni_chiuse.log non viene più scritto.
    private void LogClosedTrade(Trade trade)
    {
        if (_useDatabase)
        {
            try
            {
                using var db = BotDatabase.CreateContext();
                db.ClosedTrades.Add(ClosedTradeRecord.FromTrade(trade));
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Errore nel salvataggio dell'operazione chiusa {trade.Symbol} nel database: {ex.GetBaseException().Message}");
            }
        }
    }

    public void GenerateWeeklyReport()
    {
        var weekStartDate = DateTime.UtcNow.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
        var weekEndDate = weekStartDate.AddDays(7);

        // Solo i trade gestiti dal paper trading (quelli precedenti non hanno stop/target né P&L)
        var weekTrades = _trades.Where(t => t.IsPaperManaged &&
            t.OpenTime >= weekStartDate && t.OpenTime < weekEndDate).ToList();

        var closedTrades = weekTrades.Where(t => t.Status == "Closed").ToList();
        var openTrades = weekTrades.Where(t => t.Status == "Open").ToList();

        var totalProfit = closedTrades.Sum(t => t.Profit ?? 0);
        var totalProfitPercentage = closedTrades.Count > 0
            ? closedTrades.Average(t => t.ProfitPercentage ?? 0)
            : 0;

        var winningTrades = closedTrades.Count(t => t.Profit > 0);
        var winRate = closedTrades.Count > 0 ? (winningTrades / (decimal)closedTrades.Count) * 100 : 0;

        var report = new StringBuilder();
        report.AppendLine("\n" + new string('═', 70));
        report.AppendLine($"📈 REPORT SETTIMANALE - {weekStartDate:yyyy-MM-dd} a {weekEndDate:yyyy-MM-dd}");
        report.AppendLine(new string('═', 70));
        report.AppendLine($"\n📊 STATISTICHE GENERALI:");
        report.AppendLine($"   • Operazioni Totali: {weekTrades.Count}");
        report.AppendLine($"   • Operazioni Chiuse: {closedTrades.Count}");
        report.AppendLine($"   • Operazioni Aperte: {openTrades.Count}");

        report.AppendLine($"\n💰 PERFORMANCE:");
        report.AppendLine($"   • Profitto Totale (netto commissioni): €{totalProfit:F2}");
        report.AppendLine($"   • ROI Medio: {totalProfitPercentage:F2}%");
        report.AppendLine($"   • Win Rate: {winRate:F2}%");

        report.AppendLine($"\n🎯 OPERAZIONI CHIUSE:");
        if (closedTrades.Count > 0)
        {
            foreach (var trade in closedTrades.OrderByDescending(t => t.CloseTime))
            {
                var profitColor = trade.Profit >= 0 ? "✅" : "❌";
                report.AppendLine(
                    $"   {profitColor} {trade.Symbol}: Entry ${trade.EntryPrice:F2} → Exit ${trade.ExitPrice:F2} " +
                    $"({trade.ProfitPercentage:F2}%) | {trade.Strategy}");
            }
        }
        else
        {
            report.AppendLine("   Nessuna operazione chiusa questa settimana");
        }

        report.AppendLine($"\n📂 OPERAZIONI APERTE:");
        if (openTrades.Count > 0)
        {
            foreach (var trade in openTrades)
            {
                report.AppendLine($"   ⏳ {trade.Symbol}: Entry ${trade.EntryPrice:F2} | {trade.Strategy}");
            }
        }
        else
        {
            report.AppendLine("   Nessuna operazione aperta");
        }

        report.AppendLine("\n" + new string('═', 70) + "\n");

        var reportText = report.ToString();
        Console.WriteLine(reportText);

        SaveReportToFile(reportText, weekStartDate);
    }

    private void SaveReportToFile(string report, DateTime weekStart)
    {
        try
        {
            var reportsDirectory = Path.Combine(_dataDirectory, "Reports");
            Directory.CreateDirectory(reportsDirectory);

            var fileName = $"Weekly_Report_{weekStart:yyyy-MM-dd}.txt";
            var filePath = Path.Combine(reportsDirectory, fileName);

            File.WriteAllText(filePath, report);
            Console.WriteLine($"Report salvato: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nel salvataggio del report: {ex.Message}");
        }
    }

    // Con il database attivo le operazioni si leggono da SQL Server (bot9.bot9_trades); al primo avvio
    // la tabella vuota viene popolata con le operazioni già presenti in Data/trades.json.
    private void LoadTrades()
    {
        _trades = LoadTradesFromFile();
        if (!_useDatabase)
            return;

        try
        {
            using var db = BotDatabase.CreateContext();
            var dbTrades = db.Trades.AsNoTracking().OrderBy(t => t.OpenTime).ToList();
            if (dbTrades.Count == 0 && _trades.Count > 0)
            {
                db.Trades.AddRange(_trades);
                db.SaveChanges();
            }
            else
            {
                _trades = dbTrades;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Errore nel caricamento delle operazioni dal database, uso trades.json: {ex.GetBaseException().Message}");
        }
    }

    private List<Trade> LoadTradesFromFile()
    {
        try
        {
            if (File.Exists(_tradesFile))
            {
                var json = File.ReadAllText(_tradesFile);
                return JsonConvert.DeserializeObject<List<Trade>>(json) ?? new List<Trade>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nel caricamento delle operazioni: {ex.Message}");
        }
        return new List<Trade>();
    }

    // Salva un segnale nella tabella bot9.bot9_signals (solo con il database attivo)
    public void SaveSignal(SignalRecord signal)
    {
        if (!_useDatabase)
            return;

        try
        {
            using var db = BotDatabase.CreateContext();
            db.Signals.Add(signal);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Errore nel salvataggio del segnale {signal.Symbol} nel database: {ex.GetBaseException().Message}");
        }
    }

    // Salva un ciclo nella tabella bot9.bot9_cycles (solo con il database attivo)
    public void SaveCycle(CycleRecord cycle)
    {
        if (!_useDatabase)
            return;

        try
        {
            using var db = BotDatabase.CreateContext();
            db.Cycles.Add(cycle);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Errore nel salvataggio del ciclo #{cycle.CycleNumber} nel database: {ex.GetBaseException().Message}");
        }
    }

    // Salva il trade nel database (insert o update). Data/trades.json non viene più scritto:
    // senza database il trade resta solo in memoria fino alla chiusura del bot.
    private void SaveTrades(Trade changed, bool isNew = false)
    {
        if (!_useDatabase)
        {
            Console.WriteLine($"⚠️  Database non disponibile: l'operazione {changed.Symbol} non viene salvata");
            return;
        }

        try
        {
            using var db = BotDatabase.CreateContext();
            if (isNew || !db.Trades.Any(t => t.Id == changed.Id))
                db.Trades.Add(changed);
            else
                db.Trades.Update(changed);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Errore nel salvataggio dell'operazione {changed.Symbol} nel database: {ex.GetBaseException().Message}");
        }
    }

    public List<Trade> GetAllTrades() => _trades;
}
