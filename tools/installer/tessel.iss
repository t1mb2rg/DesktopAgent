; Tessel installer (Inno Setup 6). Built by tools\publish.ps1 -- do not run by hand.
; AppId must stay stable: the updater and the uninstaller key off it.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\release\Tessel-" + AppVersion + "-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\release"
#endif
; Same master icon the application embeds, so installer, shortcuts and
; Programs-and-Features entry all match.
#define IconFile "..\..\src\DesktopAgent.WinUI\Assets\tessel.ico"

[Setup]
AppId={{7C1E5D42-9A63-4F0B-9C1E-2B7A8D4E5F60}
AppName=Tessel
AppVersion={#AppVersion}
AppVerName=Tessel {#AppVersion}
AppPublisher=Tessel
AppComments=桌面有序，工作从容
DefaultDirName={localappdata}\Programs\Tessel
DisableDirPage=no
DefaultGroupName=Tessel
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=Tessel-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#IconFile}
UninstallDisplayName=Tessel
UninstallDisplayIcon={app}\DesktopAgent.WinUI.exe
CloseApplications=yes
RestartApplications=no

[Languages]
; Chinese is bundled with newer Inno Setup releases; fall back to English when
; the compiler does not ship it (older CI images, for example).
#if FileExists(CompilerPath + "\Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Tessel"; Filename: "{app}\DesktopAgent.WinUI.exe"
Name: "{group}\{cm:UninstallProgram,Tessel}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Tessel"; Filename: "{app}\DesktopAgent.WinUI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DesktopAgent.WinUI.exe"; Description: "{cm:LaunchProgram,Tessel}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"