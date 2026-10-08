<#
.SYNOPSIS
    Builds SemanticRouter from this checkout and installs or updates it on Windows: the semantic-router tool, its
    background service, and the desktop app.

.DESCRIPTION
    1. Pulls the latest code (unless -NoPull).
    2. Stops the background service and quits the desktop app, so their files can be replaced.
    3. Packs the semantic-router tool with a unique local version and installs or updates it globally.
    4. Publishes the desktop app, self-contained, to -DesktopDir.
    5. Starts the background service again, or installs it with -InstallService (run by the desktop app as
       SemanticRouter.exe --headless, with no console window), then starts the desktop app.

    Run it from anywhere; it finds the repository from its own location. It needs the .NET 9 SDK and git. Your
    configuration (%APPDATA%\semantic-router) is never touched.

.EXAMPLE
    .\deploy\update-windows.ps1
    Pull main, rebuild and update everything that is installed, then start the desktop app.

.EXAMPLE
    .\deploy\update-windows.ps1 -InstallService
    First time: also install the background service (runs at every sign-in).

.EXAMPLE
    .\deploy\update-windows.ps1 -NoPull -SkipDesktop
    Rebuild the tool and service from the code as it is, without touching the desktop app.
#>
[CmdletBinding()]
param(
    # The branch to pull and build.
    [string] $Branch = 'main',

    # Build the code as it is, without git pull.
    [switch] $NoPull,

    # Install the background service if it is not installed yet.
    [switch] $InstallService,

    # Leave the tool and the service alone.
    [switch] $SkipService,

    # Leave the desktop app alone.
    [switch] $SkipDesktop,

    # Do not start the desktop app at the end.
    [switch] $NoLaunch,

    # Where the desktop app is installed. Start at sign-in points here, so keep it the same between updates. The folder
    # is replaced on every run, so it must hold only the app: the script refuses a folder with anything else in it.
    [string] $DesktopDir = (Join-Path $env:LOCALAPPDATA 'Programs\SemanticRouter')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$runtime = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$desktopExe = Join-Path $DesktopDir 'SemanticRouter.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$toolsDir = Join-Path $env:USERPROFILE '.dotnet\tools'

function Write-Step([string] $message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

# Runs a native command and stops the script when it fails.
function Invoke-Checked([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed (exit code $LASTEXITCODE)."
    }
}

function Test-ServiceInstalled {
    $value = Get-ItemProperty -Path $runKey -Name 'SemanticRouter' -ErrorAction SilentlyContinue
    return $null -ne $value
}

function Test-ToolInstalled {
    return [bool](dotnet tool list --global | Select-String -Pattern '^\s*semanticrouter\.host\s' -Quiet)
}

function Get-SemanticRouter {
    $command = Get-Command 'semantic-router' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $shim = Join-Path $toolsDir 'semantic-router.exe'
    if (Test-Path $shim) { return $shim }
    return $null
}

Push-Location $repo
try {
    Write-Step 'Checking prerequisites'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet was not found. Install the .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9.0'
    }
    if (-not (dotnet --list-sdks | Select-String -Pattern '^9\.' -Quiet)) {
        throw 'The .NET 9 SDK is not installed (dotnet --list-sdks). Install it from https://dotnet.microsoft.com/download/dotnet/9.0'
    }
    Write-Host "Repository: $repo"
    Write-Host "Runtime:    $runtime"

    if (-not $NoPull) {
        Write-Step "Pulling the latest $Branch"
        if (git status --porcelain) {
            throw 'There are uncommitted changes in the checkout. Commit or stash them, or run with -NoPull to build them as they are.'
        }
        Invoke-Checked 'git fetch' { git fetch origin $Branch }
        Invoke-Checked 'git checkout' { git checkout $Branch }
        Invoke-Checked 'git pull' { git pull --ff-only origin $Branch }
    }

    # A version that is new on every run, so `dotnet tool update` always takes this build.
    $prefix = (Select-Xml -Path (Join-Path $repo 'Directory.Build.props') -XPath '//VersionPrefix' | Select-Object -First 1).Node.InnerText
    $version = "$prefix-local.$(Get-Date -Format 'yyyyMMddHHmmss')"
    Write-Host "Version:    $version"

    Write-Step 'Stopping what is running'
    # The background service first: it may run from the desktop app's folder (SemanticRouter --headless), and it is
    # a SemanticRouter process the app's quit below would otherwise wait for.
    $serviceWasInstalled = Test-ServiceInstalled
    $router = Get-SemanticRouter
    if ($router) {
        & $router service stop
        if ($LASTEXITCODE -ne 0) { Write-Host 'Nothing to stop.' }
    }

    $desktopRunning = @(Get-Process -Name 'SemanticRouter' -ErrorAction SilentlyContinue)
    if ($desktopRunning.Count -gt 0) {
        if (Test-Path $desktopExe) {
            Write-Host 'Asking the desktop app to quit...'
            & $desktopExe --quit
        }
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Process -Name 'SemanticRouter' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        $left = @(Get-Process -Name 'SemanticRouter' -ErrorAction SilentlyContinue)
        if ($left.Count -gt 0) {
            Write-Host 'The desktop app did not quit in time; ending it.'
            $left | Stop-Process -Force
        }
        Write-Host 'Desktop app stopped.'
    }
    else {
        Write-Host 'Desktop app is not running.'
    }

    if (-not $SkipService) {
        Write-Step 'Building and installing the semantic-router tool'
        $artifacts = Join-Path $repo 'artifacts\local'
        if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
        Invoke-Checked 'dotnet pack' {
            dotnet pack src/SemanticRouter.Host -c Release -o $artifacts "-p:Version=$version" --nologo
        }
        # --allow-downgrade: always take this build, even over a higher version installed earlier (say, by hand).
        if (Test-ToolInstalled) {
            Invoke-Checked 'dotnet tool update' {
                dotnet tool update --global SemanticRouter.Host --add-source $artifacts --version $version --allow-downgrade
            }
        }
        else {
            Invoke-Checked 'dotnet tool install' {
                dotnet tool install --global SemanticRouter.Host --add-source $artifacts --version $version
            }
        }
        $router = Get-SemanticRouter
        if (-not $router) {
            throw "semantic-router is installed but not found. Add $toolsDir to your PATH and open a new terminal."
        }
        & $router --version
    }

    if (-not $SkipDesktop) {
        Write-Step "Publishing the desktop app to $DesktopDir"
        # Replace the whole folder so files from older builds do not linger. Settings live in %APPDATA%, not here.
        # Only a folder that holds the app (or nothing) is replaced, so a mistyped -DesktopDir cannot delete anything else.
        if (Test-Path $DesktopDir) {
            $isApp = Test-Path (Join-Path $DesktopDir 'SemanticRouter.exe')
            $isEmpty = -not (Get-ChildItem -LiteralPath $DesktopDir -Force | Select-Object -First 1)
            if (-not ($isApp -or $isEmpty)) {
                throw "$DesktopDir is not a SemanticRouter install (no SemanticRouter.exe) and is not empty; not replacing it. Pick another -DesktopDir."
            }
            Remove-Item -LiteralPath $DesktopDir -Recurse -Force
        }
        Invoke-Checked 'dotnet publish' {
            dotnet publish src/SemanticRouter.Desktop -c Release -r $runtime --self-contained `
                "-p:Version=$version" -p:DebugType=none -o $DesktopDir --nologo
        }
        Write-Host "Installed $desktopExe"

    }

    # After the app is in place: the service may run the app's own SemanticRouter.exe --headless.
    Write-Step 'Background service'
    if ($serviceWasInstalled) {
        if ($router) {
            Invoke-Checked 'semantic-router service start' { & $router service start }
        }
        else {
            Write-Host 'Installed, but the semantic-router tool is not; it starts again at your next sign-in.'
        }
    }
    elseif ($InstallService) {
        if (-not $SkipDesktop) {
            # The app runs the service itself (SemanticRouter.exe --headless): a GUI program, no console window.
            # Started from this shell, so the service gets this PATH (where claude, codex and copilot are found).
            $install = Start-Process -FilePath $desktopExe -ArgumentList '--install-service' -Wait -PassThru
            if ($install.ExitCode -ne 0) {
                throw "Installing the background service failed (exit code $($install.ExitCode)). See $(Join-Path $env:APPDATA 'semantic-router\logs\semantic-router.log')."
            }
            Write-Host 'Installed the background service (SemanticRouter.exe --headless).'
        }
        elseif ($router) {
            Invoke-Checked 'semantic-router service install' { & $router service install }
        }
    }
    else {
        Write-Host 'Not installed. Run this script with -InstallService, or tick "Keep the router running in the background" in the tray menu.'
    }

    if (-not $SkipDesktop -and -not $NoLaunch) {
        Write-Step 'Starting the desktop app'
        Start-Process -FilePath $desktopExe
    }

    if ($router) {
        Write-Step 'Status'
        Start-Sleep -Seconds 2
        & $router service status
    }

    Write-Host ''
    Write-Host "Done: SemanticRouter $version." -ForegroundColor Green
}
finally {
    Pop-Location
}
