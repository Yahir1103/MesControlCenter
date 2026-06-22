; MES Control Center - Inno Setup Installer
; Auto-starts with Windows

#define MyAppName "MES Control Center"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "MES"
#define MyAppExeName "MesControlCenter.UI.exe"

[Setup]
AppId={{A3F1B2C4-5D6E-7F89-0A1B-2C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=installer
OutputBaseFilename=MesControlCenter_Setup_v{#MyAppVersion}
SetupIconFile=src\MesControlCenter.UI\Resources\Logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Writing machine-wide environment variables requires admin rights.
PrivilegesRequired=admin
DisableProgramGroupPage=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: checkedonce

[Files]
Source: "publish\MesControlCenter.UI.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; WebSocket server URL as a machine-wide environment variable. MySQL credentials
; no longer live on client machines — only this URL (and optionally the admin
; token for dashboards) do.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: string; ValueName: "MESCC_SERVER_URL"; ValueData: "{code:GetServerUrl}"; \
    Flags: preservestringtype; Check: ShouldWriteServerUrl

; Admin token (only needed on machines that open the admin dashboard). Optional.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: string; ValueName: "MESCC_ADMIN_TOKEN"; ValueData: "{code:GetAdminToken}"; \
    Flags: preservestringtype; Check: ShouldWriteAdminToken

[Run]
; Autostart al iniciar sesión, ELEVADO (admin) y SIN prompt de UAC, para que la
; app pueda leer la temperatura. /RL HIGHEST = privilegios máximos sin pedir UAC.
; /SC ONLOGON = arranca cuando el usuario inicia sesión (requiere autologon en el
; servidor para que se levante solo tras un corte de luz).
Filename: "schtasks"; \
    Parameters: "/create /tn ""{#MyAppName}"" /tr ""'{app}\{#MyAppExeName}'"" /sc onlogon /rl highest /f"; \
    Flags: runhidden; StatusMsg: "Configurando inicio automático..."

; Lanzar ahora al terminar la instalación (elevado, ya estamos en instalador admin).
Filename: "{app}\{#MyAppExeName}"; Description: "Ejecutar {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "schtasks"; Parameters: "/delete /tn ""{#MyAppName}"" /f"; Flags: runhidden; RunOnceId: "DelTask"

[UninstallDelete]
Type: files; Name: "{app}\*"
Type: dirifempty; Name: "{app}"

[Code]
var
  CfgPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  CfgPage := CreateInputQueryPage(wpSelectTasks,
    'Conexión al servidor',
    'Servidor WebSocket de MES Control Center',
    'Indica la URL del servidor WS (p.ej. ws://192.168.1.10:8092/ws). El token admin ' +
    'solo es necesario en equipos que abran el panel de administración; puede dejarse en blanco.');
  CfgPage.Add('URL del servidor (ws:// o wss://):', False);
  CfgPage.Add('Token admin (opcional):', True);
  CfgPage.Values[0] := GetEnv('MESCC_SERVER_URL');
  CfgPage.Values[1] := GetEnv('MESCC_ADMIN_TOKEN');
end;

function GetServerUrl(Param: string): string;
begin
  Result := Trim(CfgPage.Values[0]);
end;

function GetAdminToken(Param: string): string;
begin
  Result := Trim(CfgPage.Values[1]);
end;

function ShouldWriteServerUrl: Boolean;
begin
  Result := Trim(CfgPage.Values[0]) <> '';
end;

function ShouldWriteAdminToken: Boolean;
begin
  Result := Trim(CfgPage.Values[1]) <> '';
end;
