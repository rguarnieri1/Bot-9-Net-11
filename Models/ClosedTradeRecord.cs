namespace BotCripto.Models;

// Un'operazione chiusa, con gli stessi dati del blocco "OPERAZIONE CHIUSA" scritto in Logs/Operazioni_chiuse.log.
// Salvata nella tabella bot9.bot9_operazioni_chiuse.
public class ClosedTradeRecord
{
    public long Id { get; set; }
    public string TradeId { get; set; } = "";   // bot9.bot9_trades.Id
    public string? Symbol { get; set; }
    public string? Strategy { get; set; }
    public bool IsLong { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TargetPrice { get; set; }
    public string? ExitReason { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal? Profit { get; set; }
    public decimal? ProfitPercentage { get; set; }
    public string? Status { get; set; }
    public DateTime OpenTime { get; set; }
    public DateTime? CloseTime { get; set; }

    public static ClosedTradeRecord FromTrade(Trade t) => new()
    {
        TradeId = t.Id,
        Symbol = t.Symbol,
        Strategy = t.Strategy,
        IsLong = t.IsLong,
        StopLoss = t.IsPaperManaged ? t.StopLoss : null,
        TargetPrice = t.IsPaperManaged ? t.TargetPrice : null,
        ExitReason = t.ExitReason,
        EntryPrice = t.EntryPrice,
        ExitPrice = t.ExitPrice,
        Profit = t.Profit,
        ProfitPercentage = t.ProfitPercentage,
        Status = t.Status,
        OpenTime = t.OpenTime,
        CloseTime = t.CloseTime
    };
}
