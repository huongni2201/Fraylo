; Inno Setup: cài Fraylo cho user hiện tại (không cần admin), theo bố cục launcher + versions\<ver>.
#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif
#define MyAppName "Fraylo"

[Setup]
; AppId cố định để bộ cài mới nâng cấp đè lên bản đã cài.
AppId={{6E1D0B52-3A7C-4F0E-9C5B-7D2F4A8B1E63}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Fraylo
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\releases
OutputBaseFilename=Fraylo-Setup-v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Fraylo.Web\public\brand\fraylo.ico
UninstallDisplayIcon={app}\versions\{#MyAppVersion}\Fraylo.exe
UninstallDisplayName={#MyAppName} {#MyAppVersion}
CloseApplications=yes
RestartApplications=no
AppMutex=Fraylo.Desktop.Installation

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng trên Desktop"

[Files]
Source: "..\dist\launcher\FrayloLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\app\*"; DestDir: "{app}\versions\{#MyAppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Dọn shortcut của bản TiToAI cũ trước khi tạo shortcut Fraylo.
Type: files; Name: "{autodesktop}\TiToAI.lnk"
Type: files; Name: "{autoprograms}\TiToAI.lnk"

[Icons]
; Shortcut trỏ vào launcher để luôn mở đúng phiên bản hiện hành sau khi tự cập nhật.
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\FrayloLauncher.exe"; IconFilename: "{app}\versions\{#MyAppVersion}\Fraylo.exe"; Tasks: desktopicon
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\FrayloLauncher.exe"; IconFilename: "{app}\versions\{#MyAppVersion}\Fraylo.exe"

[Run]
Filename: "{app}\FrayloLauncher.exe"; Description: "Khởi động {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Dữ liệu chỉ được xóa khi người dùng xác nhận trong CurUninstallStepChanged.
Type: filesandordirs; Name: "{app}\versions"
Type: files; Name: "{app}\current.json"

[Code]
function CompareVersions(A, B: String): Integer;
var
  I, PA, PB, VA, VB: Integer;
  SA, SB: String;
begin
  Result := 0;
  for I := 1 to 4 do
  begin
    PA := Pos('.', A); if PA = 0 then PA := Length(A) + 1;
    PB := Pos('.', B); if PB = 0 then PB := Length(B) + 1;
    SA := Copy(A, 1, PA - 1); SB := Copy(B, 1, PB - 1);
    VA := StrToIntDef(SA, 0); VB := StrToIntDef(SB, 0);
    if VA > VB then begin Result := 1; Exit; end;
    if VA < VB then begin Result := -1; Exit; end;
    Delete(A, 1, PA); Delete(B, 1, PB);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstalledVersion: String;
  Data: AnsiString;
  Service, Processes: Variant;
  StartPos, EndPos: Integer;
begin
  Result := '';
  { current.json also covers auto-updates, which do not update the uninstall registry. }
  if LoadStringFromFile(ExpandConstant('{app}\current.json'), Data) then
  begin
    StartPos := Pos('"version"', Data);
    if StartPos > 0 then
    begin
      Delete(Data, 1, StartPos + 8);
      StartPos := Pos('"', Data);
      if StartPos > 0 then
      begin
        Delete(Data, 1, StartPos);
        EndPos := Pos('"', Data);
        if EndPos > 0 then InstalledVersion := Copy(Data, 1, EndPos - 1);
      end;
    end;
  end;
  if CompareVersions(InstalledVersion, '{#MyAppVersion}') > 0 then
  begin
    Result := 'Không thể cài bản cũ hơn ' + InstalledVersion + '. Database có thể đã được nâng cấp. Hãy dùng bộ cài mới hơn.';
    Exit;
  end;
  { Old releases have no mutex: check their processes too, without terminating jobs. }
  try
    Service := GetActiveOleObject('winmgmts:');
  except
    try
      Service := CreateOleObject('WbemScripting.SWbemLocator');
      Service := Service.ConnectServer('', 'root\CIMV2');
    except
      Result := 'Không kiểm tra được ứng dụng đang chạy. Hãy thử lại sau khi khởi động lại Windows.';
      Exit;
    end;
  end;
  try
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Fraylo.exe'' OR Name = ''FrayloLauncher.exe''');
    if Processes.Count > 0 then
      Result := 'Hãy thoát Fraylo và chờ cập nhật hiện tại hoàn tất trước khi cài đè. Dữ liệu và tác vụ sẽ không bị buộc đóng.';
  except
    Result := 'Không kiểm tra được Fraylo đang chạy. Hãy thử lại sau khi khởi động lại Windows.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\current.json'), '{"version":"{#MyAppVersion}"}', False);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  { Gỡ im lặng luôn giữ dữ liệu; chỉ hỏi sau khi gỡ chương trình. }
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then
    Exit;

  DataDir := ExpandConstant('{localappdata}\Fraylo');
  if not DirExists(DataDir) then
    Exit;

  if SuppressibleMsgBox(
    'Bạn có muốn xóa toàn bộ dữ liệu Fraylo trong thư mục sau không?' + #13#10 +
    DataDir + #13#10#13#10 +
    'Bao gồm cơ sở dữ liệu, dự án, tài khoản và phiên đăng nhập, cấu hình, ' +
    'nhật ký, bản sao lưu và các tệp ảnh/video/tài liệu trong thư mục này.' + #13#10#13#10 +
    'Tệp đã lưu hoặc xuất ở thư mục khác được giữ lại.' + #13#10 +
    'Việc xóa không thể hoàn tác. Hãy đóng Fraylo và các trình duyệt của ứng dụng trước khi chọn Có.' + #13#10#13#10 +
    'Chọn Không để giữ dữ liệu cho lần cài đặt sau.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
    Exit;

  { Chỉ xóa root cố định của user hiện tại, không đọc đường dẫn từ DB/config. }
  if not DelTree(DataDir, True, True, True) then
    SuppressibleMsgBox(
      'Đã gỡ Fraylo nhưng chưa xóa hết dữ liệu tại:' + #13#10 + DataDir + #13#10#13#10 +
      'Một số tệp có thể đang được sử dụng. Hãy đóng Fraylo và các trình duyệt của ứng dụng, ' +
      'sau đó xóa thư mục này thủ công nếu bạn vẫn muốn xóa dữ liệu.',
      mbError, MB_OK, IDOK);
end;
