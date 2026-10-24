# Nexus CLI: команды в открытый редактор Unity с Nexus.
# nexus-cli-version: 2
#
#   nexus list                                         открытые проекты
#   nexus [--project <имя|путь>] [--json] [--timeout <с>] <команда> [аргументы]
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
    foreach ($i in $list) { Write-Output ('{0,-24} {1}  (Unity {2}, порт {3})' -f $i.project, $i.path, $i.unity, $i.port) }
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
    $request = @{ token = $instance.token; command = $command; args = @($arguments); cwd = (Get-Location).ProviderPath; timeout = $timeout } |
        ConvertTo-Json -Compress -Depth 4
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', [int]$instance.port)
        $client.ReceiveTimeout = [Math]::Max(15, $timeout + 15) * 1000
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
$script:Sent = $false
$rest = New-Object System.Collections.Generic.List[string]
for ($k = 0; $k -lt $args.Count; $k++) {
    $a = [string]$args[$k]
    if ($rest.Count -eq 0 -and $a -eq '--project' -and $k + 1 -lt $args.Count) { $project = [string]$args[++$k]; continue }
    if ($rest.Count -eq 0 -and $a -eq '--json') { $json = $true; continue }
    if ($rest.Count -eq 0 -and $a -eq '--timeout' -and $k + 1 -lt $args.Count) { $timeout = [Math]::Max(1, [int]$args[++$k]); continue }
    $rest.Add($a)
}

if ($rest.Count -eq 0) {
    Write-Output 'nexus list'
    Write-Output 'nexus [--project <имя|путь>] [--json] [--timeout <с>] <команда> [аргументы]'
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

# редактор мог перекомпилироваться: перечитать файл экземпляра и повторить (до ~60 с)
$response = $null
$deadline = (Get-Date).AddSeconds(60)
while ($true) {
    try {
        $response = Send-Command $target $command $arguments $timeout
    } catch {
        $response = $null
    }

    if ($null -eq $response -and $script:Sent) {
        Write-Err "Нет ответа от редактора $($target.project): команда могла выполниться — не повторяю."
        Finish 12
    }

    $busy = ($null -eq $response) -or ($response.exitCode -eq 6) -or ($response.output -like 'Неверный токен*')
    if (-not $busy -or (Get-Date) -gt $deadline) { break }

    Write-Err 'Редактор занят (перекомпиляция?) — жду…'
    Start-Sleep -Seconds 2
    $fresh = @(Get-Instances | Where-Object { (Normalize $_.path) -eq (Normalize $target.path) })
    if ($fresh.Count -eq 1) { $target = $fresh[0] }
}

if ($null -eq $response) { Write-Err "Нет связи с редактором $($target.project) (порт $($target.port))."; Finish 12 }

if ($json) {
    if ($response.result) { Write-Output $response.result } else { $response | ConvertTo-Json -Depth 6 -Compress }
} elseif ($response.output) {
    Write-Output ($response.output.TrimEnd("`n"))
}

Finish ([int]$response.exitCode)
