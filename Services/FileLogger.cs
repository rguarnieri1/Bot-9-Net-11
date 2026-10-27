using System.Text;

namespace BotCripto.Services;

/// <summary>
/// Duplica tutto l'output della console (Console.Out e Console.Error) in un file giornaliero
/// Logs/{prefisso}_yyyy-MM-dd.log, con l'orario UTC all'inizio di ogni riga.
/// Il file è scritto subito (AutoFlush), così resta leggibile anche se il bot si chiude all'improvviso,
/// e si può aprire mentre il bot è in esecuzione. I file più vecchi di RetentionDays vengono cancellati.
/// </summary>
public sealed class FileLogger : TextWriter
{
    private const int RetentionDays = 30;

    // Stato condiviso tra Out ed Error: un solo file, un solo lock
    private static readonly object FileLock = new();
    private static string _directory = "";
    private static string _prefix = "cycles";
    private static StreamWriter? _file;
    private static DateTime _fileDate;
    private static bool _atLineStart = true;

    private readonly TextWriter _console;

    private FileLogger(TextWriter console) => _console = console;

    public override Encoding Encoding => _console.Encoding;

    /// <summary>Attiva il log su file per Console.Out e Console.Error.</summary>
    public static void Install(string directory, string prefix = "cycles")
    {
        _directory = directory;
        _prefix = prefix;
        Directory.CreateDirectory(_directory);
        Console.SetOut(new FileLogger(Console.Out));
        Console.SetError(new FileLogger(Console.Error));
    }

    public override void Write(char value)
    {
        lock (FileLock)
        {
            _console.Write(value);
            WriteToFile(value);
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        lock (FileLock)
        {
            _console.Write(value);
            foreach (var c in value)
                WriteToFile(c);
        }
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void WriteLine(string? value) => Write((value ?? "") + Environment.NewLine);

    public override void Flush()
    {
        lock (FileLock)
        {
            _console.Flush();
            _file?.Flush();
        }
    }

    // Da chiamare con FileLock acquisito. Un errore sul file non deve mai fermare il bot.
    private static void WriteToFile(char c)
    {
        try
        {
            var writer = GetWriter();
            if (writer == null)
                return;

            if (_atLineStart && c != '\r' && c != '\n')
            {
                writer.Write($"{DateTime.UtcNow:HH:mm:ss} ");
                _atLineStart = false;
            }

            writer.Write(c);
            if (c == '\n')
                _atLineStart = true;
        }
        catch
        {
            // log su file non disponibile: la console continua a funzionare
        }
    }

    private static StreamWriter? GetWriter()
    {
        var today = DateTime.UtcNow.Date;
        if (_file != null && _fileDate == today)
            return _file;

        _file?.Dispose();
        _file = null;
        _fileDate = today;
        _atLineStart = true;

        var path = Path.Combine(_directory, $"{_prefix}_{today:yyyy-MM-dd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        DeleteOldLogs();
        return _file;
    }

    private static void DeleteOldLogs()
    {
        try
        {
            var limit = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(_directory, $"{_prefix}_*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                    File.Delete(file);
            }
        }
        catch
        {
            // pulizia non essenziale
        }
    }
}
