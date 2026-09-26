param([Parameter(Mandatory = $true)][string]$WorkingDirectory)
$ErrorActionPreference = 'Stop'

# Compile the actual patched aria2 adapter; no Bilibili account or network is needed.
$Source = [IO.File]::ReadAllText((Join-Path $WorkingDirectory 'BBDown/BBDownAria2c.cs'))
$Source += @'

namespace BBDown
{
    internal static class BeflowDownloadSession { internal const bool Queued = true; }
}
namespace BBDown.Core
{
    internal static class Config { internal const string COOKIE = ""; }
}
public static class BeflowMirrorRegression
{
    public static string GetUris(string url, bool useDefaultMirrors)
        => BBDown.BBDownAria2c.BeflowMirrorUris(url, useDefaultMirrors);
}
'@
Add-Type -TypeDefinition $Source

$Mirrors = @(
    'upos-sz-mirrorcoso1.bilivideo.com', 'upos-sz-mirrorali.bilivideo.com',
    'upos-sz-mirrorhw.bilivideo.com', 'upos-sz-mirrorcos.bilivideo.com'
)
$Suffix = '/upgcxcode/123/video.m4s?deadline=123&sign=a%2Bb%2Fc&platform=pc'
$Checked = 0
foreach ($Scheme in @('http', 'https')) {
    $Url = "${Scheme}://$($Mirrors[0])$Suffix"
    $Expanded = [regex]::Matches([BeflowMirrorRegression]::GetUris($Url, $true), '"([^"]+)"')
    if ($Expanded.Count -ne 4) { throw 'Default CDN must use four mirrors.' }
    for ($Index = 0; $Index -lt $Mirrors.Count; $Index++) {
        if ($Expanded[$Index].Groups[1].Value -cne "${Scheme}://$($Mirrors[$Index])$Suffix") {
            throw 'Default CDN expansion changed the signed path or used the wrong mirror.'
        }
    }
    $Checked++

    foreach ($Cdn in ($Mirrors + @('upos-sz-mirroralib.bilivideo.com', 'pcdn.example.test'))) {
        $Url = "${Scheme}://${Cdn}$Suffix"
        if ([BeflowMirrorRegression]::GetUris($Url, $false) -cne "`"$Url`"") {
            throw "Explicit CDN must remain a single unchanged URL: $Cdn"
        }
        $Checked++
        if ($Cdn -ne $Mirrors[0]) {
            if ([BeflowMirrorRegression]::GetUris($Url, $true) -cne "`"$Url`"") {
                throw "A non-default URL must remain unchanged: $Cdn"
            }
            $Checked++
        }
    }
}
Write-Host "BBDown aria2 mirror regression checks passed ($Checked cases)."
