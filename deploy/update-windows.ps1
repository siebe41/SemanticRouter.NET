<#
.SYNOPSIS
    Builds SemanticRouter from this checkout and installs or updates it on Windows: the semantic-router tool, its
    background service, and the desktop app.

.DESCRIPTION
    1. Pulls the latest code (unless -NoPull).
    2. Quits the desktop app and stops the background service, so their files can be replaced.
    3. Packs the semantic-router tool with a unique local version and installs or updates it globally.
    4. Starts the background service again (installs it with -InstallService, or on first run when asked).
    5. Publishes the desktop app, self-contained, to -DesktopDir and starts it.

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

    # Where the desktop app is installed. Start at sign-in points here, so keep it the same between updates.
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

    $serviceWasInstalled = Test-ServiceInstalled
    $router = Get-SemanticRouter
    if (-not $SkipService -and $router) {
        # Stops the background router (and any router using the configuration directory) so the tool can be replaced.
        & $router service stop
        if ($LASTEXITCODE -ne 0) { Write-Host 'Nothing to stop.' }
    }

    if (-not $SkipService) {
        Write-Step 'Building and installing the semantic-router tool'
        $artifacts = Join-Path $repo 'artifacts\local'
        if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
        Invoke-Checked 'dotnet pack' {
            dotnet pack src/SemanticRouter.Host -c Release -o $artifacts "-p:Version=$version" --nologo
        }
        $verb = if (Test-ToolInstalled) { 'update' } else { 'install' }
        Invoke-Checked "dotnet tool $verb" {
            dotnet tool $verb --global SemanticRouter.Host --add-source $artifacts --version $version
        }
        $router = Get-SemanticRouter
        if (-not $router) {
            throw "semantic-router is installed but not found. Add $toolsDir to your PATH and open a new terminal."
        }
        & $router --version

        Write-Step 'Background service'
        if ($serviceWasInstalled) {
            Invoke-Checked 'semantic-router service start' { & $router service start }
        }
        elseif ($InstallService) {
            # Run from this shell, so the service gets this PATH (where claude, codex and copilot are found).
            Invoke-Checked 'semantic-router service install' { & $router service install }
        }
        else {
            Write-Host 'Not installed. Run this script with -InstallService to start the router at every sign-in.'
        }
    }

    if (-not $SkipDesktop) {
        Write-Step "Publishing the desktop app to $DesktopDir"
        # Replace the whole folder so files from older builds do not linger. Settings live in %APPDATA%, not here.
        if (Test-Path $DesktopDir) { Remove-Item $DesktopDir -Recurse -Force }
        Invoke-Checked 'dotnet publish' {
            dotnet publish src/SemanticRouter.Desktop -c Release -r $runtime --self-contained `
                "-p:Version=$version" -p:DebugType=none -o $DesktopDir --nologo
        }
        Write-Host "Installed $desktopExe"

        if (-not $NoLaunch) {
            Write-Step 'Starting the desktop app'
            Start-Process -FilePath $desktopExe
        }
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
