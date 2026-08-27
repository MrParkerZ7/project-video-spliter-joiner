; Inno Setup recipe for VideoSplitJoiner (release-local / release).
; Builds a per-user installer over the folder produced by packaging/package.ps1 (dist/publish/),
; which already contains the self-contained exe + the bundled ffmpeg/ folder + notices.
;
; Usage:  ISCC.exe /DMyAppVersion=1.0.0 packaging\VideoSplitJoiner.iss
; Output: dist/VideoSplitJoiner-v<version>-setup.exe

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName      "VideoSplitJoiner"
#define MyAppPublisher "MrParkerZ7"
#define MyAppURL       "https://github.com/MrParkerZ7/project-video-spliter-joiner"
#define MyAppExeName   "VideoSplitJoiner.App.exe"

[Setup]
; Stable AppId — never change it, or in-place upgrades become side-by-side installs.
AppId={{7C4E1B92-3A6D-4F58-9E21-D0B5C6A87E43}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}

; Per-user install: no admin elevation required.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; License agreement page, wired to the repo LICENSE (MIT + the bundled-ffmpeg note).
LicenseFile=..\LICENSE
InfoAfterFile=..\THIRD-PARTY-NOTICES.md

OutputDir=..\dist
OutputBaseFilename=VideoSplitJoiner-v{#MyAppVersion}-setup
SetupIconFile=..\src\App\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything package.ps1 staged into dist/publish/ — the self-contained exe plus the
; bundled ffmpeg/ folder (DLLs the preview P/Invokes + ffmpeg.exe/ffprobe.exe the engine shells to).
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
