# Esegue crea-tabelle-bot9.sql su (localdb)\MSSQLLocalDB e scrive l'esito in crea-tabelle-bot9-log.txt
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $dir 'crea-tabelle-bot9-log.txt'
"=== CREA TABELLE BOT 9 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Out-File $log -Encoding utf8
try {
    $sql = Get-Content (Join-Path $dir 'crea-tabelle-bot9.sql') -Raw -Encoding utf8
    $batches = [regex]::Split($sql, '(?m)^\s*GO\s*$') | Where-Object { $_.Trim() -ne '' }
    $conn = New-Object System.Data.SqlClient.SqlConnection 'Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=True'
    $conn.add_InfoMessage({ param($s, $e) $e.Message | Out-File $log -Append -Encoding utf8 })
    $conn.Open()
    foreach ($b in $batches) {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $b; [void]$cmd.ExecuteNonQuery()
    }
    foreach ($t in 'bot9_trades', 'bot9_signals', 'bot9_cycles', 'bot9_operazioni_chiuse') {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT c.name + ' ' + t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'bot9.$t') ORDER BY c.column_id"
        $r = $cmd.ExecuteReader(); "Colonne bot9.${t}:" | Out-File $log -Append -Encoding utf8
        while ($r.Read()) { "  " + $r.GetString(0) | Out-File $log -Append -Encoding utf8 }
        $r.Close()
    }
    $conn.Close()
    "OK" | Out-File $log -Append -Encoding utf8
} catch {
    "ERRORE: $($_.Exception.Message)" | Out-File $log -Append -Encoding utf8
}
