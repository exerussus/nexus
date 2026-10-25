# Nexus CLI: команды в редакторы Unity с Nexus через Nexus Daemon.
# nexus-cli-version: 5
#
#   nexus list                                         проекты, которые знает демон
#   nexus [--project <имя|путь>] [--json] [--timeout <с>] [--no-wait] [--force] <команда> [аргументы]
#   nexus result <id>                                  итог вызова, запущенного с --no-wait
#
# Клиент говорит только с демоном (HTTP на 127.0.0.1, токен из %LOCALAPPDATA%\Exerussus\Nexus\tokens.json).
# Демон принимает вызов сразу, даже пока редактор перезагружает домен или спит в фоне, сам ждёт задания
# (refresh, play.start, tests.run, …) и будит редактор. --timeout — общий срок, с (он же предел срока задания);
# --no-wait — только id вызова; --force — пройти блокировку идущей задачи.
# Без сети до localhost (агент в VM, WSL) — python3 <проект>/NexusTemp/nexus-fs: те же команды через файлы.
#
# Проект: --project (имя папки или путь), иначе — проект, в папке которого вы стоите, иначе — единственный.
# Коды выхода: код команды (0 — успех), 10 — нет проекта / редактор закрыт, 11 — проект не однозначен,
# 13 — демон не запущен.

$ErrorActionPreference = 'Stop'

$script:OldEncoding = $null
try { $script:OldEncoding = [Console]::OutputEncoding; [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
function Finish([int]$code) {
    if ($null -ne $script:OldEncoding) { try { [Console]::OutputEncoding = $script:OldEncoding } catch { } }
    exit $code
}

function Write-Err([string]$text) { [Console]::Error.WriteLine($text) }

$Root = Join-Path $env:LOCALAPPDATA 'Exerussus\Nexus'

function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json } catch { return $null }
}

$daemon = Read-JsonFile (Join-Path $Root 'daemon.json')
$tokens = Read-JsonFile (Join-Path $Root 'tokens.json')
$alive = $false
if ($daemon -and $daemon.pid) { $alive = $null -ne (Get-Process -Id $daemon.pid -ErrorAction SilentlyContinue) }
if (-not $alive -or -not $tokens) {
    Write-Err 'Nexus Daemon не запущен: откройте проект в Unity с включённым CLI (Exerussus/Nexus/Manage → CLI).'
    Finish 13
}
$Base = "http://127.0.0.1:$($daemon.port)"
$Token = [string]$tokens.local.token

# HTTP без исключений на 4xx/5xx: (код, объект ответа)
function Invoke-Api([string]$method, [string]$path, $body = $null, [int]$timeoutMs = 30000) {
    $req = [System.Net.HttpWebRequest]::Create($Base + $path)
    $req.Method = $method
    $req.Timeout = $timeoutMs
    $req.ReadWriteTimeout = $timeoutMs
    $req.Headers['Authorization'] = "Bearer $Token"
    $req.Proxy = $null
    $req.ServicePoint.Expect100Continue = $false
    if ($null -ne $body) {
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes(($body | ConvertTo-Json -Compress -Depth 6))
        $req.ContentType = 'application/json; charset=utf-8'
        $req.ContentLength = $bytes.Length
        $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
    }
    try { $resp = $req.GetResponse() } catch [System.Net.WebException] {
        if ($null -eq $_.Exception.Response) { Write-Err "Демон не отвечает: $($_.Exception.Message)"; Finish 13 }
        $resp = $_.Exception.Response
    }
    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), [System.Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd(); $reader.Close()
    $code = [int]$resp.StatusCode; $resp.Close()
    $obj = $null
    try { $obj = $text | ConvertFrom-Json } catch { }
    return @($code, $obj)
}

$project = $null; $json = $false; $timeout = 600; $timeoutSet = $false; $noWait = $false; $force = $false
$rest = New-Object System.Collections.Generic.List[string]
for ($k = 0; $k -lt $args.Count; $k++) {
    $a = [string]$args[$k]
    if ($rest.Count -eq 0 -and $a -eq '--project' -and $k + 1 -lt $args.Count) { $project = [string]$args[++$k]; continue }
    if ($rest.Count -eq 0 -and $a -eq '--json') { $json = $true; continue }
    if ($rest.Count -eq 0 -and $a -eq '--timeout' -and $k + 1 -lt $args.Count) { $timeout = [Math]::Max(1, [int]$args[++$k]); $timeoutSet = $true; continue }
    if ($rest.Count -eq 0 -and $a -eq '--force') { $force = $true; continue }
    if ($rest.Count -eq 0 -and $a -eq '--no-wait') { $noWait = $true; continue }
    $rest.Add($a)
}

if ($rest.Count -eq 0) {
    Write-Output 'nexus [--project <имя|путь>] [--json] [--timeout <с>] [--no-wait] [--force] <команда> [аргументы]'
    Write-Output 'nexus list — проекты; nexus help — команды проекта; nexus result <id> — итог вызова'
    Finish 2
}

$command = $rest[0]
$cmdArgs = @(); if ($rest.Count -gt 1) { $cmdArgs = $rest.GetRange(1, $rest.Count - 1).ToArray() }

if ($command -eq 'list') {
    $r = Invoke-Api 'GET' '/v1/projects'
    if ($json) { $r[1] | ConvertTo-Json -Depth 5; Finish 0 }
    $list = @($r[1].projects)
    if ($list.Count -eq 0) { Write-Err 'Демон не знает ни одного проекта с включённым CLI.'; Finish 10 }
    $names = @{ ready = 'готов'; reloading = 'перезагружается'; stalled = 'не отвечает'; closed = 'закрыт' }
    foreach ($p in $list) { Write-Output ('{0,-24} {1,-16} {2}  (Unity {3})' -f $p.id, $names[[string]$p.state], $p.path, $p.unity) }
    Finish 0
}

function Show-Result($c) {
    if ($json) { $c | ConvertTo-Json -Depth 8 -Compress; return }
    $out = [string]$c.output
    if ($out) { Write-Output $out.TrimEnd("`n") }
}

function Wait-Call([string]$id, [datetime]$deadline) {
    $last = $null
    while ($true) {
        $r = Invoke-Api 'GET' "/v1/calls/$id`?wait=20" $null 40000
        if ($r[0] -eq 404) { Write-Err "Нет вызова «$id» (итог хранится 2 минуты)."; Finish 1 }
        $c = $r[1]
        if ($c.state -eq 'done') { return $c }
        if ($c.phase -and $c.phase -ne $last) { Write-Err "… $($c.phase)"; $last = $c.phase }
        if ((Get-Date) -gt $deadline) { Write-Err "Вызов $id ещё идёт (срок --timeout $timeout с): nexus result $id"; Finish 5 }
    }
}

if ($command -eq 'result') {
    if ($cmdArgs.Count -lt 1) { Write-Err 'нужен id: nexus result <id>'; Finish 2 }
    $c = Wait-Call $cmdArgs[0] ((Get-Date).AddSeconds($timeout))
    Show-Result $c
    Finish ([int]$c.exitCode)
}

$body = @{ command = $command; args = $cmdArgs; cwd = (Get-Location).ProviderPath; force = $force; client = 'nexus' }
if ($project) { $body.project = $project }
if ($timeoutSet) { $body.timeout = $timeout }
$r = Invoke-Api 'POST' '/v1/calls' $body
if ($r[0] -eq 404) {
    $msg = [string]$r[1].error
    Write-Err $msg
    if ($msg -like 'Несколько проектов*') { Finish 11 }
    Finish 10
}
if ($r[0] -ge 400) { Write-Err "Демон: $($r[1].error)"; Finish 4 }
$call = $r[1]
if ($noWait) { Write-Output "вызов $($call.id) принят: nexus result $($call.id)"; Finish 0 }

$c = Wait-Call $call.id ((Get-Date).AddSeconds($timeout + 90))
Show-Result $c
Finish ([int]$c.exitCode)
