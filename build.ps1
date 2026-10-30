# Builds the native helper (ReCap SDK) then the AutoCAD plug-in.
# Output: build\plugin\RcpFlatten.dll (+ build\plugin\cli\RcpFlattenCli.exe and the ReCap DLLs)
#
# Usage:
#   .\build.ps1 -RecapSdkRoot "C:\path\to\ReCapSDK_v27.0.3"
#   (or set the RECAP_SDK_ROOT environment variable)
# Optional: -AutoCadDir "C:\Program Files\Autodesk\AutoCAD 2027\"

param(
    [string]$RecapSdkRoot = $env:RECAP_SDK_ROOT,
    [string]$AutoCadDir = $env:AutoCadDir
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $RecapSdkRoot) { throw 'ReCap SDK not found: pass -RecapSdkRoot or set RECAP_SDK_ROOT.' }

# Locate MSBuild through vswhere (Visual Studio or Build Tools 2022+)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found: install Visual Studio 2022 or its Build Tools.' }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found.' }

& $msbuild "$root\RcpFlattenCli\RcpFlattenCli.vcxproj" /p:Configuration=Release /p:Platform=x64 "/p:RECAP_SDK_ROOT=$RecapSdkRoot" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Native helper build failed' }

$dotnetArgs = @('build', "$root\RcpFlattenPlugin\RcpFlattenPlugin.csproj", '-c', 'Release', '-o', "$root\build\plugin", '--nologo', '-v', 'q')
if ($AutoCadDir) { $dotnetArgs += "/p:AutoCadDir=$AutoCadDir" }
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw 'Plug-in build failed' }
