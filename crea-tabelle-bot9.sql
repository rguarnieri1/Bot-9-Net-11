-- Tabelle del Bot 9 nel database "bot" (schema bot9), corrispondenti ai file di log/dati del bot:
--   bot9.bot9_trades            -> Data/trades.json (classe Trade in Models/Cryptocurrency.cs)
--   bot9.bot9_signals           -> Logs/signals_yyyy-MM-dd.log (segnali della strategia Breakout su forza relativa)
--   bot9.bot9_cycles            -> Logs/cycles_yyyy-MM-dd.log (report "Ciclo completato")
--   bot9.bot9_operazioni_chiuse -> Logs/Operazioni_chiuse.log (un blocco "OPERAZIONE CHIUSA" per riga)
-- Stessa struttura delle tabelle bot8.bot8_* del Bot 8, con in piu la colonna RS (forza relativa vs BTC) nei segnali.
-- Lo script non modifica le tabelle già esistenti.
IF DB_ID(N'bot') IS NULL
    CREATE DATABASE [bot];
GO
USE [bot];
GO
IF SCHEMA_ID(N'bot9') IS NULL
    EXEC(N'CREATE SCHEMA [bot9]');
GO
IF OBJECT_ID(N'bot9.bot9_trades', N'U') IS NULL
BEGIN
    CREATE TABLE [bot9].[bot9_trades] (
        [Id]               nvarchar(36)    NOT NULL,
        [Symbol]           nvarchar(50)    NULL,
        [OpenTime]         datetime2       NOT NULL,
        [CloseTime]        datetime2       NULL,
        [EntryPrice]       decimal(38, 18) NOT NULL,
        [ExitPrice]        decimal(38, 18) NULL,
        [Strategy]         nvarchar(200)   NULL,
        [Status]           nvarchar(20)    NULL,
        [Profit]           decimal(38, 18) NULL,
        [ProfitPercentage] decimal(38, 18) NULL,
        [IsLong]           bit             NOT NULL,
        [StopLoss]         decimal(38, 18) NOT NULL,
        [TargetPrice]      decimal(38, 18) NOT NULL,
        [PositionSize]     decimal(38, 18) NOT NULL,
        [SignalCandleTime] datetime2       NULL,
        [ExitReason]       nvarchar(100)   NULL,
        [Bot]              nvarchar(50)    NOT NULL CONSTRAINT [DF_bot9_trades_Bot] DEFAULT (N'Bot 9'), -- bot che ha scritto la riga
        CONSTRAINT [PK_bot9_trades] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_bot9_trades_Symbol_Status] ON [bot9].[bot9_trades] ([Symbol], [Status]);
    PRINT 'Tabella bot9.bot9_trades creata';
END
ELSE
    PRINT 'Tabella bot9.bot9_trades gia esistente: nessuna modifica';
GO
IF OBJECT_ID(N'bot9.bot9_signals', N'U') IS NULL
BEGIN
    CREATE TABLE [bot9].[bot9_signals] (
        [Id]                 bigint IDENTITY(1,1) NOT NULL,
        [Symbol]             nvarchar(50)    NOT NULL,
        [Strategy]           nvarchar(200)   NULL,   -- StrategyName, es. Breakout su forza relativa
        [Signal]             nvarchar(200)   NULL,   -- testo del segnale, es. BUY
        [IsLong]             bit             NOT NULL,
        [Timeframe]          nvarchar(10)    NULL,   -- es. 1d
        [SignalCandleTime]   datetime2       NOT NULL, -- inizio della candela chiusa che ha generato il segnale (UTC)
        [AnalysisTime]       datetime2       NOT NULL, -- momento dell'analisi (UTC), la data/ora della riga di log
        [Price]              decimal(38, 18) NOT NULL, -- CurrentPrice = chiusura della candela del segnale
        -- Indicatori della strategia (RelativeStrengthBreakoutStrategy)
        [EMA20]              decimal(38, 18) NULL,
        [EMA50]              decimal(38, 18) NULL,
        [RSI]                decimal(38, 18) NULL,
        [RS]                 decimal(38, 18) NULL,   -- forza relativa rispetto al benchmark BTC
        [ATR]                decimal(38, 18) NULL,
        [SignalStrength]     decimal(38, 18) NULL,
        [StopLoss]           decimal(38, 18) NULL,
        -- Esito del Risk Manager
        [Accepted]           bit             NOT NULL, -- 1 = trade aperto, 0 = scartato dal Risk Manager
        [RejectionReason]    nvarchar(100)   NULL,
        [Target]             decimal(38, 18) NULL,
        [PositionSize]       decimal(38, 18) NULL,
        [RiskRewardRatio]    decimal(38, 18) NULL,
        [ExpectedProfit]     decimal(38, 18) NULL,
        [TradeId]            nvarchar(36)    NULL,   -- bot9.bot9_trades.Id del trade aperto dal segnale
        [Bot]                nvarchar(50)    NOT NULL CONSTRAINT [DF_bot9_signals_Bot] DEFAULT (N'Bot 9'), -- bot che ha scritto la riga
        CONSTRAINT [PK_bot9_signals] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_bot9_signals_Symbol_SignalCandleTime] ON [bot9].[bot9_signals] ([Symbol], [SignalCandleTime]);
    PRINT 'Tabella bot9.bot9_signals creata';
END
ELSE
    PRINT 'Tabella bot9.bot9_signals gia esistente: nessuna modifica';
GO
IF OBJECT_ID(N'bot9.bot9_cycles', N'U') IS NULL
BEGIN
    CREATE TABLE [bot9].[bot9_cycles] (
        [Id]               bigint IDENTITY(1,1) NOT NULL,
        [CycleNumber]      int             NOT NULL, -- "Ciclo #N" (riparte da 1 a ogni avvio del bot)
        [StartTime]        datetime2       NOT NULL, -- inizio del ciclo (UTC)
        [EndTime]          datetime2       NULL,     -- fine del ciclo (UTC)
        [Timeframe]        nvarchar(10)    NULL,     -- es. 1d
        [NewCandle]        bit             NOT NULL, -- 1 = candela appena chiusa, ricerca segnali eseguita
        [SignalCandleTime] datetime2       NULL,     -- inizio dell'ultima candela chiusa (UTC)
        [CryptosAnalyzed]  int             NULL,     -- criptovalute analizzate (solo con ricerca segnali)
        [TradesClosed]     int             NOT NULL, -- trade chiusi su stop/target
        [SignalsFound]     int             NOT NULL, -- segnali trovati (trade aperti)
        [SignalsFiltered]  int             NOT NULL, -- segnali filtrati dal Risk Manager
        [OpenTrades]       int             NOT NULL, -- trade aperti a fine ciclo
        [CapitalCommitted] decimal(38, 18) NOT NULL, -- capitale impegnato nei trade aperti (EUR)
        [RealizedPnl]      decimal(38, 18) NOT NULL, -- P&L realizzato cumulativo (EUR)
        [RejectionCounts]  nvarchar(max)   NULL,     -- motivi di scarto del ciclo in JSON
        [Error]            nvarchar(max)   NULL,     -- messaggio se il ciclo e terminato con errore
        [Bot]              nvarchar(50)    NOT NULL CONSTRAINT [DF_bot9_cycles_Bot] DEFAULT (N'Bot 9'), -- bot che ha scritto la riga
        CONSTRAINT [PK_bot9_cycles] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_bot9_cycles_StartTime] ON [bot9].[bot9_cycles] ([StartTime]);
    PRINT 'Tabella bot9.bot9_cycles creata';
END
ELSE
    PRINT 'Tabella bot9.bot9_cycles gia esistente: nessuna modifica';
GO
IF OBJECT_ID(N'bot9.bot9_operazioni_chiuse', N'U') IS NULL
BEGIN
    CREATE TABLE [bot9].[bot9_operazioni_chiuse] (
        [Id]               bigint IDENTITY(1,1) NOT NULL,
        [TradeId]          nvarchar(36)    NOT NULL, -- "ID": bot9.bot9_trades.Id dell'operazione chiusa
        [Symbol]           nvarchar(50)    NULL,     -- "Simbolo"
        [Strategy]         nvarchar(200)   NULL,     -- "Strategia"
        [IsLong]           bit             NOT NULL, -- "Direzione": 1 = LONG, 0 = SHORT
        [StopLoss]         decimal(38, 18) NULL,     -- "Stop Loss"
        [TargetPrice]      decimal(38, 18) NULL,     -- "Target"
        [ExitReason]       nvarchar(100)   NULL,     -- "Motivo uscita", es. Stop loss / Target
        [EntryPrice]       decimal(38, 18) NOT NULL, -- "Prezzo Entrata"
        [ExitPrice]        decimal(38, 18) NULL,     -- "Prezzo Uscita"
        [Profit]           decimal(38, 18) NULL,     -- "Profitto" (EUR, netto commissioni)
        [ProfitPercentage] decimal(38, 18) NULL,     -- "Percentuale"
        [Status]           nvarchar(20)    NULL,     -- "Stato", es. Closed
        [OpenTime]         datetime2       NOT NULL, -- "Apertura" (UTC)
        [CloseTime]        datetime2       NULL,     -- "Chiusura" (UTC)
        [Bot]              nvarchar(50)    NOT NULL CONSTRAINT [DF_bot9_operazioni_chiuse_Bot] DEFAULT (N'Bot 9'), -- bot che ha scritto la riga
        CONSTRAINT [PK_bot9_operazioni_chiuse] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_bot9_operazioni_chiuse_TradeId] ON [bot9].[bot9_operazioni_chiuse] ([TradeId]);
    CREATE INDEX [IX_bot9_operazioni_chiuse_CloseTime] ON [bot9].[bot9_operazioni_chiuse] ([CloseTime]);
    PRINT 'Tabella bot9.bot9_operazioni_chiuse creata';
END
ELSE
    PRINT 'Tabella bot9.bot9_operazioni_chiuse gia esistente: nessuna modifica';
GO
