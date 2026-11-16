# Importa nel database "bot" lo storico del Bot 9 gia presente su file:
#   Data\trades.json          -> bot9.bot9_trades
#   Logs\signals_*.log        -> bot9.bot9_signals
#   Logs\cycles_*.log         -> bot9.bot9_cycles
# Si puo rieseguire: le righe gia presenti (stesso Id / stesso simbolo e orario / stesso orario di inizio ciclo)
# non vengono duplicate. L'esito viene scritto in importa-storico-bot9-log.txt.
# Da eseguire a bot fermo.
param([string]$BinDir)

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $BinDir) { $BinDir = Join-Path $dir 'bin\Release\net11.0' }
$log = Join-Path $dir 'importa-storico-bot9-log.txt'
$inv = [Globalization.CultureInfo]::InvariantCulture
function Log($m) { $m | Out-File $log -Append -Encoding utf8; $m }
"=== IMPORTA STORICO BOT 9 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Out-File $log -Encoding utf8

function ToDate($v) {
    if ($null -eq $v) { return $null }
    if ($v -is [datetime]) { return $v.ToUniversalTime() }
    return [datetime]::Parse([string]$v, $inv, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}
function ToDec($s) { [decimal]::Parse(($s -replace ',', '.'), $inv) }
function TfSpan($tf) {
    if ($tf -match '^(\d+)([mhd])$') {
        $n = [int]$Matches[1]
        switch ($Matches[2]) { 'm' { return [TimeSpan]::FromMinutes($n) } 'h' { return [TimeSpan]::FromHours($n) } 'd' { return [TimeSpan]::FromDays($n) } }
    }
    return [TimeSpan]::FromDays(1)
}
function AddParam($cmd, $name, $type, $value) {
    $p = $cmd.Parameters.Add($name, $type)
    if ($type -eq [Data.SqlDbType]::Decimal) { $p.Precision = 38; $p.Scale = 18 }
    if ($null -eq $value) { $p.Value = [DBNull]::Value } else { $p.Value = $value }
}
function Exec($conn, $tx, $sql, $params) {
    $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx; $cmd.CommandText = $sql
    foreach ($p in $params) { AddParam $cmd $p[0] $p[1] $p[2] }
    return $cmd.ExecuteNonQuery()
}

$T = [Data.SqlDbType]
try {
    $conn = New-Object System.Data.SqlClient.SqlConnection 'Server=(localdb)\MSSQLLocalDB;Database=bot;Integrated Security=True'
    $conn.Open()
    $tx = $conn.BeginTransaction()

    # ---------- TRADES ----------
    $trades = @()
    $tradesFile = Join-Path $BinDir 'Data\trades.json'
    if (Test-Path $tradesFile) { $trades = @((Get-Content $tradesFile -Raw -Encoding utf8 | ConvertFrom-Json) | ForEach-Object { $_ }) }
    $n = 0
    foreach ($t in $trades) {
        $n += [Math]::Max(0, (Exec $conn $tx @"
IF NOT EXISTS (SELECT 1 FROM bot9.bot9_trades WHERE Id = @Id)
INSERT INTO bot9.bot9_trades (Id, Symbol, OpenTime, CloseTime, EntryPrice, ExitPrice, Strategy, Status, Profit, ProfitPercentage,
                              IsLong, StopLoss, TargetPrice, PositionSize, SignalCandleTime, ExitReason, Bot)
VALUES (@Id, @Symbol, @OpenTime, @CloseTime, @EntryPrice, @ExitPrice, @Strategy, @Status, @Profit, @ProfitPercentage,
        @IsLong, @StopLoss, @TargetPrice, @PositionSize, @SignalCandleTime, @ExitReason, N'Bot 9')
"@ @(
            @('@Id', $T::NVarChar, $t.Id), @('@Symbol', $T::NVarChar, $t.Symbol),
            @('@OpenTime', $T::DateTime2, (ToDate $t.OpenTime)), @('@CloseTime', $T::DateTime2, (ToDate $t.CloseTime)),
            @('@EntryPrice', $T::Decimal, $t.EntryPrice), @('@ExitPrice', $T::Decimal, $t.ExitPrice),
            @('@Strategy', $T::NVarChar, $t.Strategy), @('@Status', $T::NVarChar, $t.Status),
            @('@Profit', $T::Decimal, $t.Profit), @('@ProfitPercentage', $T::Decimal, $t.ProfitPercentage),
            @('@IsLong', $T::Bit, [bool]$t.IsLong), @('@StopLoss', $T::Decimal, $t.StopLoss),
            @('@TargetPrice', $T::Decimal, $t.TargetPrice), @('@PositionSize', $T::Decimal, $t.PositionSize),
            @('@SignalCandleTime', $T::DateTime2, (ToDate $t.SignalCandleTime)), @('@ExitReason', $T::NVarChar, $t.ExitReason))))
    }
    Log "Trades: $($trades.Count) nel file, $n inseriti"

    # ---------- SIGNALS ----------
    # Riga del log: "2026-10-20 00:03:40 UTC - [Strategia] PEPE: BUY @ `$0,00000415"
    # Il log contiene solo i segnali accettati (trade aperto); stop, target e quantita vengono dal trade corrispondente.
    $found = 0; $n = 0
    foreach ($f in Get-ChildItem (Join-Path $BinDir 'Logs') -Filter 'signals_*.log' -ErrorAction SilentlyContinue | Sort-Object Name) {
        foreach ($line in Get-Content $f.FullName -Encoding utf8) {
            if ($line -notmatch '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) UTC - \[(.+)\] (\S+): (.+) @ \$(.+)$') { continue }
            $found++
            $time = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $inv)
            $strategy = $Matches[2]; $symbol = $Matches[3]; $signal = $Matches[4]; $price = ToDec $Matches[5]
            $trade = $trades | Where-Object { $_.Symbol -eq $symbol -and [Math]::Abs(((ToDate $_.OpenTime) - $time).TotalSeconds) -le 120 } | Select-Object -First 1
            $candle = $time.Date.AddDays(-1); $stop = $null; $target = $null; $size = $null; $tradeId = $null
            if ($trade) {
                $price = $trade.EntryPrice; $stop = $trade.StopLoss; $target = $trade.TargetPrice; $size = $trade.PositionSize; $tradeId = $trade.Id
                if ($trade.SignalCandleTime) { $candle = ToDate $trade.SignalCandleTime }
            }
            $n += [Math]::Max(0, (Exec $conn $tx @"
IF NOT EXISTS (SELECT 1 FROM bot9.bot9_signals WHERE Symbol = @Symbol AND AnalysisTime >= @Time AND AnalysisTime < DATEADD(second, 1, @Time))
INSERT INTO bot9.bot9_signals (Symbol, Strategy, Signal, IsLong, Timeframe, SignalCandleTime, AnalysisTime, Price,
                               StopLoss, Accepted, Target, PositionSize, TradeId, Bot)
VALUES (@Symbol, @Strategy, @Signal, @IsLong, N'1d', @Candle, @Time, @Price, @Stop, 1, @Target, @Size, @TradeId, N'Bot 9')
"@ @(
                @('@Symbol', $T::NVarChar, $symbol), @('@Strategy', $T::NVarChar, $strategy), @('@Signal', $T::NVarChar, $signal),
                @('@IsLong', $T::Bit, ($signal -match 'BUY')), @('@Candle', $T::DateTime2, $candle), @('@Time', $T::DateTime2, $time),
                @('@Price', $T::Decimal, $price), @('@Stop', $T::Decimal, $stop), @('@Target', $T::Decimal, $target),
                @('@Size', $T::Decimal, $size), @('@TradeId', $T::NVarChar, $tradeId))))
        }
    }
    Log "Signals: $found nei log, $n inseriti"

    # ---------- CYCLES ----------
    $cycles = New-Object System.Collections.ArrayList
    $c = $null; $inRej = $false
    foreach ($f in Get-ChildItem (Join-Path $BinDir 'Logs') -Filter 'cycles_*.log' -ErrorAction SilentlyContinue | Sort-Object Name) {
        foreach ($raw in Get-Content $f.FullName -Encoding utf8) {
            if ($raw -notmatch '^(\d\d:\d\d:\d\d) (.*)$') { $inRej = $false; continue }
            $hms = $Matches[1]; $line = $Matches[2]

            # Intestazione troncata (righe di due istanze mescolate): l'orario di inizio si ricava dal prefisso della riga
            if ($line -notmatch 'Ciclo #(\d+) - (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) UTC' -and $line -match 'Ciclo #(\d+) - (\d{4}-\d\d-\d\d) ') {
                $line = "Ciclo #$($Matches[1]) - $($Matches[2]) $hms UTC"
            }
            if ($line -match 'Ciclo #(\d+) - (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) UTC') {
                $c = @{ Number = [int]$Matches[1]; Start = [datetime]::ParseExact($Matches[2], 'yyyy-MM-dd HH:mm:ss', $inv); End = $null
                        Timeframe = $null; NewCandle = $false; Candle = $null; Cryptos = $null
                        Closed = 0; Found = 0; Filtered = 0; Open = 0; Capital = [decimal]0; Pnl = [decimal]0
                        Rej = (New-Object System.Collections.ArrayList); Error = $null }
                [void]$cycles.Add($c); $inRej = $false; continue
            }
            if ($null -eq $c) { continue }

            if ($inRej) {
                if ($line -match '^\s+\S (.+): (\d+)$') {
                    [void]$c.Rej.Add('"' + $Matches[1].Replace('\', '\\').Replace('"', '\"') + '":' + $Matches[2]); continue
                }
                $inRej = $false
            }

            # Fine del ciclo = orario della riga, sulla data di inizio (o il giorno dopo se si e passata la mezzanotte)
            $at = $c.Start.Date + [TimeSpan]::Parse($hms)
            if ($at -lt $c.Start.AddSeconds(-1)) { $at = $at.AddDays(1) }

            if ($line -match 'Candela (\S+) chiusa alle (\d{4}-\d\d-\d\d \d\d:\d\d) UTC: ricerca segnali') {
                $c.Timeframe = $Matches[1]; $c.NewCandle = $true
                $c.Candle = [datetime]::ParseExact($Matches[2], 'yyyy-MM-dd HH:mm', $inv) - (TfSpan $Matches[1])
            }
            elseif ($line -match 'Nessuna candela (\S+) appena chiusa.*\((\d{4}-\d\d-\d\d \d\d:\d\d) UTC\)') {
                $c.Timeframe = $Matches[1]; $span = TfSpan $Matches[1]
                $c.Candle = [datetime]::ParseExact($Matches[2], 'yyyy-MM-dd HH:mm', $inv) - $span - $span
            }
            elseif ($line -match 'Analizzando (\d+) criptovalute') { $c.Cryptos = [int]$Matches[1] }
            elseif ($line -match 'Ciclo completato') { $c.End = $at }
            elseif ($line -match 'Trade chiusi \(stop/target\): (\d+)') { $c.Closed = [int]$Matches[1] }
            elseif ($line -match 'Segnali trovati: (\d+)') { $c.Found = [int]$Matches[1] }
            elseif ($line -match 'Segnali filtrati \(rischio\): (\d+)') { $c.Filtered = [int]$Matches[1] }
            elseif ($line -match 'Trade aperti: (\d+) \(capitale impegnato \D?(-?[\d.,]+)\)') { $c.Open = [int]$Matches[1]; $c.Capital = ToDec $Matches[2] }
            elseif ($line -match 'P&L realizzato: \D?(-?[\d.,]+)') { $c.Pnl = ToDec $Matches[1] }
            elseif ($line -match 'Motivi di scarto in questo ciclo') { $inRej = $true }
            elseif ($line -match 'Errore nel controllo del mercato: (.*)$') { $c.Error = $Matches[1]; $c.End = $at }
        }
        $inRej = $false
    }

    $n = 0
    foreach ($c in $cycles) {
        $rej = $null; if ($c.Rej.Count -gt 0) { $rej = '{' + ($c.Rej -join ',') + '}' }
        $n += [Math]::Max(0, (Exec $conn $tx @"
IF NOT EXISTS (SELECT 1 FROM bot9.bot9_cycles WHERE StartTime >= @Start AND StartTime < DATEADD(second, 1, @Start))
INSERT INTO bot9.bot9_cycles (CycleNumber, StartTime, EndTime, Timeframe, NewCandle, SignalCandleTime, CryptosAnalyzed,
                              TradesClosed, SignalsFound, SignalsFiltered, OpenTrades, CapitalCommitted, RealizedPnl, RejectionCounts, Error, Bot)
VALUES (@Number, @Start, @End, @Timeframe, @NewCandle, @Candle, @Cryptos,
        @Closed, @Found, @Filtered, @Open, @Capital, @Pnl, @Rej, @Error, N'Bot 9')
"@ @(
            @('@Number', $T::Int, $c.Number), @('@Start', $T::DateTime2, $c.Start), @('@End', $T::DateTime2, $c.End),
            @('@Timeframe', $T::NVarChar, $c.Timeframe), @('@NewCandle', $T::Bit, $c.NewCandle), @('@Candle', $T::DateTime2, $c.Candle),
            @('@Cryptos', $T::Int, $c.Cryptos), @('@Closed', $T::Int, $c.Closed),
            @('@Found', $T::Int, $c.Found), @('@Filtered', $T::Int, $c.Filtered), @('@Open', $T::Int, $c.Open),
            @('@Capital', $T::Decimal, $c.Capital), @('@Pnl', $T::Decimal, $c.Pnl), @('@Rej', $T::NVarChar, $rej), @('@Error', $T::NVarChar, $c.Error))))
    }
    Log "Cycles: $($cycles.Count) nei log, $n inseriti"

    $tx.Commit()
    $conn.Close()
    Log "OK"
} catch {
    if ($tx) { try { $tx.Rollback() } catch {} }
    Log "ERRORE: $($_.Exception.Message) (riga $($_.InvocationInfo.ScriptLineNumber))"
}
