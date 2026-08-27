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
    [switch]$AutoUpload
)

$ErrorActionPreference = "Stop"

$tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
$zipName = "dogdouspec-win-x64.zip"
$winOutDir = "publish-out/win-x64"

Write-Host "== DogdouSpec Release & Sign Pipeline ($tag) ==" -ForegroundColor Cyan

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot
try {
    # 1. Clean and Publish win-x64 Native AOT
    Write-Host "`n[Step 1/4] Compiling Native AOT win-x64 binary..." -ForegroundColor Yellow
    if (Test-Path $winOutDir) { Remove-Item $winOutDir -Recurse -Force }
    dotnet publish src/DogdouSpec.Cli/DogdouSpec.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $winOutDir
    if ($LASTEXITCODE -ne 0) { throw "Native AOT compilation failed." }

    # 2. Sign binary
    Write-Host "`n[Step 2/4] Signing binary with code signing certificate..." -ForegroundColor Yellow
    $signParams = @{
        ExePath = "$winOutDir/dogdouspec.exe"
    }
    if (-not [string]::IsNullOrWhiteSpace($CertSubject)) { $signParams["CertSubject"] = $CertSubject }
    if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) { $signParams["CertThumbprint"] = $CertThumbprint }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) { $signParams["TimestampUrl"] = $TimestampUrl }
    & (Join-Path $PSScriptRoot "sign-local.ps1") @signParams

    # 3. Package and compute SHA256
    Write-Host "`n[Step 3/4] Packaging signed zip and computing SHA256..." -ForegroundColor Yellow
    if (Test-Path $zipName) { Remove-Item $zipName -Force }
    Compress-Archive -Path "$winOutDir/dogdouspec.exe" -DestinationPath $zipName
    $hash = (Get-FileHash -Path $zipName -Algorithm SHA256).Hash
    "$hash  $zipName" | Out-File -FilePath "$zipName.sha256" -Encoding ascii
    Write-Host "[OK] Package SHA256: $hash"

    # 4. Automated Upload via GitHub CLI (Optional / Automatic)
    $ghCli = Get-Command "gh" -ErrorAction SilentlyContinue
    if ($AutoUpload -or $ghCli) {
        Write-Host "`n[Step 4/4] Automated GitHub Release Upload via GitHub CLI..." -ForegroundColor Yellow
        try {
            # Check if release exists
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
    Write-Host "`n[Step 4/4] Release Package Ready!" -ForegroundColor Green
    Write-Host "---------------------------------------------------------"
    Write-Host "Artifact: $zipName"
    Write-Host "Checksum: $hash"
    Write-Host "`nTo publish this release to GitHub and trigger WinGet:"
    Write-Host "  1. git tag $tag"
    Write-Host "  2. git push origin $tag"
    Write-Host "  3. gh release create $tag $zipName $zipName.sha256 --title `"$tag`" --generate-notes"
    Write-Host "---------------------------------------------------------"
} finally {
    Pop-Location
}