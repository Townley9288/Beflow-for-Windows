param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$StrictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$StartInfo = [Diagnostics.ProcessStartInfo]::new()
$StartInfo.FileName = $ExecutablePath
$StartInfo.WorkingDirectory = Split-Path -Parent $ExecutablePath
$StartInfo.UseShellExecute = $false
$StartInfo.CreateNoWindow = $true
$StartInfo.RedirectStandardOutput = $true
$StartInfo.RedirectStandardError = $true
$StartInfo.ArgumentList.Add('--help')
$Probe = [Diagnostics.Process]::new()
$Probe.StartInfo = $StartInfo
$OutputBytes = [IO.MemoryStream]::new()
$ErrorBytes = [IO.MemoryStream]::new()
try {
    if (-not $Probe.Start()) { throw 'Could not start the BBDown output encoding check.' }
    $OutputTask = $Probe.StandardOutput.BaseStream.CopyToAsync($OutputBytes)
    $ErrorTask = $Probe.StandardError.BaseStream.CopyToAsync($ErrorBytes)
    if (-not $Probe.WaitForExit(15000)) {
        $Probe.Kill($true)
        throw 'BBDown output encoding check timed out.'
    }
    $null = $OutputTask.GetAwaiter().GetResult()
    $null = $ErrorTask.GetAwaiter().GetResult()
    # Strict decoding must reject legacy code-page output instead of guessing its encoding.
    $HelpOutput = $StrictUtf8.GetString($OutputBytes.ToArray())
    $null = $StrictUtf8.GetString($ErrorBytes.ToArray())
    if ($Probe.ExitCode -ne 0) { throw "BBDown --help failed with exit code $($Probe.ExitCode)." }
    if (-not $HelpOutput.Contains('下载') -or -not $HelpOutput.Contains('视频')) {
        throw 'BBDown UTF-8 help output is missing the expected Chinese text.'
    }
    Write-Output 'BBDown UTF-8 output check passed.'
}
finally {
    $Probe.Dispose()
    $OutputBytes.Dispose()
    $ErrorBytes.Dispose()
}
