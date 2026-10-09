<#
.SYNOPSIS
    Instala, quita o ejecuta la tarea de Windows que mantiene la base remota al dia.

.DESCRIPTION
    La base remota de un hosting compartido no se puede actualizar desde dentro:
    no hay SQL Server Agent, y el importador es CLR y ahi no se puede registrar.
    Lo que si se puede es que una tarea de esta maquina avise por la API y copie
    lo descargado, una vez al dia.

    Esa tarea es la que instala este guion. Llama a cargar-base-remota.ps1 con
    -Ingerir -Si, de modo que la cadena entera cabe en un paso: preguntar,
    descargar, borrar el destino y copiar.

    Y LO HACE AQUI, EN ESTA MAQUINA, y no subiendo MinutosEntreIngestas en el
    servidor. Es la decision que mas importa de todo el guion, y por esto:

    - El ticket se queda en la maquina de desarrollo. Con la ingesta en el
      servidor haria falta subirlo al hosting, y es una credencial con un tope de
      10.000 consultas al dia.

    - La ingesta del servidor solo ocurre mientras alguien tiene la aplicacion
      abierta. Con MinutosEntreIngestas a 5, un domingo por la tarde la base
      remota se queda con lo del viernes. Una tarea de Windows no depende de que
      haya alguien mirando.

    - Se puede apagar la ingesta sin tocar la aplicacion, con -SinIngesta.

    Lo que NO hace bien, y conviene saber antes de instalarla: la copia BORRA las
    tablas del destino antes de rellenarlas. Durante esos segundos, quien este
    mirando la pagina ve una base vacia o un error. Por eso el defecto es a las
    06:30 y no a cualquier hora.

.PARAMETRO Hora
    Hora local de la ejecucion diaria, en HH:mm. Por defecto, 06:30.

    Temprano a proposito: la copia borra y tarda unos segundos, y a las seis y
    media es mas probable que nadie este mirando que a las cuatro de la tarde.

.PARAMETRO SinIngesta
    Copia, pero no pregunta a Mercado Publico. No gasta cuota y es lo que hay que
    poner si el objetivo es solo tener la base de desarrollo en el hosting.

    Aun asi copia: seguiria borrando y reescribiendo el destino.

.PARAMETRO EjecutarAhora
    Lanza la tarea recien instalada y espera a que termine, y muestra el final
    del registro. Es la forma de saber que funciona sin esperar a manana.

    Con ingesta, gasta cuota: la primera pasada real trae los ultimos 30 dias que
    falten, del orden de veinte llamadas. Con -SinDetalle son la mitad.

.PARAMETRO Quitar
    Desinstala la tarea. No borra el registro ni toca la base remota.

.PARAMETRO Tarea
    Nombre de la tarea en el Programador de tareas. Por defecto,
    WatchMerPub-Sincronizar.

.EXAMPLE
    .\scripts\instalar-tarea.ps1

    Instala la tarea diaria de las 06:30, con ingesta y copia.

.EXAMPLE
    .\scripts\instalar-tarea.ps1 -EjecutarAhora

    Instala la tarea y la lanza, para ver que funciona hoy.

.EXAMPLE
    .\scripts\instalar-tarea.ps1 -Hora 23:00 -SinIngesta

    Reinstala la tarea, sin tocar la API, a las once de la noche.

.EXAMPLE
    .\scripts\instalar-tarea.ps1 -Quitar

    La desinstala.
#>

[CmdletBinding()]
param(
    [string] $Hora = '06:30',
    [switch] $SinIngesta,
    [switch] $EjecutarAhora,
    [switch] $Quitar,
    [string] $Tarea = 'WatchMerPub-Sincronizar'
)

$ErrorActionPreference = 'Stop'

function Info($t)  { Write-Host "  $t" }
function Malo($t)  { Write-Host "  $t" -ForegroundColor Red }
function Titulo($t) { Write-Host "`n$t" -ForegroundColor Cyan }

$guion = Join-Path $PSScriptRoot 'cargar-base-remota.ps1'
$envoltorio = Join-Path $PSScriptRoot 'tarea-sincronizar.ps1'

# El registro va FUERA del repositorio. Si fuera dentro habria que anadirlo al
# .gitignore, y un fichero que se rellena solo es justo el que un dia se
# committe por error con la cadena de conexion dentro.
$rutaRegistro = Join-Path $env:LOCALAPPDATA 'WatchMerPub'
$registro = Join-Path $rutaRegistro 'sincronizar.log'

# --------------------------------------------------------------------------
# -Quitar
# --------------------------------------------------------------------------

if ($Quitar) {
    Titulo 'Quitando la tarea'

    $existente = Get-ScheduledTask -TaskName $Tarea -ErrorAction SilentlyContinue
    if (-not $existente) {
        Info "No hay ninguna tarea llamada $Tarea. Nada que hacer."
        exit 0
    }

    Unregister-ScheduledTask -TaskName $Tarea -Confirm:$false
    Info "Quitada la tarea $Tarea."
    Info "El registro se queda en $registro, por si hay que mirarlo."
    exit 0
}

# --------------------------------------------------------------------------
# Comprobaciones previas
# --------------------------------------------------------------------------

Titulo 'Comprobando lo que hace falta'

if (-not (Test-Path $guion)) {
    Malo "No existe $guion."
    exit 1
}
Info 'El guion de carga esta.'

if (-not (Test-Path $envoltorio)) { Malo "No existe $envoltorio."; exit 1 }
Info 'El envoltorio de la tarea esta.'

# Un camino con comillas dobles romperia los argumentos de la accion, porque van
# entrecomillados. Es un caso patologico, pero avisar es mas barato que fallar a
# barato que fallar a las seis y media sin decir por que.
if ($envoltorio.Contains('"') -or $registro.Contains('"')) {
    Malo 'El caminho del repositorio o el del registro tiene comillas dobles, y eso rompe la tarea.'
    exit 1
}

try { $momento = [datetime]::ParseExact($Hora, 'HH:mm', [cultureinfo]::InvariantCulture) }
catch {
    Malo "La hora '$Hora' no vale. Se espera HH:mm, con dos digitos: 06:30, no 6:30."
    exit 1
}
Info "Se ejecutara a las $($momento.ToString('HH:mm')) hora local, todos los dias."

# --------------------------------------------------------------------------
# La accion
# --------------------------------------------------------------------------

Titulo 'Preparando la accion'

# La accion llama al ENVOLTORIO, no al guion de carga, y no lleva la redireccion
# *>>. Las dos cosas se hicieron primero al reves y las dos fallaron:
#
# 1. Con -Command y el *>>, un fallo al invocar el guion no llegaba al registro.
#    Lo escribe el motor antes de montar la tuberia, y medido:
#
#        $salida = & guion.ps1 -NoExiste 2>&1   ->   $salida.Count = 0
#
#    El registro quedaba en CERO BYTES y la tarea con codigo 1: la peor forma de
#    fallar, porque no hay nada que leer. El envoltorio lo resuelve con un
#    try/catch, que si lo ve.
#
# 2. -SinIngesta se colaba como parametro del guion de carga, que no lo tiene:
#    es un interruptor de ESTE guion. Ahora lo que se decide es si se pasa
#    -Ingerir al envoltorio, y no hay ningun parametro inventado por el camino.
$parametrosDelEnvoltorio = @('-Registro', ('"{0}"' -f $registro))
if ($SinIngesta) {
    Info 'Copia solamente. No se pregunta a Mercado Publico y no se gasta cuota.'
}
else {
    # -Ingerir es un interruptor: no pasarlo ya es decir que no. Escribir
    # -Ingerir:$false tambien funciona, pero es ruido.
    $parametrosDelEnvoltorio += '-Ingerir'
    Info 'Trae los ultimos 30 dias que falten y despues copia.'
}

$argumentos = ('-NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f $envoltorio, ($parametrosDelEnvoltorio -join ' '))

# -ExecutionPolicy Bypass es POR PROCESO: no cambia la maquina. Sin esto, en un
# equipo con la ejecucion restringida la tarea falla con "no se permite la
# ejecucion de scripts" y no hay ningun mensaje que lo diga, porque el fallo
# ocurre antes de que el envoltorio pueda escribir nada.
$accion = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argumentos

Info 'La accion:'
Info "  powershell $argumentos"

# --------------------------------------------------------------------------
# El disparo, los ajustes y quien lo ejecuta
# --------------------------------------------------------------------------

$disparador = New-ScheduledTaskTrigger -Daily -At $momento

$ajustes = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -DontStopOnIdleEnd

# IgnoreNew: si a las 06:30 de un dia la anterior todavia esta copiando, la
# nueva no arranca. Sin esto se encadenan dos copias que se borran el destino
# mutuamente, y es el fallo mas dificil de ver de todos: no da error, solo
# datos a medias.
#
# StartWhenAvailable: si el equipo estaba apagado a las 06:30, se ejecuta al
# encenderlo. Sin esto, un fin de semana apagado pierde el dia entero.
#
# Dos horas de limite: es un techo, no un objetivo. Una ingesta con detalle de
# treinta dias puede tardar, y una que se queda colgada sin limite bloquearia
# las siguientes para siempre.

# Interactive, y no "funcione tanto si ha iniciado sesion como si no". El guion
# abre la base local con -E, que es autenticacion de Windows y necesita la
# identidad del usuario que la ejecuta: con el token de otro, sqlcmd no entra.
#
# La contrapartida, y hay que decirla: si la sesion esta cerrada, la tarea no
# corre. Se ejecuta al iniciar sesion, por StartWhenAvailable. En una maquina de
# desarrollo, encendida y con el usuario dentro, es exactamente lo que se quiere.
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel Limited

# --------------------------------------------------------------------------
# Instalar
# --------------------------------------------------------------------------

Titulo 'Instalando la tarea'

$yaEstaba = Get-ScheduledTask -TaskName $Tarea -ErrorAction SilentlyContinue
if ($yaEstaba) {
    Info "Ya habia una tarea llamada $Tarea. Se sustituye por esta."
}

Register-ScheduledTask `
    -TaskName $Tarea `
    -Action $accion `
    -Trigger $disparador `
    -Settings $ajustes `
    -Principal $principal `
    -Description 'Trae los datos de Mercado Publico a la base local y los copia a la base remota del hosting. Requiere la sesion del usuario abierta.' `
    -Force | Out-Null

if (-not (Test-Path $rutaRegistro)) {
    New-Item -ItemType Directory -Path $rutaRegistro -Force | Out-Null
}

Info "Instalada la tarea $Tarea."

$info = Get-ScheduledTaskInfo -TaskName $Tarea
if ($info.NextRunTime) {
    Info "La proxima vez: $($info.NextRunTime)"
}

Titulo 'Lo que hace y lo que cuesta'

Info 'Cada dia:'
if ($SinIngesta) {
    Info '  1. borra las tablas del destino y copia lo que haya en la base local'
}
else {
    Info '  1. pregunta a la API los ultimos 30 dias que falten (@soloFaltantes = 1,'
    Info '     asi que un dia ya descargado no se vuelve a preguntar)'
}
Info '  2. borra las tablas del destino y copia lo descargado con bcp'
Info '  3. compara los recuentos y avisa si alguno no cuadra'
Write-Host ''
if ($SinIngesta) {
    Info 'El ticket no se toca: no se llama a la API. En el hosting tampoco hace'
    Info 'falta, porque en modo sql la lectura no lo necesita.'
}
else {
    Info 'El ticket no sale de esta maquina: se lee del appsettings.Development.json'
    Info 'local, y en el hosting no hace falta porque alli no se ingiere.'
}
Write-Host ''
Info "Registro: $registro"

# --------------------------------------------------------------------------
# -EjecutarAhora
# --------------------------------------------------------------------------

if (-not $EjecutarAhora) {
    Write-Host ''
    Write-Host '  Para probarla hoy:  .\scripts\instalar-tarea.ps1 -EjecutarAhora' -ForegroundColor Yellow
    Write-Host '  Para quitarla:      .\scripts\instalar-tarea.ps1 -Quitar' -ForegroundColor Yellow
    Write-Host ''
    exit 0
}

Titulo 'Ejecutandola ahora'

if (-not $SinIngesta) {
    Write-Host '  Esto pregunta a la API de verdad y gasta cuota del ticket.' -ForegroundColor Yellow
}

Start-ScheduledTask -TaskName $Tarea

# Se espera hasta 30 minutos. Una pasada con detalle de treinta dias puede ser
# lenta, pero si en media hora sigue fuera, algo esta mal y no tiene sentido
# seguir mirando.
#
# DOS BANDERAS y no una. Con una sola, al agotarse los 30 minutos el bloque de
# resultados de abajo decia "Termino el ..." de una tarea que no habia
# terminado, que es justo cuando mas caro sale: nadie se fia de un resumen que
# no se sostiene.
$limite = (Get-Date).AddMinutes(30)
$terminada = $false
$agotado = $false

while (-not $terminada) {
    Start-Sleep -Seconds 2
    $estado = (Get-ScheduledTask -TaskName $Tarea).State

    if ($estado -ne 'Running') {
        $terminada = $true
    }
    elseif ((Get-Date) -gt $limite) {
        $agotado = $true
        $terminada = $true
    }
    else {
        Write-Host '.' -NoNewline
    }
}

Write-Host ''

if ($agotado) {
    Malo 'Sigue corriendo despues de 30 minutos. No se espera mas.'
    Malo 'No se dice si acabo bien porque no se sabe. Lo que lleva hecho esta en el registro.'
}
else {
    $info = Get-ScheduledTaskInfo -TaskName $Tarea
    if ($info.LastRunTime) {
        Info "Termino el $([datetime]$info.LastRunTime)"
    }

    # 0 es "la tarea corrio". Lo que haya hecho mal, si hizo algo mal, esta en
    # el registro: el codigo de resultado de Task Scheduler no distingue un
    # fallo de red de un disco lleno.
    if ($info.LastTaskResult -eq 0) {
        Info 'La tarea termino sin error.'
    }
    else {
        Malo "La tarea termino con el codigo $($info.LastTaskResult)."
    }
}

Write-Host ''
Titulo 'Ultimas lineas del registro'

if (Test-Path $registro) {
    Get-Content $registro -Tail 25 -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
}
else {
    Info 'No hay registro todavia. O la tarea no llego a escribir, o se borro.'
}

Write-Host ''
exit 0
