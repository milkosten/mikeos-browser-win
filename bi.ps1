# Build MikeBrowserSetup.exe inside the Windows VM: publish the app, install Inno Setup,
# compile the installer, copy it back to the SMB share.
$ErrorActionPreference = 'Stop'
$share = '\\host.lan\Data'; if (-not (Test-Path $share)) { $share = '\\20.20.20.1\Data' }
$log = Join-Path $share 'build.log'
function L($m) { "$([DateTime]::Now.ToString('HH:mm:ss')) $m" | Out-File -FilePath $log -Append -Encoding utf8 }
try {
    Set-Content -Path $log -Value "installer build starting" -Encoding utf8
    $root = Join-Path $env:USERPROFILE 'mb'
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $root | Out-Null
    Copy-Item -Recurse -Force (Join-Path $share 'repo\*') $root
    L "source copied"

    # .NET 8 SDK (user-local)
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ins = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $ins -UseBasicParsing
    $dotDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    & $ins -Channel 8.0 -InstallDir $dotDir | Out-Null
    $dn = Join-Path $dotDir 'dotnet.exe'
    L "dotnet $(& $dn --version)"

    & $dn publish (Join-Path $root 'src\MikeBrowserWin\MikeBrowserWin.csproj') -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $root 'publish') 2>&1 | Out-File -FilePath $log -Append -Encoding utf8
    if (-not (Test-Path (Join-Path $root 'publish\MikeBrowser.exe'))) { throw 'publish produced no exe' }
    L "app.exe built"

    # Inno Setup (silent install if missing)
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) {
        $isexe = Join-Path $env:TEMP 'innosetup.exe'
        Invoke-WebRequest 'https://jrsoftware.org/download.php/is.exe' -OutFile $isexe -UseBasicParsing
        Start-Process $isexe -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-' -Wait
        L "Inno Setup installed"
    }
    if (-not (Test-Path $iscc)) { throw "ISCC not found at $iscc" }

    & $iscc '/Qp' (Join-Path $root 'MikeBrowser.iss') 2>&1 | Out-File -FilePath $log -Append -Encoding utf8
    $setup = Get-ChildItem -Recurse -Filter 'MikeBrowserSetup.exe' $root | Select-Object -First 1
    if (-not $setup) { throw 'installer not produced' }
    Copy-Item -Force $setup.FullName (Join-Path $share 'MikeBrowserSetup.exe')
    $mb = [math]::Round((Get-Item (Join-Path $share 'MikeBrowserSetup.exe')).Length / 1MB, 1)
    L "BUILD_OK MikeBrowserSetup.exe = $mb MB"
    'OK' | Out-File -FilePath (Join-Path $share 'build.done') -Encoding ascii
}
catch {
    L "BUILD_FAIL $($_.Exception.Message)"
    'FAIL' | Out-File -FilePath (Join-Path $share 'build.done') -Encoding ascii
}
