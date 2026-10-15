# Optional: logs this PC's power use once a minute, so the widget can show what the PC cost today
# and this month, and power-report.py can break it down by rate. Turn it on with powerLog.enabled in
# settings.json, then run install.ps1 again (it adds this script to Startup).
#
# NVIDIA GPU power is measured with nvidia-smi when available. The CPU, the rest of the PC and the
# monitors are estimated from load and idle time: tune the powerLog numbers in settings.json for your
# PC (a plug-in power meter gives the real figures). It never keeps the PC awake.

. (Join-Path $PSScriptRoot 'settings.ps1')
$cfg = (Get-WidgetSettings $PSScriptRoot).powerLog
if (-not $cfg.enabled) { exit }

$mutex = New-Object System.Threading.Mutex($false, 'Local\CnflRateWidget.PowerLog')
if (-not $mutex.WaitOne(0)) { exit }   # already running

$SampleSec = 15   # 4 samples per logged minute
$dir = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Force $dir | Out-Null

Add-Type -Namespace PowerLog -Name Win -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
public static uint IdleSeconds() { var l = new LASTINPUTINFO(); l.cbSize = (uint)Marshal.SizeOf(l); GetLastInputInfo(ref l); return ((uint)Environment.TickCount - l.dwTime) / 1000; }
public static uint ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
'@

function Get-DisplayOffSec {
    $line = powercfg /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE | Select-String 'Current AC Power Setting Index'
    if ($line -match '0x([0-9a-fA-F]+)') { return [Convert]::ToInt32($Matches[1], 16) }
    return 0
}

$hasNvidia = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)
function Get-Gpu {
    if ($hasNvidia) {
        try {
            $v = (nvidia-smi --query-gpu=power.draw,utilization.gpu --format=csv,noheader,nounits | Select-Object -First 1) -split ','
            return @([double]$v[0], [double]$v[1])
        } catch {}
    }
    return @([double]$cfg.gpuWattsWithoutNvidia, 0.0)
}

$cores = [Environment]::ProcessorCount
$displayOff = Get-DisplayOffSec
$prevProcs = @{}; Get-Process | ForEach-Object { if ($_.CPU) { $prevProcs[$_.Id] = $_.CPU } }
$prevTime = Get-Date

while ($true) {
    $gpuW = 0; $gpuU = 0; $cpu = 0; $n = 0
    for ($i = 0; $i -lt (60 / $SampleSec); $i++) {
        Start-Sleep -Seconds $SampleSec
        $g = Get-Gpu; $gpuW += $g[0]; $gpuU += $g[1]
        $cpu += (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
        $n++
    }
    $now = Get-Date
    # A long gap means the PC was asleep: don't log the stale samples as this minute.
    if (($now - $prevTime).TotalSeconds -gt 120) { $prevTime = $now; continue }
    $gpuW /= $n; $gpuU /= $n; $cpu /= $n

    # Which processes used the CPU this minute (share of the whole PC).
    $elapsed = ($now - $prevTime).TotalSeconds
    $procs = @{}; $use = @{}
    foreach ($p in Get-Process) {
        if (-not $p.CPU) { continue }
        $procs[$p.Id] = $p.CPU
        $d = if ($prevProcs.ContainsKey($p.Id)) { $p.CPU - $prevProcs[$p.Id] } else { $p.CPU }
        if ($d -gt 0 -and $p.ProcessName -ne 'Idle') { $use[$p.ProcessName] = $use[$p.ProcessName] + $d }
    }
    $top = ($use.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 3 |
        Where-Object { $_.Value / $elapsed / $cores * 100 -ge 2 } |
        ForEach-Object { '{0}:{1}' -f $_.Key, [int]($_.Value / $elapsed / $cores * 100) }) -join ';'
    $prevProcs = $procs; $prevTime = $now

    $fg = ''
    try { $fg = (Get-Process -Id ([PowerLog.Win]::ForegroundPid()) -ErrorAction Stop).ProcessName } catch {}
    $idle = [PowerLog.Win]::IdleSeconds()
    $screens = [int](($displayOff -eq 0) -or ($idle -lt $displayOff) -or ($gpuU -ge 30))

    $pcW  = ($cfg.baseWatts + $cfg.cpuIdleWatts + ($cfg.cpuMaxWatts - $cfg.cpuIdleWatts) * [Math]::Min($cpu, 100) / 100 + $gpuW) / $cfg.psuEfficiency
    $monW = $cfg.monitorsWatts * $screens

    $file = Join-Path $dir ('power-{0:yyyy-MM}.csv' -f $now)
    if (-not (Test-Path $file)) { 'time,gpu_w,gpu_util,cpu_pct,screens,pc_w,mon_w,top_cpu,foreground' | Set-Content $file -Encoding UTF8 }
    [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:yyyy-MM-dd HH:mm},{1:F1},{2:F0},{3:F1},{4},{5:F1},{6},"{7}","{8}"',
        $now, $gpuW, $gpuU, $cpu, $screens, $pcW, $monW, $top, $fg) | Add-Content $file -Encoding UTF8

    if ($now.Minute -eq 0) { $displayOff = Get-DisplayOffSec }   # pick up setting changes hourly
}
