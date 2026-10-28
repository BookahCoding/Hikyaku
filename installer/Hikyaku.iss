; Hikyaku installer (Inno Setup 6). Built by `scripts/package.ps1 -Version vX.Y.Z -Installer` from the same staged files
; as the update zip, so the installer and the zip always contain the same build.
; Per-user: no administrator rights. It installs under %LOCALAPPDATA%\Programs\Hikyaku, a folder the in-app updater can
; write to (it cannot update a copy under Program Files). Settings and sign-in in %APPDATA%\Hikyaku are never touched,
; not even by the uninstaller.
#ifndef AppVersion
  #error Pass /DAppVersion=X.Y.Z /DStageDir=<publish folder> /DOutDir=<output folder> /DSourceRoot=<checkout>
#endif

[Setup]
AppId={{87A53B08-4102-496C-85BD-789AFE96B1D7}
AppName=Hikyaku
AppVersion={#AppVersion}
AppVerName=Hikyaku {#AppVersion}
AppPublisher=Hikyaku Contributors
AppPublisherURL=https://github.com/BookahCoding/Hikyaku
AppSupportURL=https://github.com/BookahCoding/Hikyaku/issues
AppUpdatesURL=https://github.com/BookahCoding/Hikyaku/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Hikyaku
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=Hikyaku-Setup-{#AppVersion}
SetupIconFile={#SourceRoot}\src\Hikyaku.App\Assets\Hikyaku.ico
UninstallDisplayIcon={app}\Hikyaku.exe
UninstallDisplayName=Hikyaku
LicenseFile={#SourceRoot}\LICENSE
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Hikyaku"; Filename: "{app}\Hikyaku.exe"
Name: "{autodesktop}\Hikyaku"; Filename: "{app}\Hikyaku.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Hikyaku.exe"; Description: "{cm:LaunchProgram,Hikyaku}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; In-app updates can add files the installer never listed; remove the whole program folder and the updater's work folder.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\Hikyaku\updates"
