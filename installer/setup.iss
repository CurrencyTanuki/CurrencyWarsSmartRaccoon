; 货币战争智能狸 0.2.815 安装脚本 (Inno Setup) - framework-dependent 版
#define MyAppName "货币战争智能狸"
#define MyAppVersion "0.2.815"
#define MyAppExeName "CurrencyWarsAssistant.App.exe"
#define MyAppDir "C:\fd"

[Setup]
AppId={{8E2A7C4D-9B41-4F3A-8C6E-2D1B5A9E7F01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=TaskFlowAI
AppPublisherURL=https://taskflowai.cn
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\CodexHandoff-20260802\CurrencyWarsSmartRaccoon-CodexHandoff-20260801\artifacts\installer
OutputBaseFilename=CurrencyWarsSmartRaccoon-{#MyAppVersion}-setup-framework-dependent
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: checkedonce

[Files]
Source: "{#MyAppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// 检查 .NET 8 桌面运行时是否已安装（目录检查，兼容未写注册表的安装方式）；
// 未安装则提示并跳转下载页。
function IsDotNet8DesktopInstalled(): Boolean;
var
  Path: String;
  FindRec: TFindRec;
begin
  Result := False;
  Path := 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App';
  if DirExists(Path) and FindFirst(Path + '\8*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

procedure OpenDotNetDownload();
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function InitializeSetup(): Boolean;
begin
  if not IsDotNet8DesktopInstalled() then
  begin
    if MsgBox('本软件需要 Microsoft .NET 8 桌面运行时才能运行。' + #13#10 +
              '点击"是"前往微软官方下载页安装 .NET 8（约 100MB）。' + #13#10 +
              '安装完成后重新运行本安装包即可。', mbConfirmation, MB_YESNO) = IDYES then
      OpenDotNetDownload();
    Result := False;
  end
  else
    Result := True;
end;
