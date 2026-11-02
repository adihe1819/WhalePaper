param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Video,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(5,120)][int]$Seconds = 10,
    [ValidateRange(1,240)][int]$MaxFps = 240,
    [ValidateRange(0,100)][int]$MinimumLoops = 1,
    [switch]$NoPaint
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).ProviderPath
$Video = (Resolve-Path -LiteralPath $Video).ProviderPath
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
$probeArguments = @('--playback-probe', $Video, '--probe-seconds', $Seconds,
    '--probe-max-fps', $MaxFps, '--quiet', '--data-dir', $OutputDirectory)
if ($NoPaint) { $probeArguments += '--probe-no-paint' }
$quoted = $probeArguments | ForEach-Object { '"' + $_ + '"' }
$probe = Start-Process -FilePath $Executable -ArgumentList $quoted -WindowStyle Hidden -PassThru
if (!$probe.WaitForExit(($Seconds + 30) * 1000)) {
    Stop-Process -Id $probe.Id -Force
    throw 'Playback probe timed out.'
}
if ($probe.ExitCode -ne 0) { throw "Playback probe failed: exit $($probe.ExitCode)" }
$report = Get-Content -LiteralPath (Join-Path $OutputDirectory 'Logs\playback-probe.txt') -Raw -Encoding UTF8
foreach ($evidence in @('pausePassed=True', 'resumePassed=True', 'failure=none')) {
    if (!$report.Contains($evidence)) { throw "Playback evidence missing: $evidence" }
}
if ($report -notmatch 'loops=(\d+)' -or [int]$Matches[1] -lt $MinimumLoops) {
    throw "Expected at least $MinimumLoops observed loop transitions. Use a shorter input or a longer probe."
}
if ($report -notmatch 'bufferMiB=([0-9.]+)' -or
    [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) -gt 512) {
    throw 'RGB buffer memory evidence missing or above 512 MiB.'
}
Write-Output 'PASS real decode-ahead, loop transitions, pause/resume, disposal and bounded RGB buffers'
Write-Output $report
Write-Output 'Frame callback timing does not prove screen refresh or desktop/icon visibility.'
