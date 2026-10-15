# Loads settings.default.json, then applies your settings.json on top of it.
# install.ps1 creates settings.json as a copy of the defaults; edit that file, not the default one.
function Get-WidgetSettings([string]$Dir = $PSScriptRoot) {
    $cfg = Get-Content (Join-Path $Dir 'settings.default.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $userFile = Join-Path $Dir 'settings.json'
    if (Test-Path $userFile) {
        $user = Get-Content $userFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($p in $user.PSObject.Properties) {
            $d = $cfg.PSObject.Properties[$p.Name]
            if ($d -and $d.Value -is [pscustomobject] -and $p.Value -is [pscustomobject]) {
                foreach ($q in $p.Value.PSObject.Properties) { $d.Value | Add-Member -NotePropertyName $q.Name -NotePropertyValue $q.Value -Force }
            } else {
                $cfg | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value -Force
            }
        }
    }
    $cfg
}
