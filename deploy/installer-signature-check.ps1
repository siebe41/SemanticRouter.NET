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
# Who signed counts too, by the same rule as UpdatePublisher: the publisher is the certificate that signed the first
# Setup.exe. The three programs above and the app's own files (SemanticRouter.*) must be signed by it; every other
# executable file by it or by Microsoft (UpdatePublisher.Microsoft), so a valid signature from anyone else fails.
# The .NET host's unsigned files (*.runtimeconfig.json, *.deps.json), which UpdatePublisher also checks, are not.
#
# With SIGNING=signed every one must pass, or the script exits 1; otherwise it reports which are unsigned. A file
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

# A name's parts as oid=VALUE, sorted, so the same names in any order compare equal (UpdatePublisher.Names).
function Get-Names([Security.Cryptography.X509Certificates.X500DistinguishedName] $name) {
    $parts = foreach ($part in $name.EnumerateRelativeDistinguishedNames()) {
        if ($part.HasMultipleElements) {
            [Convert]::ToHexString($part.RawData.ToArray())
        } else {
            "$($part.GetSingleElementType().Value)=$("$($part.GetSingleElementValue())".Trim().ToUpperInvariant())"
        }
    }
    return (@($parts) | Sort-Object -CaseSensitive) -join "`n"
}

$microsoft = Get-Names ([Security.Cryptography.X509Certificates.X500DistinguishedName]::new(
        'O=Microsoft Corporation, L=Redmond, S=Washington, C=US'))

# Whether the certificate was issued to Microsoft: its subject is UpdatePublisher.Microsoft plus a common name.
function Test-Microsoft([Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $names = Get-Names $certificate.SubjectName
    $withoutCommonName = (($names -split "`n") | Where-Object { -not $_.StartsWith('2.5.4.3=') }) -join "`n"
    return $withoutCommonName -ceq $microsoft
}

# Why the file's signature does not pass, or $null when it does. $allowMicrosoft: Microsoft may have signed it instead
# of the publisher.
function Get-Problem([string] $path, [bool] $allowMicrosoft) {
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid') {
        return "$($signature.Status)"
    }
    $signer = $signature.SignerCertificate
    if ($script:publisher -and $signer.Thumbprint -eq $script:publisher.Thumbprint) {
        return $null
    }
    if ($allowMicrosoft -and (Test-Microsoft $signer)) {
        return $null
    }
    $expected = if ($script:publisher) { $script:publisher.Subject } else { 'the publisher (the installer is not signed)' }
    return "signed by $($signer.Subject), not by $expected$(if ($allowMicrosoft) { ' or Microsoft' })"
}

function Check([string] $path, [string] $what) {
    $problem = Get-Problem $path $false
    if (-not $problem) {
        Report $what 'signed by the publisher, signature checked'
    } elseif ($signed) {
        Fail "$what should be signed by the publisher, but: $problem."
        Report $what "**failed** ($problem)"
    } else {
        Report $what "**unsigned** ($problem)"
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
# The publisher: whoever signed the (first) installer, which vpk signs with the same certificate as everything else.
$first = Get-AuthenticodeSignature -LiteralPath $setup[0].FullName
$script:publisher = if ($first.Status -eq 'Valid') { $first.SignerCertificate } else { $null }
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

    # The rest in one line: a self-contained app has a few hundred of them, most signed by Microsoft already. The
    # app's own (SemanticRouter.*) must be the publisher's; the others may be Microsoft's instead.
    $others = @(Get-ChildItem -LiteralPath $app -Recurse -File |
        Where-Object { $named -notcontains $_.Name -and (Test-ExecutableImage $_.FullName) })
    $refused = @(foreach ($file in $others) {
            $own = $file.Name.StartsWith('SemanticRouter.', [StringComparison]::OrdinalIgnoreCase)
            $problem = Get-Problem $file.FullName (-not $own)
            if ($problem) { "$([IO.Path]::GetRelativePath($app, $file.FullName)): $problem" }
        })
    $what = "Other executable files in ``$name``"
    if ($refused.Count -eq 0) {
        Report $what "$($others.Count), all signed by the publisher or Microsoft, signatures checked"
    } else {
        $list = ($refused | Select-Object -First 5) -join '; '
        if ($refused.Count -gt 5) { $list += '; ...' }
        if ($signed) {
            Fail "$($refused.Count) of the $($others.Count) other executable files in $name are not signed by the publisher or Microsoft: $list"
            Report $what "**failed**: $($refused.Count) of $($others.Count) not signed by the publisher or Microsoft ($list)"
        } else {
            Report $what "**$($refused.Count) of $($others.Count) unsigned or signed by someone else** ($list)"
        }
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failed) {
    exit 1
}
