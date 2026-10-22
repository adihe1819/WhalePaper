param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Video,
    [string]$Gif,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).ProviderPath
$Video = (Resolve-Path -LiteralPath $Video).ProviderPath
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath

function Invoke-WhaleProbe([string[]]$ProbeArguments, [string]$DataDirectory) {
    $arguments = @($ProbeArguments) + @('--quiet', '--data-dir', $DataDirectory)
    $quoted = $arguments | ForEach-Object { '"' + $_ + '"' }
    $process = Start-Process -FilePath $Executable -ArgumentList $quoted -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(45000)) {
        Stop-Process -Id $process.Id -Force
        throw 'Probe timed out.'
    }
    return $process.ExitCode
}

$selfData = Join-Path $OutputDirectory 'self'
if ((Invoke-WhaleProbe @('--self-test') $selfData) -ne 0) { throw 'Self-test process failed.' }
$selfReport = Get-Content -LiteralPath (Join-Path $selfData 'Logs\self-test.txt') -Raw -Encoding UTF8
if ($selfReport.Contains('[FAIL]') -or !$selfReport.Contains('Convert/Lock')) {
    throw 'Native Media Foundation self-test failed or was not executed.'
}
Write-Output 'PASS native MF sample/buffer/vtable self-test'

$videoData = Join-Path $OutputDirectory 'video'
if ((Invoke-WhaleProbe @('--media-probe', $Video) $videoData) -ne 0) { throw 'Video probe failed.' }
$videoReport = Get-Content -LiteralPath (Join-Path $videoData 'Logs\media-probe.txt') -Raw -Encoding UTF8
if (!$videoReport.Contains('结果：OK') -or !$videoReport.Contains('已测试')) { throw 'Decode/seek evidence missing.' }
$first = Get-Item -LiteralPath (Join-Path $videoData 'Logs\media-probe\frame-0001.png')
$later = Get-Item -LiteralPath (Join-Path $videoData 'Logs\media-probe\frame-0030.png')
if ($first.Length -eq 0 -or $later.Length -eq 0) { throw 'Decoded PNG is empty.' }
Write-Output 'PASS video decode, exported frames and decode after seek'

if ($Gif) {
    $Gif = (Resolve-Path -LiteralPath $Gif).ProviderPath
    $gifData = Join-Path $OutputDirectory 'gif'
    if ((Invoke-WhaleProbe @('--media-probe', $Gif) $gifData) -ne 0) { throw 'GIF probe failed.' }
    Write-Output 'PASS GIF decode'
}

$invalidPath = Join-Path $OutputDirectory 'invalid.mp4'
[IO.File]::WriteAllText($invalidPath, 'WhalePaper invalid media regression sample')
$invalidData = Join-Path $OutputDirectory 'invalid'
if ((Invoke-WhaleProbe @('--media-probe', $invalidPath) $invalidData) -eq 0) {
    throw 'Invalid media incorrectly returned exit code 0.'
}
$invalidReport = Get-Content -LiteralPath (Join-Path $invalidData 'Logs\media-probe.txt') -Raw -Encoding UTF8
if ($invalidReport.Contains('结果：OK')) { throw 'Invalid media was falsely reported as OK.' }
Write-Output 'PASS invalid media returns nonzero and does not report OK'
