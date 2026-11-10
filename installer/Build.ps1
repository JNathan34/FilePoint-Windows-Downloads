#Requires -Version 7.0
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

Push-Location (Split-Path $PSScriptRoot)
try {
    $project = 'src/FilePoint.Windows/FilePoint.Windows.csproj'
    $version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version
    $publishDir = Join-Path $PWD "artifacts/publish/$Architecture"
    $msi = Join-Path $PWD "artifacts/FilePoint-Windows-$Architecture.msi"
    $setup = Join-Path $PWD "artifacts/FilePoint-Setup-$Architecture.exe"
    if ($Architecture -eq 'arm64') {
        $driverName = 'Dokan_ARM64.msi'
        $driverHash = 'F812A5BF11C15BC3910A1F0F53830FF771CEB3C80458F10686B0F45B0DB91D2E'
        $nativeMachine = 43620
    }
    else {
        $driverName = 'Dokan_x64.msi'
        $driverHash = '69FF8CB37BFEC3A75921C85FFD1C6370B50A9EC4ECEF2CF3A009D488DCBF5465'
        $nativeMachine = 34404
    }
    $driverUrl = "https://github.com/dokan-dev/dokany/releases/download/v2.3.1.1000/$driverName"
    $driverPath = Join-Path $PWD "artifacts/dependencies/$driverName"
    if (!(Test-Path -LiteralPath $driverPath)) {
        New-Item -Path (Split-Path $driverPath) -ItemType Directory -Force | Out-Null
        Invoke-WebRequest $driverUrl -OutFile $driverPath
    }
    if ((Get-FileHash -Algorithm SHA256 $driverPath).Hash -ne $driverHash) {
        throw 'The Dokan installer does not match the pinned SHA-256 hash.'
    }

    dotnet tool restore
    dotnet wix extension add WixToolset.BootstrapperApplications.wixext/6.0.2
    dotnet wix extension add WixToolset.Util.wixext/6.0.2
    dotnet publish $project -c Release -r "win-$Architecture" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDir
    dotnet wix build installer/FilePoint.wxs -arch $Architecture -d "PublishDir=$publishDir" -d "AppVersion=$version" -o $msi
    # This MSI always installs per user, which is the layout ICE91 flags for review.
    dotnet wix msi validate $msi -sice ICE91
    dotnet wix build installer/Setup.wxs -arch $Architecture -ext WixToolset.BootstrapperApplications.wixext -ext WixToolset.Util.wixext -d "PublishDir=$publishDir" -d "AppVersion=$version" -d "ClientMsi=$msi" -d "DriverPath=$driverPath" -d "DriverUrl=$driverUrl" -d "NativeMachine=$nativeMachine" -d "Architecture=$Architecture" -o $setup
    Get-FileHash -Algorithm SHA256 $setup, $msi | ForEach-Object {
        "$($_.Hash.ToLowerInvariant())  $(Split-Path $_.Path -Leaf)"
    } | Set-Content (Join-Path $PWD "artifacts/SHA256SUMS-$Architecture.txt") -Encoding ascii
}
finally {
    Pop-Location
}
