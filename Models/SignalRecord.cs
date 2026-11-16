namespace BotCripto.Models;

// Segnale valido della strategia (AnalysisResult con IsSignal = true) calcolato sulle candele di crypto.com,
// con gli indicatori e l'esito del Risk Manager. Salvato nella tabella bot9.bot9_signals.
public class SignalRecord
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";
    public string? Strategy { get; set; }
    public string? Signal { get; set; }
    public bool IsLong { get; set; }
    public string? Timeframe { get; set; }
    public DateTime SignalCandleTime { get; set; }
    public DateTime AnalysisTime { get; set; }
    public decimal Price { get; set; }

    // Indicatori della strategia (RelativeStrengthBreakoutStrategy)
    public decimal? EMA20 { get; set; }
    public decimal? EMA50 { get; set; }
    public decimal? RSI { get; set; }
    public decimal? RS { get; set; }   // forza relativa rispetto al benchmark BTC
    public decimal? ATR { get; set; }
    public decimal? SignalStrength { get; set; }
    public decimal? StopLoss { get; set; }

    // Esito del Risk Manager
    public bool Accepted { get; set; }
    public string? RejectionReason { get; set; }
    public decimal? Target { get; set; }
    public decimal? PositionSize { get; set; }
    public decimal? RiskRewardRatio { get; set; }
    public decimal? ExpectedProfit { get; set; }
    public string? TradeId { get; set; }

    // Crea il record dal risultato dell'analisi, copiando gli indicatori presenti
    public static SignalRecord FromAnalysis(AnalysisResult a, string timeframe, DateTime signalCandleTime, decimal price, bool isLong)
    {
        decimal? I(string key) => a.Indicators.TryGetValue(key, out var v) ? v : null;
        return new SignalRecord
        {
            Symbol = a.Symbol,
            Strategy = a.StrategyName,
            Signal = a.Signal,
            IsLong = isLong,
            Timeframe = timeframe,
            SignalCandleTime = signalCandleTime,
            AnalysisTime = a.AnalysisTime,
            Price = price,
            EMA20 = I("EMA20"),
            EMA50 = I("EMA50"),
            RSI = I("RSI"),
            RS = I("RS"),
            ATR = I("ATR"),
            SignalStrength = I("SignalStrength"),
            StopLoss = I("StopLoss")
        };
    }
}
