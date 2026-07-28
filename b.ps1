# Headless build bootstrap — runs inside the Windows VM, driven via Win+R sendkey.
# Reads source from the dockur SMB share, installs the .NET 8 SDK (no admin),
# publishes a single self-contained MikeBrowser.exe, and copies it back to the share.
$ErrorActionPreference = 'Stop'
$share = '\\host.lan\Data'
if (-not (Test-Path $share)) { $share = '\\20.20.20.1\Data' }
$log = Join-Path $share 'build.log'
function L($m) { "$([DateTime]::Now.ToString('HH:mm:ss')) $m" | Out-File -FilePath $log -Append -Encoding utf8 }
try {
    Set-Content -Path $log -Value "build starting" -Encoding utf8
    L "share = $share"
    $root = Join-Path $env:USERPROFILE 'mb'
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $root | Out-Null
    Copy-Item -Recurse -Force (Join-Path $share 'repo\*') $root
    L "source copied to $root"

    # .NET 8 SDK, user-local (no admin, no winget prompts)
    $ins = Join-Path $env:TEMP 'dotnet-install.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $ins -UseBasicParsing
    $dotDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    & $ins -Channel 8.0 -InstallDir $dotDir | Out-Null
    $dn = Join-Path $dotDir 'dotnet.exe'
    L "dotnet $(& $dn --version)"

    $proj = Join-Path $root 'src\MikeBrowserWin\MikeBrowserWin.csproj'
    & $dn publish $proj -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true 2>&1 | Out-File -FilePath $log -Append -Encoding utf8

    $exe = Get-ChildItem -Recurse -Filter 'MikeBrowser.exe' $root |
        Where-Object { $_.FullName -like '*publish*' } | Select-Object -First 1
    if (-not $exe) { throw 'publish produced no MikeBrowser.exe' }
    Copy-Item -Force $exe.FullName (Join-Path $share 'MikeBrowser.exe')
    $sz = [math]::Round((Get-Item (Join-Path $share 'MikeBrowser.exe')).Length / 1MB, 1)
    L "BUILD_OK MikeBrowser.exe = $sz MB"
    'OK' | Out-File -FilePath (Join-Path $share 'build.done') -Encoding ascii
}
catch {
    L "BUILD_FAIL $($_.Exception.Message)"
    'FAIL' | Out-File -FilePath (Join-Path $share 'build.done') -Encoding ascii
}
