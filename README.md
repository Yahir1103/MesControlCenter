# MES Control Center

MES Control Center es una aplicacion de escritorio WPF para administrar scripts locales y monitorear respaldos de una base de datos MySQL/MariaDB desde una sola ventana.

El programa ya no depende de Node.js ni de un agente remoto para funcionar. Los scripts y los backups se ejecutan desde la app .NET mientras MES Control Center este abierto.

## Funciones principales

- Administracion local de scripts `.exe`, `.bat`, `.cmd`, `.ps1`, comandos PowerShell y scripts `npm`.
- Organizacion por carpetas, busqueda, ejecucion individual, detener individual, ejecutar todos y detener todos.
- Captura de salida en consola dentro de la app, con opciones para copiar, guardar y abrir la carpeta de logs.
- Reinicio automatico cuando un proceso falla.
- Health checks HTTP con reinicio si se acumulan fallos.
- Liberacion de puerto antes de iniciar un servicio.
- Hooks de pre-start y post-stop.
- Deploy desde Git con `fetch`, `merge --ff-only`, comando post-pull y rollback si falla la validacion.
- Monitoreo local de CPU, RAM y temperatura.
- Indicador de estado de base de datos en la barra superior.
- Backups locales de MySQL/MariaDB con historial, ejecucion manual, horario diario configurable y retencion configurable.
- Instalador con Inno Setup y tarea programada para iniciar con Windows al iniciar sesion.

## Backups de base de datos

La ventana **Backups** permite configurar:

- Host de MySQL/MariaDB.
- Puerto.
- Nombre de la base de datos.
- Usuario y password.
- Ruta de `mysqldump.exe`.
- Carpeta destino de backups.
- Hora diaria del backup.
- Dias de retencion.

Valores por defecto:

```text
Hora diaria: 22:00
Retencion:  7 dias
Formato:    .sql.gz
```

Los backups se generan con `mysqldump`, se comprimen con gzip y se guardan como:

```text
<database>_<yyyyMMddHHmmss>.sql.gz
```

Ejemplo:

```text
mes_production_20260622171557.sql.gz
```

### Requisito importante

Para que el backup funcione, la PC debe tener `mysqldump.exe`.

Rutas comunes:

```text
C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqldump.exe
C:\Program Files\MariaDB 11.x\bin\mysqldump.exe
C:\xampp\mysql\bin\mysqldump.exe
```

Desde la ventana **Backups**, usa el boton **Browse** en `mysqldump path` para seleccionarlo.

### Donde se guarda la configuracion

La configuracion local de backups se guarda en:

```text
C:\Users\<usuario>\.script_control_center\backup_config.dat
```

El password de la base de datos no se guarda en texto plano dentro del repositorio. Se guarda localmente usando proteccion de Windows.

## Indicador de base de datos

En la barra superior aparece un indicador `DB`:

- Verde: la conexion responde correctamente.
- Rojo: no se pudo conectar.
- Gris: no hay configuracion suficiente o no se ha revisado.

La app revisa la conexion periodicamente usando la configuracion de Backups.

## Arquitectura

La solucion `MesControlCenter.sln` contiene tres proyectos:

```text
src/
  MesControlCenter.Core/
  MesControlCenter.Data/
  MesControlCenter.UI/
```

### MesControlCenter.Core

Contiene modelos y servicios de negocio:

- `LocalBackupService`: scheduler local de backups, ejecucion de `mysqldump`, compresion gzip, historial y retencion.
- `ProcessMonitorService`: deteccion de procesos locales.
- `ResourceMonitorService`: lectura de recursos del sistema y procesos.
- `GitDeployService`: deploy con Git y rollback.
- Modelos de scripts, backups y estado de base de datos.

### MesControlCenter.Data

Contiene persistencia local:

- `JsonScriptConfigRepository`: guarda la lista de scripts y carpetas configuradas.

La configuracion de scripts se guarda bajo el perfil del usuario en `.script_control_center`.

### MesControlCenter.UI

Aplicacion WPF con MVVM:

- `MainWindow`: gestion de scripts, logs, recursos, indicador DB y acceso a Backups.
- `BackupWindow`: configuracion, historial y ejecucion manual de backups.
- Ventanas de edicion para scripts normales, comandos PowerShell y comandos npm.

## Requisitos

Para desarrollar o compilar:

- Windows.
- .NET 8 SDK.
- Inno Setup 6 si quieres generar instalador.
- Git si usas la funcion de deploy.
- MySQL/MariaDB client tools si usas backups (`mysqldump.exe`).

Para ejecutar el instalador generado:

- Windows x64.
- No requiere instalar .NET aparte porque el publish es self-contained.

## Compilar en desarrollo

Desde la raiz del proyecto:

```powershell
dotnet build MesControlCenter.sln -c Release
```

## Generar ejecutable e instalador

El script `build.ps1` publica la app self-contained y compila el instalador con Inno Setup.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Tambien puedes indicar version:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Version "1.0.1"
```

Salidas esperadas:

```text
publish\MesControlCenter.UI.exe
installer\MesControlCenter_Setup_v<version>.exe
```

## Instalacion

Ejecuta el instalador generado:

```text
installer\MesControlCenter_Setup_v1.0.0.exe
```

El instalador:

- Copia la app a `Program Files`.
- Crea accesos directos.
- Crea una tarea programada llamada `MES Control Center`.
- Configura inicio automatico al iniciar sesion de Windows.

La tarea programada se puede revisar con:

```powershell
schtasks /query /tn "MES Control Center" /v /fo list
```

## Uso rapido

1. Abre MES Control Center.
2. Agrega scripts con **Add Script**, **Add PowerShell** o **Add npm**.
3. Configura parametros, carpeta de trabajo, autorestart, health check o Git Deploy si aplica.
4. Usa **Run**, **Stop**, **Run All** o **Stop All** para controlar los procesos.
5. Abre **Backups** para configurar la conexion a la base de datos.
6. Selecciona la carpeta destino y `mysqldump.exe` usando **Browse**.
7. Usa **Run Now** para probar un backup manual.
8. Deja la app abierta para que el backup programado se ejecute a la hora configurada.

## Notas operativas

- Cerrar la ventana **Backups** no detiene el scheduler.
- Cerrar MES Control Center completo si detiene scripts y backups programados.
- El backup programado solo corre si la app esta abierta.
- Si Windows reinicia, la tarea programada abre la app cuando el usuario inicia sesion.
- No subas archivos `.env` ni credenciales al repositorio.

## Validacion de backups

Una forma simple de validar un backup:

1. Confirma que el archivo `.sql.gz` pesa mas de 0 bytes.
2. Descomprime el archivo.
3. Revisa que el `.sql` termine con:

```text
-- Dump completed on ...
```

4. Verifica que contenga `CREATE TABLE` e `INSERT INTO`.
5. Para una validacion completa, restaura en una base de prueba y compara conteos de tablas clave.

## Estado actual del proyecto

Este proyecto esta enfocado en:

- Control local de scripts.
- Backups locales de MySQL/MariaDB.
- Monitoreo basico de recursos y estado de base de datos.
- Instalacion como app de escritorio para Windows.

No requiere un servidor Node ni un agente remoto para su operacion actual.
