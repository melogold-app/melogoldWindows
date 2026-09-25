; Установщик Melogold (docs/PROMPT.md §3): Inno Setup без прав администратора, папка %LOCALAPPDATA%\Programs\Melogold,
; ярлык в «Пуске» с AppUserModelID приложения (имя и значок в системной плашке воспроизведения), протокол melogold://
; в HKCU. Данные пользователя (%LOCALAPPDATA%\Melogold) удаление не трогает без явного согласия.
; Собирает scripts/release.ps1:  ISCC /DAppVersion=0.1.0 /DSourceDir=... /DOutputDir=... /DArch=x64|arm64 Melogold.iss

#ifndef AppVersion
  #error AppVersion is not set
#endif
#ifndef SourceDir
  #error SourceDir is not set
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

#define AppName "Melogold"
#define AppExeName "Melogold.exe"
#define AppUserModelId "Melogold.Melogold"

[Setup]
AppId={{5C3A9E21-7B64-4F0E-9A6B-2D8E4C1F7A93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Melogold
AppPublisherURL=https://github.com/melogold-app/melogoldWindows
AppSupportURL=https://github.com/melogold-app/melogoldWindows/issues
AppUpdatesURL=https://github.com/melogold-app/melogoldWindows/releases
DefaultDirName={localappdata}\Programs\Melogold
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
; Имя без версии: releases/latest/download/Melogold-x64-setup.exe всегда ведёт на последнюю
OutputBaseFilename=Melogold-{#Arch}-setup
SetupIconFile=..\src\Melogold.App\Assets\melogold.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Установщик своей архитектуры: ARM64 — только на ARM, x64 — там, где нет ARM
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible and not arm64
#endif
MinVersion=10.0.19041
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.RemoveData=Удалить и библиотеку Melogold на этом компьютере — Избранное, плейлисты, историю и настройки?%n%nЕсли вы входили в аккаунт, синхронизированное останется на сервере.
english.RemoveData=Also delete the Melogold library on this computer — Favorites, playlists, history and settings?%n%nIf you signed in, what was synced stays on the server.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Старые файлы прошлой версии не мешают новой: папка программы собирается заново
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; AppUserModelID: "{#AppUserModelId}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; Ссылки melogold:// (приложение проверяет регистрацию и при старте)
Root: HKCU; Subkey: "Software\Classes\melogold"; ValueType: string; ValueName: ""; ValueData: "URL:Melogold"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\melogold"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\melogold\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\melogold\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsRunning(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/c tasklist /FI "IMAGENAME eq {#AppExeName}" /NH | find /I "{#AppExeName}" >nul', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ Обновление из приложения: оно закрывается само сразу после запуска установщика — ждём до 10 с, потом закрываем }
procedure StopMelogold();
var
  Attempt, Code: Integer;
begin
  for Attempt := 1 to 40 do
  begin
    if not IsRunning() then Exit;
    Sleep(250);
  end;
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM {#AppExeName} /F >nul 2>nul', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for Attempt := 1 to 20 do
  begin
    if not IsRunning() then Exit;
    Sleep(250);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopMelogold();
  Result := '';
end;

function HasFlag(const Name: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  { Тихая установка из приложения (/RELAUNCH): после неё Melogold запускается снова }
  if (CurStep = ssDone) and WizardSilent and HasFlag('/RELAUNCH') then
    ExecAsOriginalUser(ExpandConstant('{app}\{#AppExeName}'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, Code);
end;

function InitializeUninstall(): Boolean;
begin
  StopMelogold();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\Melogold');
  if not DirExists(DataDir) then Exit;
  { Данные человека не удаляются молча: только по явному «Да», в тихом режиме — никогда }
  if UninstallSilent then Exit;
  if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
