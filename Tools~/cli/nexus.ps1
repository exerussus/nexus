# Nexus CLI: команды в открытый редактор Unity с Nexus.
# nexus-cli-version: 4
#
#   nexus list                                         открытые проекты
#   nexus [--project <имя|путь>] [--json] [--timeout <с>] [--no-wait] [--force] <команда> [аргументы]
#
# Долгие команды (refresh, compile, play.start, tests.run, …) идут заданиями редактора: клиент ждёт их конца,
# в том числе через перезагрузку домена (файл экземпляра в это время помечен reloading). --no-wait — не ждать,
# только напечатать id задания (потом: nexus job.status <id>). --timeout — общий срок ожидания, с (он же предел
# срока задания команды). --force — пройти блокировку идущей задачи (вызов пометится в её отчёте).
# Без сети до редактора (агент в VM, WSL) — python3 <проект>/NexusTemp/nexus-fs: те же команды через файлы.
#
# Проект выбирается явно: --project (имя папки проекта или путь, можно часть пути), иначе — проект,
# в папке которого вы стоите. Если подходит несколько или ни одного — ошибка со списком.
# Коды выхода: код команды (0 — успех), 10 — нет открытого редактора, 11 — проект не однозначен,
# 12 — нет связи с редактором.

$ErrorActionPreference = 'Stop'

# UTF-8 для кириллицы; без консоли (только каналы) свойство может бросить — тогда как есть.
# Старое значение возвращается при выходе, чтобы не менять кодировку вызвавшей консоли.
$script:OldEncoding = $null
try { $script:OldEncoding = [Console]::OutputEncoding; [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
function Finish([int]$code) {
    if ($null -ne $script:OldEncoding) { try { [Console]::OutputEncoding = $script:OldEncoding } catch { } }
    exit $code
}

$InstancesDir = Join-Path $env:LOCALAPPDATA 'Exerussus\Nexus\instances'

function Write-Err([string]$text) { [Console]::Error.WriteLine($text) }

function Normalize([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return '' }
    return ($path -replace '\\', '/').TrimEnd('/').ToLowerInvariant()
}

# живые экземпляры: файл есть и процесс редактора жив (мёртвые файлы убираем)
function Get-Instances {
    $list = @()
    if (-not (Test-Path $InstancesDir)) { return $list }
    foreach ($file in Get-ChildItem -Path $InstancesDir -Filter '*.json' -File -ErrorAction SilentlyContinue) {
        try {
            $i = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch { continue }
        $alive = $false
        if ($i.pid) { $alive = $null -ne (Get-Process -Id $i.pid -ErrorAction SilentlyContinue) }
        if ($alive) { $list += $i } else { Remove-Item -LiteralPath $file.FullName -ErrorAction SilentlyContinue }
    }
    return $list
}

function Show-Instances($list) {
    if ($list.Count -eq 0) { Write-Err 'Нет открытых редакторов с Nexus CLI.'; return }
    foreach ($i in $list) {
        $state = if ($i.state -eq 'reloading') { 'перезагружается' } else { "порт $($i.port)" }
        Write-Output ('{0,-24} {1}  (Unity {2}, {3})' -f $i.project, $i.path, $i.unity, $state)
    }
}

function Resolve-Target($list, [string]$project) {
    if ($project) {
        $p = $project
        if (Test-Path -LiteralPath $p) { $p = (Resolve-Path -LiteralPath $p).Path }
        $np = Normalize $p
        $byName = @($list | Where-Object { $_.project -ieq $project })
        if ($byName.Count -eq 1) { return $byName[0] }
        $byPath = @($list | Where-Object { (Normalize $_.path) -eq $np })
        if ($byPath.Count -eq 1) { return $byPath[0] }
        $byPart = @($list | Where-Object { (Normalize $_.path).Contains($np) })
        if ($byPart.Count -eq 1) { return $byPart[0] }
        $found = @($byName + $byPart | Sort-Object -Property path -Unique)
        if ($found.Count -gt 1) { Write-Err "«$project» подходит к нескольким проектам:"; Show-Instances $found; Finish 11 }
        Write-Err "Нет открытого проекта «$project». Открыты:"; Show-Instances $list; Finish 10
    }

    # без --project: проект, внутри которого текущая папка (самый глубокий)
    $cwd = Normalize (Get-Location).ProviderPath
    $inside = @($list | Where-Object { $n = Normalize $_.path; $cwd -eq $n -or $cwd.StartsWith($n + '/') } |
        Sort-Object { $_.path.Length } -Descending)
    if ($inside.Count -ge 1) { return $inside[0] }

    if ($list.Count -eq 0) { Write-Err 'Нет открытых редакторов с Nexus CLI.'; Finish 10 }
    Write-Err 'Укажите проект: --project <имя|путь>. Открыты:'
    Show-Instances $list
    Finish 11
}

# $script:Sent — запрос записан в сокет: дальше повторять нельзя (команда могла выполниться)
function Send-Command($instance, [string]$command, [string[]]$arguments, [int]$timeout) {
    $script:Sent = $false
    $request = @{ token = $instance.token; command = $command; args = @($arguments); cwd = (Get-Location).ProviderPath; timeout = $timeout; force = $script:Force } |
        ConvertTo-Json -Compress -Depth 4
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', [int]$instance.port)
        $wait = if ($timeout -gt 0) { $timeout } else { 600 }
        $client.ReceiveTimeout = [Math]::Max(15, $wait + 15) * 1000
        $stream = $client.GetStream()
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($request + "`n")
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
        $script:Sent = $true
        $reader = New-Object System.IO.StreamReader($stream, (New-Object System.Text.UTF8Encoding($false)))
        $line = $reader.ReadLine()
        if (-not $line) { return $null }
        return $line | ConvertFrom-Json
    } finally {
        $client.Close()
    }
}

# ---------------------------------------------------------------- разбор аргументов клиента

$project = $null
$json = $false
$timeout = 600
$timeoutSet = $false
$noWait = $false
$script:Force = $false
$script:Sent = $false
$rest = New-Object System.Collections.Generic.List[string]
for ($k = 0; $k -lt $args.Count; $k++) {
    $a = [string]$args[$k]
    if ($rest.Count -eq 0 -and $a -eq '--project' -and $k + 1 -lt $args.Count) { $project = [string]$args[++$k]; continue }
    if ($rest.Count -eq 0 -and $a -eq '--json') { $json = $true; continue }
    if ($rest.Count -eq 0 -and $a -eq '--timeout' -and $k + 1 -lt $args.Count) { $timeout = [Math]::Max(1, [int]$args[++$k]); $timeoutSet = $true; continue }
    if ($rest.Count -eq 0 -and $a -eq '--force') { $script:Force = $true; continue }
    if ($rest.Count -eq 0 -and $a -eq '--no-wait') { $noWait = $true; continue }
    $rest.Add($a)
}

if ($rest.Count -eq 0) {
    Write-Output 'nexus list'
    Write-Output 'nexus [--project <имя|путь>] [--json] [--timeout <с>] [--no-wait] [--force] <команда> [аргументы]'
    Write-Output 'nexus --project <имя> help       — команды проекта'
    Finish 2
}

$instances = @(Get-Instances)
if ($rest[0] -eq 'list') {
    if ($json) { ConvertTo-Json -InputObject @($instances | Select-Object project, path, unity, port, pid) -Depth 3 } else { Show-Instances $instances }
    Finish 0
}

$target = Resolve-Target $instances $project
if (-not $project) { Write-Err "→ $($target.project)" }

$command = $rest[0]
$arguments = @()
if ($rest.Count -gt 1) { $arguments = $rest.GetRange(1, $rest.Count - 1).ToArray() }

# общий срок: и на перезагрузку редактора, и на ожидание задания
$deadline = (Get-Date).AddSeconds($timeout)
$script:Waiting = $false

# Один запрос с ожиданием занятого редактора: перезагрузка домена (reloading), отказ в связи, «занят», устаревший
# токен — ждём и перечитываем файл экземпляра. $readOnly — запрос можно повторить, даже если он ушёл без ответа.
function Invoke-Editor([string]$cmd, [string[]]$cmdArgs, [bool]$readOnly, [datetime]$until, [int]$serverWait) {
    while ($true) {
        $response = $null
        $script:Sent = $false
        if ($script:target.state -ne 'reloading' -and $script:target.port) {
            try { $response = Send-Command $script:target $cmd $cmdArgs $serverWait } catch { $response = $null }
            if ($null -eq $response -and $script:Sent -and -not $readOnly) {
                Write-Err "Нет ответа от редактора $($script:target.project): команда могла выполниться — не повторяю."
                Finish 12
            }
        }

        # 5 без задания на опросе — главный поток занят дольше ожидания сервера (долгий импорт, тест): ждать дальше
        $busy = ($null -eq $response) -or ($response.exitCode -eq 6) -or ($response.output -like 'Неверный токен*') -or
            ($readOnly -and $response.exitCode -eq 5 -and -not $response.job)
        if (-not $busy) { return $response }
        if ((Get-Date) -gt $until) { return $response }

        # сообщение — один раз за ожидание: не засорять вывод
        if (-not $script:Waiting) {
            if ($script:target.state -eq 'reloading') { Write-Err 'Редактор перезагружает домен — жду…' } else { Write-Err 'Редактор занят — жду…' }
            $script:Waiting = $true
        }
        Start-Sleep -Milliseconds 1000
        $fresh = @(Get-Instances | Where-Object { (Normalize $_.path) -eq (Normalize $script:target.path) })
        if ($fresh.Count -eq 1) { $script:target = $fresh[0] }
        elseif ($fresh.Count -eq 0) {
            # файл мог быть на миг недоступен (редактор его переписывает): закрылся — только если процесс умер
            if (-not ($script:target.pid -and (Get-Process -Id $script:target.pid -ErrorAction SilentlyContinue))) {
                Write-Err "Редактор $($script:target.project) закрылся."; Finish 10
            }
        }
    }
}

function Write-Response($r) {
    if ($json) {
        if ($r.result) { Write-Output $r.result } else { $r | ConvertTo-Json -Depth 6 -Compress }
    } elseif ($r.output) {
        Write-Output ($r.output.TrimEnd("`n"))
    }
}

$script:target = $target
$firstUntil = (Get-Date).AddSeconds([Math]::Min($timeout, 300))
# сервер получает срок, только если его задали: иначе у команды свой срок задания
$serverTimeout = if ($timeoutSet) { $timeout } else { 0 }
$response = Invoke-Editor $command $arguments $false $firstUntil $serverTimeout
if ($null -eq $response) { Write-Err "Нет связи с редактором $($script:target.project)."; Finish 12 }

if ($response.pending -and $response.job) {
    $job = [string]$response.job
    if (-not $json -and $response.output) { Write-Output ($response.output.TrimEnd("`n")) }
    if ($noWait) {
        Write-Output "задание $job запущено: nexus job.status $job"
        Finish 0
    }

    # ждём задание: каждый опрос короткий и безопасен для повтора
    $lastPhase = ''
    while ($true) {
        if ((Get-Date) -gt $deadline) { Write-Err "Задание $job ещё идёт (срок --timeout $timeout с): nexus job.status $job"; Finish 5 }
        Start-Sleep -Milliseconds 700
        $script:Waiting = $false
        $r = Invoke-Editor 'job.status' @($job) $true $deadline 30
        if ($null -eq $r) { continue }
        if ($r.pending) {
            $phase = ''
            try { if ($r.result) { $phase = [string](($r.result | ConvertFrom-Json).phase) } } catch { }
            if ($phase -and $phase -ne $lastPhase -and -not $json) { Write-Err "… $phase"; $lastPhase = $phase }
            continue
        }
        $response = $r
        break
    }
    Write-Response $response
    Finish ([int]$response.exitCode)
}

Write-Response $response
Finish ([int]$response.exitCode)
