# CNFL Rate Widget launcher. install.ps1 adds this to Startup, so the widget runs when you sign in.
# The UI is C# in tariff-widget.cs, compiled here at startup (nothing to install).
#   -Preview 'yyyy-MM-dd HH:mm'          show the panel as it would look at that time, for 8 seconds
#   -Card high|mid|highnow|highends      show that heads-up card (-Silent: no sound)
#   -Toggle                              flip the optional "turn off PC when Claude is done" switch
#   -Screenshots <folder>                render the README images into a folder
param(
    [string]$Preview = '',
    [ValidateSet('', 'high', 'mid', 'highnow', 'highends')][string]$Card = '',
    [switch]$Silent,
    [switch]$Toggle,
    [string]$Screenshots = ''
)

. (Join-Path $PSScriptRoot 'settings.ps1')
$log = Join-Path $PSScriptRoot 'logs\widget.log'
try {
    if (-not ($Preview -or $Card -or $Toggle -or $Screenshots)) {
        $mutex = New-Object System.Threading.Mutex($false, 'Local\CnflRateWidget')
        if (-not $mutex.WaitOne(0)) { exit }   # already running
    }
    $cfg = Get-WidgetSettings $PSScriptRoot
    Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Speech -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'tariff-widget.cs') -Raw -Encoding UTF8)

    $s = New-Object CnflRate.Settings
    $rates = if ($cfg.useAbove500kWhRates) { $cfg.ratesAbove500kWh } else { $cfg.rates }
    $s.Low = [double]$rates.low; $s.Mid = [double]$rates.mid; $s.High = [double]$rates.high
    $s.Alerts = [bool]$cfg.alerts.enabled
    $s.MinutesBefore = [int]$cfg.alerts.minutesBefore
    $s.Sound = [bool]$cfg.alerts.sound
    $s.Voice = [bool]$cfg.alerts.voice
    $s.HighCardSeconds = [int]$cfg.alerts.highCardSeconds
    $s.MidCardSeconds = [int]$cfg.alerts.midCardSeconds
    $s.PromoteIcon = [bool]$cfg.showIconOnTaskbar
    $s.PowerStats = [bool]$cfg.powerLog.enabled
    $s.ShutdownSwitch = [bool]$cfg.claudeShutdownSwitch.enabled
    if ($s.PowerStats) {
        # The Report link needs Python (pythonw.exe, or the py launcher's pyw.exe); hidden without it.
        $py = Get-Command pythonw.exe, pyw.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($py) { $s.ReportCommand = $py.Source }
    }

    if ($Screenshots) {
        [CnflRate.App]::RenderDocs($PSScriptRoot, $s, [IO.Path]::GetFullPath($Screenshots))
    } elseif ($Preview) {
        [CnflRate.App]::Preview($PSScriptRoot, $s, [datetime]::ParseExact($Preview, 'yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture), 8000)
    } elseif ($Card) {
        [CnflRate.App]::CardPreview($PSScriptRoot, $s, $Card, [bool]$Silent)
    } elseif ($Toggle) {
        [CnflRate.App]::ToggleSwitch($PSScriptRoot, $s)
    } else {
        [CnflRate.App]::Run($PSScriptRoot, $s)
    }
} catch {
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    Add-Content $log ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $_)
    throw
}
