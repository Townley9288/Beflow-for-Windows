#define MyAppName "Beflow for Windows"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
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
VersionInfoDescription=A Simple Desktop Video Downloader / 基于 BBDown 构建的桌面视频下载器图形界面
VersionInfoProductName=Beflow for Windows
SetupLogging=yes

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
