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
; Admin rights are used for the elevated startup scheduled task.
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
