<#
.SYNOPSIS
  Builds the Windows artifacts: portable zip and the ThixNote setup (custom Avalonia installer).
.EXAMPLE
  ./build/Build-Windows.ps1                                   # local build  -> local-<date>
  ./build/Build-Windows.ps1 -Channel nightly -BuildNumber 42  # dev build    -> nightly-42-<date>
  ./build/Build-Windows.ps1 -Channel release -BuildNumber 42  # main build   -> 1.0.42-<date>
#>
param(
    [ValidateSet('local', 'nightly', 'release')] [string] $Channel = 'local',
    [int] $BuildNumber = 0,
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$date = (Get-Date).ToUniversalTime().ToString('yyyyMMdd')
$majorMinor = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$version = switch ($Channel) {
    'release' { "$majorMinor.$BuildNumber-$date" }
    'nightly' { "nightly-$BuildNumber-$date" }
    default   { "local-$date" }
}
$msbuildVersion = @("-p:QnChannel=$Channel", "-p:QnBuildNumber=$BuildNumber", "-p:QnBuildDate=$date")

$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish/$Runtime"
$payloadZip = Join-Path $artifacts "payload/payload.zip"
Remove-Item $publishDir, (Split-Path $payloadZip) -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publishDir, (Split-Path $payloadZip) | Out-Null

Write-Host "==> ThixNote $version ($Runtime)"

# 1) Application
dotnet publish (Join-Path $root 'src/ThixNote.App') -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    @msbuildVersion -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }

# 2) Portable zip
$portableZip = Join-Path $artifacts "ThixNote-$version-$Runtime-portable.zip"
Remove-Item $portableZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $portableZip
Write-Host "Portable: $portableZip"

# 3) Setup: payload (zipped app) is embedded into the installer executable
Copy-Item $portableZip $payloadZip
$setupDir = Join-Path $artifacts "setup-publish/$Runtime"
Remove-Item $setupDir -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src/ThixNote.Setup') -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    "-p:PayloadPath=$payloadZip" @msbuildVersion -o $setupDir
if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed' }

$setupExe = Join-Path $artifacts "ThixNote-Setup-$version-$Runtime.exe"
Copy-Item (Join-Path $setupDir 'ThixNote-Setup.exe') $setupExe -Force
Write-Host "Setup:    $setupExe"
