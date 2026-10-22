; Portly setup (Inno Setup 6) - built by build.ps1
;   ISCC /DAppVersion=1.0.0 /DEdition=win10 /DSourceDir=... /DOutputDir=... portly.iss
; Installs into C:\Portly by default: Portly keeps packages, databases and projects next to the exe,
; so it must not live in "Program Files" (no write access there without admin rights).

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
