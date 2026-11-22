$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = "c:\Users\tomek-nowy\Downloads\jellyfin-plugins\BadgeFin" }
$PluginDir = Join-Path $ScriptDir "Jellyfin.Plugin.BadgeFin"
$OutputDir = Join-Path $ScriptDir "output"
$PublishOut = Join-Path $PluginDir "publish_out"

Write-Host "=== Building BadgeFin Plugin ===" -ForegroundColor Cyan

if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
if (Test-Path $PublishOut) { Remove-Item -Recurse -Force $PublishOut }

Write-Host "Compiling plugin..." -ForegroundColor Yellow
dotnet publish (Join-Path $PluginDir "Jellyfin.Plugin.BadgeFin.csproj") -c Release -o $PublishOut

Write-Host "Copying files..." -ForegroundColor Yellow
Copy-Item (Join-Path $PublishOut "*.dll") $OutputDir

$Version = (dotnet msbuild (Join-Path $PluginDir "Jellyfin.Plugin.BadgeFin.csproj") -getProperty:Version).Trim()
if (-not $Version) { $Version = "2.4.0.0" }

$meta = @"
{
    "guid": "7354bd14-00d2-410e-9086-4f2b0e1a300d",
    "name": "BadgeFin",
    "overview": "Adds quality badges (4K, 1080p, etc.) to media posters and thumbnails.",
    "description": "BadgeFin automatically adds quality resolution badges to your media posters and thumbnails. Badges are visible on all clients including web, mobile, TV, and Kodi.",
    "owner": "eagleeyetom",
    "category": "General",
    "version": "$Version",
    "targetAbi": "12.0.0.0"
}
"@
Set-Content -Path (Join-Path $OutputDir "meta.json") -Value $meta -Encoding utf8

Write-Host "Creating ZIP archive..." -ForegroundColor Yellow
$zipPath = Join-Path $OutputDir "badgefin-$Version.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
$filesToZip = (Get-Item (Join-Path $OutputDir "*.dll")).FullName + (Get-Item (Join-Path $OutputDir "meta.json")).FullName
Compress-Archive -Path (Get-ChildItem $OutputDir | ForEach-Object { $_.FullName }) -DestinationPath $zipPath

Remove-Item -Recurse -Force $PublishOut

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Output files in: $OutputDir" -ForegroundColor Green
Get-ChildItem $OutputDir
