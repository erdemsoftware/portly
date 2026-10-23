; Portly setup (Inno Setup 6) - built by build.ps1
;   ISCC /DAppVersion=1.0.0 /DEdition=win10 /DSourceDir=... /DOutputDir=... portly.iss
; Installs into C:\Portly by default: Portly keeps packages, databases and projects next to the exe,
; so it must not live in "Program Files" (no write access there without admin rights).
;
; Silent install (winget, scripts, company roll-out):
;   Portly-x.y.z-win10-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/DIR="D:\Portly"] [/TASKS="desktopicon"]
;   /SILENT shows only the progress window. /DIR must be a folder the user can write to (not Program Files).
;   Portly is not started after a silent install.
; Uninstall (Settings > Apps, or unins000.exe in the Portly folder):
;   unins000.exe /VERYSILENT /SUPPRESSMSGBOXES            keeps packages, databases, projects and settings
;   unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /PURGE     also deletes them (bin, data, www, usr, backup)
;   An interactive uninstall asks; the default answer keeps your data.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Edition
  #define Edition "win10"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\stage\Portly"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6C1E2B7A-6E43-4C8E-9C59-0B1F8F2A5D11}
AppName=Portly
AppVersion={#AppVersion}
AppVerName=Portly {#AppVersion}
AppPublisher=Erdem Software
AppPublisherURL=https://erdemsoftware.com
AppSupportURL=https://github.com/erdemsoftware/portly/issues
AppUpdatesURL=https://github.com/erdemsoftware/portly/releases
DefaultDirName={sd}\Portly
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#if Edition == "win7"
MinVersion=6.1sp1
#else
MinVersion=10.0
#endif
OutputDir={#OutputDir}
OutputBaseFilename=Portly-{#AppVersion}-{#Edition}-setup
SetupIconFile=..\assets\portly.ico
UninstallDisplayIcon={app}\Portly.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UsePreviousAppDir=yes
DirExistsWarning=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Portly"; Filename: "{app}\Portly.exe"
Name: "{autodesktop}\Portly"; Filename: "{app}\Portly.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Portly.exe"; Description: "{cm:LaunchProgram,Portly}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /IM Portly.exe /F"; Flags: runhidden; RunOnceId: "StopPortly"

[UninstallDelete]
; generated files only - bin\, data\, www\, usr\ and backup\ (your packages, databases, projects, settings) are kept
Type: filesandordirs; Name: "{app}\etc"
Type: filesandordirs; Name: "{app}\tmp"
Type: filesandordirs; Name: "{app}\logs"

[CustomMessages]
en.PurgeAsk=Also delete your packages, databases, projects and settings in%n%n%1%n%nChoose No to keep them (recommended): installing Portly again into the same folder picks them up.
tr.PurgeAsk=Paketleriniz, veritabanlarınız, projeleriniz ve ayarlarınız da silinsin mi?%n%n%1%n%nSaklamak için Hayır'ı seçin (önerilir): Portly aynı klasöre yeniden kurulunca hepsini kullanır.

[Code]
// User data is kept unless /PURGE is given, or the user says Yes in an interactive uninstall.
function WantsPurge(): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/PURGE') = 0 then Result := True;
  if (not Result) and (not UninstallSilent) then
    Result := MsgBox(FmtMessage(CustomMessage('PurgeAsk'), [ExpandConstant('{app}')]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  dirs: TArrayOfString;
  i: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if not WantsPurge() then Exit;
  SetArrayLength(dirs, 5);
  dirs[0] := 'bin'; dirs[1] := 'data'; dirs[2] := 'www'; dirs[3] := 'usr'; dirs[4] := 'backup';
  for i := 0 to GetArrayLength(dirs) - 1 do
    DelTree(ExpandConstant('{app}\') + dirs[i], True, True, True);
  RemoveDir(ExpandConstant('{app}'));
end;
