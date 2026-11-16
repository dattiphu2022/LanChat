#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef PublishDir
  #if FileExists("PublishDir.local.iss")
    #include "PublishDir.local.iss"
  #else
    #error Publish LanChat.WinForms once to generate the installer input path.
  #endif
#endif
#define AppExe "LanChat.WinForms.exe"

[Setup]
AppId={{4E443C61-CA96-4788-A06E-3505499B0317}
AppName=LanChat
AppVersion={#AppVersion}
AppPublisher=LanChat
DefaultDirName={localappdata}\Programs\LanChat
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
AppMutex=Global\LanChat.WinForms.3D1D7FFD9E1C47B499752218E7C71FF3
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\artifacts\installer
OutputBaseFilename=LanChat-Setup-Windows-x64-1611
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\LanChat.WinForms\Assets\LanChat.ico

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,default-network.json"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{userprograms}\LanChat"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\LanChat"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "Open LanChat"; Flags: nowait postinstall skipifsilent
