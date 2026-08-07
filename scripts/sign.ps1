<#
.SYNOPSIS
    Authenticode-signs EchoForge artifacts with a real distribution certificate, or refuses to.

.DESCRIPTION
    Signing is a hook, not a fact. This script signs the files it is given with a code-signing
    certificate that must come from outside the repository, timestamps the signature so it outlives
    the certificate, and verifies the result. What it will not do is invent trust:

      - It never generates a self-signed certificate and calls the output signed.
      - It never reads a certificate or password from the repository. Credentials come only from
        the environment, and the password is never logged.
      - In -Release mode, if no real certificate is configured, it FAILS with a blocker message
        rather than producing an unsigned release. In development mode it warns and leaves the
        files unsigned so local installer builds still work.

    Certificate configuration (choose one), supplied by the environment only:
      ECHOFORGE_SIGNING_THUMBPRINT   - SHA-1 thumbprint of a cert in the CurrentUser\My store
      ECHOFORGE_SIGNING_PFX          - path to a .pfx, with ECHOFORGE_SIGNING_PFX_PASSWORD
    Optional:
      ECHOFORGE_TIMESTAMP_URL        - RFC3161 timestamp authority (default: DigiCert)

.PARAMETER Path
    One or more files to sign.

.PARAMETER Release
    Treat a missing certificate as a hard failure (a release must be signed).

.OUTPUTS
    Writes a one-line status to stdout and returns exit code 0 on success (signed, or unsigned in
    dev mode) / non-zero on a release-mode block or a signing/verification failure. Also emits a
    JSON status object to the path in -StatusOut when given, for the release manifest.
#>
[CmdletBinding()]
param(
    [switch] $Release,
    [string] $StatusOut,
    # The files to sign, given as trailing arguments so the list survives being passed through
    # powershell.exe -File (where a normal array parameter would collapse to its first element).
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$blockerMessage = 'AUTHENTICODE RELEASE SIGNING BLOCKED - no distribution code-signing certificate available'

function Resolve-SignTool {
    if ($env:ECHOFORGE_SIGNTOOL -and (Test-Path $env:ECHOFORGE_SIGNTOOL)) { return $env:ECHOFORGE_SIGNTOOL }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    ) | Where-Object { $_ -and (Test-Path $_) }
    $candidates = foreach ($root in $roots) {
        Get-ChildItem $root -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' }
    }
    $best = $candidates | Sort-Object { $_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if ($best) { return $best.FullName }
    return $null
}

function Write-Status([hashtable] $status) {
    if ($StatusOut) {
        $status | ConvertTo-Json -Depth 4 | Set-Content -Path $StatusOut -Encoding utf8
    }
}

$timestampUrl = if ($env:ECHOFORGE_TIMESTAMP_URL) { $env:ECHOFORGE_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }

# -- is a real certificate configured? ---------------------------------------------------------
$haveThumbprint = -not [string]::IsNullOrWhiteSpace($env:ECHOFORGE_SIGNING_THUMBPRINT)
$havePfx = (-not [string]::IsNullOrWhiteSpace($env:ECHOFORGE_SIGNING_PFX)) -and (Test-Path -LiteralPath $env:ECHOFORGE_SIGNING_PFX)
$configured = $haveThumbprint -or $havePfx

if (-not $configured) {
    Write-Host 'signing: no distribution code-signing certificate is configured.' -ForegroundColor Yellow
    Write-Host '         Set ECHOFORGE_SIGNING_THUMBPRINT or ECHOFORGE_SIGNING_PFX (+ _PASSWORD) to sign.' -ForegroundColor DarkGray
    Write-Status @{ signed = $false; reason = 'no certificate configured'; release = [bool]$Release }
    if ($Release) {
        Write-Host ''
        Write-Host "  $blockerMessage" -ForegroundColor Red
        exit 3
    }
    Write-Host '         Development build: leaving artifacts UNSIGNED.' -ForegroundColor Yellow
    exit 0
}

$signtool = Resolve-SignTool
if (-not $signtool) {
    Write-Status @{ signed = $false; reason = 'signtool.exe not found'; release = [bool]$Release }
    Write-Host '  signtool.exe was not found (install the Windows SDK).' -ForegroundColor Red
    exit 2
}

Write-Host "signing: using $signtool" -ForegroundColor Cyan
Write-Host "         timestamp authority: $timestampUrl"

# Build the common signtool arguments. The password, if any, is passed to signtool but never echoed.
$common = @('/fd', 'sha256', '/tr', $timestampUrl, '/td', 'sha256')
if ($haveThumbprint) {
    $common += @('/sha1', $env:ECHOFORGE_SIGNING_THUMBPRINT)
    Write-Host "         certificate: store CurrentUser\My, thumbprint (redacted)"
}
else {
    $common += @('/f', $env:ECHOFORGE_SIGNING_PFX)
    if (-not [string]::IsNullOrWhiteSpace($env:ECHOFORGE_SIGNING_PFX_PASSWORD)) {
        $common += @('/p', $env:ECHOFORGE_SIGNING_PFX_PASSWORD)
    }
    Write-Host "         certificate: PFX at $($env:ECHOFORGE_SIGNING_PFX) (password redacted)"
}

$signed = New-Object System.Collections.Generic.List[string]
foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file)) { Write-Host "  missing: $file" -ForegroundColor Red; exit 2 }
    Write-Host "  sign  $file"
    & $signtool sign @common $file
    if ($LASTEXITCODE -ne 0) {
        Write-Status @{ signed = $false; reason = "signtool sign failed on $file"; release = [bool]$Release }
        Write-Host "  signing failed on $file" -ForegroundColor Red
        exit 1
    }
    $signed.Add($file)
}

# Verify every signature against the default (production) policy, including the timestamp.
foreach ($file in $signed) {
    & $signtool verify /pa /all $file | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Status @{ signed = $false; reason = "verification failed on $file"; release = [bool]$Release }
        Write-Host "  verification failed on $file" -ForegroundColor Red
        exit 1
    }
}

# Read the subject/thumbprint back from a signed file for the manifest (non-secret facts only).
$subject = ''
$thumb = ''
try {
    $sig = Get-AuthenticodeSignature $signed[0]
    if ($sig.SignerCertificate) {
        $subject = $sig.SignerCertificate.Subject
        $thumb = $sig.SignerCertificate.Thumbprint
    }
}
catch { }

Write-Host "  verified $($signed.Count) file(s)" -ForegroundColor Green
Write-Status @{
    signed          = $true
    files           = $signed.ToArray()
    timestamp_url   = $timestampUrl
    cert_subject    = $subject
    cert_thumbprint = $thumb
    release         = [bool]$Release
}
exit 0
