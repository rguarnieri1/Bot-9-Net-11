using BotCripto.Models;

namespace BotCripto.Strategies;

// Breakout long su forza relativa (candele chiuse, timeframe configurato, tipicamente 1d):
//  1) trend rialzista: Close > EMA50 e EMA20 > EMA50 con EMA50 in salita
//  2) forza relativa: la linea RS (Close asset / Close benchmark) è sopra la sua media a RsSmaPeriod
//     e ha toccato un nuovo massimo a RsHighLookback candele nelle ultime RsRecentBars candele
//     (l'asset sovraperforma il benchmark, tipicamente BTC)
//  3) breakout: la candela rialzista chiude sopra il massimo delle precedenti BreakoutLookback candele,
//     con volume superiore alla media e RSI non ipercomprato
// Stop loss sotto il minimo del range di breakout (più un buffer ATR); il target lo calcola il RiskManager (R:R configurato).
public class RelativeStrengthBreakoutStrategy
{
    public const string Name = "Breakout Forza Relativa";

    private const int FastEma = 20;
    private const int SlowEma = 50;
    private const int BreakoutLookback = 20;     // massimo delle ultime N candele da superare
    private const int RsSmaPeriod = 20;
    private const int RsHighLookback = 60;       // la RS deve essere al massimo di questa finestra...
    private const int RsRecentBars = 5;          // ...in almeno una delle ultime candele
    private const int VolumePeriod = 20;
    private const decimal VolumeFactor = 1.2m;   // volume breakout >= 1.2 × media
    private const int AtrPeriod = 14;
    private const decimal AtrStopBuffer = 0.5m;  // buffer sotto il minimo del range, in ATR
    private const decimal RsiMax = 80m;
    private const decimal MaxStopPercent = 0.15m; // stop più largo del 15% = setup scartato
    public const int MinCandles = SlowEma + 20;

    public AnalysisResult Analyze(string symbol, List<Candle> candles, List<Candle>? benchmark = null)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = Name,
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < MinCandles || benchmark == null || benchmark.Count == 0)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var rs = BuildRelativeStrength(candles, benchmark);
        var closes = candles.Select(c => c.Close).ToList();
        var ema20 = TechnicalIndicators.CalculateEMA(closes, FastEma);
        var ema50 = TechnicalIndicators.CalculateEMA(closes, SlowEma);
        var rsi = TechnicalIndicators.CalculateRSI(closes);
        if (rs.Count < RsHighLookback + RsRecentBars || ema20.Count < 1 || ema50.Count < 6 || rsi.Count < 1)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var last = candles.Last();
        result.CurrentPrice = last.Close;

        // Le serie degli indicatori sono allineate alla fine
        var e20 = ema20[^1];
        var e50 = ema50[^1];
        var rsiNow = rsi[^1];
        result.Indicators["EMA20"] = e20;
        result.Indicators["EMA50"] = e50;
        result.Indicators["RSI"] = rsiNow;

        // 1) Trend di fondo rialzista
        if (!(last.Close > e50 && e20 > e50 && e50 > ema50[^6]))
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        // 2) Forza relativa: sopra la sua media e a nuovi massimi di recente
        var rsNow = rs[^1];
        var rsSma = rs.Skip(rs.Count - RsSmaPeriod).Average();
        bool rsAtHigh = false;
        for (int back = 0; back < RsRecentBars; back++)
        {
            var idx = rs.Count - 1 - back;
            var windowMax = rs.Skip(idx + 1 - RsHighLookback).Take(RsHighLookback).Max();
            if (rs[idx] >= windowMax) { rsAtHigh = true; break; }
        }
        result.Indicators["RS"] = rsNow;
        if (!(rsNow > rsSma && rsAtHigh))
        {
            result.Rejection = RejectionReason.PriceVsEma10;
            return result;
        }

        // 3) Breakout sul massimo delle precedenti BreakoutLookback candele (escluse l'ultima)
        var range = candles.Skip(candles.Count - 1 - BreakoutLookback).Take(BreakoutLookback).ToList();
        var rangeHigh = range.Max(c => c.High);
        var rangeLow = range.Min(c => c.Low);
        if (!(last.Close > rangeHigh && last.Close > last.Open))
        {
            result.Rejection = RejectionReason.NoBreakout;
            return result;
        }

        // Volume: se il dato manca la strategia non può confermare il breakout
        var volumes = candles.Skip(candles.Count - 1 - VolumePeriod).Take(VolumePeriod).Select(c => c.Volume).ToList();
        var avgVolume = volumes.Average();
        if (avgVolume <= 0)
        {
            result.Rejection = RejectionReason.NoVolumeData;
            return result;
        }
        if (last.Volume < avgVolume * VolumeFactor)
        {
            result.Rejection = RejectionReason.VolumeInsufficient;
            return result;
        }

        if (rsiNow > RsiMax)
        {
            result.Rejection = RejectionReason.RsiExtreme;
            return result;
        }

        // Stop sotto il minimo del range, con buffer ATR
        var atr = CalculateAtr(candles, AtrPeriod);
        var stop = rangeLow - atr * AtrStopBuffer;
        if (stop >= last.Close || (last.Close - stop) / last.Close > MaxStopPercent)
        {
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }

        result.IsSignal = true;
        result.Signal = "BUY";
        result.Indicators["StopLoss"] = stop;
        result.Indicators["ATR"] = atr;
        // Più forte = RS più sopra la sua media, breakout più ampio e volume più alto
        result.Indicators["SignalStrength"] =
            (rsNow / rsSma - 1m) * 100m
            + (last.Close - rangeHigh) / rangeHigh * 100m
            + last.Volume / avgVolume;
        return result;
    }

    // Linea RS = Close asset / Close benchmark, sulle sole candele con lo stesso Time
    private static List<decimal> BuildRelativeStrength(List<Candle> candles, List<Candle> benchmark)
    {
        var benchByTime = new Dictionary<DateTime, decimal>(benchmark.Count);
        foreach (var b in benchmark)
            if (b.Close > 0) benchByTime[b.Time] = b.Close;

        var rs = new List<decimal>(candles.Count);
        foreach (var c in candles)
            if (benchByTime.TryGetValue(c.Time, out var bc))
                rs.Add(c.Close / bc);
        return rs;
    }

    private static decimal CalculateAtr(List<Candle> candles, int period)
    {
        var trs = new List<decimal>();
        for (int i = candles.Count - period; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            trs.Add(Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose))));
        }
        return trs.Average();
    }
}
