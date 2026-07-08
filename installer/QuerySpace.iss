#ifndef MyAppName
  #define MyAppName "QuerySpace"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "QuerySpace contributors"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "QuerySpace.exe"
#endif

[Setup]
AppId={{9C96ECF5-AB4E-4947-9F1F-8E22A3F4769F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\installer-output
OutputBaseFilename=QuerySpace-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\QuerySpace\Assets\QuerySpace.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Windows installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "..\publish\QuerySpace\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Documentation"; Filename: "{app}\docs\README.md"
Name: "{group}\Third-party notices"; Filename: "{app}\docs\THIRD-PARTY-NOTICES.md"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  QuerySpaceUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9C96ECF5-AB4E-4947-9F1F-8E22A3F4769F}_is1';

function QueryInstalledVersion(var InstalledVersion: String): Boolean;
begin
  Result :=
    RegQueryStringValue(HKLM, QuerySpaceUninstallKey, 'DisplayVersion', InstalledVersion) or
    RegQueryStringValue(HKCU, QuerySpaceUninstallKey, 'DisplayVersion', InstalledVersion);
end;

function QueryUninstallCommand(var UninstallCommand: String): Boolean;
begin
  Result :=
    RegQueryStringValue(HKLM, QuerySpaceUninstallKey, 'UninstallString', UninstallCommand) or
    RegQueryStringValue(HKCU, QuerySpaceUninstallKey, 'UninstallString', UninstallCommand);
end;

function InitializeSetup(): Boolean;
var
  InstalledVersion: String;
  UninstallCommand: String;
  ResultCode: Integer;
begin
  Result := True;

  if QueryInstalledVersion(InstalledVersion) and QueryUninstallCommand(UninstallCommand) then
  begin
    if MsgBox(
      '{#MyAppName} version ' + InstalledVersion + ' is already installed.' + #13#10 + #13#10 +
      'Setup will uninstall the previous version before installing this version. Continue?',
      mbConfirmation,
      MB_YESNO) <> IDYES then
    begin
      Result := False;
      Exit;
    end;

    if not Exec(RemoveQuotes(UninstallCommand), '/SILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('Setup could not start the previous version uninstaller. Please uninstall {#MyAppName} from Windows Apps & Features, then run setup again.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if ResultCode <> 0 then
    begin
      MsgBox('The previous version uninstaller returned exit code ' + IntToStr(ResultCode) + '. Please uninstall {#MyAppName} from Windows Apps & Features, then run setup again.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;
