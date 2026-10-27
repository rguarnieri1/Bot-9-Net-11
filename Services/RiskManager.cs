using BotCripto.Models;

namespace BotCripto.Services;

public class RiskManager
{
    // Nessun importo totale investibile: il capitale disponibile è illimitato.
    // Ogni trade investe un importo fisso in € (positionValue) e non c'è limite al numero di trade aperti.
    private readonly decimal _positionValue;
    private readonly decimal _minRewardRatio;
    private readonly decimal _commissionsPercent;
    private readonly decimal _taxRate;

    public RiskManager(
        decimal positionValue,
        decimal rewardRiskRatio = 2.0m,
        decimal commissionsPercent = 0.10m,
        decimal taxRate = 0.26m)
    {
        if (positionValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(positionValue), "L'importo per trade deve essere maggiore di zero");

        _positionValue = positionValue;
        _minRewardRatio = rewardRiskRatio;
        _commissionsPercent = commissionsPercent;
        _taxRate = taxRate;
    }

    public PositionSizingResult CalculatePosition(
        string symbol,
        decimal entryPrice,
        decimal volatilityPercent,
        bool isLong = true,
        decimal strategyStopLoss = 0m)
    {
        var result = new PositionSizingResult
        {
            Symbol = symbol,
            EntryPrice = entryPrice,
            IsValid = false,
            Reason = ""
        };

        // 1️⃣ Stop loss: quello della strategia se fornito, altrimenti 1.5% fisso.
        // Per i long lo stop sta sotto l'entry, per gli short sopra.
        var stopLossPrice = strategyStopLoss > 0
            ? strategyStopLoss
            : (isLong ? entryPrice * (1 - 0.015m) : entryPrice * (1 + 0.015m));
        var riskPerUnit = isLong ? entryPrice - stopLossPrice : stopLossPrice - entryPrice;

        if (riskPerUnit <= 0)
        {
            result.Reason = "Stop loss calculation error";
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }

        var stopLossPercent = riskPerUnit / entryPrice;

        // 2️⃣ Position size (in unità dell'asset): importo fisso per trade in € convertito in unità
        var positionSize = _positionValue / entryPrice;

        // 3️⃣ Target a distanza rischio × R:R (2:1 di default), nella direzione del trade
        var profitPerUnit = riskPerUnit * _minRewardRatio;
        var targetPrice = isLong ? entryPrice + profitPerUnit : entryPrice - profitPerUnit;

        // 4️⃣ Verifica che il profitto atteso copra commissioni
        var expectedProfit = positionSize * profitPerUnit;
        var commissionsOnTrade = (entryPrice * positionSize * _commissionsPercent / 100) * 2; // Entry + Exit
        var netProfit = expectedProfit - commissionsOnTrade;

        if (netProfit <= 0)
        {
            result.Reason = "Expected profit doesn't cover commissions";
            result.Rejection = RejectionReason.ProfitBelowCommissions;
            return result;
        }

        // 7️⃣ Calcola rischio-reward effettivo
        var totalRisk = positionSize * riskPerUnit;
        var riskRewardRatio = netProfit / totalRisk;

        if (riskRewardRatio < 1.2m)
        {
            result.Reason = $"Risk-reward too low: {riskRewardRatio:F2} < 1.2 required";
            result.Rejection = RejectionReason.RiskRewardTooLow;
            return result;
        }

        // ✅ TRADE VALIDO
        result.IsValid = true;
        result.PositionSize = positionSize;
        result.StopLossPrice = stopLossPrice;
        result.StopLossPercent = stopLossPercent;
        result.TargetPrice = targetPrice;
        result.TargetPercent = profitPerUnit / entryPrice;
        result.RiskAmount = totalRisk;
        result.ExpectedProfit = netProfit;
        result.RiskRewardRatio = riskRewardRatio;
        result.Commissions = commissionsOnTrade;
        result.Reason = $"✅ Valid | R:R {riskRewardRatio:F2}";

        return result;
    }

    public ProfitCalculation CalculateProfitAndTaxes(
        decimal entryPrice,
        decimal exitPrice,
        decimal positionSize,
        bool isLong = true)
    {
        var result = new ProfitCalculation();

        // Profitto/Perdita lordo (per gli short si guadagna quando il prezzo scende)
        var priceChange = isLong ? exitPrice - entryPrice : entryPrice - exitPrice;
        var profitLoss = priceChange * positionSize;

        // Commissioni (entry + exit)
        var entryCommission = (entryPrice * positionSize * _commissionsPercent / 100);
        var exitCommission = (exitPrice * positionSize * _commissionsPercent / 100);
        var totalCommissions = entryCommission + exitCommission;

        // Profitto netto prima tasse
        var netProfitBeforeTax = profitLoss - totalCommissions;

        // Tasse (solo sui guadagni)
        decimal taxes = 0;
        if (netProfitBeforeTax > 0)
        {
            taxes = netProfitBeforeTax * _taxRate;
        }

        var finalProfit = netProfitBeforeTax - taxes;

        result.GrossProfit = profitLoss;
        result.Commissions = totalCommissions;
        result.NetProfitBeforeTax = netProfitBeforeTax;
        result.Taxes = taxes;
        result.NetProfit = finalProfit;
        result.ProfitPercent = (finalProfit / (entryPrice * positionSize)) * 100;

        return result;
    }

    public PerformanceMetrics CalculatePerformanceMetrics(
        List<Trade> trades,
        decimal? initialCapital = null)
    {
        var metrics = new PerformanceMetrics();

        if (trades.Count == 0)
            return metrics;

        var closedTrades = trades.Where(t => t.Status == "Closed").ToList();
        if (closedTrades.Count == 0)
            return metrics;

        // Win/Loss stats
        var winningTrades = closedTrades.Where(t => t.Profit > 0).ToList();
        var losingTrades = closedTrades.Where(t => t.Profit <= 0).ToList();

        metrics.TotalTrades = closedTrades.Count;
        metrics.WinningTrades = winningTrades.Count;
        metrics.LosingTrades = losingTrades.Count;
        metrics.WinRate = (decimal)winningTrades.Count / closedTrades.Count;

        // Profitto totale
        metrics.TotalProfit = closedTrades.Sum(t => t.Profit ?? 0);
        metrics.TotalCommissions = closedTrades.Sum(t =>
        {
            var commissions = (t.EntryPrice * t.PositionSize * _commissionsPercent / 100) * 2;
            return commissions;
        });

        // Average win/loss
        if (winningTrades.Count > 0)
            metrics.AverageWin = winningTrades.Average(t => t.Profit ?? 0);

        if (losingTrades.Count > 0)
            metrics.AverageLoss = losingTrades.Average(t => t.Profit ?? 0);

        // Profit factor
        var totalWins = winningTrades.Sum(t => t.Profit ?? 0);
        var totalLosses = Math.Abs(losingTrades.Sum(t => t.Profit ?? 0));

        if (totalLosses > 0)
            metrics.ProfitFactor = totalWins / totalLosses;
        else if (totalWins > 0)
            metrics.ProfitFactor = decimal.MaxValue;

        // ROI
        // TotalProfit è la somma di Trade.Profit: quando i trade sono chiusi via
        // CalculateProfitAndTaxes() è già al netto di commissioni e tasse, quindi qui
        // non va sottratto di nuovo TotalCommissions (che tra l'altro usa una formula
        // che non tiene conto della position size reale del trade).
        // Senza capitale iniziale (capitale illimitato) il ROI è calcolato sul totale investito nei trade chiusi.
        var capitalBase = initialCapital ?? closedTrades.Sum(t => t.EntryPrice * t.PositionSize);
        metrics.ROI = capitalBase > 0 ? (metrics.TotalProfit / capitalBase) * 100 : 0m;

        // Consecutive wins/losses
        int maxConsecutiveWins = 0;
        int maxConsecutiveLosses = 0;
        int currentWins = 0;
        int currentLosses = 0;

        foreach (var trade in closedTrades.OrderBy(t => t.CloseTime))
        {
            if (trade.Profit > 0)
            {
                currentWins++;
                currentLosses = 0;
                maxConsecutiveWins = Math.Max(maxConsecutiveWins, currentWins);
            }
            else
            {
                currentLosses++;
                currentWins = 0;
                maxConsecutiveLosses = Math.Max(maxConsecutiveLosses, currentLosses);
            }
        }

        metrics.MaxConsecutiveWins = maxConsecutiveWins;
        metrics.MaxConsecutiveLosses = maxConsecutiveLosses;

        // Expectancy (average profit per trade)
        metrics.Expectancy = metrics.TotalProfit / closedTrades.Count;

        return metrics;
    }
}

// Data classes
public class PositionSizingResult
{
    public RejectionReason Rejection { get; set; }
    public string Symbol { get; set; }
    public decimal EntryPrice { get; set; }
    public bool IsValid { get; set; }
    public string Reason { get; set; }
    public decimal PositionSize { get; set; }
    public decimal StopLossPrice { get; set; }
    public decimal StopLossPercent { get; set; }
    public decimal TargetPrice { get; set; }
    public decimal TargetPercent { get; set; }
    public decimal RiskAmount { get; set; }
    public decimal ExpectedProfit { get; set; }
    public decimal RiskRewardRatio { get; set; }
    public decimal Commissions { get; set; }
}

public class ProfitCalculation
{
    public decimal GrossProfit { get; set; }
    public decimal Commissions { get; set; }
    public decimal NetProfitBeforeTax { get; set; }
    public decimal Taxes { get; set; }
    public decimal NetProfit { get; set; }
    public decimal ProfitPercent { get; set; }
}

public class PerformanceMetrics
{
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal TotalCommissions { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal ROI { get; set; }
    public int MaxConsecutiveWins { get; set; }
    public int MaxConsecutiveLosses { get; set; }
    public decimal Expectancy { get; set; }
}
