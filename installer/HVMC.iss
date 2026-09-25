#define MyAppName "HVMC School Launcher"
#define MyAppPublisher "Bendemen Studios"
#define MyAppExeName "HVMC School Launcher.exe"

#ifndef AppVersion
  #define AppVersion "0.0"
#endif

[Setup]
AppId={{B8D7F3B7-3E67-4A62-A3A7-0D8F0F5B7E31}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Bendemen Studios\HVMC
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts\HVMC
OutputBaseFilename=HVMC
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\launcher\hvmc.ico
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\HVMCLauncher\HVMC School Launcher.exe"; DestDir: "{app}"; Flags: ignoreversion restartreplace

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Maak een snelkoppeling op het bureaublad"; GroupDescription: "Snelkoppelingen:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "HVMC School Launcher starten"; Flags: nowait postinstall skipifsilent
