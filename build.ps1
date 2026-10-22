param(
    [string]$DotNetPath,
    [string]$InnoSetupCompiler,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\WhalePaper\WhalePaper.csproj"
$buildOutput = Join-Path $root "src\WhalePaper\bin\Release\net48"
$dist = Join-Path $root "dist"
$iscc = $InnoSetupCompiler

# Keep the portable file name in sync with <Version> in the project file.
# Read explicitly as UTF-8: Windows PowerShell 5.1 would otherwise decode the
# BOM-less project file with the ANSI code page and break the XML.
$projectXml = Get-Content -Path $project -Raw -Encoding UTF8
$version = ([xml]$projectXml).Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Cannot read <Version> from $project"
}

# Use an available SDK; a runtime-only dotnet installation cannot build projects.
if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $localSdk = Join-Path (Split-Path -Parent $root) '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) {
        $DotNetPath = $localSdk
    } else {
        $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}
if (!(Test-Path -LiteralPath $DotNetPath) -or !(& $DotNetPath --list-sdks)) {
    throw 'A .NET SDK is required. Pass -DotNetPath <path to SDK dotnet.exe>.'
}

# Do not terminate another installed/portable instance while packaging this checkout.
$outputExe = Join-Path $buildOutput 'WhalePaper.exe'
$running = Get-Process WhalePaper -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $outputExe }
if ($running) {
    throw 'Close this checkout''s WhalePaper.exe before rebuilding.'
}

Write-Host "Building WhalePaper..." -ForegroundColor Cyan
& $DotNetPath build $project -c Release -v minimal --disable-build-servers "-p:PathMap=$root=/_/WhalePaper"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed"
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null

$hashLines = @()
if (!$SkipInstaller) {
    if ([string]::IsNullOrWhiteSpace($iscc)) {
        $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compilerCommand) { $iscc = $compilerCommand.Source }
    }
    if ([string]::IsNullOrWhiteSpace($iscc)) {
        $compilerCandidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $iscc = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($iscc) -or !(Test-Path -LiteralPath $iscc)) {
        throw 'Inno Setup compiler not found. Pass -InnoSetupCompiler <ISCC.exe>, or use -SkipInstaller for a portable package.'
    }

    Write-Host 'Compiling installer...' -ForegroundColor Cyan
    & $iscc (Join-Path $root 'installer\WhalePaper.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }
    $installerPath = Join-Path $dist "WhalePaper-Setup-$version-x64.exe"
    if (!(Test-Path -LiteralPath $installerPath)) { throw "Installer was not produced: $installerPath" }
    $installerHash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256
    $hashLines += "$($installerHash.Hash.ToLowerInvariant())  $(Split-Path $installerPath -Leaf)"
}

# Portable ZIP as a secondary, no-install option.
$portable = Join-Path $dist "WhalePaper-$version-portable-x64.zip"
$portableStage = Join-Path $dist ('portable-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $portableStage -Force | Out-Null
Copy-Item (Join-Path $buildOutput "WhalePaper.exe") $portableStage
Copy-Item (Join-Path $buildOutput "WhalePaper.exe.config") $portableStage
Copy-Item (Join-Path $root "README.txt") $portableStage
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt") $portableStage
Copy-Item (Join-Path $root "WhalePaper-portable.cmd") $portableStage
Copy-Item (Join-Path $root 'LICENSE') $portableStage
Copy-Item -LiteralPath (Join-Path $root 'LICENSES') -Destination (Join-Path $portableStage 'LICENSES') -Recurse
Compress-Archive -Path (Join-Path $portableStage "*") -DestinationPath $portable -Force
$resolvedStage = (Resolve-Path -LiteralPath $portableStage).ProviderPath
$resolvedDist = (Resolve-Path -LiteralPath $dist).ProviderPath.TrimEnd('\') + '\'
if (!$resolvedStage.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to remove a staging folder outside dist.'
}
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
$portableHash = Get-FileHash $portable -Algorithm SHA256
$hashLines += "$($portableHash.Hash.ToLowerInvariant())  $(Split-Path $portable -Leaf)"
Set-Content -LiteralPath (Join-Path $dist 'SHA256.txt') -Value $hashLines -Encoding ASCII

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
if (!$SkipInstaller) { Write-Host "  $installerPath" }
Write-Host "  Portable: $portable"
Write-Host "  SHA256: $($portableHash.Hash.ToLowerInvariant())"
Write-Host "  SHA256 file: $(Join-Path $dist 'SHA256.txt')"
