<#
.SYNOPSIS
    The EchoForge release pipeline: build, validate, sign, compile, sign, verify, hash, manifest.

.DESCRIPTION
    One ordered pipeline, so a release is produced the same way every time and a signed artifact is
    never modified after it is signed. The order is the point:

      1. Build, test, publish, stage           (scripts\package.ps1, unless -SkipBuild)
      2. Validate the staged package           (scripts\validate-package.ps1)
      3. Sign the payload binaries             (scripts\sign.ps1 over EchoForge's own EXE/DLLs)
      4. Compile the installer                 (scripts\build-installer.ps1)
      5. Sign the installer                    (scripts\sign.ps1 over the .exe)
      6. Verify every signature
      7. Hash the final installer
      8. Write the release manifest            (build\installer\release-manifest.json)

    Signing is real or it is absent; it is never faked. Steps 3 and 5 sign with a certificate that
    must come from the environment (see scripts\sign.ps1). With -Release and no certificate, the
    pipeline still produces the unsigned installer and a manifest that records the block, then exits
    non-zero with:

        AUTHENTICODE RELEASE SIGNING BLOCKED - no distribution code-signing certificate available

    so a release can never be declared successful without signatures. A development run (no -Release)
    produces an unsigned installer and a manifest that says so, and exits 0.

.PARAMETER Release
    Require signing. Without a certificate this reports the block and exits non-zero.

.PARAMETER SkipBuild
    Reuse the already-staged package. For iterating on the installer/manifest, never for a release.
#>
[CmdletBinding()]
param(
    [switch] $Release,
    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$package = Join-Path $repoRoot 'build\package\EchoForge'
$installerDir = Join-Path $repoRoot 'build\installer'
$scratch = Join-Path $repoRoot 'build\release-scratch'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

$blockerMessage = 'AUTHENTICODE RELEASE SIGNING BLOCKED - no distribution code-signing certificate available'

function Step([string] $m) { Write-Host ''; Write-Host "== $m" -ForegroundColor Cyan }
function Fail([string] $m) { Write-Host ''; Write-Host "  $m" -ForegroundColor Red; exit 1 }
function RunScript([string] $script, [string[]] $scriptArgs) {
    # Out-Host so the child's console output goes to the console, not into this function's return
    # value; only the exit code is returned.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @scriptArgs | Out-Host
    return $LASTEXITCODE
}

$signingConfigured =
    (-not [string]::IsNullOrWhiteSpace($env:ECHOFORGE_SIGNING_THUMBPRINT)) -or
    ((-not [string]::IsNullOrWhiteSpace($env:ECHOFORGE_SIGNING_PFX)) -and (Test-Path -LiteralPath $env:ECHOFORGE_SIGNING_PFX))

# -- 1. build ----------------------------------------------------------------------------------
if (-not $SkipBuild) {
    Step 'Build, test, publish, stage'
    if ((RunScript 'package.ps1' @()) -ne 0) { Fail 'the package build failed' }
}
else {
    Step 'Reusing the already-staged package (-SkipBuild)'
    if (-not (Test-Path (Join-Path $package 'package.json'))) { Fail 'no staged package to reuse' }
}

# -- 2. validate -------------------------------------------------------------------------------
Step 'Validate the package'
if ((RunScript 'validate-package.ps1' @('-Package', $package)) -ne 0) { Fail 'the package did not validate' }

$layout = Get-Content (Join-Path $package 'package.json') -Raw | ConvertFrom-Json
$version = ($layout.version -split '\+')[0]

# -- 3. sign the payload binaries --------------------------------------------------------------
Step 'Sign the payload binaries'
$binaries = @(Get-ChildItem $package -File -Include 'EchoForge.*.exe', 'EchoForge.*.dll' -Recurse |
    Select-Object -ExpandProperty FullName)
$binSignStatus = Join-Path $scratch 'sign-binaries.json'
# sign.ps1 collects the file list from its trailing arguments, so the paths go last.
$binSignArgs = @('-StatusOut', $binSignStatus)
if ($Release) { $binSignArgs += '-Release' }
$binSignArgs += $binaries
$binExit = RunScript 'sign.ps1' $binSignArgs
$binariesSigned = ($binExit -eq 0 -and $signingConfigured)
if ($Release -and $binExit -eq 3) {
    # Signing is blocked. Continue to produce the unsigned artifact and manifest, then fail at the end.
    Write-Host '  (continuing to produce an unsigned artifact and a manifest recording the block)' -ForegroundColor Yellow
}
elseif ($binExit -ne 0) {
    Fail 'signing the payload binaries failed'
}

# -- 4. compile the installer ------------------------------------------------------------------
Step 'Compile the installer'
$installerArgs = @('-Package', $package)
if ($binariesSigned) { $installerArgs += '-SignInstaller' }  # sign installer+uninstaller at compile
if ((RunScript 'build-installer.ps1' $installerArgs) -ne 0) { Fail 'the installer did not compile' }

$installer = Join-Path $installerDir "EchoForge-$version-win-x64.exe"
if (-not (Test-Path $installer)) { Fail "the expected installer was not produced at $installer" }

# -- 5. sign the installer ---------------------------------------------------------------------
# When signing is configured, build-installer already signed the installer and its uninstaller via
# iscc's SignTool. This is a belt-and-braces verification pass rather than a second signature.
$installerSignStatus = Join-Path $scratch 'sign-installer.json'
if ($signingConfigured -and -not $binariesSigned) {
    Step 'Sign the installer'
    $args5 = @('-StatusOut', $installerSignStatus)
    if ($Release) { $args5 += '-Release' }
    $args5 += $installer
    if ((RunScript 'sign.ps1' $args5) -ne 0) { Fail 'signing the installer failed' }
}

# -- 6. verify ---------------------------------------------------------------------------------
Step 'Verify signatures'
$installerSig = Get-AuthenticodeSignature $installer
$installerSigned = ($installerSig.Status -eq 'Valid')
if ($signingConfigured) {
    Write-Host "  installer signature: $($installerSig.Status)"
    if (-not $installerSigned) { Fail "the installer signature did not verify: $($installerSig.Status)" }
}
else {
    Write-Host "  installer signature: $($installerSig.Status) (unsigned build)" -ForegroundColor Yellow
}

# -- 7. hash -----------------------------------------------------------------------------------
Step 'Hash the release artifact'
$size = (Get-Item $installer).Length
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "  $hash"

# -- 8. release manifest -----------------------------------------------------------------------
Step 'Write the release manifest'

$manifestPath = Join-Path $repoRoot 'artifacts\manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifestDigest = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

function ArtifactRevision([string] $id) {
    $a = $manifest.artifacts | Where-Object { $_.artifact_id -eq $id } | Select-Object -First 1
    if ($a) { return $a.revision }
    return $null
}

$innoIdentityPath = Join-Path $repoRoot 'build\tools\inno-tool-identity.json'
$inno = if (Test-Path $innoIdentityPath) { Get-Content $innoIdentityPath -Raw | ConvertFrom-Json } else { $null }

$dotnetVersion = (& dotnet --version).Trim()

# Everything below is built with incremental key assignment rather than nested hashtable literals.
# It reads a little longer, but a value that is a command result or a cast never sits inside a
# literal where Windows PowerShell can mis-parse it.

$innoBlock = $null
if ($inno) {
    $innoBlock = [ordered]@{}
    $innoBlock['version'] = $inno.version
    $innoBlock['edition'] = $inno.edition
    $innoBlock['sha256'] = $inno.sha256
    $innoBlock['signer_thumbprint'] = $inno.signer_thumbprint
    $innoBlock['provenance'] = $inno.provenance
    $innoBlock['license'] = $inno.license
}

$pythonRev    = ArtifactRevision 'python.cpython-3-12-13'
$llamaRev     = ArtifactRevision 'summary.llama-cpp-cpu'
$whisperRev   = ArtifactRevision 'runtime.faster-whisper'
$gemmaRev     = ArtifactRevision 'summary.gemma-4-12b-it-qat-q4-0'
$ministralRev = ArtifactRevision 'summary.ministral-3-14b-instruct-2512-q4-k-m'

$installerSignatureStatus = [string]$installerSig.Status
$blocked = [bool]($Release -and -not $signingConfigured)

# Signing status - only non-secret facts: subject and thumbprint are public certificate identity,
# never the key or password.
$signingStatus = [ordered]@{}
$signingStatus['signed'] = [bool]$installerSigned
$signingStatus['binaries_signed'] = [bool]$binariesSigned
$signingStatus['configured'] = [bool]$signingConfigured
$signingStatus['blocked'] = $blocked
$signingStatus['cert_subject'] = $null
$signingStatus['cert_thumbprint'] = $null
$signingStatus['timestamp_url'] = $null
$signingStatus['installer_signature'] = $installerSignatureStatus
if (Test-Path $installerSignStatus) {
    $s = Get-Content $installerSignStatus -Raw | ConvertFrom-Json
    if ($s.signed) {
        $signingStatus['cert_subject'] = $s.cert_subject
        $signingStatus['cert_thumbprint'] = $s.cert_thumbprint
        $signingStatus['timestamp_url'] = $s.timestamp_url
    }
}
elseif ($installerSigned) {
    $signingStatus['cert_subject'] = $installerSig.SignerCertificate.Subject
    $signingStatus['cert_thumbprint'] = $installerSig.SignerCertificate.Thumbprint
}

$installerBlock = [ordered]@{}
$installerBlock['filename'] = [System.IO.Path]::GetFileName($installer)
$installerBlock['size_bytes'] = $size
$installerBlock['sha256'] = $hash

$packageBlock = [ordered]@{}
$packageBlock['version'] = $layout.version
$packageBlock['self_contained'] = [bool]$layout.self_contained
$packageBlock['single_file'] = [bool]$layout.single_file
$packageBlock['trimmed'] = [bool]$layout.trimmed
$packageBlock['file_count'] = @(Get-ChildItem -LiteralPath $package -Recurse -File).Count
$packageBlock['manifest_entries'] = $layout.manifest_entries

$buildToolsBlock = [ordered]@{}
$buildToolsBlock['dotnet_sdk'] = $dotnetVersion
$buildToolsBlock['inno_setup'] = $innoBlock

$artifactManifestBlock = [ordered]@{}
$artifactManifestBlock['schema_version'] = $manifest.schema_version
$artifactManifestBlock['entries'] = $manifest.artifacts.Count
$artifactManifestBlock['sha256'] = $manifestDigest

$runtimesBlock = [ordered]@{}
$runtimesBlock['python_interpreter'] = $pythonRev
$runtimesBlock['llama_cpp'] = $llamaRev
$runtimesBlock['faster_whisper'] = $whisperRev

$defaultModelsBlock = [ordered]@{}
$defaultModelsBlock['speech'] = 'faster-whisper large-v3-turbo (CTranslate2)'
$defaultModelsBlock['summary'] = "Gemma 4 12B IT QAT Q4_0 ($gemmaRev)"

$optionalModelsBlock = [ordered]@{}
$optionalModelsBlock['benchmark'] = "Ministral 3 14B Instruct Q4_K_M ($ministralRev) - never installed by default"

$qualificationBlock = [ordered]@{}
$qualificationBlock['note'] = 'Engineering facts only. Release qualification gate status is in docs/PHASE6_PASS2_REPORT.md; some gates are external blockers (clean VM, no-GPU machine, non-ASCII profile, code-signing certificate, three-hour soak, SmartScreen reputation).'

$manifestOut = [ordered]@{}
$manifestOut['product'] = 'EchoForge'
$manifestOut['version'] = $version
$manifestOut['version_full'] = $layout.version
$manifestOut['architecture'] = 'win-x64'
$manifestOut['generated_utc'] = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$manifestOut['installer'] = $installerBlock
$manifestOut['package'] = $packageBlock
$manifestOut['build_tools'] = $buildToolsBlock
$manifestOut['signing'] = $signingStatus
$manifestOut['artifact_manifest'] = $artifactManifestBlock
$manifestOut['runtimes'] = $runtimesBlock
$manifestOut['default_models'] = $defaultModelsBlock
$manifestOut['optional_models'] = $optionalModelsBlock
$manifestOut['qualification'] = $qualificationBlock

$releaseManifestPath = Join-Path $installerDir 'release-manifest.json'
($manifestOut | ConvertTo-Json -Depth 8) | Set-Content -Path $releaseManifestPath -Encoding utf8

Write-Host ''
Write-Host "  installer  $installer" -ForegroundColor Green
Write-Host ("  size       {0:N0} bytes ({1:N1} MB)" -f $size, ($size / 1MB))
Write-Host "  sha256     $hash"
Write-Host "  manifest   $releaseManifestPath" -ForegroundColor Green
Write-Host "  signing    $(if ($installerSigned) { 'SIGNED' } elseif ($signingConfigured) { 'CONFIGURED but not verified' } else { 'UNSIGNED' })" -ForegroundColor $(if ($installerSigned) { 'Green' } else { 'Yellow' })

# -- release-mode gate -------------------------------------------------------------------------
if ($Release -and -not $installerSigned) {
    Write-Host ''
    Write-Host "  $blockerMessage" -ForegroundColor Red
    Write-Host '  The unsigned installer and the release manifest were still written for inspection,' -ForegroundColor Red
    Write-Host '  but this is not a shippable release.' -ForegroundColor Red
    exit 3
}

exit 0
