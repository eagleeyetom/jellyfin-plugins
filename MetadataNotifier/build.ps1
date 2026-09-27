$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = "c:\Users\tomek-nowy\Downloads\jellyfin-plugins\MetadataNotifier" }
$PluginDir = Join-Path $ScriptDir "Jellyfin.Plugin.MetadataNotifier"
$OutputDir = Join-Path $ScriptDir "output"
$PublishOut = Join-Path $PluginDir "publish_out"

Write-Host "=== Building Metadata Notifier Plugin ===" -ForegroundColor Cyan

if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
if (Test-Path $PublishOut) { Remove-Item -Recurse -Force $PublishOut }

Write-Host "Compiling plugin..." -ForegroundColor Yellow
dotnet publish (Join-Path $PluginDir "Jellyfin.Plugin.MetadataNotifier.csproj") -c Release -o $PublishOut

Write-Host "Copying files and creating ZIP..." -ForegroundColor Yellow
$PkgDir = Join-Path $ScriptDir "pkg"
if (Test-Path $PkgDir) { Remove-Item -Recurse -Force $PkgDir }
New-Item -ItemType Directory -Force -Path $PkgDir | Out-Null

Copy-Item (Join-Path $PublishOut "Jellyfin.Plugin.MetadataNotifier.dll") $PkgDir
$PngPath = Join-Path $PluginDir "MetadataNotifier.png"
if (Test-Path $PngPath) { Copy-Item $PngPath $PkgDir }

$Version = (dotnet msbuild (Join-Path $PluginDir "Jellyfin.Plugin.MetadataNotifier.csproj") -getProperty:Version).Trim()
if (-not $Version) { $Version = "1.2.1.0" }

$zipPath = Join-Path $OutputDir "metadatanotifier-$Version.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Get-ChildItem $PkgDir | ForEach-Object { $_.FullName }) -DestinationPath $zipPath

Remove-Item -Recurse -Force $PublishOut
Remove-Item -Recurse -Force $PkgDir

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Output files in: $OutputDir" -ForegroundColor Green
Get-ChildItem $OutputDir
