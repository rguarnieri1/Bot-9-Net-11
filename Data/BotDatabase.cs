using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;

namespace BotCripto.Data;

// Legge la stringa di connessione ConnectionStrings.BotDb da appsettings.json (sovrascrivibile in
// appsettings.local.json) e all'avvio del bot prova ad aprire la connessione al database "bot",
// scrivendo a schermo l'esito: OK se la connessione è riuscita, KO con il motivo se non è riuscita.
public static class BotDatabase
{
    public static string? ConnectionString { get; private set; }
    public static bool IsAvailable { get; private set; }

    private static bool _initialized;

    // Chiamato all'avvio del bot (Program.cs): la connessione viene provata una sola volta.
    // Se il database non è raggiungibile il bot continua a funzionare.
    public static bool Initialize()
    {
        if (_initialized) return IsAvailable;
        _initialized = true;

        try
        {
            ConnectionString = ReadConnectionString();
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                Console.WriteLine("❌ Connessione al database: KO - stringa di connessione BotDb mancante o vuota");
                return IsAvailable = false;
            }

            using var conn = new SqlConnection(ConnectionString);
            conn.Open();
            Console.WriteLine($"✅ Connessione al database: OK ({conn.Database} su {conn.DataSource})");
            return IsAvailable = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Connessione al database: KO - {ex.GetBaseException().Message}");
            return IsAvailable = false;
        }
    }

    private static string? ReadConnectionString()
    {
        string? result = null;
        foreach (var file in new[] { "appsettings.json", "appsettings.local.json" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(path)) continue;
            var value = JObject.Parse(File.ReadAllText(path)).SelectToken("ConnectionStrings.BotDb")?.Value<string>();
            if (value != null) result = value;
        }
        return result;
    }
}
