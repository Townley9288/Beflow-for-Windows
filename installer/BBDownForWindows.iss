#define MyAppName "Beflow for Windows"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0.2"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef ChineseMessages
  #define ChineseMessages "compiler:Default.isl"
#endif
#ifndef AppIconFile
  #define AppIconFile "..\src\BBDownForWindows.App\Assets\AppIcon.ico"
#endif

[Setup]
AppId={{E2225E43-74CA-49C7-AB6A-48634C838A7D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Beflow contributors
DefaultDirName={autopf}\Beflow
DefaultGroupName=Beflow
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Beflow-for-Windows-v{#MyAppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UninstallDisplayIcon={app}\Beflow.exe
SetupIconFile={#AppIconFile}
VersionInfoDescription=A Simple Desktop Video Downloader / 基于 BBDown 构建的桌面视频下载器图形界面
VersionInfoProductName=Beflow for Windows
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "{#ChineseMessages}"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\BBDownForWindows.exe"
Type: files; Name: "{app}\BBDownForWindows.dll"
Type: files; Name: "{app}\BBDownForWindows.deps.json"
Type: files; Name: "{app}\BBDownForWindows.runtimeconfig.json"
Type: files; Name: "{app}\BBDownForWindows.pri"
Type: files; Name: "{autoprograms}\BBDown for Windows.lnk"
Type: files; Name: "{autodesktop}\BBDown for Windows.lnk"

[Icons]
Name: "{autoprograms}\Beflow for Windows"; Filename: "{app}\Beflow.exe"
Name: "{autodesktop}\Beflow for Windows"; Filename: "{app}\Beflow.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Beflow.exe"; Description: "启动 Beflow for Windows"; Flags: nowait postinstall skipifsilent

[Code]
function IsRetainedMuiLanguage(const DirectoryName: String): Boolean;
var
  NormalizedName: String;
begin
  NormalizedName := Lowercase(DirectoryName);
  Result := (NormalizedName = 'zh-cn') or
            (NormalizedName = 'zh-tw') or
            (NormalizedName = 'en-us');
end;

procedure RemoveUnsupportedMuiLanguageDirectories;
var
  FindRec: TFindRec;
  DirectoryPath: String;
begin
  if not DirExists(ExpandConstant('{app}')) then Exit;
  if FindFirst(ExpandConstant('{app}\*'), FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (FindRec.Name <> '.') and
           (FindRec.Name <> '..') and
           (not IsRetainedMuiLanguage(FindRec.Name)) then
        begin
          DirectoryPath := AddBackslash(ExpandConstant('{app}')) + FindRec.Name;
          if FileExists(AddBackslash(DirectoryPath) + 'Microsoft.ui.xaml.dll.mui') or
             FileExists(AddBackslash(DirectoryPath) + 'Microsoft.UI.Xaml.Phone.dll.mui') then
          begin
            Log('Removing unsupported WinUI MUI language directory: ' + DirectoryPath);
            DelTree(DirectoryPath, True, True, True);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemoveUnsupportedMuiLanguageDirectories;
end;
