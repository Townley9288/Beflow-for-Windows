param([Parameter(Mandatory = $true)][string]$WorkingDirectory)
$ErrorActionPreference = 'Stop'

function Replace-Expected([string]$RelativePath, [string]$Needle, [string]$Replacement) {
    $Path = Join-Path $WorkingDirectory $RelativePath
    $Content = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $Needle = $Needle.Replace("`r`n", "`n")
    $Replacement = $Replacement.Replace("`r`n", "`n")
    if ([regex]::Matches($Content, [regex]::Escape($Needle)).Count -ne 1) {
        throw "The pinned BBDown preparation source changed in $RelativePath; refusing to apply an unverified patch."
    }
    [IO.File]::WriteAllText($Path, $Content.Replace($Needle, $Replacement), [Text.UTF8Encoding]::new($false))
}

Replace-Expected 'BBDown\MyOption.cs' '        public bool Interactive { get; set; }' @'
        public bool Interactive { get; set; }
        public string BeflowPipe { get; set; } = string.Empty;
'@
Replace-Expected 'BBDown\CommandLineInvoker.cs' '        private readonly static Option<bool> Interactive' @'
        private readonly static Option<string> BeflowPipe = new(new string[] { "--beflow-pipe" }, "Beflow download preparation channel");
        private readonly static Option<bool> Interactive
'@
Replace-Expected 'BBDown\CommandLineInvoker.cs' '                if (bindingContext.ParseResult.HasOption(Interactive))' @'
                if (bindingContext.ParseResult.HasOption(BeflowPipe)) option.BeflowPipe = bindingContext.ParseResult.GetValueForOption(BeflowPipe)!;
                if (bindingContext.ParseResult.HasOption(Interactive))
'@
Replace-Expected 'BBDown\CommandLineInvoker.cs' "                Interactive,`n" "                Interactive,`n                BeflowPipe,`n"

$Prepare = @'
                    if (!string.IsNullOrWhiteSpace(myOption.BeflowPipe) && !selected)
                    {
                        var prepared = BeflowDownloadSession.Prepare(myOption.BeflowPipe, p, parsedResult, MUXED);
                        vIndex = prepared.VideoIndex;
                        AUDIO_INDEX
                        savePathFormat = prepared.RelativeOutputPath;
                        myOption.Aria2cArgs = prepared.Aria2Arguments;
                        downloadConfig.Aria2cArgs = prepared.Aria2Arguments;
                        selectedVideoIndex = vIndex;
                        selectedAudioIndex = AUDIO_VALUE;
                        selected = true;
                    }
                    else if (CONDITION)
'@
Replace-Expected 'BBDown\Program.cs' '                    if (myOption.Interactive && !selected)' `
    $Prepare.Replace('MUXED', 'false').Replace('AUDIO_INDEX', 'aIndex = prepared.AudioIndex;').Replace('AUDIO_VALUE', 'aIndex').Replace('CONDITION', 'myOption.Interactive && !selected')
Replace-Expected 'BBDown\Program.cs' '                    if (myOption.Interactive && !flag && !selected)' `
    $Prepare.Replace('MUXED', 'true').Replace('AUDIO_INDEX', '').Replace('AUDIO_VALUE', '-1').Replace('CONDITION', 'myOption.Interactive && !flag && !selected')

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BBDownPatches\BeflowDownloadSession.cs') `
    -Destination (Join-Path $WorkingDirectory 'BBDown\BeflowDownloadSession.cs')
