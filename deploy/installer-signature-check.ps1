# Checks the code signatures of a Windows release's installer and of the program files it installs, for the release
# workflow (docs/releases.md#code-signing):
#
#   pwsh deploy/installer-signature-check.ps1 -Installers <dir> -Version <version> [-Rid win-x64]
#   pwsh deploy/installer-signature-check.ps1 -Installers installers/win-x64 -Version 0.5.0
#
# Checked with Get-AuthenticodeSignature:
# - every *-Setup.exe in <dir>;
# - from the update package SemanticRouter-<version>-<rid>-full.nupkg in <dir>, which Setup.exe installs and the
#   installed app updates to: SemanticRouter.exe, Squirrel.exe (Velopack's updater) and SemanticRouter_ExecutionStub.exe,
#   and every other executable image in it (a program or library: a file starting with MZ, whatever its name), as the
#   desktop app checks an update with Updates:Publisher (src/SemanticRouter.Desktop/UpdatePublisher.cs).
#
# With SIGNING=signed every one must be Valid, or the script exits 1; otherwise it reports which are unsigned. A file
# that is missing fails either way: then nothing was checked. A list item per check goes to $GITHUB_STEP_SUMMARY when it
# is set. Only the version's own package is read: the folder can also hold the previous release's, for a delta update.
param(
    [Parameter(Mandatory)] [string] $Installers,
    [Parameter(Mandatory)] [string] $Version,
    [string] $Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$signed = $env:SIGNING -eq 'signed'
$script:failed = $false

function Report([string] $what, [string] $line) {
    Write-Output "${what}: $line"
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY "- ${what}: $line" }
}

function Fail([string] $text) {
    Write-Output "::error::$text"
    $script:failed = $true
}

function Check([string] $path, [string] $what) {
    $status = (Get-AuthenticodeSignature -LiteralPath $path).Status
    if ($status -eq 'Valid') {
        Report $what 'signed, signature checked'
    } elseif ($signed) {
        Fail "$what should be signed, but its signature is $status."
        Report $what "**failed** ($status)"
    } else {
        Report $what "**unsigned** ($status)"
    }
}

function Test-ExecutableImage([string] $path) {
    $stream = [IO.File]::OpenRead($path)
    try {
        $header = [byte[]]::new(2)
        return $stream.Read($header, 0, 2) -eq 2 -and $header[0] -eq 0x4D -and $header[1] -eq 0x5A
    } finally {
        $stream.Dispose()
    }
}

$setup = @(Get-ChildItem -LiteralPath $Installers -Filter '*-Setup.exe')
if ($setup.Count -eq 0) {
    Write-Output "::error::No Setup.exe in $Installers."
    exit 1
}
foreach ($file in $setup) {
    Check $file.FullName "Windows installer ``$($file.Name)``"
}

$name = "SemanticRouter-$Version-$Rid-full.nupkg"
$package = Join-Path $Installers $name
if (-not (Test-Path -LiteralPath $package)) {
    Write-Output "::error::No $name in $Installers."
    exit 1
}

$work = Join-Path ([IO.Path]::GetTempPath()) "installer-signature-check-$([guid]::NewGuid().ToString('N'))"
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $work)
    $app = Join-Path $work 'lib/app'
    $named = 'SemanticRouter.exe', 'Squirrel.exe', 'SemanticRouter_ExecutionStub.exe'
    foreach ($file in $named) {
        $path = Join-Path $app $file
        if (Test-Path -LiteralPath $path) {
            Check $path "``$file`` in ``$name``"
        } else {
            Fail "$file is not in lib/app/ of $name."
        }
    }

    # The rest in one line: a self-contained app has a few hundred of them, most signed by Microsoft already.
    $others = @(Get-ChildItem -LiteralPath $app -Recurse -File |
        Where-Object { $named -notcontains $_.Name -and (Test-ExecutableImage $_.FullName) })
    $unsigned = @($others | Where-Object { (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid' } |
        ForEach-Object { [IO.Path]::GetRelativePath($app, $_.FullName) })
    $what = "Other executable files in ``$name``"
    if ($unsigned.Count -eq 0) {
        Report $what "$($others.Count), all signed, signatures checked"
    } else {
        $list = ($unsigned | Select-Object -First 5) -join ', '
        if ($unsigned.Count -gt 5) { $list += ', ...' }
        if ($signed) {
            Fail "$($unsigned.Count) of the $($others.Count) other executable files in $name should be signed, but are not: $list"
            Report $what "**failed**: $($unsigned.Count) of $($others.Count) not signed ($list)"
        } else {
            Report $what "**$($unsigned.Count) of $($others.Count) unsigned** ($list)"
        }
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failed) {
    exit 1
}
