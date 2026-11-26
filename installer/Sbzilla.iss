; Build with: .\build-installer.ps1 -Platform x64 -Version 1.0.0
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{BEBDCE0F-E3DA-4322-8B56-DD45542065AD}
AppName=Sbzilla
AppVersion={#AppVersion}
AppPublisher=judgewest2000
DefaultDirName={localappdata}\Programs\Sbzilla
DefaultGroupName=Sbzilla
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\Sbzilla.exe
OutputDir=..\publish
OutputBaseFilename=Sbzilla-Setup-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
SetupIconFile=..\src\Sbzilla\Assets\AppIcon.ico
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x86compatible
#endif

[Files]
Source: "..\publish\win-{#Arch}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Sbzilla"; Filename: "{app}\Sbzilla.exe"
Name: "{autodesktop}\Sbzilla"; Filename: "{app}\Sbzilla.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\Sbzilla.exe"; Description: "Launch Sbzilla"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Sbzilla.exe"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Registry]
; Remove the Apps & Features entry written by install.ps1 so only this installer's entry remains.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Sbzilla"; Flags: deletekey

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
