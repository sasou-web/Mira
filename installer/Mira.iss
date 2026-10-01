; Mira installer, compiled with Inno Setup 6 by tools/package.ps1:
;   ISCC.exe /DAppVersion=x.y.z /DSourceDir=<published Mira folder> /DOutputDir=<folder> installer\Mira.iss
; Installs for the current user only, without administrator rights, in %LOCALAPPDATA%\Programs\Mira:
; Mira keeps its data folder beside Mira.exe, which must stay writable. Updates install over it and keep that data.

#ifndef AppVersion
  #error Pass the version with /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error Pass the published Mira folder with /DSourceDir=...
#endif
#ifndef OutputDir
  #define OutputDir "..\dist\packages"
#endif

[Setup]
AppId={{7D5E2C41-9B8A-4F63-A1E7-3C5B9D2F8A64}
AppName=Mira
AppVersion={#AppVersion}
AppVerName=Mira {#AppVersion}
AppPublisher=sasou-web
AppPublisherURL=https://github.com/sasou-web/Mira
AppSupportURL=https://github.com/sasou-web/Mira/issues
AppUpdatesURL=https://github.com/sasou-web/Mira/releases
AppCopyright=Copyright (c) 2026 sasou-web and Mira contributors
DefaultDirName={autopf}\Mira
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Mira-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\Mira.Desktop\Assets\mira.ico
UninstallDisplayIcon={app}\Mira.exe
UninstallDisplayName=Mira
WizardStyle=modern
ShowLanguageDialog=auto
Compression=lzma2/max
SolidCompression=yes
; A running Mira is closed (it saves its progress and stops TorLink) before its files are replaced.
CloseApplications=yes
; Held while Setup runs: an installed Mira started meanwhile waits for it instead of loading half-replaced files.
SetupMutex=MiraSetup
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=Mira
VersionInfoDescription=Mira {#AppVersion} setup

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
french.MiraRegister=Ajout de Mira au menu Démarrer…
english.MiraRegister=Adding Mira to the Start menu…
french.MiraDeleteData=Supprimer aussi les données de Mira (session Jellyfin, préférences, cache, moteur vidéo et journal TorLink) ?%n%nSinon, elles restent dans %1 et servent à la prochaine installation.
english.MiraDeleteData=Also delete Mira's data (Jellyfin session, preferences, cache, video engine and TorLink log)?%n%nOtherwise they stay in %1 for a later installation.
french.MiraEngineGroup=Lecture :
english.MiraEngineGroup=Playback:
french.MiraEngineTask=Télécharger le moteur vidéo mpv (31 Mo), nécessaire à la lecture
english.MiraEngineTask=Download the mpv video engine (31 MB), needed for playback
french.MiraEngineStatus=Téléchargement et vérification du moteur vidéo mpv…
english.MiraEngineStatus=Downloading and checking the mpv video engine…

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Mira fetches the pinned libmpv build itself (checked by SHA-256) into data\mpv; an engine already on the PC is kept.
Name: "mpvengine"; Description: "{cm:MiraEngineTask}"; GroupDescription: "{cm:MiraEngineGroup}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\data,\data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
; Mira writes its own Start menu shortcut and Windows identity, as on its first launch.
Filename: "{app}\Mira.exe"; Parameters: "--register-windows"; StatusMsg: "{cm:MiraRegister}"; Flags: runhidden waituntilterminated
Filename: "{app}\Mira.exe"; Parameters: "--register-windows --shortcut ""{userdesktop}\Mira.lnk"""; Tasks: desktopicon; Flags: runhidden waituntilterminated
; A failed download does not stop Setup: Mira offers the engine again before the first playback.
Filename: "{app}\Mira.exe"; Parameters: "--install-engine"; StatusMsg: "{cm:MiraEngineStatus}"; Tasks: mpvengine; Flags: runhidden waituntilterminated
Filename: "{app}\Mira.exe"; Description: "{cm:LaunchProgram,Mira}"; Flags: nowait postinstall skipifsilent


[Code]
{ Automatic update started by Mira with "Redémarrer" (/SILENT /RELAUNCH=1 /FROM=x.y.z /MIRA=<its Mira.exe>): Mira reopens
  when Setup ends, installed or not (a failed or cancelled Setup restores the previous files), and reports the outcome. }
procedure DeinitializeSetup();
var
  Exe: String;
  Code: Integer;
begin
  if ExpandConstant('{param:RELAUNCH|0}') <> '1' then Exit;
  Exe := ExpandConstant('{param:MIRA|}');
  if (Exe = '') or (CompareText(ExtractFileName(Exe), 'Mira.exe') <> 0) or not FileExists(Exe) then Exit;
  Exec(Exe, '--updated-from "' + ExpandConstant('{param:FROM|0.0.0}') + '"', ExtractFileDir(Exe), SW_SHOWNORMAL, ewNoWait, Code);
end;

{ Removes a shortcut only when it opens this installation: a portable Mira elsewhere keeps its own. }
procedure RemoveShortcutIfOurs(const Path: String);
var
  Shell, Link: Variant;
begin
  if not FileExists(Path) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    if CompareText(Link.TargetPath, ExpandConstant('{app}\Mira.exe')) = 0 then
      DeleteFile(Path);
  except
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Icon, Data: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveShortcutIfOurs(ExpandConstant('{userprograms}\Mira.lnk'));
    RemoveShortcutIfOurs(ExpandConstant('{userdesktop}\Mira.lnk'));
    { The Shell identity is removed only while it still names this installation's icon. }
    if RegQueryStringValue(HKCU, 'Software\Classes\AppUserModelId\Mira.Desktop', 'IconUri', Icon) then
      if Pos(Lowercase(ExpandConstant('{app}\')), Lowercase(Icon)) = 1 then
        RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\Mira.Desktop');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    { Kept unless the user asks otherwise; a silent uninstall never deletes it. }
    Data := ExpandConstant('{app}\data');
    if DirExists(Data) and (not UninstallSilent) then
      if MsgBox(FmtMessage(CustomMessage('MiraDeleteData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
