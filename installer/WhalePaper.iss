; WhalePaper installer
; Build with Inno Setup 6.0.5+ (supply ISCC.exe to build.ps1 or install it separately)

#define MyAppName "鲸鱼壁纸"
#define MyAppVersion "1.0.7"
#define MyAppPublisher "WhalePaper"
#define MyAppExeName "WhalePaper.exe"
#define MyAppId "{{6D6D8C6A-6E1D-4D8E-9F5A-8C7B4A2D1E33}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) 2026 WhalePaper
DefaultDirName={localappdata}\Programs\WhalePaper
DefaultGroupName=WhalePaper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=WhalePaper-Setup-{#MyAppVersion}-x64
SetupIconFile=..\src\WhalePaper\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "..\src\WhalePaper\bin\Release\net48\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\WhalePaper\bin\Release\net48\{#MyAppExeName}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSES\*"; DestDir: "{app}\LICENSES"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  ProbeFile: String;
begin
  Result := True;

  ProbeFile := ExpandConstant('{localappdata}\.whalepaper-write-test');
  if not SaveStringToFile(ProbeFile, 'test', False) then
  begin
    MsgBox(
      '当前安装程序没有写入用户 AppData 的权限，无法完成安装。' + #13#10 + #13#10 +
      '请把安装包复制到桌面，从资源管理器正常双击安装；' + #13#10 +
      '如果仍然失败，可以右键选择“以管理员身份运行”，' + #13#10 +
      '或直接使用便携版 WhalePaper-{#MyAppVersion}-portable-x64.zip。',
      mbError,
      MB_OK);
    Result := False;
    Exit;
  end;
  DeleteFile(ProbeFile);

  if not RegWriteStringValue(
      HKEY_CURRENT_USER,
      'Software\WhalePaper\SetupProbe',
      'X',
      '1') then
  begin
    MsgBox(
      '当前安装程序没有写入当前用户注册表的权限，无法创建卸载信息。' + #13#10 + #13#10 +
      '请在正常用户桌面运行安装包，或右键选择“以管理员身份运行”。',
      mbError,
      MB_OK);
    Result := False;
    Exit;
  end;
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\WhalePaper\SetupProbe', 'X');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  // Close an existing tray instance before replacing files during an upgrade.
  if CurStep = ssInstall then
  begin
    if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
    begin
      Exec(
        ExpandConstant('{app}\{#MyAppExeName}'),
        '--shutdown',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode);
      Sleep(1000);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
    begin
      Exec(
        ExpandConstant('{app}\{#MyAppExeName}'),
        '--shutdown',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode);
      Sleep(1200);
      Exec(
        ExpandConstant('{app}\{#MyAppExeName}'),
        '--restore-wallpaper',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode);
      Sleep(500);
    end;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(
      HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'WhalePaper');
  end;
end;
