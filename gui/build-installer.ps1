<#
.SYNOPSIS
    GoodbyeDPI Turkey GUI için MSI Setup'ı oluşturur.

.DESCRIPTION
    1. Arayüzü .NET 8 çalışma ortamı gömülü tek bir exe olarak yayınlar.
    2. GoodbyeDPI release ZIP'inden ikilileri (x86, x86_64) ve lisansları alır;
       yöntem .cmd dosyalarını bu repodaki windows\ klasöründen kopyalar (fork'taki değişiklikler gelsin).
    3. WiX ile gui\publish\GoodbyeDPI-Turkey-GUI-Setup-<sürüm>.msi üretir.

.EXAMPLE
    .\gui\build-installer.ps1 -GoodbyeDpiZip "$env:USERPROFILE\Downloads\goodbyedpi-0.2.3rc3-turkey.zip"
#>
param(
    [Parameter(Mandatory)]
    [string] $GoodbyeDpiZip,

    [string] $Version = "1.0.0"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $PSScriptRoot 'publish'
$stage = Join-Path $publish 'stage'
$extract = Join-Path $publish 'goodbyedpi-zip'

if (-not (Test-Path $GoodbyeDpiZip)) { throw "ZIP bulunamadı: $GoodbyeDpiZip" }
Remove-Item $stage, $extract -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

Write-Host "==> Arayüz yayınlanıyor (self-contained, tek dosya)"
dotnet publish (Join-Path $PSScriptRoot 'GoodbyeDPI.Gui\GoodbyeDPI.Gui.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=$Version -o $stage
if ($LASTEXITCODE) { throw "dotnet publish başarısız" }

Write-Host "==> GoodbyeDPI dosyaları hazırlanıyor"
Expand-Archive $GoodbyeDpiZip -DestinationPath $extract
foreach ($dir in 'x86', 'x86_64', 'licenses') {
    $source = Join-Path $extract $dir
    if (-not (Test-Path $source)) { throw "ZIP içinde '$dir' klasörü yok" }
    Copy-Item $source (Join-Path $stage $dir) -Recurse
}
Copy-Item (Join-Path $repo 'windows\*.cmd') $stage
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $stage 'licenses\LICENSE-goodbyedpi-turkey-gui.txt')

Write-Host "==> MSI oluşturuluyor"
dotnet build (Join-Path $PSScriptRoot 'installer\GoodbyeDPI.Installer.wixproj') -c Release `
    -p:Version=$Version -p:StageDir=$stage -o $publish
if ($LASTEXITCODE) { throw "MSI derlemesi başarısız" }

$msi = Join-Path $publish "GoodbyeDPI-Turkey-GUI-Setup-$Version.msi"
Move-Item (Join-Path $publish 'tr-TR\GoodbyeDPI-Turkey-GUI-Setup.msi') $msi -Force -ErrorAction SilentlyContinue
if (-not (Test-Path $msi)) { Move-Item (Join-Path $publish 'GoodbyeDPI-Turkey-GUI-Setup.msi') $msi -Force }
Remove-Item $extract -Recurse -Force
Write-Host "==> Hazır: $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
