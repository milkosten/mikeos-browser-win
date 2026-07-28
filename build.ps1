# Build MikeBrowser (WPF + WebView2) into a single self-contained .exe.
# Run inside the Windows VM (needs the .NET 8 SDK). Produces .\publish\MikeBrowser.exe
$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "src\MikeBrowserWin\MikeBrowserWin.csproj"
$out  = Join-Path $PSScriptRoot "publish"

dotnet publish $proj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o $out

$exe = Join-Path $out "MikeBrowser.exe"
if (Test-Path $exe) { Write-Host "OK -> $exe  ($([math]::Round((Get-Item $exe).Length/1MB,1)) MB)" }
else { throw "build produced no exe" }
