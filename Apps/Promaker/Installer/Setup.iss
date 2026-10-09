; Promaker installer script
; Requires: Inno Setup 6.4+
; Build entry point:
;   make -C installer/Apps/Promaker dist-installer        ; .exe only
;   /dist skill                                           ; full release (zip + scp + tag + push)
; This .iss accepts PublishDir / SelfContainedMode / OutputSuffix
; as /D arguments (see #ifndef defaults below).
; Direct ISCC compile: caller (Makefile) must run `dotnet publish` first.
; CodeDependencies.iss 는 [Code] 섹션을 제공하므로 파일 맨 아래에서 include —
; 그래야 사이에 끼는 ;-주석/디파인이 Pascal 코드로 오해되지 않는다 (Inno Setup 컨벤션).
;
; Promaker 는 편집·시뮬레이션 전용이다. DS2 Hub(PLC 모니터링 서비스)는 여기서 설치하지 않는다 —
; DSPilot 인스톨러(installagent) 또는 ds2-Hub 가 담당. 0.1.30 이하 설치본이 {app}\Agent 에 깔아 둔
; Hub 는 PrepareToInstall 의 RemoveLegacyPromakerHub 가 정리한다.

#ifndef PublishDir
  #define PublishDir "..\Promaker\bin\Release\net9.0-windows\win-x64\publish-self-contained"
#endif
#ifndef SelfContainedMode
  #define SelfContainedMode "true"
#endif
#ifndef OutputSuffix
  #define OutputSuffix "_sc"
#endif

#define AppExePath AddBackslash(PublishDir) + "Promaker.exe"
#define SetupIconPath "..\Promaker\Assets\Promaker.ico"
#define MyAppName "Promaker"
#define MyAppVersion GetVersionNumbersString(AppExePath)
; OutputVersion: 산출물 파일명(Promaker_Setup_<ver>) 에 쓰는 버전. Makefile 의 iscc 타깃이
; BuildVersion.txt(3-part) 값을 /DOutputVersion 으로 주입 → 파일명 SSOT 를 BuildVersion.txt 로
; 일원화. 직접 ISCC 호출(Makefile 미경유) 시엔 exe file-version(4-part)인 MyAppVersion fallback.
#ifndef OutputVersion
  #define OutputVersion MyAppVersion
#endif
#define MyAppPublisher "Dualsoft"
#define MyAppURL "https://dualsoft.co.kr"
#define MyExeName "Promaker.exe"

[Setup]
AppId={{7B74787E-6F09-4AB9-AE16-4C9D5F8B3D31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=Promaker_Setup_{#OutputVersion}{#OutputSuffix}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SetupIconPath}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=yes
UninstallDisplayIcon={app}\{#MyExeName}

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

; fd install 시 이전 sc 빌드 잔재 정리 — sc 시절 박제된 native dll 들이 app-local 에 살아남으면:
; (a) hostfxr.dll 잔재 → "self-contained" 모드로 hostfxr 가 app-local runtime 만 검색 → "install .NET" dialog
; (b) wpfgfx_cor3.dll 등 옛 WPF native 잔재 → 새 PresentationCore 가 EntryPointNotFoundException
;     ('WpfGfx_SetDisableBoundsCheckProtection' 등 새 API 호출 시) → WPF 첫 XAML load 도중 fatal UI error
; fd publish output 에는 본래 미박제이므로 안전 삭제. sc install 시는 본 블록 미적용 (필요 파일).
#if SelfContainedMode != "true"
[InstallDelete]
; .NET host / runtime native — hostfxr 가 app-local 우선 검색 회피
Type: files; Name: "{app}\hostfxr.dll"
Type: files; Name: "{app}\hostpolicy.dll"
Type: files; Name: "{app}\coreclr.dll"
Type: files; Name: "{app}\clrjit.dll"
Type: files; Name: "{app}\clrcompression.dll"
Type: files; Name: "{app}\Microsoft.DiaSymReader.Native.amd64.dll"
; WPF native — wpfgfx_cor3.dll 잔재가 새 PresentationCore 와 API mismatch (실측 회귀, 2026-05-26)
Type: files; Name: "{app}\wpfgfx_cor3.dll"
Type: files; Name: "{app}\PresentationNative_cor3.dll"
Type: files; Name: "{app}\D3DCompiler_47_cor3.dll"
Type: files; Name: "{app}\vcruntime140_cor3.dll"
; sc Microsoft.WindowsDesktop.App 의 framework dll wildcard (PresentationCore.dll 등은 시스템 dll 이라
; 위에서 명시 native 만 — wildcard 는 안전 영역만 선택. WindowsDesktop.App.* 는 sc 만 박제하므로 fd 에서 미생성).
Type: files; Name: "{app}\Microsoft.WindowsDesktop.App.*"
Type: filesandordirs; Name: "{app}\shared"
#endif

[Dirs]
; Promaker · DSPilot 공유 폴더. Promaker 의 "공유 위치에 저장(DSPilot 동기화)" 메뉴와
; DSPilot 서비스(SYSTEM)가 같은 경로(%ProgramData%\DualSoft\Shared\project.aasx)를 읽기/쓰기.
; Users 그룹 modify 권한이 있어야 일반 사용자로 실행되는 Promaker 가 덮어쓸 수 있음.
Name: "{commonappdata}\DualSoft\Shared"; Permissions: users-modify

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; SDF 파일 전용 아이콘 복사
Source: "..\Promaker\Assets\SdfFile.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"; Tasks: desktopicon

[Registry]
; SDF 파일 확장자 등록
Root: HKCR; Subkey: ".sdf"; ValueType: string; ValueName: ""; ValueData: "Promaker.SDF"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "Promaker.SDF"; ValueType: string; ValueName: ""; ValueData: "Software Defined Factory File"; Flags: uninsdeletekey
Root: HKCR; Subkey: "Promaker.SDF\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\SdfFile.ico"
Root: HKCR; Subkey: "Promaker.SDF\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent unchecked

; ── [Code] 섹션 ──
; InnoDependencyInstaller (fd 모드에서 .NET 런타임 자동 설치). 헤더 `[Code]` 포함.
#include "CodeDependencies.iss"

// 0.1.30 이하 Promaker 설치본이 {app}\Agent · {app}\AgentTray 에 깔아 둔 DS2 Hub 정리.
// 서비스 ImagePath 가 이 설치 폴더의 Agent\ 를 가리킬 때만 서비스를 지운다 — DSPilot 인스톨러가
// 자기 폴더에 등록한 같은 이름(Ds2HubService)의 서비스는 건드리지 않는다.
procedure RemoveLegacyPromakerHub();
var
  AgentDir, ImagePath: String;
  ResultCode: Integer;
  Removed: Boolean;
begin
  AgentDir := Lowercase(ExpandConstant('{app}\Agent\'));
  Removed := False;
  // 현재 이름 + 개명 전 이름(PromakerAgentService) 둘 다 확인.
  if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\Ds2HubService', 'ImagePath', ImagePath) then
    if Pos(AgentDir, Lowercase(ImagePath)) > 0 then
    begin
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop Ds2HubService',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\sc.exe'), 'delete Ds2HubService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Removed := True;
    end;
  if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\PromakerAgentService', 'ImagePath', ImagePath) then
    if Pos(AgentDir, Lowercase(ImagePath)) > 0 then
    begin
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop PromakerAgentService',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\sc.exe'), 'delete PromakerAgentService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Removed := True;
    end;
  if Removed then
  begin
    // 구 설치본이 등록한 방화벽 규칙·트레이 자동 실행 — 서비스와 함께 거둔다.
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="DS2 Hub Monitoring"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="DS2 Hub Upload"',     '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Ds2HubTray');
    // SCM stop 완료 대기 후 폴더 삭제 — 아직 잡혀 있으면 폴더는 남지만(best-effort) 서비스 등록은 이미 없다.
    Sleep(3000);
  end;
  if DirExists(ExpandConstant('{app}\AgentTray')) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Ds2.Hub.Tray.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    DelTree(ExpandConstant('{app}\AgentTray'), True, True, True);
  end;
  if DirExists(ExpandConstant('{app}\Agent')) then
    DelTree(ExpandConstant('{app}\Agent'), True, True, True);
end;

// install 진입 시 자동 stop — process 가 살아있으면 dll/exe file lock 으로 [Files] copy fail.
// CloseApplications=yes 가 user-mode process 를 닫지만 best-effort 로 한 번 더. 결과 무시.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Promaker.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  RemoveLegacyPromakerHub();
  // DLL handle close 시간 확보. 1.5s = 경험적 최소.
  Sleep(1500);
  Result := '';
end;

#if SelfContainedMode != "true"
// fd 모드: .NET 9 Desktop Runtime이 없으면 자동 다운로드/설치
function InitializeSetup: Boolean;
begin
  Dependency_AddDotNet90Desktop;
  Result := True;
end;
#endif
