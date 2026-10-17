# Exercises the Windows installer (#50): install 1.0.0 per user, set up the background service and the sign-in entry,
# start the app and hand a second launch over to it (#212), update to 1.0.1 from a local feed as Restart to update does
# (the new version starts again and resumes the service), then uninstall, and checks that nothing is left behind but
# the configuration.
#
#   pwsh tests/installer/windows.ps1 -Setup installers/SemanticRouter-win-x64-Setup-1.0.0.exe -Feed installers
#
# Runs on a throw-away CI machine: it uses the real per-user locations (%LocalAppData%, HKCU, %AppData%).
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $Feed
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'SemanticRouter'
$exe = Join-Path $root 'current\SemanticRouter.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$config = Join-Path $env:APPDATA 'semantic-router'
# The state database, models and logs: out of the roaming profile (#216).
$local = Join-Path $env:LOCALAPPDATA 'semantic-router'

function Step($text) { Write-Host "::group::$text" }
function Done { Write-Host '::endgroup::' }
function Fail($text) { Write-Host "::error::$text"; exit 1 }

function Wait-Until([scriptblock] $condition, [int] $seconds, [string] $what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 500
    }
    Fail "Timed out after $seconds s waiting for: $what"
}

function Installed-Version {
    $manifest = Join-Path $root 'current\sq.version'
    if (-not (Test-Path $manifest)) { return $null }
    ([xml](Get-Content -Raw $manifest)).package.metadata.version
}

function Router-Processes {
    @(Get-CimInstance Win32_Process -Filter "Name = 'SemanticRouter.exe'" | Where-Object { $_.CommandLine -like '*--headless*' })
}

function Run-Value($name) { (Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue).$name }

# Starts a program and waits for it alone: the app is a GUI program, and the router it starts as the background service
# outlives it, so neither `&` nor Start-Process -Wait fits. Returns the exit code; the output goes to the log.
function Invoke-Program([string] $program, [string[]] $arguments, [int] $seconds = 120) {
    $out = New-TemporaryFile
    $err = New-TemporaryFile
    $p = Start-Process -FilePath $program -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $out.FullName -RedirectStandardError $err.FullName
    $null = $p.Handle # keeps the exit code readable after the process ends
    if (-not $p.WaitForExit($seconds * 1000)) { Fail "$program $arguments did not exit within $seconds s" }
    Get-Content $out, $err | Out-Host
    $p.ExitCode
}

Step 'A home from the newest release'
# The upgrade fixture of the newest release (#206): what it set up and used, in the default configuration directory.
# Its state database is moved out of the roaming profile on the first start (#216).
$tools = Join-Path $PSScriptRoot '..\..\tools\upgrade-fixtures'
$newest = Get-ChildItem (Join-Path $PSScriptRoot '..\fixtures\upgrade') -Filter '*.zip' | Sort-Object { [version]$_.BaseName } | Select-Object -Last 1
$usage = (python (Join-Path $tools 'seed-home.py') $newest.FullName $config).Trim()
if ($LASTEXITCODE -ne 0) { Fail "Could not seed $config from $($newest.Name)" }
Write-Host "Seeded $config from $($newest.Name) ($usage usage records)"
Done

Step 'Install 1.0.0'
$setupLog = Join-Path $env:RUNNER_TEMP 'setup.log'
$code = Invoke-Program (Resolve-Path $Setup).Path @('--silent', '--log', $setupLog) 300
if ($code -ne 0) { Get-Content $setupLog -ErrorAction SilentlyContinue; Fail "Setup exited with $code" }
if (-not (Test-Path $exe)) { Get-Content $setupLog -ErrorAction SilentlyContinue; Fail "Not installed at $exe" }
if ((Installed-Version) -ne '1.0.0') { Fail "Installed version is '$(Installed-Version)', expected 1.0.0" }

# Setup starts the app (the tray, with its welcome window) after installing; this test drives it from the command line.
Start-Sleep -Seconds 5
Get-Process SemanticRouter -ErrorAction SilentlyContinue | Stop-Process -Force
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\SemanticRouter.lnk'
if (-not (Test-Path $shortcut)) { Fail "No Start menu entry at $shortcut" }
Write-Host "Installed $(Installed-Version) at $root"
Done

Step 'Background service and sign-in entry'
$code = Invoke-Program $exe @('--install-service')
if ($code -ne 0) { Fail "--install-service exited with $code" }
if (-not (Run-Value 'SemanticRouter')) { Fail 'No sign-in entry for the background service' }
Wait-Until { (Router-Processes).Count -ge 1 } 60 'the background service to run'

# The app's own sign-in entry (the tray's "Start at sign-in"), as the app writes it.
New-ItemProperty -Path $runKey -Name 'SemanticRouter Desktop' -Value "`"$exe`" --background --config-dir `"$config`"" -PropertyType String -Force | Out-Null
Wait-Until { Test-Path (Join-Path $local 'semantic-router.db') } 60 "the service's state database in $local"
if (Test-Path (Join-Path $config 'semantic-router.db')) { Fail "The state database is still in the roaming $config" }
python (Join-Path $tools 'check-usage.py') http://127.0.0.1:5080 $usage "1.0.0 on the $($newest.BaseName) home"
if ($LASTEXITCODE -ne 0) { Fail 'The home from the newest release lost its usage' }
Done

Step 'The app starts in the background, takes a second launch and quits'
# Its own configuration directory and port: the background service has the default ones.
$other = Join-Path $env:RUNNER_TEMP 'app-config'
$appArgs = @('--background', '--config-dir', "`"$other`"", '--urls', 'http://127.0.0.1:5291')
$app = Start-Process -FilePath $exe -ArgumentList $appArgs -PassThru
Wait-Until { Test-Path (Join-Path $other 'semantic-router.instance.json') } 90 "the app's router to listen"
$code = Invoke-Program $exe $appArgs 30
if ($code -ne 0) { Fail "A second launch exited with $code instead of handing over" }
if ($app.HasExited) { Fail 'The running app exited when a second launch handed over to it' }
$code = Invoke-Program $exe @('--quit', '--config-dir', "`"$other`"") 30
if ($code -ne 0) { Fail "--quit exited with $code" }
if (-not $app.WaitForExit(30000)) { Stop-Process -Id $app.Id -Force; Fail '--quit did not stop the running app' }
if (Test-Path (Join-Path $other 'semantic-router.instance.json')) { Fail "The app quit but left its router's instance file" }
Write-Host 'Started, handed a second launch over, and quit.'
Done

Step 'Update to 1.0.1, as Restart to update'
# A release build takes SEMANTICROUTER_UPDATE_SOURCE only when the machine policy allows a custom feed (CI runs as an administrator).
$policy = 'HKLM:\Software\Policies\SemanticRouter\Updates'
New-Item -Path $policy -Force | Out-Null
New-ItemProperty -Path $policy -Name 'AllowCustomFeed' -Value 1 -PropertyType DWord -Force | Out-Null
$env:SEMANTICROUTER_UPDATE_SOURCE = (Resolve-Path $Feed).Path
$code = Invoke-Program $exe @('--update', '--restart') 300
if ($code -ne 0) { Get-Content (Join-Path $local 'logs\semantic-router.log') -Tail 20 -ErrorAction SilentlyContinue; Fail "--update exited with $code" }
Wait-Until { (Installed-Version) -eq '1.0.1' } 120 'version 1.0.1 to be installed'
Remove-Item Env:SEMANTICROUTER_UPDATE_SOURCE
Remove-Item -Path 'HKLM:\Software\Policies\SemanticRouter' -Recurse -Force

# The update stopped the service; the new version started again and resumed it.
function App-Processes {
    @(Get-CimInstance Win32_Process -Filter "Name = 'SemanticRouter.exe'" | Where-Object { $_.CommandLine -like '*--resume-service*' })
}
Wait-Until { (App-Processes).Count -ge 1 } 90 'the new version to start again with --resume-service'
$app = (App-Processes)[0]
if ($app.ExecutablePath -ne $exe) { Fail "The app restarted from $($app.ExecutablePath), expected $exe" }
if ($app.CommandLine -notlike "*$config*") { Fail "The app restarted without its configuration directory: $($app.CommandLine)" }
Wait-Until { (Router-Processes).Count -ge 1 } 90 'the background service to run again after the update'
$running = (Router-Processes)[0].ExecutablePath
if ($running -ne $exe) { Fail "The service runs $running, expected $exe" }

# Quit the restarted app; the service keeps running. The app starts the service before its window, and only then takes
# a second launch: until it does, --quit finds no app and does nothing, so it is asked again until it is gone.
Wait-Until {
    $code = Invoke-Program $exe @('--quit', '--config-dir', "`"$config`"") 30
    if ($code -ne 0) { Fail "--quit exited with $code" }
    Start-Sleep -Seconds 2
    (App-Processes).Count -eq 0
} 90 'the restarted app to quit'
if ((Router-Processes).Count -lt 1) { Fail 'Quitting the app stopped the background service' }
python (Join-Path $tools 'check-usage.py') http://127.0.0.1:5080 $usage '1.0.1 after the update'
if ($LASTEXITCODE -ne 0) { Fail 'The update lost the usage' }
Write-Host "Updated to $(Installed-Version); the app started again and the service runs again"
Done

Step 'Uninstall'
$update = Join-Path $root 'Update.exe'
$code = Invoke-Program $update @('uninstall', '--silent') 180
if ($code -ne 0) { Fail "Update.exe uninstall exited with $code" }
Wait-Until { -not (Test-Path $exe) } 60 'the program files to be removed'
Wait-Until { (Router-Processes).Count -eq 0 } 30 'the background service to stop'
if (Run-Value 'SemanticRouter') { Fail 'The background service sign-in entry is still there' }
if (Run-Value 'SemanticRouter Desktop') { Fail "The app's sign-in entry is still there" }
if (Test-Path $shortcut) { Fail 'The Start menu entry is still there' }
# The uninstaller removes its own Update.exe a few seconds after it exits (a scheduled `rmdir`).
function Files-Left { @(Get-ChildItem -Recurse -Force $root -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer }) }
$deadline = (Get-Date).AddSeconds(30)
while ((Files-Left).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
$left = Files-Left
if ($left.Count -gt 0) { $left | ForEach-Object { Write-Host "  left: $($_.FullName)" }; Fail "$($left.Count) file(s) left in $root" }
if (-not (Test-Path $config)) { Fail "The configuration directory $config was removed; it should stay" }
Write-Host 'Uninstalled: no service, sign-in entry or program files left; the configuration stays.'
Done
