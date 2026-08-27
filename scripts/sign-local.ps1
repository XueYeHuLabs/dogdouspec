[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ExePath = "publish-out/win-x64/dogdouspec.exe",

    [Parameter(Mandatory = $false)]
    [string]$CertSubject = "",

    [Parameter(Mandatory = $false)]
    [string]$TimestampUrl = "http://timestamp.globalsign.com/tsa/r6advanced1"
)

$ErrorActionPreference = "Stop"

Write-Host "== DogdouSpec GlobalSign USB Key Code Signing ==" -ForegroundColor Cyan

# 1. Check ExePath
if (-not (Test-Path $ExePath)) {
    throw "Target executable not found at: $ExePath. Please build it first via 'dotnet publish src/DogdouSpec.Cli/DogdouSpec.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-out/win-x64'"
}

# 2. Locate signtool.exe from Windows SDK
$signtool = (Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits" -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*x64*" } | Select-Object -First 1).FullName
if (-not $signtool) {
    $signtool = (Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits" -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
if (-not $signtool) {
    throw "signtool.exe not found. Please ensure Windows 10/11 SDK is installed."
}
Write-Host "[OK] Found SignTool: $signtool"

# 3. Prepare sign arguments
Write-Host "Please ensure your GlobalSign USB Token is inserted and SafeNet Authentication Client is running." -ForegroundColor Yellow

$signArgs = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/a")
if (![string]::IsNullOrWhiteSpace($CertSubject)) {
    $signArgs += @("/n", $CertSubject)
}
$signArgs += (Resolve-Path $ExePath).Path

Write-Host "Executing signtool (a PIN prompt will pop up from SafeNet driver if required)..."
& $signtool $signArgs
if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed with exit code $LASTEXITCODE"
}

# 4. Verify signature
Write-Host "`nVerifying signature on $ExePath..."
& $signtool verify /pa (Resolve-Path $ExePath).Path
if ($LASTEXITCODE -ne 0) {
    throw "Signature verification failed."
}

Write-Host "`n[SUCCESS] $ExePath has been successfully signed with your GlobalSign EV USB Key!" -ForegroundColor Green