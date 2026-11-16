param(
    [switch]$NativeOnly,
    [switch]$Package,
    [string]$Version = "2.7"
)
# Builds the Whiteout client runtime (T7InternalWS.dll), embeds it in the launcher and publishes
# a single-file launcher. -Package also creates the release folder and zip on the Desktop.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$native = Join-Path $root "resurge_source"
$launcher = Join-Path $root "resurge_portable_source"
$out = Join-Path $root "_out"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
$props = Join-Path $root "_deps\x64libs.props"

$log = & $msbuild (Join-Path $native "T7InternalWS.vcxproj") /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v145 `
    "/p:SolutionDir=$native\" "/p:OutDir=$native\WhiteoutNativeBuild\" "/p:ForceImportAfterCppProps=$props" /m /v:minimal /nologo
$code = $LASTEXITCODE
$log | Where-Object { $_ -match ' error |-> ' } | Select-Object -First 15 | ForEach-Object { Write-Host $_ }
if ($code -ne 0) { throw "Native build failed" }
Copy-Item (Join-Path $native "WhiteoutNativeBuild\T7InternalWS.dll") (Join-Path $launcher "Assets\WhiteoutGdkRuntime.dll") -Force
if ($NativeOnly) { return }

dotnet publish (Join-Path $launcher "ResurgeLauncherCSharp.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $out
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed" }

if ($Package) {
    $desktop = [Environment]::GetFolderPath("Desktop")
    $name = "Resurge Whiteout $Version Windows x64"
    $folder = Join-Path $desktop $name
    New-Item -ItemType Directory -Force $folder, (Join-Path $folder "Server"), (Join-Path $folder "Licenses") | Out-Null
    Copy-Item (Join-Path $out "Resurge.exe") (Join-Path $folder "Resurge Whiteout.exe") -Force
    Copy-Item (Join-Path $root "README-ar.md") $folder -Force
    Copy-Item (Join-Path $launcher "Assets\whiteout-public.cfg") (Join-Path $folder "Server") -Force
    Copy-Item (Join-Path $root "Licenses\*") (Join-Path $folder "Licenses") -Force
    $zip = "$folder.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$folder\*" -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Packaged $zip"
}
