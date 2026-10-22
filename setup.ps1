param([string]$DotNetPath)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source
}
if (!(Test-Path -LiteralPath $DotNetPath) -or !(& $DotNetPath --list-sdks)) {
    throw 'A .NET SDK is required; a runtime alone cannot build WhalePaper. Install a supported SDK or pass -DotNetPath <SDK dotnet.exe>.'
}

foreach ($project in @('src\WhalePaper\WhalePaper.csproj', 'tests\DesktopPlaybackSmoke.csproj')) {
    & $DotNetPath build (Join-Path $root $project) -c Release -v minimal --disable-build-servers "-p:PathMap=$root=/_/WhalePaper"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
Write-Host 'Built the application and desktop test harness. See README.md for packaging and interactive testing.'
