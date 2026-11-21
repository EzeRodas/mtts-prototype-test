#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "Moon-TTS"
#define MyAppPublisher "EzeRodas"
#define MyAppURL "https://github.com/EzeRodas/Moon-TTS-CS"
#define MyAppExeName "MoonTTS.Avalonia.exe"

[Setup]
AppId={{8F4E7A21-5B9C-4D3E-A1F2-9C8B7D6E5F4A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableWelcomePage=no
OutputDir=dist
OutputBaseFilename=MoonTTS-Setup-{#MyAppVersion}-win-x64
SetupIconFile=MoonTTS.Avalonia\Assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesEnvironment=yes

[Messages]
WelcomeLabel1=Welcome to the {#MyAppName} Setup Wizard
WelcomeLabel2=This installer sets up {#MyAppName}, a lightweight local Text-to-Speech accessibility HUD and CLI, along with its offline neural speech engines.%n%nIt is recommended that you close {#MyAppName} before continuing.

[Components]
Name: "core"; Description: "Moon-TTS Application & CLI"; Types: full compact custom; Flags: fixed
Name: "engines\kokoro"; Description: "Kokoro ONNX Neural Engine"; Types: full compact
Name: "engines\kitten"; Description: "Kitten TTS Lightweight ONNX Engine"; Types: full
Name: "engines\mbrola"; Description: "MBROLA Multilingual Diphone Engine"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start {#MyAppName} automatically when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[InstallDelete]
; Clear cached voices.json files on install/update so updated engines regenerate fresh voice catalogs
Type: files; Name: "{userappdata}\Moon-TTS\engines\Kokoro\voices.json"; Components: engines\kokoro
Type: files; Name: "{userappdata}\Moon-TTS\engines\Kitten\voices.json"; Components: engines\kitten
Type: files; Name: "{userappdata}\Moon-TTS\engines\Mbrola\voices.json"; Components: engines\mbrola

[Files]
; Main GUI (MoonTTS.Avalonia.exe), CLI (mtts.exe), and runtime DLLs
Source: "dist\app\*"; DestDir: "{app}"; Components: core; Excludes: "*.pdb,*.dbg,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

; Engine workers installed to %APPDATA%\Moon-TTS\engines
Source: "dist\engines\Kokoro\*"; DestDir: "{userappdata}\Moon-TTS\engines\Kokoro"; Components: engines\kokoro; Excludes: "*.pdb,*.dbg,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "dist\engines\Kitten\*"; DestDir: "{userappdata}\Moon-TTS\engines\Kitten"; Components: engines\kitten; Excludes: "*.pdb,*.dbg,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "dist\engines\Mbrola\*"; DestDir: "{userappdata}\Moon-TTS\engines\Mbrola"; Components: engines\mbrola; Excludes: "*.pdb,*.dbg,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent


[Code]
const
  UninstallRegKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F4E7A21-5B9C-4D3E-A1F2-9C8B7D6E5F4A}_is1';

var
  MaintenancePage: TInputOptionWizardPage;
  ExistingUninstallExe: string;
  ExitWithoutPrompt: Boolean;

{ Looks up an existing Moon-TTS installation's UninstallString in HKCU or HKLM }
function GetUninstallString(): string;
var
  UninstallPath: string;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallRegKey, 'UninstallString', UninstallPath) then
    Result := RemoveQuotes(UninstallPath)
  else if RegQueryStringValue(HKLM, UninstallRegKey, 'UninstallString', UninstallPath) then
    Result := RemoveQuotes(UninstallPath);
end;

{ Looks up the currently installed Moon-TTS version string, if present }
function GetInstalledVersion(): string;
var
  VersionStr: string;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallRegKey, 'DisplayVersion', VersionStr) then
    Result := VersionStr
  else if RegQueryStringValue(HKLM, UninstallRegKey, 'DisplayVersion', VersionStr) then
    Result := VersionStr;
end;

{ Adds an Update / Uninstall choice page right after Welcome when an existing install is detected }
procedure InitializeWizard();
var
  InstalledVer, SubCaption: string;
begin
  ExitWithoutPrompt := False;
  ExistingUninstallExe := GetUninstallString();

  if ExistingUninstallExe <> '' then
  begin
    InstalledVer := GetInstalledVersion();
    if InstalledVer <> '' then
      SubCaption := '{#MyAppName} (version ' + InstalledVer + ') is already installed on this computer. Select an action below:'
    else
      SubCaption := '{#MyAppName} is already installed on this computer. Select an action below:';

    MaintenancePage := CreateInputOptionPage(
      wpWelcome,
      'Existing Installation Detected',
      'Choose whether to update or uninstall {#MyAppName}.',
      SubCaption,
      True,
      False
    );
    MaintenancePage.Add('Update {#MyAppName} to version {#MyAppVersion} (keeps settings and models; refreshes cached voice lists)');
    MaintenancePage.Add('Uninstall {#MyAppName} from this computer');
    MaintenancePage.SelectedValueIndex := 0;
  end;
end;

{ Launches the existing uninstaller and exits Setup cleanly when the user selects Uninstall }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if (MaintenancePage <> nil) and (CurPageID = MaintenancePage.ID) and (MaintenancePage.SelectedValueIndex = 1) then
  begin
    if FileExists(ExistingUninstallExe) then
    begin
      Exec(ExistingUninstallExe, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
      ExitWithoutPrompt := True;
      WizardForm.Close;
      Result := False;
    end
    else
    begin
      MsgBox('Could not find the uninstaller at:' + #13#10 + ExistingUninstallExe, mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ Suppresses the exit confirmation dialog when closing Setup after launching the uninstaller }
procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if ExitWithoutPrompt then
    Confirm := False;
end;

{ Adds app to the per-user PATH so the mtts CLI resolves from any terminal; idempotent }
function PathContains(const Dir: string): Boolean;
var
  Paths: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Paths) then
    Paths := '';
  Result := Pos(';' + Lowercase(Dir) + ';', ';' + Lowercase(Paths) + ';') > 0;
end;

procedure AddToPath(const Dir: string);
var
  Paths: string;
begin
  if PathContains(Dir) then Exit;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Paths) then
    Paths := '';
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then
    Paths := Paths + ';';
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Paths + Dir);
end;

procedure RemoveFromPath(const Dir: string);
var
  Paths, Wrapped, Needle: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Paths) then Exit;
  Wrapped := ';' + Paths + ';';
  Needle := ';' + Dir + ';';
  P := Pos(Lowercase(Needle), Lowercase(Wrapped));
  if P = 0 then Exit;
  Delete(Wrapped, P, Length(Needle) - 1);
  Paths := Copy(Wrapped, 2, Length(Wrapped) - 2);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    AddToPath(ExpandConstant('{app}'));
end;

{ Cleans up PATH and optionally removes %APPDATA%\Moon-TTS user data on uninstall (works from both Setup and Windows Settings) }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}'));

    if not UninstallSilent then
    begin
      UserDataDir := ExpandConstant('{userappdata}\Moon-TTS');
      if DirExists(UserDataDir) then
      begin
        if MsgBox(
          'Do you also want to remove all {#MyAppName} user data?' + #13#10#13#10 +
          'This includes your settings, phonetic dictionary, hotkey profiles, synthesis history, logs, and downloaded TTS models in:' + #13#10 +
          UserDataDir,
          mbConfirmation,
          MB_YESNO or MB_DEFBUTTON2
        ) = IDYES then
        begin
          DelTree(UserDataDir, True, True, True);
        end;
      end;
    end;
  end;
end;
