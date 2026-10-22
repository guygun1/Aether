# Builds the Aether installer: self-contained publish, then the WiX .msi.
# Output: artifacts\installer\Aether-<version>-x64.msi
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts\publish"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\src\MultiIde\MultiIde.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

dotnet build "$PSScriptRoot\Aether.Installer.wixproj" -c Release -p:ProductVersion=$Version -p:PublishDir=$publish -nologo
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

Get-ChildItem (Join-Path $root "artifacts\installer") -Filter *.msi | Select-Object FullName, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
