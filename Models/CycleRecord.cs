namespace BotCripto.Models;

// Un ciclo di controllo del mercato (CheckMarketAsync), con gli stessi dati del report "Ciclo completato"
// scritto in Logs/cycles_yyyy-MM-dd.log. Salvato nella tabella bot9.bot9_cycles.
public class CycleRecord
{
    public long Id { get; set; }
    public int CycleNumber { get; set; }            // "Ciclo #N" (riparte da 1 a ogni avvio del bot)
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Timeframe { get; set; }
    public bool NewCandle { get; set; }             // true = prima ricerca segnali su questa candela chiusa
    public DateTime? SignalCandleTime { get; set; } // inizio dell'ultima candela chiusa
    public int? CryptosAnalyzed { get; set; }
    public int TradesClosed { get; set; }
    public int SignalsFound { get; set; }
    public int SignalsFiltered { get; set; }
    public int OpenTrades { get; set; }
    public decimal CapitalCommitted { get; set; }
    public decimal RealizedPnl { get; set; }
    public string? RejectionCounts { get; set; }    // motivi di scarto del ciclo in JSON
    public string? Error { get; set; }
}
