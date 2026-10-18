# Builds the 32-bit Dark Messiah tap and, with -Install, copies it into the
# game's mm\addons folder together with its plugin .vdf.
param(
    [switch]$Install,
    [string]$Game = 'C:\Program Files (x86)\Steam\steamapps\common\Dark Messiah Might and Magic Single Player'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'out'
New-Item -ItemType Directory -Force $out | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio with x86 C++ tools found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars32.bat'

$src = Join-Path $here 'src\plugin.cpp'
$cmd = "`"$vcvars`" >nul && cl /nologo /O2 /MT /EHa /W3 /LD /Fo`"$out\\`" /Fe`"$out\rustybridge.dll`" `"$src`" /link /NOLOGO user32.lib"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

if ($Install) {
    $addons = Join-Path $Game 'mm\addons'
    New-Item -ItemType Directory -Force $addons | Out-Null
    Copy-Item (Join-Path $out 'rustybridge.dll') $addons -Force
    Copy-Item (Join-Path $here 'rustybridge.vdf') $addons -Force
    Write-Host "Installed to $addons"
}
