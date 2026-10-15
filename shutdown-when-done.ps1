# Optional, for Claude Code users: shuts the PC down once the Claude Code sessions that are working
# now have finished. Turn it on with claudeShutdownSwitch.enabled in settings.json: the widget's panel
# then shows a "Turn off PC when Claude is done" switch that starts (and cancels) this script.
# Unofficial: it reads Claude Code's local session files (~/.claude/sessions), which may change.
#
# A session counts as done once its status is no longer "busy", no shell command or job is running
# under it, and nothing has happened for 10 minutes. Renders, encodes and batch builds running
# anywhere on the PC hold the shutdown too. Then Windows shuts down after a 2-minute countdown
# (cancel with the switch, or run: shutdown /a).
#   -Auto                        watch every session that is working right now (what the switch does)
#   -Sessions 'Name=id,Name=id'  watch these session IDs instead
#   -MustWake 'Name'             a session on hold, waiting to be handed work: it must wake up before it can count as done
#   -DryRun                      print the current state once and exit
param([switch]$DryRun, [switch]$Auto, [string]$Sessions = '', [string]$MustWake = '')

. (Join-Path $PSScriptRoot 'settings.ps1')
$cfg = (Get-WidgetSettings $PSScriptRoot).claudeShutdownSwitch
$claude      = Join-Path $env:USERPROFILE '.claude'
$quietMin    = 10    # idle + no transcript writes / shell commands for this long = done
$failsafeMin = 120   # a shell command running longer than this is treated as hung and ignored
$stuckMin    = 90    # "busy" with no transcript writes and no shell commands this long = stuck, treated as done
$wakeGraceMin = 60   # a must-wake session that never wakes counts as done this long after the rest finished
$ignoreShell = if ($cfg.ignoreCommands) { $cfg.ignoreCommands } else { '(?!)' }   # long-running helpers (dev servers) that never "finish"
$extraJobs   = $cfg.extraJobs                                                    # more processes to wait for (regex on path + command line)
$logDir = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'shutdown-watch.log'
$statusFile = Join-Path $logDir 'shutdown-watch.status'   # read by the widget's switch
$pidFile = Join-Path $logDir 'shutdown-watch.pid'         # lets the widget cancel this watcher
if (-not $DryRun) {
    # One watcher at a time; the widget also checks this mutex to show the switch as on.
    $mutex = New-Object System.Threading.Mutex($false, 'Local\CnflRateWidget.Shutdown')
    if (-not $mutex.WaitOne(0)) { Write-Host 'Already armed: another shutdown watcher is running.'; Start-Sleep -Seconds 5; exit }
    Set-Content $pidFile $PID
}
function Write-Status($mode, $lines) {
    if (-not $DryRun) { @($mode) + @($lines) | Set-Content $statusFile -Encoding UTF8 }
}

$Host.UI.RawUI.WindowTitle = 'Shutdown watcher (close to cancel)'

function Log($msg) {
    $line = '{0:MM-dd HH:mm:ss}  {1}' -f (Get-Date), $msg
    Write-Host $line
    Add-Content -Path $log -Value $line
}

function Get-Sessions {
    foreach ($f in Get-ChildItem (Join-Path $claude 'sessions') -Filter *.json -ErrorAction SilentlyContinue) {
        try { Get-Content $f.FullName -Raw | ConvertFrom-Json } catch {}
    }
}

function Get-ProjDir($cwd) { Join-Path $claude ('projects\' + ($cwd -replace '[^A-Za-z0-9]', '-')) }

function Get-Title($info) {
    # The session's auto-generated title, from the tail of its transcript.
    $p = Join-Path (Get-ProjDir $info.cwd) "$($info.sessionId).jsonl"
    try {
        $fs = [IO.File]::Open($p, 'Open', 'Read', 'ReadWrite')
        $n = [int][Math]::Min($fs.Length, 4MB)
        [void]$fs.Seek(-$n, 'End'); $buf = New-Object byte[] $n; [void]$fs.Read($buf, 0, $n); $fs.Close()
        $m = [regex]::Matches([Text.Encoding]::UTF8.GetString($buf), '"aiTitle":"([^"]+)"')
        if ($m.Count) { return $m[$m.Count - 1].Groups[1].Value }
    } catch {}
    return $info.name
}

$procs = @()   # process snapshot, refreshed once per check
function Update-Procs { $script:procs = @(Get-CimInstance Win32_Process) }

# Shell commands and the jobs they run, at any depth under a session (not just its direct children).
function Get-LiveShells($procId) {
    $now = Get-Date
    $found = @(); $queue = @($procId)
    while ($queue.Count) {
        $kids = @($script:procs | Where-Object { $queue -contains $_.ParentProcessId })
        $queue = @($kids | ForEach-Object { $_.ProcessId })
        $found += @($kids | Where-Object { $_.Name -match '^(bash|sh|powershell|pwsh|cmd|node|ffmpeg|python|Unity)\.exe$' -and
                        $_.CommandLine -notmatch $ignoreShell -and ($now - $_.CreationDate).TotalMinutes -lt $failsafeMin })
    }
    $found
}

# Heavy jobs anywhere on the PC, whoever started them: a background render can end up detached
# from the session's process tree, and it must not be killed halfway.
$jobFailsafeMin = 240
function Get-Jobs {
    $now = Get-Date
    @($script:procs | Where-Object {
        ($now - $_.CreationDate).TotalMinutes -lt $jobFailsafeMin -and (
            $_.Name -match '^(ffmpeg.*|chrome-headless-shell|remotion)\.exe$' -or   # video encodes, Remotion renders
            ($_.Name -eq 'node.exe' -and $_.CommandLine -match 'remotion' -and $_.CommandLine -match '\brender\b') -or
            ($_.Name -eq 'Unity.exe' -and $_.CommandLine -match '-batchmode') -or
            ($extraJobs -and ("$($_.ExecutablePath) $($_.CommandLine)" -match $extraJobs)))
    })
}

# Keep the PC awake while waiting (the display can still turn off).
Add-Type -Namespace Win32 -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[Win32.Power]::SetThreadExecutionState([uint32]2147483649) | Out-Null  # ES_CONTINUOUS | ES_SYSTEM_REQUIRED

$watch = [ordered]@{}
Update-Procs
if ($Auto) {
    foreach ($s in Get-Sessions) {
        if (-not (Get-Process -Id $s.pid -ErrorAction SilentlyContinue)) { continue }
        if ($s.status -ne 'busy' -and (Get-LiveShells $s.pid).Count -eq 0) { continue }   # idle with nothing running: already done
        $name = Get-Title $s
        if ($watch.Contains($name)) { $name = "$name ($($s.name))" }
        $watch[$name] = $s.sessionId
    }
} else {
    foreach ($pair in $Sessions -split ',') {
        $k, $v = $pair -split '=', 2
        if ($v) { $watch[$k.Trim()] = $v.Trim() }
    }
}
$mustWakeList = @($MustWake -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$lastShell = @{}   # last time each session had a live shell command (background agents leave no transcript we can watch)
$woke = @{}
$heldAt = @{}      # status time when a must-wake session was first seen on hold; only a wake after that counts

function Get-SessionState($name, $id) {
    $info = Get-Sessions | Where-Object { $_.sessionId -eq $id } | Select-Object -First 1
    if (-not $info -or -not (Get-Process -Id $info.pid -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ Name = $name; Done = $true; State = 'closed'; Detail = 'closed'; StatusTime = $null }
    }
    $now = Get-Date
    $statusTime = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$info.statusUpdatedAt).LocalDateTime
    $shells = Get-LiveShells $info.pid
    if ($shells.Count -gt 0) { $script:lastShell[$name] = $now }

    # How long since anything happened: status change, transcript writes or a live shell command.
    $projDir = Get-ProjDir $info.cwd
    $files  = @(Get-Item (Join-Path $projDir "$id.jsonl") -ErrorAction SilentlyContinue)
    $files += @(Get-ChildItem (Join-Path $projDir $id) -Recurse -File -ErrorAction SilentlyContinue)
    $lastWrite = ($files | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
    $since = @($lastWrite, $statusTime, $script:lastShell[$name]) | Where-Object { $_ } | Sort-Object -Descending | Select-Object -First 1
    $quiet = [int]($now - $since).TotalMinutes

    if ($info.status -eq 'busy') {
        if ($quiet -ge $stuckMin) {
            return [pscustomobject]@{ Name = $name; Done = $true; State = 'stuck'; Detail = "busy but nothing happened for ${quiet}m: treated as stuck"; StatusTime = $statusTime }
        }
        return [pscustomobject]@{ Name = $name; Done = $false; State = 'busy'; Detail = 'working'; StatusTime = $statusTime }
    }
    if ($shells.Count -gt 0) {
        return [pscustomobject]@{ Name = $name; Done = $false; State = 'bg-job'; Detail = "$($info.status), $($shells.Count) background job(s)"; StatusTime = $statusTime }
    }
    if ($quiet -lt $quietMin) {
        return [pscustomobject]@{ Name = $name; Done = $false; State = 'settling'; Detail = "$($info.status), quiet ${quiet}m of $quietMin"; StatusTime = $statusTime }
    }
    return [pscustomobject]@{ Name = $name; Done = $true; State = 'done'; Detail = "$($info.status), quiet ${quiet}m"; StatusTime = $statusTime }
}

$jobsAtStart = @(Get-Jobs).Count
if ($watch.Count -eq 0 -and $jobsAtStart -eq 0) {
    Write-Status 'nothing' @()
    Log 'No Claude session or background job is working right now.'
} else {
    $what = if ($watch.Count) { $watch.Keys -join ' + ' } else { 'background jobs only' }
    Log ("Watching: $what" + $(if ($mustWakeList) { "  (on hold: $($mustWakeList -join ', '))" }) + " (dry run: $DryRun)")
}
$prev = ''
$othersDoneSince = $null
$lastJob = $null
while ($watch.Count -gt 0 -or $jobsAtStart -gt 0) {
    Update-Procs
    $states = @(foreach ($k in $watch.Keys) { Get-SessionState $k $watch[$k] })
    # Heavy jobs anywhere on the PC hold the shutdown too, until 10 quiet minutes after the last one ends,
    # so the session that started them has time to wake up and carry on.
    $jobs = @(Get-Jobs)
    if ($jobs.Count) { $lastJob = Get-Date }
    if ($lastJob) {
        $jq = [int]((Get-Date) - $lastJob).TotalMinutes
        $names = ($jobs | ForEach-Object { $_.Name -replace '\.exe$', '' } | Sort-Object -Unique) -join ', '
        $jobState = if ($jobs.Count) { [pscustomobject]@{ Name = '(jobs)'; Done = $false; State = 'bg-job'; Detail = "running: $names"; StatusTime = $null } }
                    elseif ($jq -lt $quietMin) { [pscustomobject]@{ Name = '(jobs)'; Done = $false; State = 'settling'; Detail = "finished ${jq}m ago"; StatusTime = $null } }
                    else { [pscustomobject]@{ Name = '(jobs)'; Done = $true; State = 'done'; Detail = 'all finished'; StatusTime = $null } }
        $states += $jobState
    }
    foreach ($s in $states | Where-Object { $mustWakeList -contains $_.Name }) {
        if (-not $heldAt.ContainsKey($s.Name)) {
            if ($s.State -ne 'busy' -and $s.StatusTime) { $heldAt[$s.Name] = $s.StatusTime }
        } elseif ($s.State -eq 'busy' -or $s.StatusTime -gt $heldAt[$s.Name]) { $woke[$s.Name] = $true }
    }
    $others = @($states | Where-Object { $mustWakeList -notcontains $_.Name })
    if ($others.Count -and -not ($others | Where-Object { -not $_.Done })) { if (-not $othersDoneSince) { $othersDoneSince = Get-Date } } else { $othersDoneSince = $null }
    foreach ($s in $states | Where-Object { $mustWakeList -contains $_.Name -and $_.Done -and $_.State -ne 'closed' -and -not $woke[$_.Name] }) {
        if ($othersDoneSince -and ((Get-Date) - $othersDoneSince).TotalMinutes -ge $wakeGraceMin) {
            $s.State = 'never-woke'; $s.Detail += ", never woke ($wakeGraceMin m after the rest finished)"
        } else {
            $s.Done = $false; $s.State = 'on-hold'; $s.Detail = 'on hold, waiting to be woken'
        }
    }
    Write-Status 'watching' @($states | ForEach-Object { "$($_.Name)`t$($_.State)`t$($_.Detail)" })
    $key = ($states | ForEach-Object { "$($_.Name)=$($_.State)" }) -join ' '
    if ($key -ne $prev -or $DryRun) {
        Log (($states | ForEach-Object { "$($_.Name): $($_.Detail)" }) -join '  |  ')
        $prev = $key
    }
    if ($DryRun -or -not ($states | Where-Object { -not $_.Done })) { break }
    Start-Sleep -Seconds 60
}
if (-not $DryRun) {
    Log 'Done - shutting down in 2 minutes (run "shutdown /a" to cancel).'
    Write-Status 'shutting-down' @((Get-Date).AddSeconds(120).ToString('o'))
    shutdown.exe /s /t 120 /c "The Claude sessions are done. Shutting down in 2 minutes - run 'shutdown /a' to cancel."
    Start-Sleep -Seconds 130   # stay up through the countdown, so the widget still shows it and can cancel
}
