; The Windows installer for the Open Video Menu menu server: Node.js, the bundled server and WinSW, installed as
; a Windows service that starts with the PC. Built by build.ps1 (which defines AppVersion and Stage); compiles with
; Inno Setup 6 and 7.
;
; The wizard asks which media server to use, gets a sign-in for it (Plex's own on this PC, a plex.tv code, or a
; Jellyfin API key made with an administrator's sign-in), shows the library folders the server will follow, and
; asks for a Windows account only when a folder is on a network share. The PowerShell helper (setup-helper.ps1)
; does the network calls and the system changes.
;
; Silent install (testing, scripted setups):
;   OpenVideoMenu-MenuServer-Setup.exe /VERYSILENT /PLEXURL=http://127.0.0.1:32400 /PLEXTOKEN=...
;   ... /JELLYFINURL=http://127.0.0.1:8096 /JELLYFINKEY=...   [/ACCOUNT=PC\user /PASSWORD=...] [/SERVERNAME=...] [/PORT=8790]
;   [/MEDIAROOTS=D:\More] [/PATHMAP=Z:\=\\nas\media\]

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Stage
  #define Stage "..\build\windows-stage"
#endif

[Setup]
AppId={{6E2B7B4C-3F0D-4D8B-9C1E-0A7F5D2C4B91}
AppName=Open Video Menu menu server
AppVersion={#AppVersion}
AppVerName=Open Video Menu menu server {#AppVersion}
AppPublisher=Open Video Menu
AppPublisherURL=https://openvideomenu.org/
AppSupportURL=https://openvideomenu.org/
DefaultDirName={autopf}\Open Video Menu\Menu Server
DisableDirPage=yes
DefaultGroupName=Open Video Menu
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\build\out
OutputBaseFilename=OpenVideoMenu-MenuServer-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Open Video Menu menu server
UninstallDisplayIcon={app}\node.exe
LicenseFile={#Stage}\LICENSE.txt
CloseApplications=no
#ifdef UseSign
SignTool=ovm $f
SignedUninstaller=yes
#endif

[Files]
Source: "{#Stage}\node.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\menu-server.mjs"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\OpenVideoMenuServer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\OpenVideoMenuServer.template.xml"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\setup-helper.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\NOTICE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\README.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\Menu server status"; Filename: "http://127.0.0.1:{code:GetPort}/v1/health"
Name: "{group}\Menu server logs"; Filename: "{commonappdata}\Open Video Menu\logs"
Name: "{group}\Uninstall the menu server"; Filename: "{uninstallexe}"

[UninstallDelete]
Type: files; Name: "{app}\OpenVideoMenuServer.xml"

[Code]
var
  ServerPage: TInputOptionWizardPage;
  PlexPage: TInputQueryWizardPage;
  JellyfinPage: TInputQueryWizardPage;
  FoldersPage: TOutputMsgMemoWizardPage;
  AccountChoicePage: TInputOptionWizardPage;
  AccountPage: TInputQueryWizardPage;
  SettingsPage: TInputQueryWizardPage;

  PlexToken, JellyfinKey, ClientId, PathMap, HealthText: String;
  FoldersLoaded, NeedsAccount: Boolean;

{ --- the helper ------------------------------------------------------------------------------------- }

function HelperPath(): String;
begin
  if FileExists(ExpandConstant('{app}\setup-helper.ps1')) then
    Result := ExpandConstant('{app}\setup-helper.ps1')
  else
  begin
    ExtractTemporaryFile('setup-helper.ps1');
    Result := ExpandConstant('{tmp}\setup-helper.ps1');
  end;
end;

{ Setup has its own temporary folder; the uninstaller uses the user's. }
function TempFolder(): String;
begin
  if IsUninstaller() then Result := GetTempDir() else Result := ExpandConstant('{tmp}');
end;

{ Runs one helper command. Input is KEY=VALUE lines (written to a temporary file, never on the command line);
  Output gets the result lines, or the reason it failed as Output[0]. }
function RunHelper(const Cmd: String; const Input: TArrayOfString; var Output: TArrayOfString): Boolean;
var
  InFile, OutFile: String;
  Code: Integer;
begin
  InFile := AddBackslash(TempFolder()) + 'ovm-helper-in.txt';
  OutFile := AddBackslash(TempFolder()) + 'ovm-helper-out.txt';
  DeleteFile(OutFile);
  SaveStringsToUTF8File(InFile, Input, False);
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + HelperPath() + '" -Command ' + Cmd +
    ' -In "' + InFile + '" -Out "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  DeleteFile(InFile);
  SetArrayLength(Output, 0);
  if not LoadStringsFromFile(OutFile, Output) then
  begin
    SetArrayLength(Output, 1);
    Output[0] := 'The setup helper gave no answer.';
  end;
  DeleteFile(OutFile);
end;

procedure AddLine(var A: TArrayOfString; const S: String);
begin
  SetArrayLength(A, GetArrayLength(A) + 1);
  A[GetArrayLength(A) - 1] := S;
end;

function FirstLine(const A: TArrayOfString): String;
begin
  if GetArrayLength(A) > 0 then Result := A[0] else Result := '';
end;

function UsePlex(): Boolean;
begin
  Result := ServerPage.SelectedValueIndex = 0;
end;

function GetPort(Param: String): String;
begin
  Result := Trim(SettingsPage.Values[1]);
  if Result = '' then Result := '8790';
end;

{ A random client identifier for Plex's PIN sign-in. }
function NewClientId(): String;
var
  I: Integer;
begin
  Result := 'ovm-menu-server-';
  for I := 1 to 16 do Result := Result + Copy('0123456789abcdef', Random(16) + 1, 1);
end;

{ --- signing in --------------------------------------------------------------------------------------- }

function PlexSignIn(): Boolean;
var
  Input, Output: TArrayOfString;
  PinId, Code: String;
  ErrorCode: Integer;
begin
  Result := False;
  PlexToken := Trim(PlexPage.Values[1]);
  if PlexToken <> '' then begin Result := True; exit; end;

  { Plex on this PC: its own sign-in, from the registry. }
  if RunHelper('plex-local-token', Input, Output) then
  begin
    PlexToken := FirstLine(Output);
    Result := True;
    exit;
  end;

  { Otherwise a code at plex.tv/link. }
  if ClientId = '' then ClientId := NewClientId();
  AddLine(Input, 'CLIENT_ID=' + ClientId);
  if not RunHelper('plex-pin-start', Input, Output) or (GetArrayLength(Output) < 2) then
  begin
    MsgBox('Couldn''t reach plex.tv: ' + FirstLine(Output), mbError, MB_OK);
    exit;
  end;
  PinId := Output[0];
  Code := Output[1];
  ShellExec('open', 'https://plex.tv/link', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  SetArrayLength(Input, 0);
  AddLine(Input, 'CLIENT_ID=' + ClientId);
  AddLine(Input, 'PIN_ID=' + PinId);
  repeat
    if MsgBox('Sign in to Plex: at plex.tv/link (it has just opened in your browser), enter this code:' + #13#10#13#10 +
      '        ' + Code + #13#10#13#10 + 'Then click OK.', mbInformation, MB_OKCANCEL) <> IDOK then exit;
    if RunHelper('plex-pin-check', Input, Output) then
    begin
      PlexToken := FirstLine(Output);
      Result := True;
      exit;
    end;
    MsgBox(FirstLine(Output), mbError, MB_OK);
  until False;
end;

function JellyfinSignIn(): Boolean;
var
  Input, Output: TArrayOfString;
begin
  AddLine(Input, 'URL=' + Trim(JellyfinPage.Values[0]));
  AddLine(Input, 'USER=' + Trim(JellyfinPage.Values[1]));
  AddLine(Input, 'PASSWORD=' + JellyfinPage.Values[2]);
  Result := RunHelper('jellyfin-key', Input, Output);
  if Result then JellyfinKey := FirstLine(Output)
  else MsgBox('Couldn''t sign in to Jellyfin: ' + FirstLine(Output), mbError, MB_OK);
end;

{ --- the library folders ------------------------------------------------------------------------------- }

function LoadFolders(): Boolean;
var
  Input, Output, Kinds: TArrayOfString;
  I, Bar: Integer;
  Memo, Line, Kind, Path, Rest: String;
begin
  Result := False;
  if UsePlex() then
  begin
    AddLine(Input, 'URL=' + Trim(PlexPage.Values[0]));
    AddLine(Input, 'TOKEN=' + PlexToken);
    if ClientId = '' then ClientId := NewClientId();
    AddLine(Input, 'CLIENT_ID=' + ClientId);
    if not RunHelper('plex-folders', Input, Output) then
    begin
      MsgBox('Couldn''t read Plex''s libraries: ' + FirstLine(Output), mbError, MB_OK);
      exit;
    end;
  end
  else
  begin
    AddLine(Input, 'URL=' + Trim(JellyfinPage.Values[0]));
    AddLine(Input, 'TOKEN=' + JellyfinKey);
    if not RunHelper('jellyfin-folders', Input, Output) then
    begin
      MsgBox('Couldn''t read Jellyfin''s libraries: ' + FirstLine(Output), mbError, MB_OK);
      exit;
    end;
  end;

  SetArrayLength(Input, 0);
  for I := 0 to GetArrayLength(Output) - 1 do AddLine(Input, 'FOLDER' + IntToStr(I + 1) + '=' + Output[I]);
  if not RunHelper('classify', Input, Kinds) then
  begin
    MsgBox('Couldn''t check the folders: ' + FirstLine(Kinds), mbError, MB_OK);
    exit;
  end;

  NeedsAccount := False;
  PathMap := '';
  Memo := 'The menu server follows these library folders, as your media server lists them, and finds new ones by itself:' + #13#10#13#10;
  for I := 0 to GetArrayLength(Kinds) - 1 do
  begin
    Line := Kinds[I];
    Bar := Pos('|', Line);
    Kind := Copy(Line, 1, Bar - 1);
    Rest := Copy(Line, Bar + 1, Length(Line));
    if Kind = 'mapped' then
    begin
      Bar := Pos('|', Rest);
      Path := Copy(Rest, 1, Bar - 1);
      Rest := Copy(Rest, Bar + 1, Length(Rest));
      { A mapped drive is a share the service can't see by its letter: read it by its \\server\share path. }
      if Pos(Copy(Path, 1, 2) + '\=', PathMap) = 0 then
      begin
        if PathMap <> '' then PathMap := PathMap + ';';
        PathMap := PathMap + Copy(Path, 1, 2) + '\=' + Rest + '\';
      end;
      Memo := Memo + '  ' + Path + '   (network share ' + Rest + ')' + #13#10;
      NeedsAccount := True;
    end
    else if Kind = 'share' then
    begin
      Memo := Memo + '  ' + Rest + '   (network share)' + #13#10;
      NeedsAccount := True;
    end
    else if Kind = 'other' then
      Memo := Memo + '  ' + Rest + '   (not a Windows path: the media server runs elsewhere; add this PC''s path below if it differs)' + #13#10
    else
      Memo := Memo + '  ' + Rest + #13#10;
  end;
  if NeedsAccount then
    Memo := Memo + #13#10 + 'Some folders are on network shares. Windows services can only read shares as a Windows user, so you''ll be asked for one next (the account Plex or Jellyfin runs as is a good choice).';
  FoldersPage.RichEditViewer.Lines.Text := Memo;
  if NeedsAccount then AccountChoicePage.SelectedValueIndex := 1 else AccountChoicePage.SelectedValueIndex := 0;
  Result := True;
end;

{ --- the wizard ----------------------------------------------------------------------------------------- }

procedure InitializeWizard();
begin
  ServerPage := CreateInputOptionPage(wpLicense, 'Media server', 'Which media server holds your films and shows?',
    'The menu server reads your library folders from it, and checks the TV apps'' sign-ins against it.', True, False);
  ServerPage.Add('Plex');
  ServerPage.Add('Jellyfin');
  ServerPage.SelectedValueIndex := 0;

  PlexPage := CreateInputQueryPage(ServerPage.ID, 'Plex', 'Where is your Plex server?',
    'Leave the token empty: setup uses this PC''s Plex sign-in, or asks you to sign in at plex.tv/link. ' +
    'The token is kept in the menu server''s settings, which only Windows and administrators can read.');
  PlexPage.Add('Plex server address:', False);
  PlexPage.Add('Plex token (optional):', True);
  PlexPage.Values[0] := 'http://127.0.0.1:32400';

  JellyfinPage := CreateInputQueryPage(PlexPage.ID, 'Jellyfin', 'Where is your Jellyfin server?',
    'Sign in as a Jellyfin administrator. Setup makes an API key for the menu server and keeps only that, not your password.');
  JellyfinPage.Add('Jellyfin server address:', False);
  JellyfinPage.Add('Administrator user name:', False);
  JellyfinPage.Add('Password:', True);
  JellyfinPage.Values[0] := 'http://127.0.0.1:8096';

  FoldersPage := CreateOutputMsgMemoPage(JellyfinPage.ID, 'Library folders', 'The folders your media server uses.', '', '');

  AccountChoicePage := CreateInputOptionPage(FoldersPage.ID, 'Service account', 'Which account should the menu server run as?',
    'It only reads your media folders. A Windows user is needed to read network shares, or folders only that user can open.', True, False);
  AccountChoicePage.Add('Windows'' built-in Local Service account (folders on this PC)');
  AccountChoicePage.Add('A Windows user (network shares, private folders)');

  AccountPage := CreateInputQueryPage(AccountChoicePage.ID, 'Windows user', 'Run the menu server as this Windows user.',
    'Windows keeps the password for the service; setup doesn''t store it. The user gets the "Log on as a service" right.');
  AccountPage.Add('User name (PC\user or DOMAIN\user):', False);
  AccountPage.Add('Password:', True);
  AccountPage.Values[0] := GetEnv('USERDOMAIN') + '\' + GetUserNameString();

  SettingsPage := CreateInputQueryPage(AccountPage.ID, 'Menu server', 'Last details.',
    'TV apps show the name when they find the menu server on your network.');
  SettingsPage.Add('Name:', False);
  SettingsPage.Add('Port:', False);
  SettingsPage.Add('Extra folders to scan (optional, separated by commas):', False);
  SettingsPage.Values[0] := 'Open Video Menu on ' + GetComputerNameString();
  SettingsPage.Values[1] := '8790';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = PlexPage.ID then Result := not UsePlex()
  else if PageID = JellyfinPage.ID then Result := UsePlex()
  else if PageID = AccountPage.ID then Result := AccountChoicePage.SelectedValueIndex = 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Input, Output: TArrayOfString;
begin
  Result := True;
  if CurPageID = PlexPage.ID then
  begin
    Result := PlexSignIn();
    FoldersLoaded := False;
  end
  else if CurPageID = JellyfinPage.ID then
  begin
    Result := JellyfinSignIn();
    FoldersLoaded := False;
  end
  else if CurPageID = AccountChoicePage.ID then
  begin
    if NeedsAccount and (AccountChoicePage.SelectedValueIndex = 0) then
      Result := MsgBox('Local Service can''t read network shares, so those folders won''t have menus. Continue anyway?',
        mbConfirmation, MB_YESNO) = IDYES;
  end
  else if CurPageID = AccountPage.ID then
  begin
    AddLine(Input, 'ACCOUNT=' + Trim(AccountPage.Values[0]));
    AddLine(Input, 'PASSWORD=' + AccountPage.Values[1]);
    Result := RunHelper('check-account', Input, Output);
    if not Result then MsgBox(FirstLine(Output), mbError, MB_OK);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { The last page tells how the menu server is doing. }
  if (CurPageID = wpFinished) and (HealthText <> '') then
    WizardForm.FinishedLabel.Caption := HealthText + #13#10#13#10 +
      'TV apps (ovmplayer) find it on your network by themselves. Start menu: "Menu server status".';
  if (CurPageID = FoldersPage.ID) and not FoldersLoaded then
  begin
    FoldersPage.RichEditViewer.Lines.Text := 'Reading the library folders...';
    WizardForm.NextButton.Enabled := False;
    FoldersLoaded := LoadFolders();
    WizardForm.NextButton.Enabled := FoldersLoaded;
    if not FoldersLoaded then
      FoldersPage.RichEditViewer.Lines.Text := 'The folders couldn''t be read. Go back and check the media server''s address and sign-in.';
  end;
end;

{ Silent installs take everything from the command line. }
procedure ReadSilentParams();
begin
  if ExpandConstant('{param:JELLYFINURL|}') <> '' then
  begin
    ServerPage.SelectedValueIndex := 1;
    JellyfinPage.Values[0] := ExpandConstant('{param:JELLYFINURL|}');
    JellyfinKey := ExpandConstant('{param:JELLYFINKEY|}');
  end
  else
  begin
    PlexPage.Values[0] := ExpandConstant('{param:PLEXURL|http://127.0.0.1:32400}');
    PlexToken := ExpandConstant('{param:PLEXTOKEN|}');
  end;
  AccountPage.Values[0] := ExpandConstant('{param:ACCOUNT|}');
  AccountPage.Values[1] := ExpandConstant('{param:PASSWORD|}');
  if AccountPage.Values[0] <> '' then AccountChoicePage.SelectedValueIndex := 1 else AccountChoicePage.SelectedValueIndex := 0;
  if ExpandConstant('{param:SERVERNAME|}') <> '' then SettingsPage.Values[0] := ExpandConstant('{param:SERVERNAME|}');
  if ExpandConstant('{param:PORT|}') <> '' then SettingsPage.Values[1] := ExpandConstant('{param:PORT|}');
  SettingsPage.Values[2] := ExpandConstant('{param:MEDIAROOTS|}');
  PathMap := ExpandConstant('{param:PATHMAP|}');
end;

{ --- installing ------------------------------------------------------------------------------------------- }

procedure CurStepChanged(CurStep: TSetupStep);
var
  Input, Output: TArrayOfString;
  Account: String;
begin
  if CurStep = ssInstall then
  begin
    if WizardSilent() then ReadSilentParams();
    { Running setup again: the old service lets go of its files first. }
    AddLine(Input, 'APP_DIR=' + ExpandConstant('{app}'));
    RunHelper('remove-service', Input, Output);
  end
  else if CurStep = ssPostInstall then
  begin
    Account := '';
    if AccountChoicePage.SelectedValueIndex = 1 then Account := Trim(AccountPage.Values[0]);

    SetArrayLength(Input, 0);
    if UsePlex() then
    begin
      AddLine(Input, 'PLEX_URL=' + Trim(PlexPage.Values[0]));
      AddLine(Input, 'PLEX_TOKEN=' + PlexToken);
    end
    else
    begin
      AddLine(Input, 'JELLYFIN_URL=' + Trim(JellyfinPage.Values[0]));
      AddLine(Input, 'JELLYFIN_API_KEY=' + JellyfinKey);
    end;
    AddLine(Input, 'MEDIA_ROOTS=' + Trim(SettingsPage.Values[2]));
    AddLine(Input, 'PATH_MAP=' + PathMap);
    AddLine(Input, 'SERVER_NAME=' + Trim(SettingsPage.Values[0]));
    AddLine(Input, 'PORT=' + GetPort(''));
    AddLine(Input, 'ACCOUNT=' + Account);
    if not RunHelper('write-config', Input, Output) then
    begin
      MsgBox('Couldn''t write the menu server''s settings: ' + FirstLine(Output), mbError, MB_OK);
      exit;
    end;

    SetArrayLength(Input, 0);
    AddLine(Input, 'APP_DIR=' + ExpandConstant('{app}'));
    AddLine(Input, 'ACCOUNT=' + Account);
    if Account <> '' then AddLine(Input, 'PASSWORD=' + AccountPage.Values[1]);
    if not RunHelper('install-service', Input, Output) then
    begin
      MsgBox('Couldn''t install the menu server''s service: ' + FirstLine(Output), mbError, MB_OK);
      exit;
    end;

    SetArrayLength(Input, 0);
    AddLine(Input, 'PORT=' + GetPort(''));
    if not RunHelper('firewall-add', Input, Output) then
      MsgBox('Couldn''t add the firewall rules (TCP ' + GetPort('') + ', UDP 5353): ' + FirstLine(Output), mbError, MB_OK);

    if RunHelper('health', Input, Output) and (GetArrayLength(Output) >= 2) then
      HealthText := 'The menu server is running and found ' + Output[1] + ' menu(s) in your libraries.'
    else
      HealthText := 'The menu server is installed, but it didn''t answer yet: ' + FirstLine(Output) + #13#10 +
        'Its logs are in ' + ExpandConstant('{commonappdata}\Open Video Menu\logs') + '.';
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  if UsePlex() then Result := 'Media server: Plex at ' + Trim(PlexPage.Values[0])
  else Result := 'Media server: Jellyfin at ' + Trim(JellyfinPage.Values[0]);
  if AccountChoicePage.SelectedValueIndex = 1 then
    Result := Result + NewLine + 'Runs as: ' + Trim(AccountPage.Values[0])
  else
    Result := Result + NewLine + 'Runs as: Local Service';
  Result := Result + NewLine + 'Name: ' + Trim(SettingsPage.Values[0]) + NewLine + 'Port: ' + GetPort('') + NewLine +
    'Starts with Windows; firewall open on home (private) networks only.';
end;

{ --- uninstalling ----------------------------------------------------------------------------------------- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Input, Output: TArrayOfString;
  Data: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    AddLine(Input, 'APP_DIR=' + ExpandConstant('{app}'));
    RunHelper('remove-service', Input, Output);
    RunHelper('firewall-remove', Input, Output);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\Open Video Menu');
    if DirExists(Data) and (UninstallSilent() or
      (MsgBox('Remove the menu server''s settings and logs too (' + Data + ')? The settings hold your media server sign-in.',
        mbConfirmation, MB_YESNO) = IDYES)) then
      DelTree(Data, True, True, True);
  end;
end;
