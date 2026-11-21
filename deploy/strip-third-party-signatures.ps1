# Removes the Authenticode signature from every executable image in a published Windows app that someone other than
# Microsoft signed, so that vpk signs it with the release's certificate like the app's other unsigned files (#336):
#
#   pwsh deploy/strip-third-party-signatures.ps1 -Directory <published app>
#
# vpk signs only the files that are not signed yet. A library that comes signed by its own author, such as Avalonia's
# av_libglesv2.dll (signed by AvaloniaUI OÜ), would keep that signature in a signed release, and both the release's
# check (deploy/installer-signature-check.ps1) and the desktop app's update check with Updates:Publisher
# (src/SemanticRouter.Desktop/UpdatePublisher.cs) accept only the publisher or Microsoft. Re-signing it keeps that rule
# as it is, instead of trusting every file a third party signs. Files signed by Microsoft keep their signature, by the
# same rule as those two checks: a valid signature whose subject is Microsoft's organization plus a common name. Any
# other signature goes, a broken or untrusted one included.
#
# deploy/package-installer.sh runs it, on Windows, on a copy of the app it packages. It changes only the files it
# lists: their certificate table is cut off the end of the file and the security directory entry and the CheckSum are
# zeroed (vpk recomputes the CheckSum when it signs). A file whose certificate table is not at its end is left alone
# and fails the script. A list item per stripped file goes to $GITHUB_STEP_SUMMARY when it is set. Needs Windows:
# Get-AuthenticodeSignature checks the signatures.
param(
    [Parameter(Mandatory)] [string] $Directory
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue)) {
    Write-Output '::error::deploy/strip-third-party-signatures.ps1 needs Windows (Get-AuthenticodeSignature).'
    exit 1
}

function Report([string] $line) {
    Write-Output $line
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY "- $line" }
}

# A name's parts as oid=VALUE, sorted, so the same names in any order compare equal. The same as Get-Names in
# deploy/installer-signature-check.ps1 and UpdatePublisher.Names: the three must agree on who Microsoft is.
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

function Test-Microsoft([Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $names = Get-Names $certificate.SubjectName
    $withoutCommonName = (($names -split "`n") | Where-Object { -not $_.StartsWith('2.5.4.3=') }) -join "`n"
    return $withoutCommonName -ceq $microsoft
}

# Where a PE image's certificate table is: the offsets of its security directory entry and its CheckSum, and the
# table's own offset and size. $null when the file is not a PE image or has no certificate table.
function Get-CertificateTable([byte[]] $bytes) {
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $null }
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -lt 0x40 -or [long]$pe + 26 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) {
        return $null
    }
    # The optional header follows the 4-byte signature and the 20-byte COFF header; its layout depends on its magic.
    $optional = $pe + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    if ($magic -eq 0x10B) {
        $countAt = 92; $directoriesAt = 96
    } elseif ($magic -eq 0x20B) {
        $countAt = 108; $directoriesAt = 112
    } else {
        return $null
    }
    # The security directory is entry 4, eight bytes each (an address, which here is a file offset, and a size).
    $entry = $optional + $directoriesAt + 4 * 8
    if ([long]$entry + 8 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $optional + $countAt) -lt 5) {
        return $null
    }
    $size = [long][BitConverter]::ToUInt32($bytes, $entry + 4)
    if ($size -eq 0) { return $null }
    return [pscustomobject]@{
        Entry    = $entry
        CheckSum = $optional + 64
        Offset   = [long][BitConverter]::ToUInt32($bytes, $entry)
        Size     = $size
    }
}

$root = (Resolve-Path -LiteralPath $Directory).Path
$failed = $false
$kept = 0
$stripped = 0
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $table = Get-CertificateTable $bytes
    if (-not $table) { continue }

    $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -eq 'Valid' -and (Test-Microsoft $signature.SignerCertificate)) {
        $kept++
        continue
    }
    $who = if ($signature.SignerCertificate) { "signed by $($signature.SignerCertificate.Subject)" } else { 'no signer' }
    $why = "$who, signature $($signature.Status)"

    if ($table.Offset + $table.Size -ne $bytes.Length) {
        Write-Output "::error::${relative}: its certificate table is not at the end of the file ($why); not stripped."
        $failed = $true
        continue
    }
    $without = [byte[]]::new($table.Offset)
    [Array]::Copy($bytes, $without, $table.Offset)
    [Array]::Clear($without, $table.Entry, 8)
    [Array]::Clear($without, $table.CheckSum, 4)
    [IO.File]::WriteAllBytes($file.FullName, $without)

    $after = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($after.Status -ne 'NotSigned') {
        Write-Output "::error::${relative}: still has a signature after stripping it ($($after.Status))."
        $failed = $true
        continue
    }
    $stripped++
    Report "``$relative``: signature removed, so the release signs it ($why)"
}

Write-Output "Third-party signatures: $stripped removed, $kept Microsoft signatures kept."
if ($failed) {
    exit 1
}
exit 0
