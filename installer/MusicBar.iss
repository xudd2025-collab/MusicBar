#ifndef AppVersion
  #define AppVersion "1.0.11"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\stage\MusicBar"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{774672F7-379B-48D6-BDFE-563C496290BA}
AppName=MusicBar
AppVersion={#AppVersion}
AppVerName=MusicBar {#AppVersion}
AppPublisher=MusicBar contributors
DefaultDirName={localappdata}\Programs\MusicBar
DefaultGroupName=MusicBar
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=MusicBar-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#SourceDir}\LICENSE
UninstallDisplayIcon={app}\MusicBar.exe
CloseApplications=yes
CloseApplicationsFilter=MusicBar.exe
RestartApplications=no
SetupMutex=MusicBarInstaller
VersionInfoVersion={#AppVersion}.0

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\MusicBar.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\MusicBar.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\update-source.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{app}\data"; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\MusicBar"; Filename: "{app}\MusicBar.exe"
Name: "{autodesktop}\MusicBar"; Filename: "{app}\MusicBar.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MusicBar.exe"; Description: "启动 MusicBar"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox('MusicBar 需要 .NET Framework 4.8。请安装后重新运行安装程序。' + #13#10 + 'https://dotnet.microsoft.com/download/dotnet-framework/net48', mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var StartupCommand, Expected: String;
begin
  if CurUninstallStep = usUninstall then begin
    Expected := '"' + ExpandConstant('{app}\MusicBar.exe') + '" --autostart';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MusicBarTaskbarLyrics', StartupCommand) and (CompareText(StartupCommand, Expected) = 0) then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MusicBarTaskbarLyrics');
  end;
end;
