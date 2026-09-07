param([Parameter(Mandatory = $true)][string]$WorkingDirectory)
$ErrorActionPreference = 'Stop'
function Replace-QueueSource([string]$RelativePath, [string]$Needle, [string]$Replacement, [int]$Count = 1) {
    $Path = Join-Path $WorkingDirectory $RelativePath
    $Content = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $Needle = $Needle.Replace("`r`n", "`n")
    if ([regex]::Matches($Content, [regex]::Escape($Needle)).Count -ne $Count) {
        throw "Pinned queue patch did not match $RelativePath exactly ($Count expected)."
    }
    [IO.File]::WriteAllText($Path, $Content.Replace($Needle, $Replacement.Replace("`r`n", "`n")), [Text.UTF8Encoding]::new($false))
}
Replace-QueueSource 'BBDown/MyOption.cs' '        public string BeflowPipe' "        public bool BeflowQueue { get; set; }`n        public string BeflowPipe"
Replace-QueueSource 'BBDown/CommandLineInvoker.cs' '        private readonly static Option<string> BeflowPipe' @'
        private readonly static Option<bool> BeflowQueue = new(new string[] { "--beflow-queue" }, "Beflow durable queue protocol 2");
        private readonly static Option<string> BeflowPipe
'@
Replace-QueueSource 'BBDown/CommandLineInvoker.cs' '                if (bindingContext.ParseResult.HasOption(BeflowPipe))' @'
                option.BeflowQueue = bindingContext.ParseResult.GetValueForOption(BeflowQueue);
                if (bindingContext.ParseResult.HasOption(BeflowPipe))
'@
Replace-QueueSource 'BBDown/CommandLineInvoker.cs' "                BeflowPipe,`n" "                BeflowPipe,`n                BeflowQueue,`n"
foreach ($Muxed in @('true', 'false')) {
    Replace-QueueSource 'BBDown/Program.cs' "Prepare(myOption.BeflowPipe, p, parsedResult, $Muxed)" "Prepare(myOption.BeflowPipe, p, parsedResult, $Muxed, myOption.BeflowQueue)"
}
Replace-QueueSource 'BBDown/Program.Methods.cs' @'
        {
            if (downloadConfig.MultiThread && !url.Contains("-cmcc-"))
'@ @'
        {
            if (BeflowDownloadSession.Queued)
            {
                await BeflowResumableTransfer.DownloadAsync(url, destPath, downloadConfig);
                BeflowDownloadSession.Stage(video ? "video" : "audio", destPath);
                return;
            }
            if (downloadConfig.MultiThread && !url.Contains("-cmcc-"))
'@
Replace-QueueSource 'BBDown/Program.cs' '                    int code = BBDownMuxer.MuxAV(' "                    BeflowDownloadSession.Stage(`"muxing`", savePath);`n                    int code = BBDownMuxer.MuxAV(" 2
Replace-QueueSource 'BBDown/Program.cs' '                    Log("清理临时文件...");' "                    BeflowDownloadSession.Stage(`"muxed`", savePath);`n                    Log(`"清理临时文件...`");" 2
Replace-QueueSource 'BBDown/Program.cs' '                        LogError("合并失败"); return;' "                        if (myOption.BeflowQueue) throw new IOException(`"封装失败`" );`n                        LogError(`"合并失败`"); return;" 2
Replace-QueueSource 'BBDown/Program.cs' '                    BBDownMuxer.MergeFLV(files, videoPath);' @'
                    if (myOption.BeflowQueue) BeflowResumableTransfer.MergeClips(files, videoPath);
                    else BBDownMuxer.MergeFLV(files, videoPath);
'@
Replace-QueueSource 'BBDown/Program.cs' '                    var files = GetFiles(Path.GetDirectoryName(videoPath)!, ".mp4");' @'
                    var files = myOption.BeflowQueue
                        ? Enumerable.Range(0, clips.Count).Select(i => $"{p.aid}/{p.aid}.P{p.index}.{p.cid}.{i.ToString(pad)}.mp4").ToArray()
                        : GetFiles(Path.GetDirectoryName(videoPath)!, ".mp4");
'@
Replace-QueueSource 'BBDown/BBDownAria2c.cs' '            await RunCommandCodeAsync(ARIA2C,' '            var code = await RunCommandCodeAsync(ARIA2C,'
Replace-QueueSource 'BBDown/BBDownAria2c.cs' @'
        }
    }
}
'@ @'
            if (BeflowDownloadSession.Queued && code != 0) throw new IOException($"aria2c exited with code {code}");
        }
    }
}
'@
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BBDownPatches/BeflowResumableTransfer.cs') -Destination (Join-Path $WorkingDirectory 'BBDown/BeflowResumableTransfer.cs')
Replace-QueueSource 'BBDown/BBDownMuxer.cs' '                    RunExe("ffmpeg", arguments);' @'
                    var mergeCode = RunExe(FFMPEG, arguments);
                    if (BeflowDownloadSession.Queued && mergeCode != 0) throw new IOException($"分段封装失败：{mergeCode}");
'@
Replace-QueueSource 'BBDown/Program.cs' '                            myOption.UseMP4box = true;' @'
                            if (myOption.BeflowQueue) throw new IOException("当前 FFmpeg 不支持所选杜比视界封装，请更新工具");
                            myOption.UseMP4box = true;
'@
