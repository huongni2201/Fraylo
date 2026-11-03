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
; Chỉ xóa phần chương trình; dữ liệu người dùng ở %LOCALAPPDATA%\Fraylo được giữ nguyên.
Type: filesandordirs; Name: "{app}\versions"
Type: files; Name: "{app}\current.json"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\current.json'), '{"version":"{#MyAppVersion}"}', False);
end;
