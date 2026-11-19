<#
  Builds a Rimlight release: self-contained app → Velopack installer (Setup.exe) + portable zip in .\dist.
  With -Publish it also uploads the release to GitHub, which is what friends' auto-update checks.

  Usage:  .\build.ps1 -Version 1.0.1            # build only
          .\build.ps1 -Version 1.0.1 -Publish   # build + publish to GitHub Releases
  Needs: .NET SDK, vpk (dotnet tool install -g vpk), and for -Publish the GitHub CLI logged in (gh auth login).
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$Publish,
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$dotnet = if (Test-Path "$dotnetDir\dotnet.exe") { "$dotnetDir\dotnet.exe" } else { 'dotnet' }
if (Test-Path "$dotnetDir\dotnet.exe") { $env:DOTNET_ROOT = $dotnetDir }
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
if (-not (Test-Path $vpk)) { $vpk = 'vpk' }
$repo = 'https://github.com/nuralmahtab1-glitch/rimlight'
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$dist = Join-Path $root 'dist'

Write-Host "== Testing"
& $dotnet test (Join-Path $root 'tests\Rimlight.Tests') -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

Write-Host "== Publishing $Version ($Runtime)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\Rimlight\Rimlight.csproj') -c Release -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -o $publishDir -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

# Code signing goes here later (one step), e.g. vpk pack --signParams "..." once a certificate exists.

Write-Host "== Packaging installer"
& $vpk pack --packId Rimlight --packVersion $Version --packDir $publishDir --mainExe Rimlight.exe `
    --packTitle Rimlight --packAuthors 'nuralmahtab1-glitch' --icon (Join-Path $root 'src\Rimlight\Assets\Rimlight.ico') `
    --outputDir $dist --runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

if ($Publish) {
    Write-Host "== Uploading to GitHub Releases"
    $token = & 'C:\Program Files\GitHub CLI\gh.exe' auth token
    & $vpk upload github --repoUrl $repo --token $token --outputDir $dist --publish `
        --releaseName "Rimlight $Version" --tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Upload failed' }
}
Write-Host "Done. Files in $dist"
