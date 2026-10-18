param([Parameter(Mandatory = $true)][string]$WorkingDirectory)
$ErrorActionPreference = 'Stop'
$Parser = [IO.File]::ReadAllText((Join-Path $WorkingDirectory 'BBDown.Core/Parser.cs'))
$Start = $Parser.IndexOf('private static async Task<string> GetPlayJsonAsync(string encoding,')
$End = $Parser.IndexOf('private static async Task<string> GetPlayJsonAsync(string aid,', $Start)
if ($Start -lt 0 -or $End -lt 0) { throw 'Pinned TV parser method boundaries changed.' }
$Method = $Parser.Substring($Start, $End - $Start)
$Exception = [IO.File]::ReadAllText((Join-Path $WorkingDirectory 'BBDown.Core/BeflowTvSessionException.cs')).Replace('namespace BBDown.Core;', '')
$Source = @'
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static TvRequestTest;
using BBDown.Core;
namespace BBDown.Core { EXCEPTION }
public static class TvRequestTest
{
    public static string Response = "";
    public static Exception Failure;
    public static List<string> Requests = new List<string>();
    public static Task<string> GetWebSourceAsync(string api)
    {
        Requests.Add(api);
        return Failure == null ? Task.FromResult(Response) : Task.FromException<string>(Failure);
    }
    public static void LogDebug(string message, params object[] values) { }
    public static void Log(string message) { }
    public static string GetTimeStamp(bool value) => "0";
    public static string GetSign(string query, bool value) => "test";
}
public static class Config
{
    public static string TOKEN = "", HOST = "api.bilibili.com", AREA = "", COOKIE = "";
}
public static class AppHelper
{
    public static Task<string> DoReqAsync(string aid, string cid, string epId, string qn, bool bangumi, string encoding, string token) => throw new Exception("Unexpected APP request");
}
public static class TvParserTest
{
    METHOD
    public static Task<string> Fetch() => GetPlayJsonAsync("HEVC", "ep:1", "1", "2", "3", true, false, false);
    private static Task<string> GetPlayJsonAsync(string aid, string cid, string epId, string qn) => throw new Exception("Unexpected international request");
    private static string WbiSign(string query) => query;
    private static Regex PlayerJsonRegex() => new Regex("PlayerJson");
}
'@.Replace('EXCEPTION', $Exception).Replace('METHOD', $Method)
Add-Type -TypeDefinition $Source
$Cases = @(
    @{ Body = '{"code":0,"data":{"dash":{"video":[]}}}'; Success = $true },
    @{ Body = '{"code":-352,"message":"风控校验失败"}' },
    @{ Body = '{"code":-101,"message":"账号未登录"}' },
    @{ Body = '{"code":-10403,"message":"大会员专享限制"}' },
    @{ Body = '{"code":61000,"message":"61000"}' },
    @{ Body = '{"data":null}' },
    @{ Body = '{"code":"0"}' },
    @{ Body = '{"code":9999999999999999999}' },
    @{ Body = '[]' },
    @{ Body = '{' },
    @{ Body = ''; HttpError = $true }
)
foreach ($Case in $Cases) {
    [TvRequestTest]::Requests.Clear()
    [TvRequestTest]::Response = $Case.Body
    [TvRequestTest]::Failure = if ($Case.HttpError) { [Net.Http.HttpRequestException]::new('HTTP 412') } else { $null }
    $Failed = $false
    try { $Result = [TvParserTest]::Fetch().GetAwaiter().GetResult() }
    catch {
        $Failed = $true
        $Actual = $_.Exception
        while ($Actual.InnerException) { $Actual = $Actual.InnerException }
        if ($Actual.GetType().Name -ne 'BeflowTvSessionException') { throw }
    }
    if ($Failed -eq [bool]$Case.Success) { throw 'TV response success/failure did not match expected behavior.' }
    if ([TvRequestTest]::Requests.Count -ne 1 -or -not [TvRequestTest]::Requests[0].Contains('aisee.tv/')) {
        throw 'A TV failure unexpectedly retried or requested WEB.'
    }
}
$Program = [IO.File]::ReadAllText((Join-Path $WorkingDirectory 'BBDown/Program.cs'))
if (-not $Program.Contains('if (ex is BeflowTvSessionException) throw;') -or $Program.Contains('myOption.UseTvApi = false;')) {
    throw 'TV automatic retry or implicit WEB switching was not disabled.'
}
Write-Host "BBDown strict TV session checks passed ($($Cases.Count) cases)."
