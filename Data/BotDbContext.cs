using BotCripto.Models;
using Microsoft.EntityFrameworkCore;

namespace BotCripto.Data;

// Contesto Entity Framework sul database SQL Server "bot" (autenticazione Windows).
// Le tabelle del Bot 9 stanno nello schema "bot9", così il database può essere condiviso con gli altri bot.
// Le tabelle si creano con crea-tabelle-bot9.sql.
public class BotDbContext : DbContext
{
    public const string Schema = "bot9";

    // Valore fisso della colonna Bot nelle tabelle bot9.bot9_trades, bot9.bot9_signals e bot9.bot9_cycles
    public const string BotName = "Bot 9";

    private const string BotColumn = "Bot";

    private readonly string _connectionString;

    public BotDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<SignalRecord> Signals => Set<SignalRecord>();
    public DbSet<CycleRecord> Cycles => Set<CycleRecord>();
    public DbSet<ClosedTradeRecord> ClosedTrades => Set<ClosedTradeRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(_connectionString, sql => sql.EnableRetryOnFailure(3));
    }

    // Prima di ogni salvataggio scrive "Bot 9" nella colonna Bot delle righe inserite o aggiornate
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        foreach (var entry in ChangeTracker.Entries())
            if (entry.State is EntityState.Added or EntityState.Modified
                && entry.Metadata.FindProperty(BotColumn) != null)
                entry.Property(BotColumn).CurrentValue = BotName;
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Trade>(e =>
        {
            e.ToTable("bot9_trades");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(36);
            e.Property(t => t.Symbol).HasMaxLength(50).IsRequired(false);
            e.Property(t => t.Strategy).HasMaxLength(200).IsRequired(false);
            e.Property(t => t.Status).HasMaxLength(20).IsRequired(false);
            e.Property(t => t.ExitReason).HasMaxLength(100);
            e.Ignore(t => t.IsPaperManaged);
            ConfigureCommon(e, typeof(Trade));
        });

        modelBuilder.Entity<SignalRecord>(e =>
        {
            e.ToTable("bot9_signals");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).UseIdentityColumn();
            e.Property(s => s.Symbol).HasMaxLength(50);
            e.Property(s => s.Strategy).HasMaxLength(200);
            e.Property(s => s.Signal).HasMaxLength(200);
            e.Property(s => s.Timeframe).HasMaxLength(10);
            e.Property(s => s.RejectionReason).HasMaxLength(100);
            e.Property(s => s.TradeId).HasMaxLength(36);
            ConfigureCommon(e, typeof(SignalRecord));
        });

        modelBuilder.Entity<CycleRecord>(e =>
        {
            e.ToTable("bot9_cycles");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).UseIdentityColumn();
            e.Property(c => c.Timeframe).HasMaxLength(10);
            ConfigureCommon(e, typeof(CycleRecord));
        });

        modelBuilder.Entity<ClosedTradeRecord>(e =>
        {
            e.ToTable("bot9_operazioni_chiuse");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).UseIdentityColumn();
            e.Property(c => c.TradeId).HasMaxLength(36);
            e.Property(c => c.Symbol).HasMaxLength(50);
            e.Property(c => c.Strategy).HasMaxLength(200);
            e.Property(c => c.ExitReason).HasMaxLength(100);
            e.Property(c => c.Status).HasMaxLength(20);
            ConfigureCommon(e, typeof(ClosedTradeRecord));
        });
    }

    // Colonna Bot e precisione dei decimali: prezzi e quantità delle crypto possono avere molte cifre (es. 0.00000123)
    private static void ConfigureCommon(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder e, Type type)
    {
        foreach (var p in type.GetProperties()
                     .Where(p => p.CanWrite && (p.PropertyType == typeof(decimal) || p.PropertyType == typeof(decimal?))))
            e.Property(p.Name).HasPrecision(38, 18);
        e.Property<string>(BotColumn).HasMaxLength(50).IsRequired();
    }
}
