; EchoForge installer.
;
; SKELETON. This compiles and produces a working per-user installer, and it is deliberately not
; claimed to be finished: signing, upgrade and downgrade policy, and qualification on a clean
; Windows 11 virtual machine belong to Phase 6 Pass 2. What is here is the layout and the
; decisions that shape it.
;
; Two of those decisions are worth stating plainly.
;
; PrivilegesRequired=lowest. EchoForge records meetings for one person, writes everything it keeps
; under that person's profile, and needs no service, no driver and no machine-wide component. An
; installer that demanded administrator rights would be asking for something it does not use, and
; on a managed machine that is the difference between "install it" and "raise a ticket".
;
; The uninstaller removes the application and nothing else. Recordings, transcripts, summaries,
; downloaded models and the app-local runtime all live under %LOCALAPPDATA%\EchoForge and are left
; exactly where they are. Uninstalling is not a request to destroy somebody's meetings, and a
; reinstall should find everything still there. Removing that data is a separate, explicit choice
; which Pass 2 will offer as an unticked checkbox.
;
; Build with:
;   iscc packaging\inno\EchoForge.iss /DSourceDir=..\..\build\package\EchoForge

#ifndef SourceDir
  #define SourceDir "..\..\build\package\EchoForge"
#endif

#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif

#define AppName "EchoForge"
#define AppPublisher "EchoForge"
#define AppExe "EchoForge.App.exe"

[Setup]
AppId={{7F3C1B84-0C4E-4C1B-9A44-4A9E5F2D6C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per-user, no elevation. See the note at the top.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; x64 only, because every pinned inference artifact is win_amd64. An installer that ran on x86 or
; ARM64 would install an application that cannot transcribe.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\..\build\installer
OutputBaseFilename=EchoForge-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Nothing here is signed yet. Signing, and the SmartScreen reputation that follows it, is Pass 2.
; SignTool=...

LicenseFile=..\..\LICENSE
InfoAfterFile=..\..\third_party\NOTICE.md

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The whole staged package. scripts\package.ps1 has already checked that it contains the runtime,
; WPF, the native SQLite, the worker package, the pinned manifest and the licence texts.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only what the installer itself created. There is deliberately no entry for
; {localappdata}\EchoForge: that is where every recording, transcript, summary and downloaded
; model lives, and uninstalling an application is not a request to destroy them.
Type: filesandordirs; Name: "{app}\runtimes"

[Code]
// A published EchoForge needs nothing on the machine — the .NET runtime is in the package and the
// interpreter is downloaded at setup. So there is no prerequisite check here, and that absence is
// the point rather than an omission.
//
// Pass 2 adds: refusing to install over a newer version, an explicit "also remove my recordings
// and models" choice in the uninstaller, and the signing configuration.
