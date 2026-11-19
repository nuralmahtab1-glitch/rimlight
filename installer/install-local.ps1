<#
  Builds Rimlight as a standalone app (includes .NET, no dev tools needed) and installs it for the
  current user: %LOCALAPPDATA%\Programs\Rimlight, plus Start menu and Desktop shortcuts.
  No admin rights needed. Re-run any time to update the installed copy.
  Usage: powershell -ExecutionPolicy Bypass -File installer\install-local.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Rimlight'
$publishDir = Join-Path $root 'artifacts\publish\win-x64'

Write-Host 'Building Rimlight (Release, self-contained)...'
& $dotnet publish (Join-Path $root 'src\Rimlight\Rimlight.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -o $publishDir -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Write-Host 'Closing any running Rimlight...'
Get-Process Rimlight -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Installing to $installDir ..."
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item (Join-Path $publishDir '*') $installDir -Recurse -Force

$exe = Join-Path $installDir 'Rimlight.exe'
$shell = New-Object -ComObject WScript.Shell
foreach ($lnkPath in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Rimlight.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Rimlight.lnk'))) {
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $exe
    $lnk.Arguments = '--settings'
    $lnk.WorkingDirectory = $installDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Rimlight - music-reactive screen edge lighting'
    $lnk.Save()
}

Write-Host 'Starting Rimlight...'
Start-Process $exe -ArgumentList '--settings'
Write-Host 'Done. Rimlight is in your Start menu and on your Desktop.'
