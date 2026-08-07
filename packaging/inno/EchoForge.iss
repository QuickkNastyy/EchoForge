; EchoForge installer.
;
; A per-user, x64-only Windows installer for a self-contained application. The two decisions that
; shape everything else:
;
; PrivilegesRequired=lowest. EchoForge records meetings for one person, writes everything it keeps
; under that person's profile, needs no service, no driver, and no machine-wide component. An
; installer that demanded administrator rights would be asking for something it does not use, and
; on a managed machine that is the difference between "install it" and "raise a ticket". So the
; application installs under the user's own %LOCALAPPDATA%\Programs, not Program Files.
;
; The uninstaller removes the application and nothing else by default. Recordings, transcripts,
; summaries, downloaded models and the app-local runtime all live under %LOCALAPPDATA%\EchoForge
; and are left exactly where they are, so a reinstall finds them again. Deleting them is a separate,
; twice-confirmed, opt-in choice offered at the end of an interactive uninstall - never the default,
; and never in a silent uninstall.
;
; The installer consumes the validated package that scripts\package.ps1 stages. It does not gather
; files from the source tree. The compile-time guards below refuse to build if that package is
; incomplete, so a half-staged directory fails loudly here rather than shipping a broken install.
;
; Build (unsigned, development):
;   powershell -File scripts\build-installer.ps1
; Build + sign (release): scripts\release.ps1 drives publish -> validate -> sign -> compile -> sign.

#ifndef SourceDir
  #define SourceDir "..\..\build\package\EchoForge"
#endif

; The version is single-sourced from Directory.Build.props (VersionPrefix) and passed in by the
; build scripts as /DAppVersion=. This literal default is only for a bare manual compile and is
; asserted equal to VersionPrefix by an automated test, so the two cannot drift.
#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif

#define AppName "EchoForge"
#define AppPublisher "EchoForge"
#define AppExe "EchoForge.App.exe"

; -- the package must be complete before this compiles ------------------------------------------
; scripts\package.ps1 writes package.json last, after every layout check has passed, so its
; presence is the signal that staging finished. The rest are the load-bearing files an installer
; that "compiled" without them would omit silently.
#if !FileExists(SourceDir + "\package.json")
  #error Package staging is incomplete: package.json is missing. Run scripts\package.ps1 first.
#endif
#if !FileExists(SourceDir + "\" + AppExe)
  #error Package staging is incomplete: EchoForge.App.exe is missing.
#endif
#if !FileExists(SourceDir + "\artifacts\manifest.json")
  #error Package staging is incomplete: artifacts\manifest.json is missing.
#endif
#if !FileExists(SourceDir + "\worker\echoforge_worker\__init__.py")
  #error Package staging is incomplete: the worker package is missing.
#endif
#if !FileExists(SourceDir + "\third_party\NOTICE.md")
  #error Package staging is incomplete: the third-party notice is missing.
#endif
; The .NET runtime and WPF have to be *in the package* (self-contained), not on the machine.
#if !FileExists(SourceDir + "\System.Private.CoreLib.dll")
  #error Package is not self-contained: the .NET runtime is missing from the staged output.
#endif
#if !FileExists(SourceDir + "\PresentationFramework.dll")
  #error Package is not self-contained: WPF is missing from the staged output.
#endif

[Setup]
; AppId is the upgrade identity. It never changes: a new AppId would make an upgrade install a
; second, unrelated copy instead of replacing the first. The doubled leading brace is Inno's escape
; for a literal '{', so the stored AppId is {7F3C1B84-0C4E-4C1B-9A44-4A9E5F2D6C11}.
AppId={{7F3C1B84-0C4E-4C1B-9A44-4A9E5F2D6C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}

; Per-user, no elevation. {autopf} under lowest privileges resolves to %LOCALAPPDATA%\Programs, a
; per-user application directory a standard user can write. User data never lives here.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
; Reuse the previous install directory on upgrade, and let the wizard replace files that are in use
; so upgrading over a running EchoForge works.
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no

; x64 only, because every pinned inference artifact is win_amd64. An installer that ran on x86 or
; ARM64 would install an application that cannot transcribe.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 11 is the supported floor (build 22000). The application is not qualified below it.
MinVersion=10.0.22000

OutputDir=..\..\build\installer
OutputBaseFilename=EchoForge-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}

; Signing. No certificate is embedded here and none may be. When scripts\release.ps1 has real
; signing credentials it registers a sign tool named "echoforge" with iscc (/Sechoforge=...) and
; defines SignInstaller; the directive below then routes both the uninstaller and the installer
; through it. Without it, the build is explicitly unsigned and release mode refuses to ship it.
#ifdef SignInstaller
SignTool=echoforge
SignedUninstaller=yes
#endif

; The third-party notice travels with the install; it is not an end-user licence agreement for
; EchoForge (there is no separate EULA), so it is shown as information rather than a gate.
InfoAfterFile={#SourceDir}\third_party\NOTICE.md

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; The whole staged package, verified complete by the guards above and by scripts\package.ps1.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only what belongs to the application directory. There is deliberately no entry that points at
; {localappdata}\EchoForge: that is where every recording, transcript, summary and downloaded model
; lives. Files the self-contained runtime wrote beside itself (nothing should, but defensively) go
; with the app; user data does not.
Type: filesandordirs; Name: "{app}"

[Code]
// -- version comparison ------------------------------------------------------------------------
// A plain numeric compare of dotted versions. Returns -1, 0, or 1 for a<b, a=b, a>b. Missing
// components read as zero so "0.6" and "0.6.0" compare equal.
function VersionPart(const S: String; Index: Integer): Integer;
var
  parts: TStringList;
begin
  Result := 0;
  parts := TStringList.Create;
  try
    parts.Delimiter := '.';
    parts.StrictDelimiter := True;
    parts.DelimitedText := S;
    if Index < parts.Count then
      Result := StrToIntDef(Trim(parts[Index]), 0);
  finally
    parts.Free;
  end;
end;

function CompareVersion(const A, B: String): Integer;
var
  i, av, bv: Integer;
begin
  Result := 0;
  for i := 0 to 3 do
  begin
    av := VersionPart(A, i);
    bv := VersionPart(B, i);
    if av < bv then begin Result := -1; Exit; end;
    if av > bv then begin Result := 1; Exit; end;
  end;
end;

// -- downgrade policy --------------------------------------------------------------------------
// The previous install records its version through Inno's own per-AppId data store; this reads it
// back. Refusing an unsupported downgrade is the safe default: an older EchoForge can read a data
// root written by a newer one only by luck, and the failure mode is a corrupt-looking library, not
// a clean error. Someone who genuinely means to go back uninstalls the newer version first.
procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'InstalledVersion', '{#AppVersion}');
end;

function InitializeSetup(): Boolean;
var
  previous: String;
begin
  Result := True;
  previous := GetPreviousData('InstalledVersion', '');
  if (previous <> '') and (CompareVersion(previous, '{#AppVersion}') > 0) then
  begin
    if WizardSilent() then
    begin
      Log('Refusing silent downgrade: installed ' + previous + ' is newer than this installer {#AppVersion}.');
    end
    else
    begin
      MsgBox('A newer version of EchoForge (' + previous + ') is already installed.' + #13#10#13#10 +
        'This installer is version {#AppVersion} and will not replace a newer one: a downgrade can ' +
        'leave your sessions and search index in a state the older version cannot read.' + #13#10#13#10 +
        'If you really intend to go back, uninstall the newer version first.', mbCriticalError, MB_OK);
    end;
    Result := False;
  end;
end;

// -- optional, explicit, twice-confirmed data removal ------------------------------------------
// Default uninstall preserves user data. This runs only after the application has been removed,
// only in an interactive uninstall, and only deletes anything after two confirmations that both
// default to "keep". A silent uninstall never reaches it.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  dataRoot: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if UninstallSilent() then
    Exit;

  dataRoot := ExpandConstant('{localappdata}\EchoForge');
  if not DirExists(dataRoot) then
    Exit;

  if MsgBox(
    'EchoForge has been removed.' + #13#10#13#10 +
    'Your recordings, transcripts, summaries and downloaded models are still on this computer:' + #13#10 +
    dataRoot + #13#10#13#10 +
    'Keep them (recommended) so a future reinstall finds your meetings again?' + #13#10#13#10 +
    'Choose Yes to KEEP your data. Choose No to permanently DELETE all of it.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES then
    Exit;

  if MsgBox(
    'Permanently delete every EchoForge recording, transcript, summary and downloaded model in' + #13#10 +
    dataRoot + '?' + #13#10#13#10 +
    'This cannot be undone.',
    mbCriticalError, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(dataRoot, True, True, True);
  end;
end;
