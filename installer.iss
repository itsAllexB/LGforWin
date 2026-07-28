; Inno Setup script for LGforWin — produces dist\LGforWin-<version>-setup.exe
;
; The app is unpackaged and self-contained, so "installing" is really just laying the
; publish output down in Program Files and adding shortcuts + an uninstall entry.
; Build it with:  pwsh -File build.ps1 -Configuration Release -Package

#define AppName "LGforWin"
#define AppPublisher "Alex Bolocan"
#define AppUrl "https://github.com/itsAllexB/LGforWin"
#define AppExe "LGforWin.exe"

; -DAppVersion=... is passed by build.ps1 so the version lives in one place (the .csproj).
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif

; Where the Release build put its output.
#ifndef SourceDir
  #define SourceDir "bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
#endif

[Setup]
AppId={{8E6F2A41-2C7B-4D19-9F3E-1B7A5C9D4E20}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE
OutputDir=dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; WinUI 3 / Windows App SDK needs 64-bit Windows 10 1809+.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Per-machine when elevated, per-user otherwise — no forced UAC prompt.
PrivilegesRequiredOverridesAllowed=dialog
; A running instance locks the exe; offer to close it instead of failing.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; No autostart entry here on purpose: a per-machine install runs elevated, so an HKCU Run
; value would land in the installing admin's hive rather than the user's. The app's
; Settings -> "Start with Windows" toggle writes it at runtime, as the right user.

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings live in %LOCALAPPDATA%\LGforWin and are deliberately left behind, so
; reinstalling keeps your TVs and their pairing keys.
Type: filesandordirs; Name: "{app}"
