<#
.SYNOPSIS
    Builds the client's installer into artifacts/installer. Needs Windows, .NET SDK 10, Flutter and Inno Setup 6.
.EXAMPLE
    ./installer/build.ps1 -Version 0.2.0
#>
param([string]$Version = '0.2.0')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\installer'
$payload = Join-Path $output 'payload'

function Invoke-Checked([scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

Invoke-Checked { dotnet publish "$root\src\Ambit.Agent.Worker" -c Release -r win-x64 --self-contained true -o "$payload\worker" -p:Version=$Version }
Invoke-Checked { dotnet publish "$root\src\Ambit.Agent.Setup" -c Release -r win-x64 --self-contained true -o "$payload\setup" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none }

Push-Location "$root\src\ambit-agent-desktop"
try { Invoke-Checked { flutter build windows --release --build-name=$Version } } finally { Pop-Location }
Copy-Item "$root\src\ambit-agent-desktop\build\windows\x64\runner\Release" "$payload\desktop" -Recurse

# A clean Windows Server may not have the Visual C++ runtime the Flutter app needs, so it travels next to the app.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$runtime = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath |
    ForEach-Object { Get-ChildItem "$_\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT" -ErrorAction SilentlyContinue } |
    Select-Object -First 1
if (-not $runtime) { throw 'The Visual C++ redistributable files were not found.' }
Copy-Item "$runtime\msvcp140.dll", "$runtime\vcruntime140.dll", "$runtime\vcruntime140_1.dll" "$payload\desktop"

Invoke-Checked { & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /Q "/DPayloadDir=$payload" "/DAppVersion=$Version" "$PSScriptRoot\AmbitOptimaAgent.iss" }
Get-ChildItem $output -Filter *.exe | ForEach-Object { "$($_.FullName) ($([math]::Round($_.Length / 1MB)) MB)" }
