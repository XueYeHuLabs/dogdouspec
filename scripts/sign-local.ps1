[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ExePath = "publish-out/win-x64/dogdouspec.exe",

    [Parameter(Mandatory = $false)]
    [string]$CertSubject = "",

    [Parameter(Mandatory = $false)]
    [string]$CertThumbprint = "",

    [Parameter(Mandatory = $false)]
    [string]$TimestampUrl = "http://timestamp.globalsign.com/tsa/r6advanced1"
)

$ErrorActionPreference = "Stop"

Write-Host "== DogdouSpec Code Signing ==" -ForegroundColor Cyan

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

# 3. Resolve signing certificate (explicit parameter, environment variable, or automatic discovery)
if ([string]::IsNullOrWhiteSpace($CertSubject) -and $env:DOGDOUSPEC_SIGN_SUBJECT) {
    $CertSubject = $env:DOGDOUSPEC_SIGN_SUBJECT
}
if ([string]::IsNullOrWhiteSpace($CertThumbprint) -and $env:DOGDOUSPEC_SIGN_THUMBPRINT) {
    $CertThumbprint = $env:DOGDOUSPEC_SIGN_THUMBPRINT
}
if ([string]::IsNullOrWhiteSpace($TimestampUrl) -and $env:DOGDOUSPEC_SIGN_TIMESTAMP) {
    $TimestampUrl = $env:DOGDOUSPEC_SIGN_TIMESTAMP
}

$signArgs = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/u", "1.3.6.1.5.5.7.3.3")

if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) {
    Write-Host "[OK] Using configured certificate thumbprint: $CertThumbprint"
    $signArgs += @("/sha1", $CertThumbprint)
} elseif (-not [string]::IsNullOrWhiteSpace($CertSubject)) {
    Write-Host "[OK] Using configured certificate subject: $CertSubject"
    $signArgs += @("/n", $CertSubject)
} else {
    # Automatic Discovery: Filter store for valid, unexpired End-Entity Code Signing certificates with private keys
    $validCerts = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.HasPrivateKey -and
            ($_.NotAfter -gt (Get-Date)) -and
            # Must contain Code Signing EKU (1.3.6.1.5.5.7.3.3)
            ($_.Extensions | Where-Object {
                $_.Oid.Value -eq "2.5.29.37" -and
                ($_.EnhancedKeyUsages.Value -contains "1.3.6.1.5.5.7.3.3" -or $_.EnhancedKeyUsages.FriendlyName -contains "Code Signing")
            }) -and
            # Must NOT be a CA certificate (BasicConstraints: IsCA = false)
            (-not ($_.Extensions | Where-Object {
                $_.Oid.Value -eq "2.5.29.19" -and
                $_.CertificateAuthority -eq $true
            }))
        })

    if ($validCerts.Count -eq 0) {
        Write-Warning "No unexpired End-Entity Code Signing certificate with private keys found in CurrentUser/LocalMachine store."
        Write-Host "Falling back to automatic signtool search (/a)..."
        $signArgs += @("/a")
    } else {
        # Prioritize commercial CA-issued certificates over self-signed test certificates
        $chosenCert = $validCerts |
            Sort-Object -Property @{ Expression = { if ($_.Subject -ne $_.Issuer) { 0 } else { 1 } } },
                                  @{ Expression = { $_.NotAfter }; Descending = $true } |
            Select-Object -First 1

        Write-Host "[OK] Auto-detected Code Signing certificate:" -ForegroundColor Green
        Write-Host "     Subject   : $($chosenCert.Subject)"
        Write-Host "     Issuer    : $($chosenCert.Issuer)"
        Write-Host "     Thumbprint: $($chosenCert.Thumbprint)"
        Write-Host "     Expires   : $($chosenCert.NotAfter)"
        $signArgs += @("/sha1", $chosenCert.Thumbprint)
    }
}

$signArgs += (Resolve-Path $ExePath).Path

# 4. Execute SignTool
Write-Host "`nPlease ensure your hardware token (if using USB Key) is inserted and CSP/SafeNet client is running." -ForegroundColor Yellow
Write-Host "Executing signtool (a PIN prompt will pop up if hardware token requires authentication)..."
& $signtool $signArgs
if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed with exit code $LASTEXITCODE"
}

# 5. Verify signature
Write-Host "`nVerifying signature on $ExePath..."
& $signtool verify /pa (Resolve-Path $ExePath).Path
if ($LASTEXITCODE -ne 0) {
    throw "Signature verification failed."
}

Write-Host "`n[SUCCESS] $ExePath has been successfully signed and verified!" -ForegroundColor Green