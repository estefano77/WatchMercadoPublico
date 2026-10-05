<#
.SYNOPSIS
    Levanta WatchMercadoPublico en local.

.DESCRIPTION
    Compila el CSS, publica el servidor y lo arranca. Por defecto en MODO DEMO:
    datos inventados, sin gastar cupo del ticket y sin depender de que la API de
    Mercado Público esté en pie. Es lo que hay que usar mientras se adjusts la
    pantalla.

    En modo real (v1) lee la configuración de
    src\WatchMercadoPublico.Server\appsettings.Development.json, que es donde vive
    el ticket, y la pasa por variables de entorno. El ticket NUNCA se escribe en
    este script ni se imprime.

    POR QUÉ PUBLICA Y NO USA 'dotnet run'

    El target SuperponerClienteBlazor, que es lo que deja el sitio en
    condiciones de funcionar, corre solo AfterTargets="Publish" y con PublishDir
    informado. Con 'dotnet run' no se ejecuta y la aplicación se queda en
    "Cargando" para siempre. No es un detalle menor: es la razón de que este
    script no ofrezca esa opción.

.PARAMETRO Modo
    'demo' (por defecto) o 'v1'.

.PARAMETRO Puerto
    Puerto del servidor. Por defecto 0 = busca uno libre.

.PARAMETRO SinCss
    No recompila Tailwind. Solo si no se ha tocado el CSS.

.EXAMPLE
    .\scripts\arrancar.ps1

.EXAMPLE
    .\scripts\arrancar.ps1 -Modo v1
#>

[CmdletBinding()]
param(
    [ValidateSet('demo', 'v1')]
    [string] $Modo = 'demo',

    [int] $Puerto = 0,

    [switch] $SinCss
)

$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent $PSScriptRoot
$salida = Join-Path $raiz 'publicacion'
$configLocal = Join-Path $raiz 'src\WatchMercadoPublico.Server\appsettings.Development.json'

function Info($t)   { Write-Host "  $t" }
function Malo($t)   { Write-Host "  $t" -ForegroundColor Red }
function Titulo($t) { Write-Host "`n$t" -ForegroundColor Cyan }

# --- 0. Ejecutar herramientas externas sin que sus avisos maten el script ---

# npm y dotnet escriben avisos por stderr SIEMPRE: el "npm notice run ..." que
# aparece al arrancar, los avisos de MSBuild, etc. Con ErrorActionPreference en
# 'Stop', PowerShell convierte eso en un error TERMINANTE y el script se para en
# silencio justo despues de imprimir el aviso. Pasa en cuanto hay que compilar el
# CSS.
#
# Aqui se baja la preferencia solo mientras dura la llamada, y el fallo se decide
# por el codigo de salida, que es lo unico que significa algo.

function Ejecutar([string] $orden, [string] $etiqueta, [int] $ultimasLineas = 8) {
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        $salida = & cmd.exe /c $orden 2>&1
        $codigo = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $antes
    }

    if ($codigo -ne 0) {
        Malo "$etiqueta ha fallado (codigo $codigo). Sus ultimas lineas:"
        @($salida) | Select-Object -Last $ultimasLineas | ForEach-Object { Malo "  $_" }
        exit 1
    }

    return @($salida)
}

# --- 1. Que falte de menos --------------------------------------------------

Titulo 'Comprobando lo que hace falta'

$problemas = @()

foreach ($herramienta in @('dotnet', 'npm')) {
    if (-not (Get-Command $herramienta -ErrorAction SilentlyContinue)) {
        $problemas += "$herramienta no esta en el PATH."
    }
}

if ($Modo -eq 'v1' -and -not (Test-Path $configLocal)) {
    $problemas += "En modo v1 hace falta la configuracion local, y no esta:`n  $configLocal`nSe crea copiando la plantilla, y ahi se pone el ticket:`n  Copy-Item secrets\appsettings.Development.json.ejemplo src\WatchMercadoPublico.Server\appsettings.Development.json"
}

if ($problemas.Count -gt 0) {
    Malo 'No se puede arrancar:'
    $problemas | ForEach-Object { Malo "  - $_" }
    exit 1
}

Info "dotnet  $(dotnet --version)"
Info "node    $(node --version)"

# --- 2. El puerto -----------------------------------------------------------

function Buscar-PuertoLibre {
    $oyente = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    $oyente.Start()
    $libre = ([System.Net.IPEndPoint]$oyente.LocalEndpoint).Port
    $oyente.Stop()
    return $libre
}

if ($Puerto -eq 0) { $Puerto = Buscar-PuertoLibre }

# --- 3. El CSS --------------------------------------------------------------

# Sin esto, app.css se queda como esta en el repositorio y los estilos nuevos no
# se ven. Pasa al ejecutar, y es de los fallos mas duros de reconocer: la pagina
# carga bien y solo parece que el CSS no funciona.

if (-not $SinCss) {
    Titulo 'Compilando el CSS'
    Push-Location $raiz
    try {
        Ejecutar 'npm run css' 'La compilacion del CSS' | Out-Null
        Info 'app.css al dia.'
    }
    finally { Pop-Location }
}

# --- 4. Detener lo que hubiera arrancado ------------------------------------

# Sin esto, publicar encima falla: el .exe esta en uso y el directorio se queda
# a medias. Ademas el error que sale no dice nada util.

$anteriores = Get-Process -Name 'WatchMercadoPublico.Server' -ErrorAction SilentlyContinue
if ($anteriores) {
    Titulo 'Deteniendo la instancia anterior'
    Info "$($anteriores.Count) proceso(s). Puede que sea de otra terminal."
    $anteriores | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# --- 5. Publicar ------------------------------------------------------------

# 'publish' y no 'build': el target SuperponerClienteBlazor solo corre al
# publicar. Ver la cabecera del script.

Titulo 'Publicando'

if (Test-Path $salida) { Remove-Item -Recurse -Force $salida }

Push-Location $raiz
try {
    $salidaPublish = Ejecutar 'dotnet publish .\src\WatchMercadoPublico.Server -c Release -o publicacion -v quiet --nologo' 'La publicacion'
    $salidaPublish | Select-Object -Last 1 | ForEach-Object { Info $_ }
}
finally { Pop-Location }

# --- 6. Configuracion -------------------------------------------------------

# La local NO se publica, a proposito: lleva el ticket dentro. Por eso aqui hay
# que pasarla entera por variables de entorno en vez de copiar el fichero.

$variables = @{
    'ASPNETCORE_ENVIRONMENT'       = 'Production'
    'MercadoPublico__ModoConsulta' = $Modo
}

if ($Modo -eq 'v1') {
    Titulo 'Leyendo la configuracion local'

    # El fichero lleva comentarios // y ConvertFrom-Json no los admite en
    # Windows PowerShell 5.1. Se quitan antes de parsear.
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $texto = [System.IO.File]::ReadAllText($configLocal, $utf8)
    $limpio = ($texto -split "`n" | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"

    $config = $limpio | ConvertFrom-Json
    if (-not $config.MercadoPublico) { throw 'La configuracion local no tiene la seccion MercadoPublico.' }

    $mapeo = @{
        'NombreEmpresa'         = 'MercadoPublico__NombreEmpresa'
        'RutEmpresa'            = 'MercadoPublico__RutEmpresa'
        'CodigoProveedor'       = 'MercadoPublico__CodigoProveedor'
        'Ticket'                = 'MercadoPublico__Ticket'
        'MinutosDeCache'        = 'MercadoPublico__MinutosDeCache'
        'MinutosEntreRefrescos' = 'MercadoPublico__MinutosEntreRefrescos'
        'SegundosTimeout'       = 'MercadoPublico__SegundosTimeout'
    }

    foreach ($clave in $mapeo.Keys) {
        $valor = $config.MercadoPublico.$clave
        if ($null -ne $valor -and "$valor" -ne '') { $variables[$mapeo[$clave]] = "$valor" }
    }

    if (-not $variables.ContainsKey('MercadoPublico__Ticket')) {
        Malo 'El fichero local no tiene Ticket. Ponlo y vuelve a lanzar.'
        exit 1
    }

    Info "Empresa: $variables['MercadoPublico__NombreEmpresa']"
    Info 'Ticket:  leido del fichero local'
}
else {
    Titulo 'Modo demo'

    # Sin esto la cabecera pone "EMPRESA SIN CONFIGURAR", que es lo que trae el
    # appsettings.json publicado. Para pulir la interfaz hace falta un nombre de
    # verdad: si no, no se ve si el titulo cabe en dos lineas ni si el subtitulo
    # se parte bien. Este es falso a proposito y tiene la misma Extension que
    # un nombre real de empresa, para que el ancho del texto sea parecido.
    $variables['MercadoPublico__NombreEmpresa'] = 'EMPRESA DE EJEMPLO PARA DEMOSTRACIONES SPA'

    Info 'Datos inventados. No se llama a la API y no se gasta cupo.'
    Info 'Empresa:  nombre falso, para ver el texto con la Extension de uno real.'
}

# --- 7. Arrancar ------------------------------------------------------------

Titulo 'Arrancando'

$exe = Join-Path $salida 'WatchMercadoPublico.Server.exe'
if (-not (Test-Path $exe)) { Malo "No se encontro $exe"; exit 1 }

$variables['ASPNETCORE_URLS'] = "http://localhost:$Puerto"

# Start-Process hereda el entorno del proceso, asi que basta con fijarlas aqui.
# Se restauran despues: si no, un -Modo v1 dejaria el ticket puesto en la sesion
# de quien lanzo el script.
$guardadas = @{}
foreach ($clave in $variables.Keys) {
    $guardadas[$clave] = [Environment]::GetEnvironmentVariable($clave, 'Process')
    [Environment]::SetEnvironmentVariable($clave, $variables[$clave], 'Process')
}

try {
    # WorkingDirectory NO es opcional. Sin el, el proceso hereda el directorio
    # desde el que se lanzo el script y toma ESA como raiz de contenido: no
    # encuentra el wwwroot y todo lo que no sea /api responde 404, y tampoco
    # carga el appsettings.json. El sitio entero parece roto y el unico sintoma
    # es un 404 en la raiz, que no lleva a ninguna parte.
    $proceso = Start-Process -FilePath $exe -WorkingDirectory $salida -PassThru -WindowStyle Hidden
}
finally {
    foreach ($clave in $guardadas.Keys) {
        [Environment]::SetEnvironmentVariable($clave, $guardadas[$clave], 'Process')
    }
}

# --- 8. Esperar a que responda ---------------------------------------------

# Imprimirlo sin esperar haria que el primer clic cayera en un servidor que aun
# no esta, y eso se ve como un error de red en vez de como un arranque lento.

$url = "http://localhost:$Puerto"
$listo = $false

for ($intento = 1; $intento -le 40; $intento++) {
    Start-Sleep -Milliseconds 250
    try {
        Invoke-WebRequest -Uri "$url/api/estado" -TimeoutSec 2 -UseBasicParsing | Out-Null
        $listo = $true
        break
    }
    catch { }
}

if (-not $listo) {
    Malo 'El servidor no respondio en 10 segundos. Puede que ya este apagado.'
    exit 1
}

$resumen = if ($Modo -eq 'demo') { 'DEMO, datos inventados' } else { 'REAL, consultando Mercado Publico' }

Write-Host ''
Write-Host '  WatchMercadoPublico en marcha' -ForegroundColor Green
Write-Host "  $url"
Write-Host "  $resumen"
Write-Host ''
Write-Host "  Se cierra con:  Stop-Process -Id $($proceso.Id)"
Write-Host ''
