#define AppName "BilvaskRegistrering"
#define AppVersion "5.1"
#define Publisher "RoBo"
#define PublishRoot "publish\"
#define RepoRoot "..\"
#define DefaultServerHost "10.44.1.158"

#ifexist "assets\SetupAssets_BilvaskRegistrering.ico"
  #define AppIco "assets\SetupAssets_BilvaskRegistrering.ico"
  #define HasAppIco "1"
#else
  #ifexist "SetupAssets_BilvaskRegistrering.ico"
    #define AppIco "SetupAssets_BilvaskRegistrering.ico"
    #define HasAppIco "1"
  #else
    #ifexist "BilvaskRegistrering.ico"
      #define AppIco "BilvaskRegistrering.ico"
      #define HasAppIco "1"
    #else
      #define HasAppIco "0"
    #endif
  #endif
#endif

[Setup]
AppId={{7E7B5B6B-9C6D-4E6A-9B6F-3C0D5C4F2B11}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={pf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=BilvaskRegistrering_v5.1_Setup_NET8_ServerSyncAuto
Compression=lzma
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64
SolidCompression=yes
WizardStyle=modern
#if HasAppIco == "1"
SetupIconFile={#AppIco}
UninstallDisplayIcon={app}\assets\SetupAssets_BilvaskRegistrering.ico
#endif

[Types]
Name: "full";   Description: "Server + Admin + Worker"
Name: "server"; Description: "Server (DB + Admin)"
Name: "admin";  Description: "Admin (uten DB)"
Name: "worker"; Description: "Worker"

[Components]
Name: "admin";      Description: "BilvaskRegistrering (Admin)"; Types: full server admin
Name: "worker";     Description: "BilvaskRegistrering (Worker)"; Types: full worker
Name: "tools";      Description: "DB-verktøy (server)"; Types: full server; Flags: fixed
Name: "serversync"; Description: "ServerSync (CSV -> DB)"; Types: full server

[Tasks]
Name: "desktopicon"; Description: "Opprett ikon på skrivebordet"; GroupDescription: "Snarveier:"; Flags: checkedonce

[Files]
Source: "tools\license_verify.ps1"; DestName: "license_verify_tmp.ps1"; Flags: dontcopy
Source: "_license_keys\public.xml"; DestName: "public_tmp.xml"; Flags: dontcopy
Source: "_license_keys\public.xml"; DestDir: "{app}\_license_keys"; Flags: ignoreversion; Components: admin worker tools
#if HasAppIco == "1"
Source: "{#AppIco}"; DestDir: "{app}\assets"; DestName: "SetupAssets_BilvaskRegistrering.ico"; Flags: ignoreversion
#endif
Source: "publish\Admin\*";  DestDir: "{app}\Admin";  Flags: recursesubdirs createallsubdirs ignoreversion; Components: admin
Source: "publish\Worker\*"; DestDir: "{app}\Worker"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: worker
Source: "tools\*"; DestDir: "{app}\tools"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: tools admin worker
Source: "..\ServerSync\*"; DestDir: "{app}\ServerSync"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: serversync

[Icons]
#if HasAppIco == "1"
Name: "{group}\BilvaskRegistrering (Admin)"; Filename: "{app}\Admin\BilvaskRegistrering.exe"; WorkingDir: "{app}\Admin"; IconFilename: "{app}\assets\SetupAssets_BilvaskRegistrering.ico"; Components: admin
Name: "{group}\BilvaskRegistrering (Worker)"; Filename: "{app}\Worker\BilvaskRegistrering.Worker.exe"; WorkingDir: "{app}\Worker"; IconFilename: "{app}\assets\SetupAssets_BilvaskRegistrering.ico"; Components: worker
Name: "{commondesktop}\BilvaskRegistrering (Admin)"; Filename: "{app}\Admin\BilvaskRegistrering.exe"; WorkingDir: "{app}\Admin"; IconFilename: "{app}\assets\SetupAssets_BilvaskRegistrering.ico"; Tasks: desktopicon; Components: admin
Name: "{commondesktop}\BilvaskRegistrering (Worker)"; Filename: "{app}\Worker\BilvaskRegistrering.Worker.exe"; WorkingDir: "{app}\Worker"; IconFilename: "{app}\assets\SetupAssets_BilvaskRegistrering.ico"; Tasks: desktopicon; Components: worker
#else
Name: "{group}\BilvaskRegistrering (Admin)"; Filename: "{app}\Admin\BilvaskRegistrering.exe"; WorkingDir: "{app}\Admin"; Components: admin
Name: "{group}\BilvaskRegistrering (Worker)"; Filename: "{app}\Worker\BilvaskRegistrering.Worker.exe"; WorkingDir: "{app}\Worker"; Components: worker
Name: "{commondesktop}\BilvaskRegistrering (Admin)"; Filename: "{app}\Admin\BilvaskRegistrering.exe"; WorkingDir: "{app}\Admin"; Tasks: desktopicon; Components: admin
Name: "{commondesktop}\BilvaskRegistrering (Worker)"; Filename: "{app}\Worker\BilvaskRegistrering.Worker.exe"; WorkingDir: "{app}\Worker"; Tasks: desktopicon; Components: worker
#endif
Name: "{group}\Bilvask ServerSync - Installer automatisk sync"; Filename: "{app}\ServerSync\01_Install_AutoSync_Task_1min.bat"; WorkingDir: "{app}\ServerSync"; Components: serversync
Name: "{group}\Bilvask ServerSync - Fjern automatisk sync"; Filename: "{app}\ServerSync\02_Remove_AutoSync_Task.bat"; WorkingDir: "{app}\ServerSync"; Components: serversync

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\db_bootstrap_FIXED2.ps1"" -PgHost ""{code:GetPgHost}"" -PgPort ""{code:GetPgPort}"" -PgSuperPass ""{code:GetPgPass}"" -DbName ""{code:GetDbName}"" -ActivationCode ""{code:GetInstallCode}"" -UiAdminPassword ""{code:GetUiAdminPassword}"" -UiWorkerPassword ""{code:GetUiWorkerPassword}"""; StatusMsg: "Konfigurerer databasen..."; Flags: runhidden waituntilterminated; Check: IsServerInstall
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\write_runtime_settings_full_FIXED4.ps1"" -ActivationCode ""{code:GetInstallCode}"" -InstallType ""{code:GetInstallType}"" -ServerHost ""{code:GetDbHost}"" -WorkerHost ""{code:GetDbWorkerHost}"" -PgPort ""{code:GetDbPort}"" -DbName ""{code:GetDbName}"" -DatabaseEnabled ""{code:GetDbEnabled}"" -UiAdminPassword ""{code:GetUiAdminPassword}"" -UiWorkerPassword ""{code:GetUiWorkerPassword}"" -AnprRtspUrl ""{code:GetAnprRtspUrl}"" -AnprApiToken ""{code:GetAnprApiToken}"" -DahuaHost ""{code:GetDahuaHost}"" -DahuaPort ""{code:GetDahuaPort}"" -DahuaUser ""{code:GetDahuaUser}"" -DahuaPassword ""{code:GetDahuaPassword}"" -ItsApiHost ""{code:GetItsApiHost}"" -ItsApiPort ""{code:GetItsApiPort}"" -ItsApiPath ""{code:GetItsApiPath}"" -DokumentFolder ""{code:GetDokFolder}"" -AutoRegisterOnPlate ""{code:GetAutoRegister}"" -DisplaySeconds ""{code:GetDisplaySeconds}"" -WorkerRefreshSeconds ""{code:GetWorkerRefreshSeconds}"" -ShowOnlyUnconfirmed ""{code:GetShowOnlyUnconfirmed}"" -Cam2Enabled ""{code:GetCam2Enabled}"" -Cam2Protocol ""{code:GetCam2Protocol}"" -Cam2Host ""{code:GetCam2Host}"" -Cam2Port ""{code:GetCam2Port}"" -Cam2User ""{code:GetCam2User}"" -Cam2Password ""{code:GetCam2Password}"" -Cam2Channel ""{code:GetCam2Channel}"" -Cam2Path ""{code:GetCam2Path}"" -Cam2RtspUrl ""{code:GetCam2RtspUrl}"" -Cam2AutoRefreshOnFreeze ""{code:GetCam2AutoRefresh}"" -Cam3Enabled ""{code:GetCam3Enabled}"" -Cam3Protocol ""{code:GetCam3Protocol}"" -Cam3Host ""{code:GetCam3Host}"" -Cam3Port ""{code:GetCam3Port}"" -Cam3User ""{code:GetCam3User}"" -Cam3Password ""{code:GetCam3Password}"" -Cam3Channel ""{code:GetCam3Channel}"" -Cam3Path ""{code:GetCam3Path}"" -Cam3RtspUrl ""{code:GetCam3RtspUrl}"""; StatusMsg: "Lagrer full konfigurasjon (Documents + ProgramData)..."; Flags: runhidden waituntilterminated; Check: NeedsRuntimeWriter
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\post_install_finalize_FIXED.ps1"" -InstallType ""{code:GetInstallType}"" -ActivationCode ""{code:GetInstallCode}"" -ServerHost ""{code:GetDbHost}"" -PgPort ""{code:GetDbPort}"" -DokumentFolder ""{code:GetDokFolder}"" -ShareName ""BilvaskSCV"""; StatusMsg: "Fullfører installasjon og verifiserer filer / share..."; Flags: runhidden waituntilterminated; Check: NeedsRuntimeWriter
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server_sync_install_and_seed.ps1"" -CsvDir ""{code:GetDokFolder}"" -DbHost ""{code:GetPgHost}"" -DbPort ""{code:GetPgPort}"" -DbName ""{code:GetDbName}"" -DbUser ""postgres"" -DbPass ""{code:GetPgPass}"" -SyncDir ""C:\Bilvask\sync"" -TaskName ""Bilvask CSV->DB Sync"""; StatusMsg: "Installerer ServerSync og kjorer forste full sync..."; Flags: runhidden waituntilterminated; Check: IsServerInstall

[Code]
var
  CodePage: TWizardPage;
  CodeMemo: TNewMemo;
  CodeInfo: TNewStaticText;

  DbPage: TInputQueryWizardPage;
  AnprPage: TInputQueryWizardPage;
  RuntimeOptionPage: TInputOptionWizardPage;
  RuntimePage: TInputQueryWizardPage;
  DahuaPage: TInputQueryWizardPage;
  ItsApiPage: TInputQueryWizardPage;
  Cam2OptionPage: TInputOptionWizardPage;
  Cam2Page: TInputQueryWizardPage;
  Cam2AuthPage: TInputQueryWizardPage;
  Cam3OptionPage: TInputOptionWizardPage;
  Cam3Page: TInputQueryWizardPage;
  Cam3AuthPage: TInputQueryWizardPage;
  DbEnabledPage: TInputOptionWizardPage;
  WorkerUiOptionPage: TInputOptionWizardPage;
  WorkerUiPage: TInputQueryWizardPage;
  AuthPage: TInputQueryWizardPage;
  PgPage: TInputQueryWizardPage;

  BtnTestDb: TNewButton;
  TxtTestHint: TNewStaticText;

  InstallCodeValue: string;
  PgHostValue: string;
  PgPortValue: string;
  PgPassValue: string;

function IsBase64UrlChar(c: Char): Boolean;
begin
  Result :=
    ((c >= 'a') and (c <= 'z')) or
    ((c >= 'A') and (c <= 'Z')) or
    ((c >= '0') and (c <= '9')) or
    (c = '-') or
    (c = '_');
end;

function IsWhitespace(c: Char): Boolean;
begin
  Result := (c = #9) or (c = #10) or (c = #13) or (c = ' ');
end;

function RemoveWhitespace(const S: string): string;
var
  i: Integer;
  c: Char;
begin
  Result := '';
  for i := 1 to Length(S) do
  begin
    c := S[i];
    if not IsWhitespace(c) then
      Result := Result + c;
  end;
end;

function JsonFindValue(const Json, Key: string): string;
var
  P, I, StartPos: Integer;
begin
  Result := '';
  P := Pos('"' + Key + '"', Json);
  if P = 0 then Exit;
  P := P + Length(Key) + 2;
  while (P <= Length(Json)) and (Json[P] <> ':') do P := P + 1;
  if P > Length(Json) then Exit;
  P := P + 1;
  while (P <= Length(Json)) and ((Json[P] = ' ') or (Json[P] = #9) or (Json[P] = #13) or (Json[P] = #10)) do P := P + 1;
  if P > Length(Json) then Exit;

  if Json[P] = '"' then
  begin
    StartPos := P + 1;
    I := StartPos;
    while I <= Length(Json) do
    begin
      if (Json[I] = '"') and ((I = StartPos) or (Json[I - 1] <> '\')) then
      begin
        Result := Copy(Json, StartPos, I - StartPos);
        Exit;
      end;
      I := I + 1;
    end;
  end
  else
  begin
    StartPos := P;
    I := StartPos;
    while (I <= Length(Json)) and (Json[I] <> ',') and (Json[I] <> '}') and (Json[I] <> #13) and (Json[I] <> #10) do
      I := I + 1;
    Result := Trim(Copy(Json, StartPos, I - StartPos));
  end;
end;

function JsonFindSectionValue(const Json, SectionName, Key: string): string;
var
  P, I, StartPos, Depth: Integer;
  SectionJson: string;
begin
  Result := '';
  P := Pos('"' + SectionName + '"', Json);
  if P = 0 then Exit;
  while (P <= Length(Json)) and (Json[P] <> '{') do P := P + 1;
  if P > Length(Json) then Exit;
  StartPos := P;
  Depth := 0;
  for I := StartPos to Length(Json) do
  begin
    if Json[I] = '{' then Inc(Depth)
    else if Json[I] = '}' then
    begin
      Dec(Depth);
      if Depth = 0 then
      begin
        SectionJson := Copy(Json, StartPos, I - StartPos + 1);
        Result := JsonFindValue(SectionJson, Key);
        Exit;
      end;
    end;
  end;
end;

function JsonFindNestedSectionValue(const Json, SectionName, ChildSectionName, Key: string): string;
var
  P, I, J, StartPos, ChildStartPos, Depth: Integer;
  SectionJson, ChildJson: string;
begin
  Result := '';
  P := Pos('"' + SectionName + '"', Json);
  if P = 0 then Exit;
  while (P <= Length(Json)) and (Json[P] <> '{') do P := P + 1;
  if P > Length(Json) then Exit;
  StartPos := P;
  Depth := 0;
  for I := StartPos to Length(Json) do
  begin
    if Json[I] = '{' then Inc(Depth)
    else if Json[I] = '}' then
    begin
      Dec(Depth);
      if Depth = 0 then
      begin
        SectionJson := Copy(Json, StartPos, I - StartPos + 1);
        P := Pos('"' + ChildSectionName + '"', SectionJson);
        if P = 0 then Exit;
        while (P <= Length(SectionJson)) and (SectionJson[P] <> '{') do P := P + 1;
        if P > Length(SectionJson) then Exit;
        ChildStartPos := P;
        Depth := 0;
        for J := ChildStartPos to Length(SectionJson) do
        begin
          if SectionJson[J] = '{' then Inc(Depth)
          else if SectionJson[J] = '}' then
          begin
            Dec(Depth);
            if Depth = 0 then
            begin
              ChildJson := Copy(SectionJson, ChildStartPos, J - ChildStartPos + 1);
              Result := JsonFindValue(ChildJson, Key);
              Exit;
            end;
          end;
        end;
        Exit;
      end;
    end;
  end;
end;

function JsonToBool(const S: string): Boolean;
var
  V: string;
begin
  V := LowerCase(Trim(S));
  Result := (V = 'true') or (V = '1') or (V = 'yes') or (V = 'on');
end;

function ExtractInstallCode(const RawText: string): string;
var
  s: string;
  i, lastPos, j, dots: Integer;
begin
  Result := '';
  s := RemoveWhitespace(RawText);

  lastPos := 0;
  for i := 1 to Length(s) - 4 do
    if Copy(s, i, 5) = 'BVR1.' then
      lastPos := i;

  if lastPos = 0 then Exit;

  j := lastPos;
  while (j <= Length(s)) and ((s[j] = '.') or IsBase64UrlChar(s[j])) do
    j := j + 1;
  Result := Copy(s, lastPos, j - lastPos);

  dots := 0;
  for i := 1 to Length(Result) do
    if Result[i] = '.' then Inc(dots);
  if dots < 2 then Result := '';
end;

function VerifyInstallCodePS(const Code: string): Boolean;
var
  rc: Integer;
  params: string;
begin
  params :=
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\license_verify_tmp.ps1') + '"' +
    ' -PublicKeyPath "' + ExpandConstant('{tmp}\public_tmp.xml') + '"' +
    ' -ActivationCode "' + Code + '"';

  if Exec('powershell.exe', params, '', SW_HIDE, ewWaitUntilTerminated, rc) then
    Result := (rc = 0)
  else
    Result := False;
end;

function ExistingSettingsPath: string;
begin
  if FileExists(ExpandConstant('{userdocs}\BilvaskRegistrering\settings.runtime.json')) then
    Result := ExpandConstant('{userdocs}\BilvaskRegistrering\settings.runtime.json')
  else if FileExists(ExpandConstant('{commonappdata}\BilvaskRegistrering\settings.runtime.json')) then
    Result := ExpandConstant('{commonappdata}\BilvaskRegistrering\settings.runtime.json')
  else
    Result := '';
end;

function TryLoadTextFile(const FilePath: string; var S: string): Boolean;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  S := '';
  if not FileExists(FilePath) then Exit;
  if not LoadStringsFromFile(FilePath, Lines) then Exit;

  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if I > 0 then S := S + #13#10;
    S := S + Lines[I];
  end;
  Result := True;
end;

procedure ApplyHostDefaultsToPages();
begin
  // Intentionally left blank.
  // First install should not prefill defaults; updates are handled by PrefillFromExistingSettings().
end;

procedure PrefillFromExistingSettings();
var
  Path, Json: string;
begin
  Path := ExistingSettingsPath;
  if Path = '' then Exit;
  if not TryLoadTextFile(Path, Json) then Exit;

  DbPage.Values[0] := JsonFindSectionValue(Json, 'Database', 'Host');
  DbPage.Values[1] := JsonFindSectionValue(Json, 'Database', 'WorkerHost');
  DbPage.Values[2] := JsonFindSectionValue(Json, 'Database', 'Port');
  DbPage.Values[3] := JsonFindSectionValue(Json, 'Database', 'Database');
  DbEnabledPage.Values[0] := JsonToBool(JsonFindSectionValue(Json, 'Database', 'Enabled'));

  AnprPage.Values[0] := JsonFindSectionValue(Json, 'Anpr', 'RtspUrl');
  AnprPage.Values[1] := JsonFindSectionValue(Json, 'Anpr', 'ApiToken');

  RuntimeOptionPage.Values[0] := JsonToBool(JsonFindSectionValue(Json, 'Dokument', 'AutoRegisterOnPlate'));
  RuntimePage.Values[0] := JsonFindSectionValue(Json, 'Dokument', 'Folder');
  RuntimePage.Values[1] := JsonFindSectionValue(Json, 'Dokument', 'DisplaySeconds');

  DahuaPage.Values[0] := JsonFindSectionValue(Json, 'Dahua', 'Host');
  DahuaPage.Values[1] := JsonFindSectionValue(Json, 'Dahua', 'Port');
  DahuaPage.Values[2] := JsonFindSectionValue(Json, 'Dahua', 'User');
  DahuaPage.Values[3] := JsonFindSectionValue(Json, 'Dahua', 'Password');

  ItsApiPage.Values[0] := JsonFindSectionValue(Json, 'ItsApi', 'Host');
  ItsApiPage.Values[1] := JsonFindSectionValue(Json, 'ItsApi', 'Port');
  ItsApiPage.Values[2] := JsonFindSectionValue(Json, 'ItsApi', 'Path');

  WorkerUiPage.Values[0] := JsonFindSectionValue(Json, 'WorkerUi', 'RefreshSeconds');
  WorkerUiOptionPage.Values[0] := JsonToBool(JsonFindSectionValue(Json, 'WorkerUi', 'ShowOnlyUnconfirmed'));

  AuthPage.Values[0] := JsonFindSectionValue(Json, 'Auth', 'AdminPassword');
  AuthPage.Values[1] := JsonFindSectionValue(Json, 'Auth', 'WorkerPassword');

  Cam2OptionPage.Values[0] := JsonToBool(JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'Enabled'));
  Cam2OptionPage.Values[1] := LowerCase(JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'Protocol')) = 'axis_http_mjpeg';
  Cam2OptionPage.Values[2] := JsonToBool(JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'AutoRefreshOnFreeze'));
  Cam2Page.Values[0] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'RtspUrl');
  Cam2Page.Values[1] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'Host');
  Cam2Page.Values[2] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'Port');
  Cam2Page.Values[3] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'Path');
  Cam2AuthPage.Values[0] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'Username');
  Cam2AuthPage.Values[1] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'Password');
  Cam2AuthPage.Values[2] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera2', 'Channel');

  Cam3OptionPage.Values[0] := JsonToBool(JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'Enabled'));
  Cam3OptionPage.Values[1] := LowerCase(JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'Protocol')) = 'axis_http_mjpeg';
  Cam3Page.Values[0] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'RtspUrl');
  Cam3Page.Values[1] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'Host');
  Cam3Page.Values[2] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'Port');
  Cam3Page.Values[3] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'Path');
  Cam3AuthPage.Values[0] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'Username');
  Cam3AuthPage.Values[1] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'Password');
  Cam3AuthPage.Values[2] := JsonFindNestedSectionValue(Json, 'PreviewCameras', 'Camera3', 'Channel');
end;

procedure TestDbButtonClick(Sender: TObject);
var
  host, port, ps, resultPath, msg: string;
  rc: Integer;
  lines: TArrayOfString;
begin
  host := Trim(DbPage.Values[0]);
  port := Trim(DbPage.Values[2]);

  if host = '' then
  begin
    MsgBox('Oppgi DB Host / IP før test.', mbError, MB_OK);
    Exit;
  end;
  if port = '' then port := '5432';

  resultPath := ExpandConstant('{tmp}\db_test_result.txt');
  DeleteFile(resultPath);

  ps :=
    '$ErrorActionPreference=''Stop'';' +
    '$client = New-Object System.Net.Sockets.TcpClient;' +
    '$iar = $client.BeginConnect(''' + host + ''',' + port + ', $null, $null);' +
    'if (-not $iar.AsyncWaitHandle.WaitOne(2500,$false)) { $client.Close(); ''TIMEOUT'' | Set-Content -Encoding UTF8 ''' + resultPath + '''; exit 2 };' +
    '$client.EndConnect($iar); $client.Close(); ''OK: TCP-tilkobling til ' + host + ':' + port + ' virker.'' | Set-Content -Encoding UTF8 ''' + resultPath + '''; exit 0';

  if not Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "' + ps + '"', '', SW_HIDE, ewWaitUntilTerminated, rc) then
  begin
    MsgBox('Kunne ikke kjøre DB-test (PowerShell).', mbError, MB_OK);
    Exit;
  end;

  msg := '';
  if FileExists(resultPath) then
    if LoadStringsFromFile(resultPath, lines) and (GetArrayLength(lines) > 0) then
      msg := Trim(lines[0]);

  if msg = '' then msg := 'Test ferdig uten detaljmelding.';

  if rc = 0 then
    MsgBox(msg, mbInformation, MB_OK)
  else
    MsgBox(msg + #13#10 + #13#10 + 'Sjekk IP/port, brannmur på serveren og at PostgreSQL lytter på nettverket.', mbError, MB_OK);
end;

procedure InitializeWizard();
begin
  ExtractTemporaryFile('license_verify_tmp.ps1');
  ExtractTemporaryFile('public_tmp.xml');

  CodePage := CreateCustomPage(wpSelectComponents, 'Installasjonskode', 'Skriv inn installasjonskoden');

  CodeInfo := TNewStaticText.Create(CodePage);
  CodeInfo.Parent := CodePage.Surface;
  CodeInfo.Left := 0;
  CodeInfo.Top := 0;
  CodeInfo.Width := CodePage.SurfaceWidth;
  CodeInfo.AutoSize := True;
  CodeInfo.Caption := 'Koden er påkrevd for installasjonen. Du kan lime inn hele e-posten – installasjonen henter ut riktig kode automatisk.';

  CodeMemo := TNewMemo.Create(CodePage);
  CodeMemo.Parent := CodePage.Surface;
  CodeMemo.Left := 0;
  CodeMemo.Top := CodeInfo.Top + CodeInfo.Height + ScaleY(8);
  CodeMemo.Width := CodePage.SurfaceWidth;
  CodeMemo.Height := ScaleY(90);
  CodeMemo.ScrollBars := ssBoth;
  CodeMemo.WordWrap := False;

  DbPage := CreateInputQueryPage(CodePage.ID, 'Database / server', 'DB-tilkobling', 'Oppgi DB-host, worker-host, port og databasenavn.');
  DbPage.Add('DB Host / IP:', False);
  DbPage.Values[0] := '';
  DbPage.Add('WorkerHost:', False);
  DbPage.Values[1] := '';
  DbPage.Add('DB Port:', False);
  DbPage.Values[2] := '';
  DbPage.Add('Database:', False);
  DbPage.Values[3] := '';

  TxtTestHint := TNewStaticText.Create(DbPage);
  TxtTestHint.Parent := DbPage.Surface;
  TxtTestHint.Left := 0;
  TxtTestHint.Top := DbPage.Edits[3].Top + DbPage.Edits[3].Height + ScaleY(8);
  TxtTestHint.Width := DbPage.SurfaceWidth;
  TxtTestHint.AutoSize := True;
  TxtTestHint.Caption := 'Tips: Test kun TCP-port. Innlogging verifiseres av applikasjonen etter installasjon.';

  BtnTestDb := TNewButton.Create(DbPage);
  BtnTestDb.Parent := DbPage.Surface;
  BtnTestDb.Caption := 'Test DB-tilkobling';
  BtnTestDb.Left := 0;
  BtnTestDb.Top := TxtTestHint.Top + TxtTestHint.Height + ScaleY(6);
  BtnTestDb.Width := ScaleX(180);
  BtnTestDb.Height := ScaleY(28);
  BtnTestDb.OnClick := @TestDbButtonClick;

  AnprPage := CreateInputQueryPage(DbPage.ID, 'ANPR', 'ANPR-innstillinger', 'Oppgi RTSP-adresse og API-token for skanner/ANPR.');
  AnprPage.Add('RtspUrl:', False);
  AnprPage.Values[0] := '';
  AnprPage.Add('ApiToken:', True);
  AnprPage.Values[1] := '';

  RuntimeOptionPage := CreateInputOptionPage(AnprPage.ID, 'Dokument / registrering', 'Dokumentmappe og auto-registrering', 'Velg om ANPR skal auto-registrere og oppgi dokumentmappe.', False, False);
  RuntimeOptionPage.Add('Auto-registerer ved skiltgjenkjenning');
  RuntimeOptionPage.Values[0] := False;

  RuntimePage := CreateInputQueryPage(RuntimeOptionPage.ID, 'Dokument / registrering', 'Dokumentmappe og visningstid', 'Oppgi dokumentmappe og visningstid.');
  RuntimePage.Add('Dokumentmappe:', False);
  RuntimePage.Values[0] := '';
  RuntimePage.Add('DisplaySeconds:', False);
  RuntimePage.Values[1] := '';

  DahuaPage := CreateInputQueryPage(RuntimePage.ID, 'Dahua', 'Dahua-innstillinger', 'Oppgi host, port og brukerinformasjon for Dahua.');
  DahuaPage.Add('Host:', False);
  DahuaPage.Values[0] := '';
  DahuaPage.Add('Port:', False);
  DahuaPage.Values[1] := '';
  DahuaPage.Add('User:', False);
  DahuaPage.Values[2] := '';
  DahuaPage.Add('Password:', True);
  DahuaPage.Values[3] := '';

  ItsApiPage := CreateInputQueryPage(DahuaPage.ID, 'ITS API', 'ITS API-innstillinger', 'Oppgi host, port og path for ITS API / skanner.');
  ItsApiPage.Add('Host:', False);
  ItsApiPage.Values[0] := '';
  ItsApiPage.Add('Port:', False);
  ItsApiPage.Values[1] := '';
  ItsApiPage.Add('Path:', False);
  ItsApiPage.Values[2] := '';

  Cam2OptionPage := CreateInputOptionPage(ItsApiPage.ID, 'Kamera 2', 'Inngang til vaskehallen', 'Velg aktive valg for Kamera 2.', False, False);
  Cam2OptionPage.Add('Aktiv');
  Cam2OptionPage.Values[0] := False;
  Cam2OptionPage.Add('Type = HTTP MJPEG (av = RTSP)');
  Cam2OptionPage.Values[1] := False;
  Cam2OptionPage.Add('Auto-oppfrisk ved heng');
  Cam2OptionPage.Values[2] := False;

  Cam2Page := CreateInputQueryPage(Cam2OptionPage.ID, 'Kamera 2 detaljer', 'Kamera 2 detaljer (1/2)', 'Oppgi full URL, IP/vert, port og sti for Kamera 2.');
  Cam2Page.Add('Full URL (valgfritt):', False);
  Cam2Page.Values[0] := '';
  Cam2Page.Add('IP/Vert:', False);
  Cam2Page.Values[1] := '';
  Cam2Page.Add('Port:', False);
  Cam2Page.Values[2] := '';
  Cam2Page.Add('Sti:', False);
  Cam2Page.Values[3] := '';

  Cam2AuthPage := CreateInputQueryPage(Cam2Page.ID, 'Kamera 2 detaljer', 'Kamera 2 detaljer (2/2)', 'Oppgi bruker, passord og kanal for Kamera 2.');
  Cam2AuthPage.Add('Bruker:', False);
  Cam2AuthPage.Values[0] := '';
  Cam2AuthPage.Add('Passord:', True);
  Cam2AuthPage.Values[1] := '';
  Cam2AuthPage.Add('Kanal:', False);
  Cam2AuthPage.Values[2] := '';

  Cam3OptionPage := CreateInputOptionPage(Cam2AuthPage.ID, 'Kamera 3', 'Avgang fra vaskehallen', 'Velg aktive valg for Kamera 3.', False, False);
  Cam3OptionPage.Add('Aktiv');
  Cam3OptionPage.Values[0] := False;
  Cam3OptionPage.Add('Type = HTTP MJPEG (av = RTSP)');
  Cam3OptionPage.Values[1] := False;

  Cam3Page := CreateInputQueryPage(Cam3OptionPage.ID, 'Kamera 3 detaljer', 'Kamera 3 detaljer (1/2)', 'Oppgi full URL, IP/vert, port og sti for Kamera 3.');
  Cam3Page.Add('Full URL (valgfritt):', False);
  Cam3Page.Values[0] := '';
  Cam3Page.Add('IP/Vert:', False);
  Cam3Page.Values[1] := '';
  Cam3Page.Add('Port:', False);
  Cam3Page.Values[2] := '';
  Cam3Page.Add('Sti:', False);
  Cam3Page.Values[3] := '';

  Cam3AuthPage := CreateInputQueryPage(Cam3Page.ID, 'Kamera 3 detaljer', 'Kamera 3 detaljer (2/2)', 'Oppgi bruker, passord og kanal for Kamera 3.');
  Cam3AuthPage.Add('Bruker:', False);
  Cam3AuthPage.Values[0] := '';
  Cam3AuthPage.Add('Passord:', True);
  Cam3AuthPage.Values[1] := '';
  Cam3AuthPage.Add('Kanal:', False);
  Cam3AuthPage.Values[2] := '';

  DbEnabledPage := CreateInputOptionPage(Cam3AuthPage.ID, 'Database aktiv', 'Database-aktivering', 'Velg om database skal være aktivert etter installasjon.', False, False);
  DbEnabledPage.Add('Aktivert');
  DbEnabledPage.Values[0] := False;

  WorkerUiOptionPage := CreateInputOptionPage(DbEnabledPage.ID, 'Worker UI', 'Worker standardfilter', 'Velg standardfilter for Worker.', False, False);
  WorkerUiOptionPage.Add('Vis kun ubekreftet');
  WorkerUiOptionPage.Values[0] := False;

  WorkerUiPage := CreateInputQueryPage(WorkerUiOptionPage.ID, 'Worker UI', 'Worker-visning', 'Oppgi oppfriskningstid for Worker.');
  WorkerUiPage.Add('RefreshSeconds:', False);
  WorkerUiPage.Values[0] := '';

  AuthPage := CreateInputQueryPage(WorkerUiPage.ID, 'UI-passord', 'Passord i appen', 'Oppgi passord for Admin og Worker UI.');
  AuthPage.Add('AdminPassword:', True);
  AuthPage.Values[0] := '';
  AuthPage.Add('WorkerPassword:', True);
  AuthPage.Values[1] := '';

  PgPage := CreateInputQueryPage(AuthPage.ID, 'PostgreSQL (server)', 'Databasebootstrap', 'Kun for Server-installasjon: oppgi superuser-tilkobling for å opprette/oppdatere database og brukere.');
  PgPage.Add('PostgreSQL Host:', False);
  PgPage.Values[0] := '';
  PgPage.Add('PostgreSQL Port:', False);
  PgPage.Values[1] := '';
  PgPage.Add('postgres-passord:', True);

  PrefillFromExistingSettings();
end;

function IsServerInstall: Boolean;
begin
  Result := WizardIsComponentSelected('tools');
end;

function NeedsRuntimeWriter: Boolean;
begin
  Result := WizardIsComponentSelected('admin') or WizardIsComponentSelected('worker') or WizardIsComponentSelected('tools');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  if PageID = PgPage.ID then
    Result := not IsServerInstall
  else
    Result := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  raw, code: string;
begin
  Result := True;

  if CurPageID = CodePage.ID then
  begin
    raw := CodeMemo.Text;
    code := ExtractInstallCode(raw);
    if (code = '') or (not VerifyInstallCodePS(code)) then
    begin
      MsgBox('Ugyldig eller manglende installasjonskode.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    InstallCodeValue := code;
  end;

  if CurPageID = DbPage.ID then
  begin
    if Trim(DbPage.Values[0]) = '' then
    begin
      MsgBox('Oppgi DB Host / IP.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(DbPage.Values[1]) = '' then DbPage.Values[1] := Trim(DbPage.Values[0]);
    end;

  if CurPageID = RuntimePage.ID then
  begin
    if Trim(RuntimePage.Values[0]) = '' then
    begin
      MsgBox('Oppgi Dokumentmappe.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(RuntimePage.Values[1]) = '' then RuntimePage.Values[1] := '10';
  end;

  if CurPageID = DahuaPage.ID then
  begin
    if Trim(DahuaPage.Values[0]) = '' then DahuaPage.Values[0] := Trim(DbPage.Values[0]);
    if Trim(DahuaPage.Values[1]) = '' then DahuaPage.Values[1] := '37777';
    if Trim(DahuaPage.Values[2]) = '' then DahuaPage.Values[2] := 'admin';
  end;

  if CurPageID = ItsApiPage.ID then
  begin
    if Trim(ItsApiPage.Values[0]) = '' then ItsApiPage.Values[0] := Trim(DbPage.Values[0]);
    if Trim(ItsApiPage.Values[1]) = '' then ItsApiPage.Values[1] := '7070';
    if Trim(ItsApiPage.Values[2]) = '' then ItsApiPage.Values[2] := '/NotificationInfo/TollgateInfo';
  end;

  if CurPageID = Cam2Page.ID then
  begin
    if Trim(Cam2Page.Values[2]) = '' then Cam2Page.Values[2] := '554';
    if Trim(Cam2Page.Values[3]) = '' then Cam2Page.Values[3] := '/axis-media/media.amp';
  end;

  if CurPageID = Cam2AuthPage.ID then
  begin
    if Trim(Cam2AuthPage.Values[2]) = '' then Cam2AuthPage.Values[2] := '0';
  end;

  if CurPageID = Cam3Page.ID then
  begin
    if Trim(Cam3Page.Values[2]) = '' then Cam3Page.Values[2] := '554';
    if Trim(Cam3Page.Values[3]) = '' then Cam3Page.Values[3] := '/axis-media/media.amp';
  end;

  if CurPageID = Cam3AuthPage.ID then
  begin
    if Trim(Cam3AuthPage.Values[2]) = '' then Cam3AuthPage.Values[2] := '0';
  end;

  if CurPageID = WorkerUiPage.ID then
  begin
    if Trim(WorkerUiPage.Values[0]) = '' then WorkerUiPage.Values[0] := '5';
  end;

  if CurPageID = AuthPage.ID then
  begin
    if Trim(AuthPage.Values[0]) = '' then
    begin
      MsgBox('Oppgi AdminPassword.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(AuthPage.Values[1]) = '' then
    begin
      MsgBox('Oppgi WorkerPassword.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if CurPageID = PgPage.ID then
  begin
    PgHostValue := Trim(PgPage.Values[0]);
    PgPortValue := Trim(PgPage.Values[1]);
    PgPassValue := PgPage.Values[2];
    if PgHostValue = '' then PgHostValue := '10.44.1.158';
    if PgPortValue = '' then PgPortValue := '5432';
    if PgPassValue = '' then
    begin
      MsgBox('Oppgi passordet for brukeren postgres.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine +
            'Type: ' + WizardSetupType(False) + NewLine +
            'DB: ' + Trim(DbPage.Values[0]) + ':' + Trim(DbPage.Values[2]) + ' / ' + Trim(DbPage.Values[3]) + NewLine +
            'ANPR RTSP: ' + Trim(AnprPage.Values[0]) + NewLine +
            'Dokumentmappe: ' + Trim(RuntimePage.Values[0]) + NewLine +
            'Dahua: ' + Trim(DahuaPage.Values[0]) + ':' + Trim(DahuaPage.Values[1]) + NewLine +
            'ITS API: ' + Trim(ItsApiPage.Values[0]) + ':' + Trim(ItsApiPage.Values[1]) + Trim(ItsApiPage.Values[2]) + NewLine +
            'Kamera 2 host: ' + Trim(Cam2Page.Values[1]) + NewLine +
            'Kamera 3 host: ' + Trim(Cam3Page.Values[1]);
end;

function GetInstallType(Param: string): string;
begin
  if WizardIsComponentSelected('tools') and WizardIsComponentSelected('admin') and WizardIsComponentSelected('worker') then Result := 'full'
  else if WizardIsComponentSelected('tools') then Result := 'server'
  else if WizardIsComponentSelected('admin') then Result := 'admin'
  else if WizardIsComponentSelected('worker') then Result := 'worker'
  else Result := 'unknown';
end;

function GetInstallCode(Param: string): string;
begin Result := InstallCodeValue; end;
function GetPgHost(Param: string): string;
begin if PgHostValue = '' then Result := Trim(PgPage.Values[0]) else Result := PgHostValue; if Result = '' then Result := '10.44.1.158'; end;
function GetPgPort(Param: string): string;
begin if PgPortValue = '' then Result := Trim(PgPage.Values[1]) else Result := PgPortValue; if Result = '' then Result := '5432'; end;
function GetPgPass(Param: string): string;
begin Result := PgPassValue; if Result = '' then Result := PgPage.Values[2]; end;

function GetDbHost(Param: string): string;
begin Result := Trim(DbPage.Values[0]); end;
function GetDbWorkerHost(Param: string): string;
begin Result := Trim(DbPage.Values[1]); if Result = '' then Result := Trim(DbPage.Values[0]); end;
function GetDbPort(Param: string): string;
begin Result := Trim(DbPage.Values[2]); if Result = '' then Result := '5432'; end;
function GetDbName(Param: string): string;
begin Result := Trim(DbPage.Values[3]); if Result = '' then Result := 'bilvask'; end;

function GetAnprRtspUrl(Param: string): string;
begin Result := Trim(AnprPage.Values[0]); end;
function GetAnprApiToken(Param: string): string;
begin Result := Trim(AnprPage.Values[1]); end;

function GetDokFolder(Param: string): string;
begin Result := Trim(RuntimePage.Values[0]); end;
function GetAutoRegister(Param: string): string;
begin if RuntimeOptionPage.Values[0] then Result := 'true' else Result := 'false'; end;
function GetDisplaySeconds(Param: string): string;
begin Result := Trim(RuntimePage.Values[1]); if Result = '' then Result := '10'; end;

function GetDahuaHost(Param: string): string;
begin Result := Trim(DahuaPage.Values[0]); if Result = '' then Result := Trim(DbPage.Values[0]); end;
function GetDahuaPort(Param: string): string;
begin Result := Trim(DahuaPage.Values[1]); if Result = '' then Result := '37777'; end;
function GetDahuaUser(Param: string): string;
begin Result := Trim(DahuaPage.Values[2]); if Result = '' then Result := 'admin'; end;
function GetDahuaPassword(Param: string): string;
begin Result := Trim(DahuaPage.Values[3]); end;

function GetItsApiHost(Param: string): string;
begin Result := Trim(ItsApiPage.Values[0]); if Result = '' then Result := Trim(DbPage.Values[0]); end;
function GetItsApiPort(Param: string): string;
begin Result := Trim(ItsApiPage.Values[1]); if Result = '' then Result := '7070'; end;
function GetItsApiPath(Param: string): string;
begin Result := Trim(ItsApiPage.Values[2]); if Result = '' then Result := '/NotificationInfo/TollgateInfo'; end;

function GetWorkerRefreshSeconds(Param: string): string;
begin Result := Trim(WorkerUiPage.Values[0]); if Result = '' then Result := '5'; end;
function GetDbEnabled(Param: string): string;
begin if DbEnabledPage.Values[0] then Result := 'true' else Result := 'false'; end;

function GetShowOnlyUnconfirmed(Param: string): string;
begin if WorkerUiOptionPage.Values[0] then Result := 'true' else Result := 'false'; end;

function GetUiAdminPassword(Param: string): string;
begin Result := Trim(AuthPage.Values[0]); if Result = '' then Result := 'admin'; end;
function GetUiWorkerPassword(Param: string): string;
begin Result := Trim(AuthPage.Values[1]); if Result = '' then Result := 'worker'; end;

function GetCam2Enabled(Param: string): string;
begin if Cam2OptionPage.Values[0] then Result := 'true' else Result := 'false'; end;
function GetCam2Protocol(Param: string): string;
begin if Cam2OptionPage.Values[1] then Result := 'axis_http_mjpeg' else Result := 'rtsp'; end;
function GetCam2AutoRefresh(Param: string): string;
begin if Cam2OptionPage.Values[2] then Result := 'true' else Result := 'false'; end;
function GetCam2RtspUrl(Param: string): string;
begin Result := Trim(Cam2Page.Values[0]); end;
function GetCam2Host(Param: string): string;
begin Result := Trim(Cam2Page.Values[1]); end;
function GetCam2Port(Param: string): string;
begin Result := Trim(Cam2Page.Values[2]); if Result = '' then Result := '554'; end;
function GetCam2User(Param: string): string;
begin Result := Trim(Cam2AuthPage.Values[0]); end;
function GetCam2Password(Param: string): string;
begin Result := Trim(Cam2AuthPage.Values[1]); end;
function GetCam2Channel(Param: string): string;
begin Result := Trim(Cam2AuthPage.Values[2]); if Result = '' then Result := '0'; end;
function GetCam2Path(Param: string): string;
begin Result := Trim(Cam2Page.Values[3]); if Result = '' then Result := '/axis-media/media.amp'; end;

function GetCam3Enabled(Param: string): string;
begin if Cam3OptionPage.Values[0] then Result := 'true' else Result := 'false'; end;
function GetCam3Protocol(Param: string): string;
begin if Cam3OptionPage.Values[1] then Result := 'axis_http_mjpeg' else Result := 'rtsp'; end;
function GetCam3RtspUrl(Param: string): string;
begin Result := Trim(Cam3Page.Values[0]); end;
function GetCam3Host(Param: string): string;
begin Result := Trim(Cam3Page.Values[1]); end;
function GetCam3Port(Param: string): string;
begin Result := Trim(Cam3Page.Values[2]); if Result = '' then Result := '554'; end;
function GetCam3User(Param: string): string;
begin Result := Trim(Cam3AuthPage.Values[0]); end;
function GetCam3Password(Param: string): string;
begin Result := Trim(Cam3AuthPage.Values[1]); end;
function GetCam3Channel(Param: string): string;
begin Result := Trim(Cam3AuthPage.Values[2]); if Result = '' then Result := '0'; end;
function GetCam3Path(Param: string): string;
begin Result := Trim(Cam3Page.Values[3]); if Result = '' then Result := '/axis-media/media.amp'; end;
