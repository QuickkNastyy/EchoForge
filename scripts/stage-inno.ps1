<#
.SYNOPSIS
    Stages the pinned Inno Setup compiler, verified, without a manual machine-wide install.

.DESCRIPTION
    The installer is built by a specific, pinned version of the Inno Setup compiler, and "whatever
    iscc happens to be on this developer's PATH" is not a build input anyone can reproduce. This
    downloads the exact pinned release from its authoritative upstream, checks it three ways -
    byte length, SHA-256, and the Authenticode signature and signer thumbprint - and then lays it
    down as a *portable* install under build\tools, which touches no registry and needs no
    administrator. Nothing here is committed; build\ is ignored.

    The pinned facts below are the tool's identity. They are what the release manifest records, and
    changing the compiler is a deliberate edit to this file, not an accident of what was installed.

    Inno Setup is free for commercial installer building under its own licence (a modified
    BSD/zlib-style permission grant): "Permission is granted to anyone to use this software for any
    purpose, including commercial applications". The paid "commercial licence" offered on the
    download page buys support and a window of updates; it is not required to use the compiler. The
    licence requires copyright notices to be preserved, which the shipped installer's About text
    and this project's notices do. The retained licence text is staged alongside the compiler as
    license.txt. A distribution lawyer should still confirm this for the shipped product; the fact
    recorded here is what upstream states.

.PARAMETER ToolsDir
    Where to stage. Defaults to build\tools beside the repository.

.PARAMETER Force
    Re-download and re-stage even if a matching compiler is already present.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stage-inno.ps1
#>
[CmdletBinding()]
param(
    [string] $ToolsDir,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -- the pin ---------------------------------------------------------------------------------------
# Inno Setup 7.0.2, x64 edition, from the project's own GitHub releases. The SHA-256 and the signer
# thumbprint were recorded on first download (2026-08-07) and verified against the Authenticode
# signature on the downloaded file.
$Inno = [ordered]@{
    tool               = 'Inno Setup'
    version            = '7.0.2'
    edition            = 'x64'
    file               = 'innosetup-7.0.2-x64.exe'
    url                = 'https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-7.0.2-x64.exe'
    size_bytes         = 17020192
    sha256             = '5ad54ca3def786f8f4212552e54cc6d8d61329e2d24a1cfee0571d42c2684ff1'
    signer_subject     = 'CN=Pyrsys B.V., O=Pyrsys B.V., S=Noord-Holland, C=NL'
    signer_thumbprint  = 'E0AB19C8D38CBF9C44709925122A7A02F8C70CB7'
    license            = 'Inno Setup licence (modified BSD/zlib-style; free for commercial use, copyright notices preserved)'
    provenance         = 'jrsoftware/issrc GitHub release is-7_0_2, Authenticode-signed by Pyrsys B.V. via Sectigo'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ToolsDir) { $ToolsDir = Join-Path $repoRoot 'build\tools' }

$installerPath = Join-Path $ToolsDir $Inno.file
$compilerDir   = Join-Path $ToolsDir ("inno-" + $Inno.version)
$isccPath      = Join-Path $compilerDir 'ISCC.exe'
$identityPath  = Join-Path $ToolsDir 'inno-tool-identity.json'

function Step([string] $m) { Write-Host ''; Write-Host "== $m" -ForegroundColor Cyan }
function Fail([string] $m) { Write-Host ''; Write-Host "  $m" -ForegroundColor Red; exit 1 }

New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

if ((Test-Path $isccPath) -and -not $Force) {
    $existing = (& $isccPath /? 2>&1 | Select-Object -First 1)
    if ($existing -match [regex]::Escape('Inno Setup 7')) {
        Write-Host "Inno Setup already staged at $isccPath" -ForegroundColor Green
        Write-Host "  $existing"
        # Refresh the identity file so the release manifest can always read it.
        $Inno['iscc'] = $isccPath
        $Inno | ConvertTo-Json -Depth 4 | Set-Content -Path $identityPath -Encoding utf8
        exit 0
    }
}

Step "Download $($Inno.file)"

$needDownload = $true
if ((Test-Path $installerPath) -and -not $Force) {
    $have = (Get-FileHash $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($have -eq $Inno.sha256) { $needDownload = $false; Write-Host '  already downloaded and verified' }
}

if ($needDownload) {
    Write-Host "  $($Inno.url)"
    # TLS 1.2 for older Windows PowerShell; harmless on newer.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Inno.url -OutFile $installerPath -UseBasicParsing
}

Step 'Verify the download'

$size = (Get-Item $installerPath).Length
if ($size -ne $Inno.size_bytes) { Fail "size mismatch: expected $($Inno.size_bytes), got $size" }
Write-Host "  size    $size bytes (matches pin)" -ForegroundColor Green

$hash = (Get-FileHash $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $Inno.sha256) { Fail "SHA-256 mismatch:`n    expected $($Inno.sha256)`n    got      $hash" }
Write-Host "  sha256  $hash (matches pin)" -ForegroundColor Green

$sig = Get-AuthenticodeSignature $installerPath
if ($sig.Status -ne 'Valid') { Fail "Authenticode signature is not Valid: $($sig.Status)" }
$thumb = $sig.SignerCertificate.Thumbprint
if ($thumb -ne $Inno.signer_thumbprint) {
    Fail "signer thumbprint mismatch:`n    expected $($Inno.signer_thumbprint)`n    got      $thumb"
}
Write-Host "  signed  $($sig.SignerCertificate.Subject)" -ForegroundColor Green
Write-Host "          thumbprint $thumb (matches pin)" -ForegroundColor Green

Step 'Stage the portable compiler'

if (Test-Path $compilerDir) { Remove-Item $compilerDir -Recurse -Force }

# /PORTABLE=1 lays down a self-contained copy with no registry writes and no uninstall entry, which
# is exactly what a build tool wants: reproducible, removable with the directory, and needing no
# administrator. /VERYSILENT /SP- suppress every prompt.
$logPath = Join-Path $ToolsDir 'inno-install.log'
$installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/PORTABLE=1',
    "/DIR=$compilerDir", "/LOG=$logPath")
$proc = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru
if ($proc.ExitCode -ne 0) { Fail "the portable install exited with $($proc.ExitCode); see $logPath" }
if (-not (Test-Path $isccPath)) { Fail "ISCC.exe was not produced at $isccPath" }

$banner = (& $isccPath /? 2>&1 | Select-Object -First 1)
if ($banner -notmatch [regex]::Escape('Inno Setup 7')) { Fail "unexpected compiler banner: $banner" }

Write-Host "  compiler  $isccPath" -ForegroundColor Green
Write-Host "  $banner" -ForegroundColor Green

# Record the tool identity for the release manifest and for anyone auditing the build.
$Inno['iscc'] = $isccPath
$Inno | ConvertTo-Json -Depth 4 | Set-Content -Path $identityPath -Encoding utf8
Write-Host "  identity  $identityPath"

exit 0
