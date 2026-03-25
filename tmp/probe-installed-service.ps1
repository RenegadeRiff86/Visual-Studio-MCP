$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe'
$psi.Arguments = 'mcp-server'
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$p = New-Object System.Diagnostics.Process
$p.StartInfo = $psi
$null = $p.Start()
$sw = $p.StandardInput
$sr = $p.StandardOutput

function Read-McpResponse {
    param([System.IO.StreamReader]$Reader)

    $headers = @()
    while (($line = $Reader.ReadLine()) -ne '') {
        if ($null -eq $line) {
            throw 'EOF while reading headers'
        }

        $headers += $line
    }

    $lengthHeader = $headers | Where-Object { $_ -like 'Content-Length:*' } | Select-Object -First 1
    $lengthText = $lengthHeader -replace 'Content-Length:\s*', ''
    $length = [int]$lengthText
    $buffer = New-Object char[] $length
    $read = $Reader.ReadBlock($buffer, 0, $length)
    return -join $buffer[0..($read - 1)]
}

$init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"codex-test","version":"1.0"}}}'
$initBytes = [System.Text.Encoding]::UTF8.GetByteCount($init)
$sw.Write("Content-Length: $initBytes`r`n`r`n")
$sw.Write($init)
$sw.Flush()
$initResp = Read-McpResponse -Reader $sr

$call = '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"write_file","arguments":{"file":"C:\\Users\\elsto\\source\\repos\\vs-ide-bridge-major-cleanup\\tmp\\mcp-direct-test.txt","content":"one\ntwo\n","post_check":true}}}'
$callBytes = [System.Text.Encoding]::UTF8.GetByteCount($call)
$sw.Write("Content-Length: $callBytes`r`n`r`n")
$sw.Write($call)
$sw.Flush()
$callResp = Read-McpResponse -Reader $sr

$sw.Close()
if (-not $p.HasExited) {
    $p.Kill()
}

Write-Output '---INIT---'
Write-Output $initResp
Write-Output '---CALL---'
Write-Output $callResp
Write-Output '---STDERR---'
Write-Output ($p.StandardError.ReadToEnd())
