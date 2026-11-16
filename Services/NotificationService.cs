using BotCripto.Models;

namespace BotCripto.Services;

// Notifica a schermo dei segnali. I segnali vengono salvati nella tabella bot9.bot9_signals
// (ReportingService.SaveSignal), non più nel file Logs/signals_yyyy-MM-dd.log.
public class NotificationService
{
    public Task SendNotificationAsync(AnalysisResult result)
    {
        ConsoleNotification(result);

        return Task.CompletedTask;
    }

    private void ConsoleNotification(AnalysisResult result)
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.ForegroundColor = result.Signal.Contains("BUY") ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"📊 SEGNALE: {result.Signal}");
        Console.ResetColor();
        Console.WriteLine($"   Crypto: {result.Symbol}");
        Console.WriteLine($"   Prezzo: ${result.CurrentPrice:F8}");
        Console.WriteLine($"   Strategia: {result.StrategyName}");
        Console.WriteLine($"   Ora: {result.AnalysisTime:yyyy-MM-dd HH:mm:ss}");

        foreach (var indicator in result.Indicators)
        {
            Console.WriteLine($"   {indicator.Key}: {indicator.Value:F4}");
        }

        Console.WriteLine(new string('=', 60) + "\n");

        Console.Beep(800, 500);
    }
}
