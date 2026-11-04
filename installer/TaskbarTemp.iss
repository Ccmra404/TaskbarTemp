#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{647AB0E5-B0D6-4756-BF1E-33D3138F9394}
AppName=TaskbarTemp
AppVersion={#MyAppVersion}
AppPublisher=Ccmra404
AppPublisherURL=https://github.com/Ccmra404/TaskbarTemp
DefaultDirName={autopf}\TaskbarTemp
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=TaskbarTemp-Setup-{#MyAppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\TaskbarTemp.exe
AppMutex=Global\TaskbarTemp_SingleInstance
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"; InfoAfterFile: "InstallationNotes.zh-CN.txt"
Name: "english"; MessagesFile: "compiler:Default.isl"; InfoAfterFile: "InstallationNotes.en.txt"

[CustomMessages]
chinesesimplified.DesktopIcon=创建桌面快捷方式
english.DesktopIcon=Create a desktop shortcut
chinesesimplified.Autostart=设置开机自启（仅更新由本程序管理或指向本程序的任务）
english.Autostart=Start with Windows (updates only a TaskbarTemp-owned or matching legacy task)
chinesesimplified.Launch=启动 TaskbarTemp
english.Launch=Launch TaskbarTemp
chinesesimplified.StartupFailed=程序已安装，但开机自启设置失败。若存在旧版或其他同名任务，请先确认其属于 TaskbarTemp 后再重试。
english.StartupFailed=The application was installed, but startup configuration failed. Check whether an older or unrelated task already uses the TaskbarTemp name.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
Name: "autostart"; Description: "{cm:Autostart}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "config.ini,*.log,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ManageStartup.ps1"; DestDir: "{autopf}\TaskbarTemp\Support"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\\config.ini"

[Icons]
Name: "{autoprograms}\TaskbarTemp"; Filename: "{app}\TaskbarTemp.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\TaskbarTemp"; Filename: "{app}\TaskbarTemp.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TaskbarTemp.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{autopf}\TaskbarTemp\Support\ManageStartup.ps1"" -Action Remove -ExecutablePath ""{app}\TaskbarTemp.exe"""; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveOwnedTaskbarTempStartup"

[UninstallDelete]
Type: files; Name: "{app}\config.ini"
Type: files; Name: "{localappdata}\TaskbarTemp\run.log"
Type: dirifempty; Name: "{localappdata}\TaskbarTemp"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Args: String;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('autostart') then begin
    Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{autopf}\TaskbarTemp\Support\ManageStartup.ps1') + '" -Action Install -ExecutablePath "' +
      ExpandConstant('{app}\TaskbarTemp.exe') + '"';
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox(ExpandConstant('{cm:StartupFailed}'), mbError, MB_OK);
  end;
end;
