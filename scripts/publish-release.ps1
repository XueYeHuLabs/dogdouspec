[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [string]$CertSubject = "",

    [Parameter(Mandatory = $false)]
    [string]$CertThumbprint = "",

    [Parameter(Mandatory = $false)]
    [string]$TimestampUrl = "",

    [Parameter(Mandatory = $false)]
    [switch]$SkipSigning,

    [Parameter(Mandatory = $false)]
    [switch]$AutoUpload
)

$ErrorActionPreference = "Stop"

$tag          = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
$cleanVersion = $tag.TrimStart("v")   # e.g. "1.0.1"
$zipName      = "dogdouspec-win-x64.zip"
$winOutDir    = "publish-out/win-x64"

Write-Host "== DogdouSpec Release & Sign Pipeline ($tag) ==" -ForegroundColor Cyan

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot
try {
    # 1. Clean and Publish win-x64 Native AOT (version stamped into the binary)
    Write-Host "`n[Step 1/5] Compiling Native AOT win-x64 binary (Version=$cleanVersion)..." -ForegroundColor Yellow
    if (Test-Path $winOutDir) { Remove-Item $winOutDir -Recurse -Force }
    dotnet publish src/DogdouSpec.Cli/DogdouSpec.Cli.csproj `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
        -p:VersionPrefix=$cleanVersion `
        -o $winOutDir
    if ($LASTEXITCODE -ne 0) { throw "Native AOT compilation failed." }

    # 2. Sign binary unless the caller explicitly accepts an unsigned portable package.
    if ($SkipSigning) {
        Write-Warning "[Step 2/5] Signing explicitly skipped; the release executable is unsigned."
    } else {
        Write-Host "`n[Step 2/5] Signing binary with code signing certificate..." -ForegroundColor Yellow
        $signParams = @{ ExePath = "$winOutDir/dogdouspec.exe" }
        if (-not [string]::IsNullOrWhiteSpace($CertSubject))    { $signParams["CertSubject"]    = $CertSubject }
        if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) { $signParams["CertThumbprint"] = $CertThumbprint }
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl))   { $signParams["TimestampUrl"]   = $TimestampUrl }
        & (Join-Path $PSScriptRoot "sign-local.ps1") @signParams
    }

    # 3. Package and compute SHA256
    Write-Host "`n[Step 3/5] Packaging release zip and computing SHA256..." -ForegroundColor Yellow
    if (Test-Path $zipName) { Remove-Item $zipName -Force }
    Compress-Archive -Path "$winOutDir/dogdouspec.exe" -DestinationPath $zipName
    $hash = (Get-FileHash -Path $zipName -Algorithm SHA256).Hash
    "$hash  $zipName" | Out-File -FilePath "$zipName.sha256" -Encoding ascii
    Write-Host "[OK] Package SHA256: $hash"

    # 4. Update winget manifests
    Write-Host "`n[Step 4/5] Updating winget manifests to $cleanVersion..." -ForegroundColor Yellow
    $manifestDir    = Join-Path $repoRoot "manifests/winget"
    $installerUrl   = "https://github.com/XueYeHuLabs/dogdouspec/releases/download/$tag/dogdouspec-win-x64.zip"

    # Helper: update a single top-level YAML field value in-place (line-based, top-level non-indented fields only).
    function Update-YamlField([string]$FilePath, [string]$Field, [string]$Value) {
        $escapedField = [regex]::Escape($Field)
        $lines = Get-Content $FilePath
        $lines = $lines | ForEach-Object {
            if ($_ -match "^$escapedField\s*:") { "$Field`: $Value" } else { $_ }
        }
        [System.IO.File]::WriteAllLines($FilePath, [string[]]$lines, [System.Text.UTF8Encoding]::new($false))
    }

    # Vixasol.DogdouSpec.yaml
    $versionYaml = Join-Path $manifestDir "Vixasol.DogdouSpec.yaml"
    Update-YamlField $versionYaml "PackageVersion" $cleanVersion
    Write-Host "[OK] Updated $versionYaml"

    # Vixasol.DogdouSpec.locale.en-US.yaml
    $localeYaml = Join-Path $manifestDir "Vixasol.DogdouSpec.locale.en-US.yaml"
    Update-YamlField $localeYaml "PackageVersion" $cleanVersion
    Write-Host "[OK] Updated $localeYaml"

    # Vixasol.DogdouSpec.installer.yaml
    # InstallerUrl and InstallerSha256 are indented inside the Installers list,
    # so handle them with a dedicated line-by-line rewrite.
    $installerYaml  = Join-Path $manifestDir "Vixasol.DogdouSpec.installer.yaml"
    $installerLines = Get-Content $installerYaml
    $installerLines = $installerLines | ForEach-Object {
        if      ($_ -match "^PackageVersion\s*:")    { "PackageVersion: $cleanVersion" }
        elseif  ($_ -match "^(\s+InstallerUrl):")    { "$($Matches[1]): $installerUrl" }
        elseif  ($_ -match "^(\s+InstallerSha256):") { "$($Matches[1]): $hash" }
        else    { $_ }
    }
    [System.IO.File]::WriteAllLines($installerYaml, [string[]]$installerLines, [System.Text.UTF8Encoding]::new($false))
    Write-Host "[OK] Updated $installerYaml"

    # 5. Automated Upload via GitHub CLI (Optional / Automatic)
    if ($AutoUpload) {
        $ghCli = Get-Command "gh" -ErrorAction SilentlyContinue
        if (-not $ghCli) {
            throw "GitHub CLI was requested with -AutoUpload but 'gh' is not available."
        }
        Write-Host "`n[Step 5/5] Automated GitHub Release Upload via GitHub CLI..." -ForegroundColor Yellow
        try {
            $releaseCheck = & gh release view $tag 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "Uploading signed assets to existing release $tag..."
                & gh release upload $tag $zipName "$zipName.sha256" --clobber
            } else {
                Write-Host "Creating release $tag and uploading signed assets..."
                & gh release create $tag $zipName "$zipName.sha256" --title "$tag" --generate-notes
            }
            Write-Host "`n[SUCCESS] Release $tag created/updated and signed assets uploaded automatically!" -ForegroundColor Green
            return
        } catch {
            Write-Warning "Automated gh upload failed: $_"
        }
    }

    # Manual instructions fallback
    Write-Host "`n[Step 5/5] Release Package Ready!" -ForegroundColor Green
    Write-Host "---------------------------------------------------------"
    Write-Host "Artifact : $zipName"
    Write-Host "Checksum : $hash"
    Write-Host "Version  : $cleanVersion"
    Write-Host "`nManifests updated. Remaining manual steps:"
    Write-Host "  1. git tag $tag"
    Write-Host "  2. git push origin $tag"
    Write-Host "  3. gh release create $tag $zipName $zipName.sha256 --title `"$tag`" --generate-notes"
    Write-Host "  4. Submit a PR to https://github.com/microsoft/winget-pkgs with the updated manifests/winget/ files."
    Write-Host "---------------------------------------------------------"
} finally {
    Pop-Location
}
