# nexus-watchdog-version: 2
#
# Сторож Nexus CLI: отдельный процесс рядом с редактором (запускает сервер CLI, один на сессию редактора).
# Переживает перезагрузки домена и зависания главного потока — то, чего не видит код внутри Unity.
#
# Диагноз. Пока пульс NexusTemp/cli/alive.json свежий — молчит. Пульс стоит дольше StallSeconds — пишет
# NexusTemp/cli/stall.json: сколько стоит, на какой фазе, отвечает ли окно Unity, на переднем ли она плане, окна
# процесса (модальное окно видно по заголовку) и ошибки из хвоста Editor.log. Пульс ожил — stall.json удаляется.
#
# Будильник (настройка Wake в UserSettings/Exerussus.Nexus/nexus-cli.json, читается на каждом зависании).
# Unity в фоне не продолжает работу после перезагрузки домена, пока окно не получит фокус. Будим, только если
# окно отвечает (поток не занят), Unity в фоне и нет диалога Windows:
#   soft  — окнам Unity сообщение «приложение активно» (WM_ACTIVATEAPP), фокус не меняется;
#   focus — если мягкого мало: Unity на передний план, пульс ожил — фокус возвращается окну, где работали.
#   off   — только диагноз.
# Каждая попытка — в NexusTemp/cli/wake.json (последние 20).
#
# Выходит сам: Unity закрыт, папка канала удалена (CLI выключен) или его сменил другой сторож.

param(
    [Parameter(Mandatory = $true)][int]$UnityPid,
    [Parameter(Mandatory = $true)][string]$Cli,
    [string]$Log = "",
    [string]$Config = "",
    [int]$StallSeconds = 10,
    [int]$WakeSeconds = 4
)

$Version = 2
$ErrorActionPreference = 'SilentlyContinue'

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class NexusWatchdogWin
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private const uint WM_ACTIVATEAPP = 0x001C;
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;

    private static List<IntPtr> Handles(int target)
    {
        var list = new List<IntPtr>();
        EnumProc callback = (hwnd, param) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == (uint)target && IsWindowVisible(hwnd)) list.Add(hwnd);
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString();
    }

    // видимые окна верхнего уровня процесса: класс \t включено(1/0) \t заголовок
    public static string[] Windows(int target)
    {
        var result = new List<string>();
        foreach (var hwnd in Handles(target))
        {
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            result.Add(ClassOf(hwnd) + "\t" + (IsWindowEnabled(hwnd) ? "1" : "0") + "\t" + title);
        }
        return result.ToArray();
    }

    // диалог Windows (#32770): модальное окно — будить нельзя, нужен человек
    public static bool HasDialog(int target)
    {
        foreach (var hwnd in Handles(target))
            if (ClassOf(hwnd) == "#32770") return true;
        return false;
    }

    public static bool Foreground(int target)
    {
        uint pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        return pid == (uint)target;
    }

    public static long ForegroundHandle() { return GetForegroundWindow().ToInt64(); }

    // главное окно редактора: первое включённое окно класса UnityContainerWndClass
    public static long MainWindow(int target)
    {
        foreach (var hwnd in Handles(target))
            if (ClassOf(hwnd) == "UnityContainerWndClass" && IsWindowEnabled(hwnd)) return hwnd.ToInt64();
        return 0;
    }

    public static bool Iconic(long hwnd) { return hwnd != 0 && IsIconic(new IntPtr(hwnd)); }

    // мягко: «приложение активно / неактивно» всем окнам процесса, без смены фокуса
    public static void ActivateApp(int target, bool active)
    {
        foreach (var hwnd in Handles(target))
            PostMessage(hwnd, WM_ACTIVATEAPP, new IntPtr(active ? 1 : 0), IntPtr.Zero);
    }

    // передний план без мыши и клавиатуры: на время привязать ввод к потоку текущего переднего окна
    public static bool Focus(long handle, bool restore)
    {
        var hwnd = new IntPtr(handle);
        if (handle == 0 || !IsWindow(hwnd)) return false;
        if (restore && IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        uint ignored;
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
        var self = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(self, fgThread, false);
        }
    }

    public static void Minimize(long handle) { if (handle != 0) ShowWindow(new IntPtr(handle), SW_MINIMIZE); }
}
"@

$utf8 = New-Object System.Text.UTF8Encoding $false
$alive = Join-Path $Cli 'alive.json'
$stall = Join-Path $Cli 'stall.json'
$self = Join-Path $Cli 'watchdog.json'
$wakeLog = Join-Path $Cli 'wake.json'
$domainFile = Join-Path $Cli 'domain.json'

function Write-Atomic([string]$path, [string]$text) {
    $tmp = $path + '.tmp'
    [System.IO.File]::WriteAllText($tmp, $text, $utf8)
    Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Read-Json([string]$path) {
    try { return [System.IO.File]::ReadAllText($path, $utf8) | ConvertFrom-Json } catch { return $null }
}

# хвост Editor.log: файл открыт Unity на запись — читаем с общим доступом, последние 128 КБ
function Read-LogTail([string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return @() }
    try {
        $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        try {
            $take = [Math]::Min($fs.Length, 131072)
            [void]$fs.Seek(-$take, [System.IO.SeekOrigin]::End)
            $buf = New-Object byte[] $take
            $read = $fs.Read($buf, 0, $take)
            return ($utf8.GetString($buf, 0, $read) -split "`r?`n")
        } finally { $fs.Dispose() }
    } catch { return @() }
}

function Cut([string]$s, [int]$n) { if ($s -and $s.Length -gt $n) { return $s.Substring(0, $n) + '…' } return $s }

function Wake-Mode {
    $mode = $null
    if ($Config) { $mode = (Read-Json $Config).Wake }
    if ($mode -in @('off', 'soft', 'focus')) { return $mode }
    return 'soft'
}

function Write-WakeEvent($event) {
    $list = @()
    $old = Read-Json $wakeLog
    if ($old) { $list = @($old) }
    $list += [pscustomobject]$event
    if ($list.Count -gt 20) { $list = $list[($list.Count - 20)..($list.Count - 1)] }
    Write-Atomic $wakeLog (ConvertTo-Json -InputObject @($list) -Depth 3 -Compress)
}

if (-not (Test-Path -LiteralPath $Cli)) { exit 0 }
Write-Atomic $self (@{ pid = $PID; unityPid = $UnityPid; version = $Version; started = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress)

$lastReport = [DateTime]::MinValue
$wake = $null                 # идущая попытка: step soft|focus, started, prevFg, main, wasIconic, phase
$cooldownUntil = [DateTime]::MinValue
$attempts = 0                 # попыток за текущее зависание
$freshSince = $null           # пульс ожил — когда (фокус возвращаем не сразу: пусть редактор доделает)

while ($true) {
    Start-Sleep -Milliseconds 500

    $unity = Get-Process -Id $UnityPid -ErrorAction SilentlyContinue
    if (-not $unity) { break }
    if (-not (Test-Path -LiteralPath $Cli)) { break }
    $owner = Read-Json $self
    if ($owner -and $owner.pid -ne $PID) { break }

    $age = 9999
    if (Test-Path -LiteralPath $alive) { $age = ((Get-Date) - (Get-Item -LiteralPath $alive).LastWriteTime).TotalSeconds }
    $now = Get-Date

    # ---------------------------------------------------------------- пульс свежий
    if ($age -lt $WakeSeconds) {
        if (Test-Path -LiteralPath $stall) { Remove-Item -LiteralPath $stall -Force }
        $attempts = 0
        if ($wake) {
            if (-not $freshSince) { $freshSince = $now }
            if (($now - $freshSince).TotalSeconds -ge 1.5) {
                $seconds = [Math]::Round(($freshSince - $wake.started).TotalSeconds, 1)
                if ($wake.step -eq 'soft') {
                    if (-not [NexusWatchdogWin]::Foreground($UnityPid)) { [NexusWatchdogWin]::ActivateApp($UnityPid, $false) }
                } elseif ($wake.step -eq 'focus') {
                    # фокус возвращаем, только если он всё ещё у Unity: человек мог переключиться сам
                    if ([NexusWatchdogWin]::Foreground($UnityPid)) {
                        if ($wake.wasIconic) { [NexusWatchdogWin]::Minimize($wake.main) }
                        [void][NexusWatchdogWin]::Focus($wake.prevFg, $false)
                    }
                }
                Write-WakeEvent @{ at = $now.ToUniversalTime().ToString('o'); step = $wake.step; ok = $true; seconds = $seconds; phase = $wake.phase }
                $wake = $null
                $freshSince = $null
            }
        }
        continue
    }
    $freshSince = $null

    # ---------------------------------------------------------------- пульс стоит
    $beat = Read-Json $alive
    $responding = $null
    try { $unity.Refresh(); $responding = $unity.Responding } catch { }
    $foreground = [NexusWatchdogWin]::Foreground($UnityPid)
    $dialog = [NexusWatchdogWin]::HasDialog($UnityPid)
    $mode = Wake-Mode

    if ($wake) {
        $elapsed = ($now - $wake.started).TotalSeconds
        if ($wake.step -eq 'soft' -and $elapsed -ge 4 -and $mode -eq 'focus' -and -not $foreground -and -not $dialog) {
            $wake.prevFg = [NexusWatchdogWin]::ForegroundHandle()
            $wake.main = [NexusWatchdogWin]::MainWindow($UnityPid)
            $wake.wasIconic = [NexusWatchdogWin]::Iconic($wake.main)
            [NexusWatchdogWin]::ActivateApp($UnityPid, $false)
            if ([NexusWatchdogWin]::Focus($wake.main, $true)) {
                Write-WakeEvent @{ at = $now.ToUniversalTime().ToString('o'); step = 'soft'; ok = $false; seconds = [Math]::Round($elapsed, 1); phase = $wake.phase }
                $wake.step = 'focus'
                $wake.started = $now
            }
        } elseif ($elapsed -ge 10 -or ($wake.step -eq 'soft' -and $elapsed -ge 4 -and $mode -ne 'focus')) {
            # не помогло: вернуть как было, отметить, подождать перед следующей попыткой
            if ($wake.step -eq 'soft') { [NexusWatchdogWin]::ActivateApp($UnityPid, $false) }
            elseif ([NexusWatchdogWin]::Foreground($UnityPid)) {
                if ($wake.wasIconic) { [NexusWatchdogWin]::Minimize($wake.main) }
                [void][NexusWatchdogWin]::Focus($wake.prevFg, $false)
            }
            Write-WakeEvent @{ at = $now.ToUniversalTime().ToString('o'); step = $wake.step; ok = $false; seconds = [Math]::Round($elapsed, 1); phase = $wake.phase }
            $wake = $null
            $cooldownUntil = $now.AddSeconds(15)
        }
    } elseif ($mode -ne 'off' -and $responding -eq $true -and -not $foreground -and -not $dialog -and $now -ge $cooldownUntil -and $attempts -lt 3) {
        # окно отвечает (поток свободен), Unity в фоне, диалога нет — редактор ждёт фокуса
        $attempts++
        $domain = Read-Json $domainFile
        $wake = @{ step = 'soft'; started = $now; prevFg = 0; main = 0; wasIconic = $false; phase = "$($domain.state) / $($beat.phase)" }
        [NexusWatchdogWin]::ActivateApp($UnityPid, $true)
    }

    # ---------------------------------------------------------------- отчёт (не чаще раза в 3 с)
    if ($age -lt $StallSeconds -or ($now - $lastReport).TotalSeconds -lt 3) { continue }
    $lastReport = $now

    $windows = @()
    foreach ($w in [NexusWatchdogWin]::Windows($UnityPid)) {
        $parts = $w -split "`t", 3
        $windows += [ordered]@{ class = $parts[0]; enabled = ($parts[1] -eq '1'); title = (Cut $parts[2] 200) }
    }

    $lines = Read-LogTail $Log
    $errors = @($lines | Where-Object { $_ -match '(?i)(error|exception|failed|assert)' } | Select-Object -Last 15 | ForEach-Object { Cut $_ 300 })
    $tail = @($lines | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 25 | ForEach-Object { Cut $_ 300 })

    $report = [ordered]@{
        seconds    = [int]$age
        at         = $now.ToUniversalTime().ToString('o')
        state      = $beat.state
        phase      = $beat.phase
        responding = $responding
        foreground = $foreground
        dialog     = $dialog
        wake       = $mode
        waking     = $(if ($wake) { $wake.step } else { $null })
        windows    = $windows
        errors     = $errors
        tail       = $tail
        log        = $Log
    }
    Write-Atomic $stall ($report | ConvertTo-Json -Depth 4 -Compress)
}

if ((Read-Json $self).pid -eq $PID) { Remove-Item -LiteralPath $self -Force }
