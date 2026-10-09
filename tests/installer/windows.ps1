# Exercises the Windows installer (#50): install 1.0.0 per user, set up the background service and the sign-in entry,
# update to 1.0.1 from a local feed, then uninstall, and checks that nothing is left behind but the configuration.
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
Done

Step 'Update to 1.0.1'
$env:SEMANTICROUTER_UPDATE_SOURCE = (Resolve-Path $Feed).Path
$code = Invoke-Program $exe @('--update') 300
if ($code -ne 0) { Get-Content (Join-Path $config 'logs\semantic-router.log') -Tail 20 -ErrorAction SilentlyContinue; Fail "--update exited with $code" }
Wait-Until { (Installed-Version) -eq '1.0.1' } 120 'version 1.0.1 to be installed'
Remove-Item Env:SEMANTICROUTER_UPDATE_SOURCE

# The update stopped the service; the new version installed and started it again.
Wait-Until { (Router-Processes).Count -ge 1 } 90 'the background service to run again after the update'
$running = (Router-Processes)[0].ExecutablePath
if ($running -ne $exe) { Fail "The service runs $running, expected $exe" }
Write-Host "Updated to $(Installed-Version); the service runs again"
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
$left = @(Get-ChildItem -Recurse -Force $root -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer })
if ($left.Count -gt 0) { $left | ForEach-Object { Write-Host "  left: $($_.FullName)" }; Fail "$($left.Count) file(s) left in $root" }
if (-not (Test-Path $config)) { Fail "The configuration directory $config was removed; it should stay" }
Write-Host 'Uninstalled: no service, sign-in entry or program files left; the configuration stays.'
Done
