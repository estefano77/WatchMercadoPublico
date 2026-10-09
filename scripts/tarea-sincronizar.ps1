<#
.SYNOPSIS
    El paso que ejecuta la tarea programada de Windows. No se ejecuta a mano.

.DESCRIPTION
    Existe por un motivo concreto, medido, y no por gusto:

    Un fallo al INVOCAR el guion de carga -un parametro mal escrito, el fichero
    que no esta, una cadena de ejecucion que no vale- lo escribe el motor de
    PowerShell ANTES de montar la tuberia de salida. Medido:

        $salida = & guion.ps1 -NoExiste 2>&1
        $salida.Count   ->  0

    Ni la redireccion 2>&1 ni el *>> se enteran. Solo un try/catch la ve. Y sin
    eso, la tarea muera con el codigo de resultado 1 y un registro de CERO
    BYTES, que es la peor de las formas de fallar: no hay nada que leer y el
    Programador de tareas tampoco lo explica.

    Con el envoltorio, cualquier fallo -este o cualquier otro- deja su mensaje en
    el registro. Y cada pasada escribe una cabecera con la hora, que es lo que
    hace falta a las siete de la manana para saber si esto corio hoy.

    Por lo demas no hace nada: llama a cargar-base-remota.ps1 y copia lo que
    sale al registro, linea a linea, para que si se cuelga a la mitad se vea lo
    que llevaba hecho.

.PARAMETRO Ingerir
    Trae los datos de Mercado Publico antes de copiar. Sin este, solo copia.

.PARAMETRO Registro
    Fichero donde se escribe. Por defecto, en la carpeta de datos locales del
    usuario, fuera del repositorio.
#>

[CmdletBinding()]
param(
    [switch] $Ingerir,
    [string] $Registro = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Registro) {
    $Registro = Join-Path $env:LOCALAPPDATA 'WatchMerPub\sincronizar.log'
}

$guion = Join-Path $PSScriptRoot 'cargar-base-remota.ps1'

# Sin BOM. Un fichero de texto con BOM lo lee el Bloc de notas como texto y el
# SQL Server como si el primer SELECT llevara un caracter invisible delante.
$utf8 = New-Object System.Text.UTF8Encoding($false)

function AlRegistro([string] $linea) {
    [System.IO.File]::AppendAllText($Registro, $linea + "`r`n", $utf8)
}

# La carpeta puede no existir todavia, porque el instalador la crea despues de
# registrar la tarea y esta se puede lanzar en cualquier momento.
$carpeta = Split-Path -Parent $Registro
if (-not (Test-Path $carpeta)) {
    New-Item -ItemType Directory -Path $carpeta -Force | Out-Null
}

$modo = 'copia solamente'
if ($Ingerir) { $modo = 'ingesta y copia' }

Write-Host ''
AlRegistro ''
AlRegistro ('=' * 70)
AlRegistro "  $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))   $modo"
AlRegistro ('=' * 70)

# UN HASH, NO UN ARRAY, para splatear. Medido, y no es lo que parece:
#
#     $a = @('-Si');  & guion.ps1 @a     ->  $Origen = '-Si', $Si = False
#
# Un ARRAY splateado a un script de PowerShell pasa los elementos POSICIONALES.
# El guion de carga tiene como primer parametro [string] $Origen, asi que -Si se
# le colaba ahi, y $Si se quedaba en falso. Lo que se veía desde fuera era un
# error de sqlcmd que no decia nada de esto:
#
#     No se pudo entrar en el origen: Sqlcmd: '-S': Missing argument.
#
# Un HASH splatea por NOMBRE, que es lo que se quiere. Con un array pasandolo a
# un ejecutable nativo -sqlcmd, bcp- si estaria bien, porque ahi no hay
# parametros con nombre.
# Aqui no hay nadie a quien preguntarle, asi que -Si lo pone el envoltorio y no el
# instalador: el guion de carga pide confirmacion antes de borrar el destino.
$opciones = @{ Si = $true }
if ($Ingerir) { $opciones.Ingerir = $true }

# $LASTEXITCODE PEGAJOSO, y esto no es una suposicion. Medido: tras un guion que
# hace exit 7, al que SIGUE sin llamar a exit, $LASTEXITCODE seguia valiendo 7.
# Sin ponerlo a cero antes de la llamada, una tarea que acabase bien se
# reportaria como fallida.
$global:LASTEXITCODE = 0
$codigoSalida = 0

try {
    # *>&1 y no 2>&1. El guion de carga cuenta todo con Write-Host, y Write-Host
    # escribe en el flujo 6, no en el 1: con 2>&1 el pipeline no recibia NI UNA
    # LINEA y el registro salia con la cabecera y el pie, y en medio un hueco.
    #
    # El ForEachObject es para ir escribiendo linea a linea en vez de acumularlo
    # todo y perderlo si el proceso se cuelga a mitad.
    & $guion @opciones *>&1 | ForEach-Object { AlRegistro ("{0}" -f $_) }

    $codigoSalida = $LASTEXITCODE
}
catch {
    # Lo que llega aqui es justo lo que 2>&1 no ve: el fallo de arranque.
    Write-Host ''
    AlRegistro ''
    AlRegistro '*** No se pudo ni empezar la carga ***'
    AlRegistro ("{0}" -f $_.Exception.Message)
    AlRegistro ("{0}" -f $_.ScriptStackTrace)
    $codigoSalida = 1
}

if ($codigoSalida -eq 0) {
    AlRegistro ''
    AlRegistro '  Termino bien.'
}
else {
    AlRegistro ''
    AlRegistro "  Termino con el codigo $codigoSalida. Lo que esta mal esta arriba."
}

# El codigo de salida se propaga a proposito: es lo que lee el Programador de
# tareas y lo que decide si la proxima pasada se cree que todo fue bien.
exit $codigoSalida
