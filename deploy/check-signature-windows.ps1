# Checks the Authenticode signature of each Windows installer (*-Setup.exe) in a directory, for the release's installer
# job and the signing dry run (docs/desktop.md#signing), so that both check the same way:
#
#   deploy/check-signature-windows.ps1 <installer-dir> [-Required]
#
# With -Required, fails unless every installer's signature is Valid (signed, and the chain trusted); without it, an
# unsigned installer is only reported as such. Fails when the directory holds no installer. Adds a line per installer
# to $GITHUB_STEP_SUMMARY when that is set. Windows only: Get-AuthenticodeSignature.
param(
    [Parameter(Mandatory)][string] $Directory,
    [switch] $Required
)
$ErrorActionPreference = 'Stop'

$setup = @(Get-ChildItem $Directory -Filter '*-Setup.exe')
if ($setup.Count -eq 0) { Write-Output "::error::No Setup.exe in $Directory."; exit 1 }
foreach ($file in $setup) {
    $signature = Get-AuthenticodeSignature $file.FullName
    if ($Required -and $signature.Status -ne 'Valid') {
        Write-Output "::error::$($file.Name) should be signed, but its signature is $($signature.Status): $($signature.StatusMessage)"
        exit 1
    }
    $line = if ($signature.Status -eq 'Valid') {
        "signed by $($signature.SignerCertificate.Subject), signature checked"
    } else {
        "**unsigned** ($($signature.Status))"
    }
    Write-Output "$($file.Name): $line"
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content $env:GITHUB_STEP_SUMMARY "Windows installer ``$($file.Name)``: $line" }
}
