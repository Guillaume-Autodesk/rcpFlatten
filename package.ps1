# Builds the release archive Dist\rcpflatten-v<version>-win-x64.zip from the output of build.ps1.
# The Autodesk ReCap SDK DLLs are deliberately NOT included (see THIRD_PARTY_NOTICES.md).
#
# Usage: .\package.ps1 [-Version 0.2.0]

param(
    [string]$Version = '0.2.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$name = "rcpflatten-v$Version-win-x64"
$dist = Join-Path $root 'Dist'
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

$pluginDll = Join-Path $root 'build\plugin\RcpFlatten.dll'
$cliExe = Join-Path $root 'build\cli\RcpFlattenCli.exe'
foreach ($f in @($pluginDll, $cliExe)) {
    if (-not (Test-Path $f)) { throw "Missing ${f}: run build.ps1 first." }
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path "$stage\cli" -Force | Out-Null

Copy-Item $pluginDll $stage
Copy-Item $cliExe "$stage\cli"
Copy-Item (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $stage
(Get-Content (Join-Path $root 'dist-readme.txt') -Raw).Replace('{{VERSION}}', $Version) |
    Set-Content (Join-Path $stage 'README.txt') -Encoding utf8

Compress-Archive -Path $stage -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Write-Host "Created $zip"
Write-Host "SHA256  $hash"
