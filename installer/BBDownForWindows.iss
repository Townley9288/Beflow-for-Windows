#define MyAppName "BBDown for Windows"
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
AppPublisher=BBDown for Windows contributors
DefaultDirName={autopf}\BBDown for Windows
DefaultGroupName=BBDown for Windows
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=BBDown-for-Windows-v{#MyAppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UninstallDisplayIcon={app}\BBDownForWindows.exe
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "{#ChineseMessages}"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\BBDown for Windows"; Filename: "{app}\BBDownForWindows.exe"
Name: "{autodesktop}\BBDown for Windows"; Filename: "{app}\BBDownForWindows.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BBDownForWindows.exe"; Description: "启动 BBDown for Windows"; Flags: nowait postinstall skipifsilent
