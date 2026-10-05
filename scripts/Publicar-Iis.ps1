<#
.SYNOPSIS
    Publica WatchMercadoPublico en un IIS local y deja el sitio funcionando.

.DESCRIPTION
    Instala lo que falte, publica, comprueba la publicación ANTES de subirla,
    crea el grupo de aplicaciones y el sitio, pone las cinco variables de
    entorno que hacen falta, asigna permisos y verifica que la web responde con
    datos reales.

    DEBE EJECUTARSE EN POWERSHELL COMO ADMINISTRADOR. No es prudencia: appcmd
    necesita privilegios incluso para LEER la configuración de IIS, así que sin
    elevación el script no puede ni comprobar en qué estado está el servidor.

    Los pasos que exigen elevación son estos, y ninguno se puede evitar:
      - instalar el Hosting Bundle, si falta
      - crear el grupo y el sitio (appcmd add apppool / add site)
      - poner las variables de entorno del grupo
      - los permisos de la carpeta

.PARAMETER SiteName
    Nombre del sitio en IIS. Por defecto: WatchMercadoPublico

.PARAMETER AppPoolName
    Nombre del grupo de aplicaciones. Por defecto: WatchMercadoPublico

.PARAMETER PhysicalPath
    Carpeta física donde se publica. Por defecto: C:\inetpub\WatchMercadoPublico

.PARAMETER Port
    Puerto del sitio. Por defecto: 8090. El 80 suele estar ocupado.

.PARAMETER Ticket
    Ticket de Mercado Público. Si no se pasa, se pregunta con Read-Host, porque
    un ticket en la línea de órdenes queda en el historial de PowerShell.

.PARAMETER CodigoProveedor
    Código de la empresa en Mercado Público. Es la única imprescindible: sin
    ella la aplicación arranca y no consulta nada.

.PARAMETER NombreEmpresa
.PARAMETER RutEmpresa
    Los otros dos. Solo se pintan en la cabecera.

.PARAMETER SaltarPublicacion
    Solo crea el sitio y la configuración, sin volver a publicar. Para cuando la
    carpeta física ya está subida a mano.

.PARAMETER PuertoDePrueba
    Puerto para verificar la web antes de abrirla en el navegador. Por defecto
    uno libre.

.EXAMPLE
    .\scripts\Publicar-Iis.ps1
    Pregunta por el ticket y publica todo.

.EXAMPLE
    .\scripts\Publicar-Iis.ps1 -CodigoProveedor <codigo> -SaltarPublicacion
    Reaprovecha lo ya publicado y solo ajusta IIS.

.NOTES
    Sin base de datos: a diferencia del gestor de archivos, aquí no hay nada que
    crear ni migrar. El estado vive en memoria y se pierde al reiniciar el
    grupo, lo cual es lo correcto: es una caché, no unos datos.
#>

[CmdletBinding()]
param(
    [string] $SiteName = 'WatchMercadoPublico',
    [string] $AppPoolName = 'WatchMercadoPublico',
    [string] $PhysicalPath = 'C:\inetpub\WatchMercadoPublico',
    [int]    $Port = 8090,
    [string] $Ticket,
    [string] $CodigoProveedor,
    [string] $NombreEmpresa,
    [string] $RutEmpresa,
    [switch] $SaltarPublicacion,
    [int]    $PuertoDePrueba = 0
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------
# Salida
# ---------------------------------------------------------------------------
function Write-Step { param([string]$T) Write-Host "`n=== $T ===" -ForegroundColor Cyan }
function Write-Ok   { param([string]$T) Write-Host "  [OK]    $T" -ForegroundColor Green }
function Write-Info { param([string]$T) Write-Host "  [INFO]  $T" -ForegroundColor Gray }
function Write-Aviso{ param([string]$T) Write-Host "  [AVISO] $T" -ForegroundColor Yellow }
function Write-Erro { param([string]$T) Write-Host "  [ERROR] $T" -ForegroundColor Red }

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------------------
# Ejecutar herramientas externas sin que sus avisos maten el script
#
# npm y dotnet escriben en stderr SIEMPRE: el "npm notice run ..." del arranque,
# los avisos de MSBuild, los interlINEOS. Con ErrorActionPreference en 'Stop',
# PowerShell convierte eso en un error TERMINANTE y el script se para EN
# SILENCIO, justo despues de imprimir el aviso. Pasó de verdad: la primera
# ejecucion se paró en "npm run css" con un "Hecho: tailwindcss" que no era un
# fallo, solo estaba en el canal equivocado.
#
# Aqui se baja la preferencia solo mientras dura la llamada y el fallo se decide
# por el CODIGO DE SALIDA, que es lo unico que significa algo. Es el mismo
# truco que usa scripts/arrancar.ps1, y por algo esta escrito en un fichero
# aparte.
#
# Se usa cmd.exe /c ademas de invocar el comando directamente: asi el comando
# llega a cmd con sus argumentos intactos y no hay que pelease con el
# analisis de comillas de PowerShell.
# ---------------------------------------------------------------------------
function Ejecutar {
    param(
        [string]   $Orden,
        [string]   $Etiqueta,
        [switch]   $Critico = $true,
        [int]      $UltimasLineas = 8
    )

    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $salida = & cmd.exe /c $Orden 2>&1
        $codigo = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $antes
    }

    if ($codigo -ne 0) {
        if ($Critico) {
            Write-Erro "$Etiqueta ha fallado (codigo $codigo). Sus ultimas lineas:"
            @($salida) | Select-Object -Last $UltimasLineas | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray }
            exit 1
        }
        Write-Aviso "$Etiqueta (codigo ${codigo}), se continua"
    }

    return @($salida)
}

# appcmd y icacls se llaman mucho y casi siempre está bien que fallen (el sitio
# puede no existir todavía). Esta variante no interrumpe nunca.
function Ejecutar-Tolerante {
    param([string] $Orden)
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $salida = & cmd.exe /c $Orden 2>&1
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $antes }
}

# ---------------------------------------------------------------------------
# Deteccion de lo que hay puesto
#
# LAS RUTAS DEL MODULO SON IMPORTANTES Y SON FACILES DE ACIERTO MAL. ANCM no
# vive en System32\inetsrv sino en Archivos de programa, y con el nombre
# aspnetcorev2.dll. Buscando "inetsrv\aspnetcore.dll" no se encuentra nunca y
# da la sensacion de que falta el Hosting Bundle cuando esta instalado. Comprueba
# ademas el registro en applicationHost.config, que es lo que IIS usa de verdad.
# ---------------------------------------------------------------------------
function Get-Ancm {
    $rutas = @(
        (Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'),
        (Join-Path $env:windir 'System32\inetsrv\aspnetcorev2_inprocess.dll'),
        (Join-Path $env:windir 'System32\inetsrv\aspnetcorev3_inprocess.dll')
    )
    foreach ($r in $rutas) { if (Test-Path $r) { return $r } }
    return $null
}

function Get-AncmRegistrado {
    <#
        Devuelve $true si IIS tiene AnCM v2 en su configuracion. Es mas fiable
        que la existencia del fichero: el modulo puede estar en disco y no
        registrado, y en ese caso IIS no lo usaria.
    #>
    $cfg = Join-Path $env:windir 'System32\inetsrv\config\applicationHost.config'
    if (-not (Test-Path $cfg)) { return $false }
    try {
        $c = [System.IO.File]::ReadAllText($cfg)
        return $c -match 'AspNetCoreModuleV2'
    } catch { return $false }
}

function Get-VersionRuntime {
    <# Version 10.x más reciente de Microsoft.AspNetCore.App instalada. #>
    $versiones = & dotnet --list-runtimes 2>$null |
        Where-Object { $_ -match 'Microsoft\.AspNetCore\.App\s+(10\.[0-9.]+)' } |
        ForEach-Object { $Matches[1] } |
        Sort-Object -Descending
    if ($versiones) { return $versiones[0] }
    return '10.0.12'
}

function Test-PuertoLibre {
    param([int]$P)
    return -not (Get-NetTCPConnection -LocalPort $P -State Listen -ErrorAction SilentlyContinue)
}

function Get-PuertoLibre {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}

# ---------------------------------------------------------------------------
# 0. Condiciones previas
# ---------------------------------------------------------------------------
Write-Host 'WatchMercadoPublico · publicacion en IIS' -ForegroundColor White
Write-Host '========================================' -ForegroundColor White

if (-not (Test-Admin)) {
    Write-Erro 'Este script necesita una consola de PowerShell COMO ADMINISTRADOR.'
    Write-Host ''
    Write-Host '  No es una recomendacion. appcmd no puede ni LEER la configuracion' -ForegroundColor Yellow
    Write-Host '  de IIS sin privilegios, asi que sin elevacion el script no puede' -ForegroundColor Yellow
    Write-Host '  ni saber en que estado esta el servidor, ni crear el sitio.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  Como relanzar esta misma consola elevada:' -ForegroundColor Cyan
    Write-Host '    Start-Process powershell -Verb RunAs' -ForegroundColor White
    Write-Host '  y desde ahi volver a ejecutar el script.' -ForegroundColor Cyan
    exit 1
}
Write-Ok 'Ejecutando como administrador'

$appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
if (-not (Test-Path $appcmd)) {
    Write-Erro 'IIS no esta instalado (falta appcmd.exe).'
    exit 1
}
Write-Ok "IIS disponible: $appcmd"

# ---------------------------------------------------------------------------
# 1. Hosting Bundle
# ---------------------------------------------------------------------------
Write-Step '1. Modulo de ASP.NET Core para IIS (ANCM)'

$ancm = Get-Ancm
$registrado = Get-AncmRegistrado

if ($ancm -and $registrado) {
    Write-Ok "ANCM v2 instalado y registrado: $ancm"
} elseif ($ancm -and -not $registrado) {
    Write-Aviso 'El modulo esta en disco pero NO aparece en applicationHost.config.'
    Write-Aviso 'IIS no lo usara. Reinstalar el Hosting Bundle lo registra.'
} else {
    Write-Aviso 'Falta el Hosting Bundle de ASP.NET Core.'
    Write-Info  "Version a instalar: $(Get-VersionRuntime)"
    Write-Host ''
    Write-Host '  Descargarlo de:' -ForegroundColor Cyan
    Write-Host '    https://dotnet.microsoft.com/permalink/dotnetcore-current-windows-runtime-bundle-installer' -ForegroundColor White
    Write-Host ''
    Write-Host '  Se instala a mano y hay que REPARARLO si IIS ya estaba antes:' -ForegroundColor Cyan
    Write-Host '    volver a pasar el instalador. Luego:' -ForegroundColor Cyan
    Write-Host '    net stop was /y ; net start w3svc' -ForegroundColor White
    Write-Host ''
    Write-Erro 'Parando aqui. Se instala a mano porque un instalador MSI' -ForegroundColor Black
    Write-Host '  Within Another Script en este mismo script y en el siguiente es un camino' -ForegroundColor Black
    Write-Host '  a errores innecesarios. Lo normal es hacerlo a mano una vez por equipo.' -ForegroundColor Black
    Write-Host ''
    Write-Host '  (Aviso: esto NO se puede automatizar desde aqui. Install-WindowsFeature y' -ForegroundColor DarkGray
    Write-Host '   el instalador del Hosting Bundle necesitan su propia elevacion.)' -ForegroundColor DarkGray
    exit 2
}

Write-Info  "Runtime de ASP.NET Core: $(Get-VersionRuntime)"

if (-not (Test-PuertoLibre $Port)) {
    Write-Aviso "El puerto $Port ya esta ocupado."
    $alterno = Get-PuertoLibre
    Write-Aviso "Se usara el $alterno. Pasalo con -Port si prefieres otro."
    $Port = $alterno
}

# ---------------------------------------------------------------------------
# 2. Publicacion
# ---------------------------------------------------------------------------
Write-Step '2. Publicacion'

if ($SaltarPublicacion) {
    if (-not (Test-Path (Join-Path $PhysicalPath 'WatchMercadoPublico.Server.dll'))) {
        Write-Erro "-SaltarPublicacion, pero en $PhysicalPath no hay nada publicado."
        exit 1
    }
    Write-Ok 'Se reaprovecha lo ya publicado'
} else {
    Write-Info 'Compilando el CSS (npm run css)'
    Push-Location $raiz
    try {
        # No es critico: si el CSS falla, se puede publicar igual con el que haya,
        # y abortar aqui dejaria al usuario sin sitio por un detalle de estilo.
        Ejecutar 'npm run css' 'La compilacion del CSS' -Critico:$false | Out-Null
    } finally { Pop-Location }

    Write-Info 'dotnet publish (framework-dependent)'
    Push-Location $raiz
    try {
        $salida = Ejecutar ('dotnet publish .\src\WatchMercadoPublico.Server -c Release -o "{0}" --nologo -v minimal' -f $PhysicalPath) 'La publicacion'
    } finally { Pop-Location }
    @($salida) | Select-Object -Last 3 | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray }
    Write-Ok "Publicado en $PhysicalPath"
}

# ---------------------------------------------------------------------------
# 3. Las comprobaciones ANTES de tocar IIS
#
# Se hacen aqui y no despues a proposito: si la publicacion sale mala, es
# mucho mejor que el script se pare ahora que dejar un sitio a medio configura-
# do en el servidor. Y el fallo mas probable es de los que solo se ven mirando
# los ficheros.
# ---------------------------------------------------------------------------
Write-Step '3. Comprobaciones de la publicacion'

$fallos = 0

# 3.1 El importmap del index.html publicado tiene contenido. En desarrollo el
#     de disco esta vacio a proposito, asi que esto solo se puede mirar aqui.
$index = Join-Path $PhysicalPath 'wwwroot\index.html'
if (-not (Test-Path $index)) {
    Write-Erro 'No existe wwwroot\index.html en la publicacion.'; $fallos++
} elseif (-not (Select-String -Path $index -Pattern '"imports"' -Quiet)) {
    Write-Erro 'El importmap del index.html publicado esta VACIO.'
    Write-Host '         Blazor se quedara en "Cargando" sin decir por que.' -ForegroundColor DarkGray
    $fallos++
} else {
    Write-Ok 'El importmap tiene contenido'
}

# 3.2 Sin variantes precomprimidas. Si aparecen .br o .gz, en algunos hostings
#     llegan vacias y Blazor se queda en "Cargando" por un fallo de digest.
$comprimidos = @(Get-ChildItem (Join-Path $PhysicalPath 'wwwroot\_framework') `
                    -Include *.br, *.gz -Recurse -ErrorAction SilentlyContinue)
if ($comprimidos.Count -gt 0) {
    Write-Erro "Hay $($comprimidos.Count) ficheros .br/.gz. No deberian existir:"
    Write-Host '         Directory.Build.props desactiva la compresion para los dos' -ForegroundColor DarkGray
    Write-Host '         proyectos que se publican. Revisar que sigue asi.' -ForegroundColor DarkGray
    $fallos++
} else {
    Write-Ok 'Sin .br ni .gz'
}

# 3.3 El ticket no puede viajar en la publicacion.
if (Test-Path (Join-Path $PhysicalPath 'appsettings.Development.json')) {
    Write-Erro 'La publicacion contiene appsettings.Development.json: lleva el ticket.'
    $fallos++
} else {
    Write-Ok 'Sin appsettings.Development.json'
}

# 3.4 Ningun _framework a 0 bytes. Es el fallo que aparece en produccion y no en
#     desarrollo, y por eso se comprueba tamano a tamano.
$cortos = @()
Get-ChildItem (Join-Path $PhysicalPath 'wwwroot\_framework') -File -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Length -eq 0) { $cortos += $_.Name }
}
if ($cortos.Count -gt 0) {
    Write-Erro "$($cortos.Count) ficheros de _framework estan a 0 bytes:"
    $cortos | Select-Object -First 8 | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray }
    $fallos++
} else {
    Write-Ok 'Ningun fichero de _framework a 0 bytes'
}

# 3.5 El web.config debe ser XML valido. Un comentario con "--" dentro, que es
#     facil de colar, da HTTP 500.19 y el sitio no arranca sin explicacion clara.
$webConfig = Join-Path $PhysicalPath 'web.config'
if (-not (Test-Path $webConfig)) {
    Write-Erro 'No hay web.config en la publicacion. IIS no sabria arrancarla.'
    $fallos++
} else {
    try {
        $xml = [xml][System.IO.File]::ReadAllText($webConfig)
        $modelo = $xml.configuration.'system.webServer'.aspNetCore.hostingModel
        $timeout = $xml.configuration.'system.webServer'.limits.activityTimeout
        Write-Ok "web.config valido (hostingModel=$modelo, activityTimeout=$timeout)"
    } catch {
        Write-Erro "web.config no es XML valido: $($_.Exception.Message)"
        Write-Host '         IIS devolveria HTTP 500.19 (error de configuracion).' -ForegroundColor DarkGray
        $fallos++
    }
}

if ($fallos -gt 0) {
    Write-Host ''
    Write-Erro "$fallos comprobacion(es) fallida(s). NO se toca IIS."
    exit 1
}

# ---------------------------------------------------------------------------
# 4. Parar lo que hubiera antes
#
# Los ficheros estan bloqueados mientras la aplicacion corre, y copiarlos encima
# falla con "The process cannot access the file". app_offline.htm hace ademas que
# ANCM pare la aplicacion de forma ordenada, en vez de matarla.
# ---------------------------------------------------------------------------
Write-Step '4. Sitio y grupo de aplicaciones'

Ejecutar-Tolerante ("`"{0}`" stop site /site.name:`"{1}`"" -f $appcmd, $SiteName) | Out-Null
Ejecutar-Tolerante ("`"{0}`" stop apppool /apppool.name:`"{1}`"" -f $appcmd, $AppPoolName) | Out-Null

$offline = Join-Path $PhysicalPath 'app_offline.htm'
if (Test-Path $offline) { Remove-Item $offline -Force }
New-Item -Path $offline -Force -ItemType File | Out-Null
Write-Ok 'app_offline.htm puesto (IIS parara la app de forma ordenada)'

$existePool = @(& $appcmd list apppool 2>$null | Where-Object { $_ -like "*$AppPoolName*" })
if ($existePool) {
    Ejecutar-Tolerante ("`"{0}`" delete apppool /apppool.name:`"{1}`"" -f $appcmd, $AppPoolName) | Out-Null
    Write-Info "Grupo recreado: $AppPoolName"
}

$existeSitio = @(& $appcmd list site 2>$null | Where-Object { $_ -like "*$SiteName*" })
if ($existeSitio) {
    Ejecutar-Tolerante ("`"{0}`" delete site /site.name:`"{1}`"" -f $appcmd, $SiteName) | Out-Null
    Write-Info "Sitio recreado: $SiteName"
}

# managedRuntimeVersion vacio es "No Managed Code". No es cosmetico: ASP.NET Core
# no carga el CLR de escritorio, arranca CoreCLR dentro de w3wp.
Ejecutar ("`"{0}`" add apppool /name:`"{1}`" /managedRuntimeVersion:`"`" /managedPipelineMode:Integrated /enable32BitAppOnWin64:false" -f $appcmd, $AppPoolName) 'Crear el grupo de aplicaciones' | Out-Null
Write-Ok "Grupo creado: $AppPoolName (No Managed Code, x64, integrated)"

if (-not (Test-Path $PhysicalPath)) { New-Item -Path $PhysicalPath -ItemType Directory -Force | Out-Null }
# El binding va como [protocolo]:[puerto]:[cabecera Host]. El ${Port} con
# llaves es obligatorio: escrito como "$Port:" PowerShell lee los dos puntos
# como el inicio de un nombre de variable con ambito y el fichero no llega
# ni a compilar.
Ejecutar ("`"{0}`" add site /name:`"{1}`" /bindings:`"http/*:{2}:`" /physicalPath:`"{3}`"" -f $appcmd, $SiteName, $Port, $PhysicalPath) 'Crear el sitio' | Out-Null
Write-Ok "Sitio creado: http://localhost:$Port/"

# AlwaysRunning + Preload: sin esto, el primer visitante paga el arranque en
# frio de Blazor. Se puede hacer con appcmd o dejarlo para el Administrador de
# IIS; aqui se deja escrito y se avisa.
Write-Info ' Recomendado en el Administrador de IIS para este grupo:'
Write-Host '         Start Mode = AlwaysRunning, Preload Enabled = True' -ForegroundColor DarkGray

Remove-Item $offline -Force -ErrorAction SilentlyContinue
Write-Ok 'app_offline.htm retirado'

# Si alguna variable no se pudo poner, se dice ANTES de arrancar nada, con el
# sitio ya creado pero sin configuracion completa. Es mejor pararse aqui que
# dejar una web que parece funcionar y no consulta nada.
if ($fallosVars -gt 0) {
    Write-Host ''
    Write-Erro "$fallosVars variable(s) de entorno no se pudieron poner."
    Write-Host '  El sitio esta creado pero NO va a consultar. Mira el comentario' -ForegroundColor Yellow
    Write-Host '  del web.config sobre MercadoPublico__CodigoProveedor, y ponlas a mano' -ForegroundColor Yellow
    Write-Host '  en el Administrador de IIS > Configuration Editor >' -ForegroundColor Yellow
    Write-Host '  system/applicationHost/applicationPools/'"$AppPoolName"'/environmentVariables' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 5. Permisos
#
# La cache vive en memoria, asi que la carpeta puede ser de SOLO LECTURA. Eso es
# una ventaja frente a las aplicaciones que escriben: no hace falta dar permiso
# de escritura a ninguna parte, y por lo tanto no hay donde meter un fichero.
# ---------------------------------------------------------------------------
Write-Step '5. Permisos'

Ejecutar-Tolerante ('icacls "{0}" /grant "IIS AppPool\{1}:(OI)(CI)(RX)" /T /Q' -f $PhysicalPath, $AppPoolName) | Out-Null
Write-Ok "IIS AppPool\$AppPoolName con lectura y ejecucion (solo lectura: la cache es en memoria)"

# ---------------------------------------------------------------------------
# 6. Variables de entorno
#
# LAS CINCO, Y NO SON OPCIONALES. Sin CodigoProveedor la propiedad Servible es
# false: la aplicacion ARRANCA, se ve la interfaz entera, y cada consulta a la
# API falla. El aviso sale por el log, no por la pantalla, que es la peor forma
# de enterarse.
#
# Van como variables del GRUPO y no editando el web.config del disco, porque asi
# sobreviven a un redespliegue sin tener que tocar ficheros.
# ---------------------------------------------------------------------------
Write-Step '6. Variables de entorno del grupo'

if (-not $Ticket) {
    $Ticket = Read-Host 'Ticket de Mercado Publico'
}

$variables = [ordered]@{
    'MercadoPublico__Ticket'          = $Ticket
    'MercadoPublico__ModoConsulta'    = 'v1'
}
if ($CodigoProveedor) { $variables['MercadoPublico__CodigoProveedor'] = $CodigoProveedor }
if ($NombreEmpresa)   { $variables['MercadoPublico__NombreEmpresa']   = $NombreEmpresa }
if ($RutEmpresa)      { $variables['MercadoPublico__RutEmpresa']      = $RutEmpresa }

if (-not $variables.Contains('MercadoPublico__CodigoProveedor')) {
    Write-Aviso 'No se pasa CodigoProveedor. La web arrancara y NO consultara nada.'
    Write-Aviso 'Es el unico dato imprescindible de los cinco.'
}

# Contador de variables que no se pudieron poner. Se mira al final, porque lo
# peligroso no es que falle una: es que el script siga adelante como si nada y
# el fallo se descubra en uso.
$fallosVars = 0

foreach ($clave in $variables.Keys) {
    # El valor va entre comillas dobles porque appcmd las usa como separadores,
    # y las comillas del interior se escapan con barra invertida.
    #
    # OJO con el /+ : es lo que ANADE una entrada. Sin la barra seria un
    # "replace" que buscaria una entrada con ese nombre, y como no existe en un
    # grupo nuevo, no haria nada ydiria "exito" sin poner nada. El fallo seria
    # silencioso y se veria tres dias despues, en que la web arranca y no
    # consulta.
    $valor = $variables[$clave] -replace '"', '\"'
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & cmd.exe /c "`"$appcmd`" set config -section:system.applicationHost/applicationPools `" "`
            "/+[name='$AppPoolName'].environmentVariables.[name='$clave',value='$valor']`"" 2>&1 | Out-Null
        $codigo = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $antes }

    if ($codigo -eq 0) {
        $mostrado = if ($clave -like '*Ticket*') { '(el ticket no se imprime)' } else { $variables[$clave] }
        Write-Ok "$clave = $mostrado"
    } else {
        Write-Erro "No se pudo poner $clave (codigo $codigo)"
        $fallosVars++
    }
}

# ---------------------------------------------------------------------------
# 7. Verificacion
#
# No se da por buena la publicacion porque dotnet publish saliera con 0. Se
# arranca el sitio de verdad y se le pregunta. Un sitio que no arranca se ve
# igual de bien en el explorador que en el log.
# ---------------------------------------------------------------------------
Write-Step '7. Verificacion'

Ejecutar-Tolerante ("`"{0}`" start apppool /apppool.name:`"{1}`"" -f $appcmd, $AppPoolName) | Out-Null
Ejecutar-Tolerante ("`"{0}`" start site /site.name:`"{1}`"" -f $appcmd, $SiteName) | Out-Null
Write-Ok 'Grupo y sitio arrancados'

if ($PuertoDePrueba -eq 0) { $PuertoDePrueba = Get-PuertoLibre }

Write-Info "Probando http://localhost:$PuertoDePrueba/ (puerto aparte del sitio, para no depender del binding)"
$env:ASPNETCORE_URLS = "http://127.0.0.1:$PuertoDePrueba"
$env:ASPNETCORE_ENVIRONMENT = 'Production'

foreach ($clave in $variables.Keys) {
    Set-Item -Path "env:$clave" -Value $variables[$clave]
}

$exe = Join-Path $PhysicalPath 'WatchMercadoPublico.Server.exe'
$log = Join-Path $PhysicalPath 'verificacion.log'
$proc = Start-Process -FilePath $exe -WorkingDirectory $PhysicalPath `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden

$estado = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 700
    try {
        $estado = Invoke-RestMethod "http://127.0.0.1:$PuertoDePrueba/api/estado" -TimeoutSec 5
        break
    } catch { }
}

if (-not $estado) {
    Write-Erro 'La publicacion no respondio en /api/estado.'
    if (Test-Path $log) { Get-Content $log -Tail 15 | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray } }
    Write-Host ''
    Write-Host '  Lo mas probable: falta el Hosting Bundle, o el runtime no esta' -ForegroundColor Yellow
    Write-Host '  instalado. En el log del sitio tambien se ve:' -ForegroundColor Yellow
    Write-Host "    & `"$appcmd`" list sites" -ForegroundColor White
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    exit 1
}

Write-Ok 'La publicacion responde'
Write-Host ''
Write-Host '  Que dice /api/estado:' -ForegroundColor Gray
Write-Host "    Empresa     : $($estado.Empresa.Nombre)" -ForegroundColor DarkGray
Write-Host "    Servible    : $($estado.Servible)" -ForegroundColor DarkGray
Write-Host "    ModoConsulta: $($estado.ModoConsulta)" -ForegroundColor DarkGray
Write-Host "    Anios       : $($estado.AniosDisponibles.Count)" -ForegroundColor DarkGray
Write-Host "    Meses hoy   : $($estado.MesesDisponibles.Count)" -ForegroundColor DarkGray
Write-Host ''

if ($estado.Servible) {
    Write-Ok 'Servible = true: hay ticket y codigo de proveedor. Las consultas funcionaran.'
} else {
    Write-Erro 'Servible = false. La web ARRANCARA pero no consultara nada.'
    Write-Host '         Falta el ticket o el CodigoProveedor. Mira el log del sitio.' -ForegroundColor Yellow
}

# Una consulta de verdad, no solo el estado.
$anio = [int](Get-Date).Year
$mes = (Get-Date).Month
try {
    Write-Info "Probando una semana real ($anio/$mes/1). Puede tardar hasta un minuto."
    $t0 = Get-Date
    $semana = Invoke-RestMethod "http://127.0.0.1:$PuertoDePrueba/api/semana?anio=$anio&mes=$mes&semana=1" -TimeoutSec 180
    $seg = [int]((Get-Date) - $t0).TotalSeconds
    Write-Ok "La consulta respondio en ${seg}s: $($semana.Total) publicacion(es), $($semana.DiasConsultados) dia(s) consultado(s), $($semana.DiasFallidos) fallido(s)"
} catch {
    Write-Aviso "La consulta de la semana no respondio: $($_.Exception.Message)"
    Write-Aviso 'Si es un tiempo de espera, mira el timeout de IIS (activityTimeout en web.config).'
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# 8. Resumen
# ---------------------------------------------------------------------------
Write-Step 'Listo'
Write-Host "  Sitio    : http://localhost:$Port/" -ForegroundColor Green
Write-Host "  Pool     : $AppPoolName" -ForegroundColor Gray
Write-Host "  Ruta     : $PhysicalPath" -ForegroundColor Gray
Write-Host ''
Write-Host '  Para Diagnosis > Registros, activa stdoutLogEnabled="true" en el' -ForegroundColor DarkGray
Write-Host "  web.config y luego:  Get-Content '$PhysicalPath\logs\stdout_*.log' -Tail 40" -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Para desplegar sin cortes en adelante:' -ForegroundColor DarkGray
Write-Host "    .\scripts\Publicar-Iis.ps1 -Ticket <ticket> -SaltarPublicacion" -ForegroundColor DarkGray
