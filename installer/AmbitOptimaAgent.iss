; The client's installer. Built by build.ps1, which passes PayloadDir and AppVersion.
; SQL and the service are handled by Ambit.Agent.Setup; this script only asks and copies.
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{6F1C2B7E-9D34-4E58-A7C1-3B8E5D20F914}
AppName=Ambit Optima Agent
AppVersion={#AppVersion}
AppPublisher=Ambit
DefaultDirName={autopf}\Ambit Optima Agent
DefaultGroupName=Ambit Optima Agent
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=AmbitOptimaAgentSetup-{#AppVersion}
OutputDir={#PayloadDir}\..
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\desktop\ambit_agent_desktop.exe

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Files]
Source: "{#PayloadDir}\setup\Ambit.Agent.Setup.exe"; Flags: dontcopy
Source: "{#PayloadDir}\worker\*"; DestDir: "{app}"; Excludes: "*.pdb,appsettings.Development.json"; Flags: recursesubdirs ignoreversion
Source: "{#PayloadDir}\desktop\*"; DestDir: "{app}\desktop"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Ambit Optima Agent"; Filename: "{app}\desktop\ambit_agent_desktop.exe"
Name: "{autodesktop}\Ambit Optima Agent"; Filename: "{app}\desktop\ambit_agent_desktop.exe"

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ambit_agent_desktop.exe"; Flags: runhidden; RunOnceId: "CloseUi"
Filename: "{sys}\net.exe"; Parameters: "stop AmbitOptimaAgent"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete AmbitOptimaAgent"; Flags: runhidden; RunOnceId: "DeleteService"

[UninstallDelete]
Type: files; Name: "{app}\appsettings.Production.json"

[Code]
const
  DefaultAmbitUrl = 'https://app.ambitsys.eu';
  AmbitProblem = 2;

var
  SqlPage: TInputQueryWizardPage;
  FirmsPage: TInputOptionWizardPage;
  AmbitPage: TInputQueryWizardPage;
  ApplyFailed: Boolean;
  AmbitLoaded: Boolean;
  MachineHasToken: Boolean;

function SetupTool: String;
begin
  ExtractTemporaryFile('Ambit.Agent.Setup.exe');
  Result := ExpandConstant('{tmp}\Ambit.Agent.Setup.exe');
end;

function JoinLines(const Lines: TArrayOfString): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    Result := Result + Lines[I] + #13#10;
end;

{ '.\OPTIMA' when the machine has one named instance, '.' for the default one. }
function DetectInstance: String;
var
  Names: TArrayOfString;
begin
  Result := '.';
  if RegGetValueNames(HKLM64, 'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL', Names)
    and (GetArrayLength(Names) > 0) and (CompareText(Names[0], 'MSSQLSERVER') <> 0) then
    Result := '.\' + Names[0];
end;

procedure InitializeWizard;
begin
  SqlPage := CreateInputQueryPage(wpSelectDir, 'SQL Server',
    'Gdzie działa Comarch ERP Optima?',
    'Instalator łączy się kontem systemu Windows, które musi mieć rolę sysadmin na tej instancji.');
  SqlPage.Add('Instancja SQL Server:', False);
  SqlPage.Add('Baza konfiguracyjna Optimy:', False);
  SqlPage.Values[0] := ExpandConstant('{param:SERVER|' + DetectInstance + '}');
  SqlPage.Values[1] := ExpandConstant('{param:CONFIGDB|CDN_KNF_Konfiguracja}');

  FirmsPage := CreateInputOptionPage(SqlPage.ID, 'Firmy',
    'Do których firm agent ma mieć dostęp?',
    'Agent sięgnie wyłącznie do zaznaczonych baz. Aby później zmienić wybór, uruchom instalator ponownie.',
    False, False);

  AmbitPage := CreateInputQueryPage(FirmsPage.ID, 'Ambit',
    'Z jakim systemem Ambit ma się łączyć agent?',
    'Token wystawia właściciel systemu Ambit, osobno dla każdej organizacji.');
  AmbitPage.Add('Ambit Url:', False);
  AmbitPage.Add('Token:', True);
  AmbitPage.Values[0] := DefaultAmbitUrl;
end;

{ A repeat run shows the address already in use. It cannot show the token, so an empty field keeps it. }
procedure LoadAmbitSettings;
var
  Output: TExecOutput;
  Code, Tab: Integer;
  Line: String;
begin
  AmbitLoaded := True;
  if ExecAndCaptureOutput(SetupTool, 'ambit-settings --app-dir "' + WizardDirValue + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code, Output) and (Code = 0) and (GetArrayLength(Output.StdOut) > 0) then
  begin
    Line := Output.StdOut[0];
    Tab := Pos(#9, Line);
    if Tab > 0 then
    begin
      AmbitPage.Values[0] := Copy(Line, 1, Tab - 1);
      MachineHasToken := Copy(Line, Tab + 1, 1) = '1';
      if MachineHasToken then
        AmbitPage.PromptLabels[1].Caption := 'Token (puste pole zachowa obecny):';
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = AmbitPage.ID) and not AmbitLoaded then LoadAmbitSettings;
end;

{ In a file, because a command line can be read by any local user. The temporary folder is the installer's own. }
function WriteTokenFile: String;
var
  Token: String;
begin
  Result := '';
  Token := Trim(AmbitPage.Values[1]);
  if Token = '' then Exit;
  Result := ExpandConstant('{tmp}\ambit-token.txt');
  if not SaveStringToFile(Result, Token, False) then
    RaiseException('Nie udało się zapisać pliku tymczasowego z tokenem.');
end;

procedure DeleteTokenFile;
begin
  DeleteFile(ExpandConstant('{tmp}\ambit-token.txt'));
end;

function CheckAmbit: Boolean;
var
  Output: TExecOutput;
  Code: Integer;
  Ran: Boolean;
begin
  Result := False;
  if (Trim(AmbitPage.Values[1]) = '') and not MachineHasToken then
  begin
    MsgBox('Wpisz token wystawiony przez właściciela systemu Ambit.', mbError, MB_OK);
    Exit;
  end;

  Ran := ExecAndCaptureOutput(SetupTool,
    'check-ambit --ambit-url "' + Trim(AmbitPage.Values[0]) + '" --ambit-token-file "' + WriteTokenFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code, Output);
  DeleteTokenFile;

  if Ran and (Code = 0) then
    Result := True
  else if Ran and (Code = AmbitProblem) then
    { The client's firewall may open only later, so this is a warning and the choice is the administrator's. }
    Result := MsgBox('Nie udało się potwierdzić połączenia z Ambit:' + #13#10#13#10 + JoinLines(Output.StdErr) + #13#10 +
      'Agent będzie próbował dalej po instalacji. Kontynuować?', mbConfirmation, MB_YESNO) = IDYES
  else
    MsgBox('Sprawdź adres i token:' + #13#10#13#10 + JoinLines(Output.StdErr), mbError, MB_OK);
end;

function LoadFirms: Boolean;
var
  Output: TExecOutput;
  Code, I, Tab: Integer;
  Line: String;
begin
  Result := False;
  if not ExecAndCaptureOutput(SetupTool,
    'firms --server ' + AddQuotes(SqlPage.Values[0]) + ' --config-database ' + AddQuotes(SqlPage.Values[1]),
    '', SW_HIDE, ewWaitUntilTerminated, Code, Output) or (Code <> 0) then
  begin
    MsgBox('Nie udało się odczytać listy firm:' + #13#10#13#10 + JoinLines(Output.StdErr), mbError, MB_OK);
    Exit;
  end;

  FirmsPage.CheckListBox.Items.Clear;
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
  begin
    Line := Output.StdOut[I];
    Tab := Pos(#9, Line);
    if Tab > 0 then
    begin
      FirmsPage.Add(Copy(Line, 1, Tab - 1));
      { Nothing is ticked on a first install; a repeat run shows what the agent can reach now. }
      FirmsPage.Values[FirmsPage.CheckListBox.Items.Count - 1] := Copy(Line, Tab + 1, 1) = '1';
    end;
  end;

  if FirmsPage.CheckListBox.Items.Count = 0 then
  begin
    MsgBox('Lista firm Optimy jest pusta.', mbError, MB_OK);
    Exit;
  end;
  Result := True;
end;

function SelectedFirms: String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to FirmsPage.CheckListBox.Items.Count - 1 do
    if FirmsPage.Values[I] then
    begin
      if Result <> '' then Result := Result + ',';
      Result := Result + FirmsPage.CheckListBox.Items[I];
    end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if WizardSilent then Exit;
  if CurPageID = SqlPage.ID then
    Result := LoadFirms
  else if (CurPageID = FirmsPage.ID) and (SelectedFirms = '') then
  begin
    MsgBox('Zaznacz co najmniej jedną firmę.', mbError, MB_OK);
    Result := False;
  end
  else if CurPageID = AmbitPage.ID then
    Result := CheckAmbit;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  { An upgrade cannot replace the files of a running service. net stop waits; on a first install it just fails. }
  Exec(ExpandConstant('{sys}\net.exe'), 'stop AmbitOptimaAgent', '', SW_HIDE, ewWaitUntilTerminated, Code);
  { The UI may be hidden in the tray, where nobody would think of closing it. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ambit_agent_desktop.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

procedure Apply;
var
  Output: TExecOutput;
  Code: Integer;
  Firms, AmbitUrl, TokenFile: String;
begin
  if WizardSilent then
  begin
    Firms := ExpandConstant('{param:FIRMS|}');
    AmbitUrl := ExpandConstant('{param:AMBITURL|}');
    TokenFile := ExpandConstant('{param:TOKENFILE|}');
  end
  else
  begin
    Firms := SelectedFirms;
    AmbitUrl := Trim(AmbitPage.Values[0]);
    TokenFile := WriteTokenFile;
  end;

  WizardForm.StatusLabel.Caption := 'Konfigurowanie bazy i usługi...';
  if not ExecAndCaptureOutput(SetupTool,
    'apply --server ' + AddQuotes(SqlPage.Values[0]) +
    ' --config-database ' + AddQuotes(SqlPage.Values[1]) +
    ' --firms "' + Firms + '"' +
    ' --app-dir ' + AddQuotes(ExpandConstant('{app}')) +
    ' --ambit-url "' + AmbitUrl + '"' +
    ' --ambit-token-file "' + TokenFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code, Output) or (Code <> 0) then
  begin
    DeleteTokenFile;
    ApplyFailed := True;
    { A failed upgrade must not leave the previous version stopped; on a first install this just fails. }
    Exec(ExpandConstant('{sys}\net.exe'), 'start AmbitOptimaAgent', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Log('apply failed: ' + JoinLines(Output.StdErr));
    SuppressibleMsgBox('Pliki zostały skopiowane, ale konfiguracja agenta się nie powiodła:' + #13#10#13#10 +
      JoinLines(Output.StdErr) + #13#10 + 'Usuń przyczynę i uruchom instalator ponownie.',
      mbCriticalError, MB_OK, IDOK);
  end
  else
  begin
    DeleteTokenFile;
    Log('apply: ' + JoinLines(Output.StdOut));
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then Apply;
end;

function GetCustomSetupExitCode: Integer;
begin
  if ApplyFailed then Result := 1 else Result := 0;
end;
