<#
.SYNOPSIS
    Copia los datos de la base local a la base remota, para poder servir en modo
    base de datos desde un hosting donde no se puede instalar el CLR.

.DESCRIPTION
    La aplicacion lee SIEMPRE por procedimientos almacenados, y la ingesta de la
    base la hace MpImportarRango, que es CLR y va DENTRO de la base. Un hosting
    compartido no deja registrar un ensamblado: deniega sp_configure,
    sp_add_trusted_assembly y CREATE ASSEMBLY. Por eso alli la capa de lectura
    si se puede desplegar —01, 04 y 05 no hablan de CLR— pero la base se queda
    vacia y no se actualiza sola.

    Este script salva la primera mitad: copia las filas de la base local, que si
    tiene el importador, a la remota. La segunda mitad no tiene arreglo desde el
    codigo —la remota se congela y hay que volver a correr esto— y por eso el
    guion lo dice al final en vez de dejar que se descubra luego.

    USA BCP, y no un script de INSERT, por tres motivos que se midieron:

      - No necesita permisos en el servidor de destino. bcp va por el protocolo
        TDS como cualquier cliente, asi que funciona contra una base remota sin
        permisos de sysadmin. Ademas no hay que pasar un texto de SQL por el
        panel del hosting.

      - bcp -E conserva los valores de las columnas IDENTITY. Medido: una tabla
        con IDENTITY(1,1) y filas en los IDs 5 y 9 se exporta a un fichero con
        5 y 9, y al importarlo en otra base siguen siendo 5 y 9. Sin eso, cada
        tabla recibiria IDs nuevos y las claves foraneas quedarian apuntando a
        filas equivocadas.

      - El orden de carga lo dictan las claves foraneas, y bcp no las tiene en
        cuenta. Por eso las tablas van en un orden explicito de padre a hijo.

    DOS INTERRUPTORES QUE NO SON LO QUE PARECEN.

    -E NO es la conexion de confianza. En bcp, -E es "keep identity values"; la
    conexion de confianza es -T. En sqlcmd es al reves: -E es la de confianza.
    Copiar los interruptores de una Herramienta a otra da "User name not
    provided" y bcp se limita a imprimir su ayuda, que es lo mas desconcertante
    que puede pasar porque no dice que hay un interruptor equivocado.

    EL FORMATO DE TEXTO NO PIERDE NI LOS NILL NI LAS CADENAS VACIAS. Se
    midio, porque no es lo que se espera.

    Con -c -C 65001, bcp escribe un NULL como un campo de longitud CERO, y una
    cadena vacia de verdad como un unico byte 00. El fichero de una tabla con
    "Publicacion con tilde", NULL y cadena vacia queda asi, en hexadecimal:

        31 09 50 75 62 6C 69 63 61 63 69 C3 B3 6E ...   1<9>Publicaci<n...
        32 09 0D 0A                                    2<9>            <- NULL
        33 09 00 0D 0A                                 3<9><00>         <- cadena vacia

    Ese 00 es lo que impide que NULL y cadena vacia se confundan, y al importar
    vuelven a ser lo que eran. Comprobado comparando el hexadecimal del destino
    con el del origen: identicos en los tres casos, con y sin -k.

    -k NO se usa, y por el motivo contrario al que se supone. Con -k, un campo
    de longitud cero se guarda como NULL, de modo que una cadena vacia de
    verdad se pierde y se convierte en NULL sin decir nada. Ese aviso de bcp
    --"BCP import with a format file will convert empty strings in delimited
    columns to NULL"-- sale cuando hay fichero de formato, que no es este caso.

    -q NO ES OPCIONAL, Y SI FALTA NO PARECE UN ERROR DE DATOS.

    Seis indices de la base son FILTRADOS —con WHERE dentro del indice, no un
    filtro de consulta— y MpLicitacionItem.Subtotal es una columna calculada.
    Las dos cosas exigen QUOTED_IDENTIFIER ON en la sesion que inserta. bcp no lo
    pone, porque en ANSI el valor por defecto es OFF, y entonces cada INSERT
    falla con:

        INSERT failed because the following SET options have incorrect
        settings: 'QUOTED_IDENTIFIER'

    El mensaje no menciona indices ni columnas calculadas, y un archivo de
    texto con 46 filas perfectamente bueno parece la causa. No lo es. Se
    arrangla con bcp -q, que es "quoted identifier", y no con
    ALTER DATABASE SET QUOTED_IDENTIFIER ON, que tambien lo arregla pero
    cambia la sesion de todo el mundo y para siempre.

    El texto del error tampoco dice CUAL de las dos cosas lo pide, asi que
    saberlo de antemano evita un rato mirando el esquema a ciegas.

    OJO: en este guion -q significa quoted identifier. En el de bcp antes
    significaba "consulta". Los dos existen y el que manda es el del tool.

.PARAMETRO Origen
    Servidor de la base de la que se copia. Es la base LOCAL, la que tiene el
    importador. Por defecto localhost\SQLEXPRESS.

.PARAMETRO OrigenBase
    Base de datos de origen. Por defecto WatchMerPub.

.PARAMETRO OrigenUsuario
    Usuario de origen. Vacio = conexion de confianza de Windows, que es lo que
    hay en local.

.PARAMETRO OrigenPassword
    Contrasena de origen. Si se deja vacia se lee de SQLCMDPASSWORD.

.PARAMETOR Remoto
    Cadena de conexion del destino. Por defecto la CadenaConexionSql de
    src\WatchMercadoPublico.Server\appsettings.Development.json, que es la que
    ya se usa para leer en modo base de datos. Ese fichero no se versiona.

.PARAMETOR SinLimpiar
    No borra lo que hubiera en el destino antes de cargar. Por defecto SI se
    borra: la remota se va a congelar y esto se va a repetir, y cargar encima
    sin borrar deja las dos copias mezcladas con los mismos IDs, que es peor que
    no hacer nada.

.PARAMETOR Si
    No pide confirmacion. Para scripts y para cuando ya se ha visto que se
    puede repetir.

.PARAMETOR SoloComprobar
    Compara el esquema origen con el destino y no copia nada. Es el control de
    vuelo: si las columnas no casan, mejor enterarse antes de quedarse a medias.

.PARAMETRO Ingerir
    Trae los datos de Mercado Publico a la base local ANTES de copiar. Sin
    este interruptor solo se copia lo que ya haya, que es lo que hacia el guion
    hasta ahora.

    Es lo que consume el ticket, asi que no viene de serie: una descarga sin
    que nadie la haya pedido es justo lo que MinutosEntreIngestas = 0 evita a
    proposito.

.PARAMETRO Anio
    Año del mes a traer, con -Ingerir. Por defecto, el año en curso.

.PARAMETRO Mes
    Mes a traer, con -Ingerir, del 1 al 12. Por defecto NO se trae el mes en
    curso, sino los ultimos 30 dias, que es la ventana de la aplicacion.

    Un mes concreto se pide para rellenar un hueco: si te has perdido marzo y
    quieres ese mes entero, -Anio 2026 -Mes 3. El guion avisa antes de gastar
    nada si ese mes ya esta entero en la base.

.PARAMETRO SinDetalle
    No trae la ficha de cada licitacion, solo el listado. Sale mas rapido y
    gasta menos, pero en la pantalla no habra organismo ni montos, y el boton de
    "Ver detalle" no tendra nada que enseñar.

.EXAMPLE
    .\scripts\cargar-base-remota.ps1 -SoloComprobar

.EXAMPLE
    .\scripts\cargar-base-remota.ps1 -Si

.EXAMPLE
    .\scripts\cargar-base-remota.ps1 -Ingerir -Si

.EXAMPLE
    .\scripts\cargar-base-remota.ps1 -Ingerir -Anio 2026 -Mes 3 -Si
#>

[CmdletBinding()]
param(
    [string] $Origen = 'localhost\SQLEXPRESS',
    [string] $OrigenBase = 'WatchMerPub',
    [string] $OrigenUsuario = '',
    [string] $OrigenPassword = '',
    [string] $Remoto = '',
    [switch] $SinLimpiar,
    [switch] $Si,
    [switch] $SoloComprobar,

    # --- Ingesta -------------------------------------------------------------
    # Que el guion traiga los datos de la API antes de copiarlos. Sin esto solo
    # copia lo que haya en la base local, que es lo que hacia hasta ahora.
    [switch] $Ingerir,

    # Que mes traer. Con -Ingestar y sin -Anio/-Mes, se traen los ultimos 30 dias,
    # que es la ventana que usa la aplicacion. Con un mes concreto se trae ese
    # mes entero, que es para rellenar un hueco concreto a mano.
    [int] $Anio = 0,
    [int] $Mes = 0,

    # Que la ingesta traiga tambien la ficha de cada licitacion, no solo el
    # listado. Sin esto no se ve el organismo ni los montos en ninguna parte.
    [switch] $SinDetalle
)

$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent $PSScriptRoot
$configLocal = Join-Path $raiz 'src\WatchMercadoPublico.Server\appsettings.Development.json'

function Info($t)  { Write-Host "  $t" }
function Malo($t)  { Write-Host "  $t" -ForegroundColor Red }
function Titulo($t) { Write-Host "`n$t" -ForegroundColor Cyan }

# El orden de carga. Padre antes que hijo, por las tres claves foraneas:
#   MpLicitacionDetalle        -> MpLicitacion
#   MpLicitacionEstadoHistorico -> MpLicitacion
#   MpLicitacionItem            -> MpLicitacionDetalle
# MpConsulta y MpEmpresa no tienen ninguna FK apuntandoles, asi que podrian ir
# donde quiera. Van primero por logica, no por necesidad.
$tablas = @(
    'dbo.MpEmpresa'
    'dbo.MpConsulta'
    'dbo.MpLicitacion'
    'dbo.MpLicitacionDetalle'
    'dbo.MpLicitacionItem'
    'dbo.MpLicitacionEstadoHistorico'
)

# El orden de borrado es el inverso. Si se borra al reves, MpLicitacionItem
# dejaria filas apuntando a un MpLicitacionDetalle que ya no existe.
$tablasParaBorrar = $tablas[($tablas.Count - 1)..0]

# --- 0. Herramientas ---------------------------------------------------------

Titulo 'Comprobando lo que hace falta'

foreach ($exe in @('sqlcmd', 'bcp')) {
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
        Malo "No esta $exe en el PATH. Viene con las herramientas de cliente de SQL Server."
        exit 1
    }
}
Info 'sqlcmd y bcp encontrados.'

# --- 1. De donde sale la cadena del destino ----------------------------------

if ($Remoto) {
    Info 'Destino: el que se paso en -Remoto'
}
else {
    if (-not (Test-Path $configLocal)) { throw "No esta $configLocal y no se paso -Remoto." }

    # El fichero lleva comentarios // que ConvertFrom-Json no admite en Windows
    # PowerShell 5.1. Se quitan antes de parsear. Mismo truco que arrancar.ps1.
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $texto = [System.IO.File]::ReadAllText($configLocal, $utf8)
    $limpio = ($texto -split "`n" | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"

    $config = $limpio | ConvertFrom-Json
    if (-not $config.MercadoPublico) { throw 'La configuracion local no tiene la seccion MercadoPublico.' }
    if (-not $config.MercadoPublico.CadenaConexionSql) { throw 'La configuracion local no tiene CadenaConexionSql.' }

    $Remoto = $config.MercadoPublico.CadenaConexionSql
    Info 'Destino: el CadenaConexionSql de appsettings.Development.json'
}

# Se parte a mano porque no se puede usar SqlConnectionStringBuilder sin cargar
# el ensamblado, y la cadena es "clave=valor;clave=valor" a pelo.
$kv = @{}
foreach ($par in ($Remoto -split ';')) {
    $i = $par.IndexOf('=')
    if ($i -gt 0) { $kv[$par.Substring(0, $i).Trim()] = $par.Substring($i + 1).Trim() }
}

foreach ($necesaria in @('Server', 'Database')) {
    if (-not $kv.ContainsKey($necesaria)) { throw "La cadena de destino no tiene $necesaria." }
}

# La contrasena viaja por SQLCMDPASSWORD y no en la linea de comandos: asi no
# sale en la salida, ni queda en la lista de procesos, ni en el historial.
$destinoUsaSqlAuth = $false
$destinoPassword = ''
if ($kv.ContainsKey('User ID') -or $kv.ContainsKey('User Id') -or $kv.ContainsKey('UID')) {
    $destinoUsuario = @($kv['User ID'], $kv['User Id'], $kv['UID'] | Where-Object { $_ })[0]
    $destinoUsaSqlAuth = $true
    if ($kv.ContainsKey('Password')) { $destinoPassword = $kv['Password'] }
    elseif ($kv.ContainsKey('Pwd')) { $destinoPassword = $kv['Pwd'] }
    else { throw 'El destino lleva usuario pero no contrasena.' }

    # sqlcmd si la lee del entorno; bcp no, y se le pasa con -P mas abajo.
    $env:SQLCMDPASSWORD = $destinoPassword
}
elseif ($kv.ContainsKey('Integrated Security') -and $kv['Integrated Security'] -eq 'True') {
    $destinoUsuario = ''
}
else {
    throw 'El destino no lleva usuario ni Integrated Security. No se sabe como entrar.'
}

# -h -1 quita la cabecera. Sin eso, el primer elemento de la salida que devuelve
# sqlcmd es la linea de guiones —o una vacia, en las columnas sin nombre como
# @@SERVERNAME— y leer [0] daria eso en vez del valor.
function SqlDestino([string] $sql) {
    $fichero = Join-Path $env:TEMP ("cbr-destino-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    [System.IO.File]::WriteAllText($fichero, "SET NOCOUNT ON;`n$sql", (New-Object System.Text.UTF8Encoding($false)))
    try {
        # -I pone QUOTED_IDENTIFIER ON. Sin esto, sqlcmd conecta con los
        # valores de ANSI, donde esa opcion esta en OFF, y cualquier DELETE o
        # UPDATE sobre estas tablas falla con el mismo error de los indices
        # filtrados. En bcp lo resuelve -q y en sqlcmd -I: son el mismo
        # problema con dos herramientas distintas y el mismo mensaje.
        $a = @('-S', $kv['Server'], '-d', $kv['Database'], '-b', '-W', '-h', '-1', '-f', '65001', '-I', '-s', '|', '-i', $fichero)
        # -E es la conexion de confianza EN SQLCMD, al reves que en bcp, donde -E
        # es conservar los identity. Sin esto, y sin -U, sqlcmd prueba
        # autenticacion SQL con el usuario de Windows y falla.
        if ($destinoUsaSqlAuth) { $a += @('-U', $destinoUsuario) } else { $a += '-E' }
        & sqlcmd @a
    }
    finally { Remove-Item $fichero -ErrorAction SilentlyContinue }
}

function SqlOrigen([string] $sql) {
    $fichero = Join-Path $env:TEMP ("cbr-origen-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    [System.IO.File]::WriteAllText($fichero, "SET NOCOUNT ON;`n$sql", (New-Object System.Text.UTF8Encoding($false)))
    try {
        $a = @('-S', $Origen, '-d', $OrigenBase, '-b', '-W', '-h', '-1', '-f', '65001', '-I', '-s', '|', '-i', $fichero)
        if ($OrigenUsuario) {
            $a += @('-U', $OrigenUsuario)
            if ($OrigenPassword) { $env:SQLCMDPASSWORD = $OrigenPassword }
        }
        else { $a += '-E' }
        & sqlcmd @a
    }
    finally { Remove-Item $fichero -ErrorAction SilentlyContinue }
}

# --- 2 bis. La ingesta, si se ha pedido ---------------------------------------
#
# Va ANTES de comparar los esquemas, y no por orden: va antes de la copia porque
# la copia se lleva lo que hay, y lo que hay lo decide esto.
#
# Dos decisiones que no son obvias:
#
# EL RANGO POR DEFECTO SON 30 DIAS, NO EL MES EN CURSO. Es la ventana de la
# aplicacion (IngestaMercadoPublico.DiasMirandoAtras) y con ella el procedimiento
# se salta solo los dias ya consultados. Con el mes entero se traeria tambien
# todo lo anterior al ultimo intento, gastando cupo en dias que ya estan, y el
# motivo por el que MinutosEntreIngestas vale 0 es precisamente no gastar eso.
#
# UN MES CONCRETO SI SE IMPORTA ENTERO, y por el motivo contrario: si el usuario
# pide marzo es porque le falta marzo, y las partes que ya tuviera no pasan nada.
#
# Y el guion avisa ANTES de gastar, en vez de preguntar: con -Si esta pensado
# para las tareas programadas, que no pueden contestarte nada. Avisa, y si el mes
# ya esta entero se salta solo, que es lo que evita la pasada mensual de una
# tarea que correra cada dia.

function LeerTicketLocal {
    if (-not (Test-Path $configLocal)) { return $null }

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $texto = [System.IO.File]::ReadAllText($configLocal, $utf8)
    $limpio = ($texto -split "`n" | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"

    try { $config = $limpio | ConvertFrom-Json }
    catch { return $null }

    if (-not $config.MercadoPublico) { return $null }
    return $config.MercadoPublico
}

if ($Ingerir) {
    Titulo 'Ingesta: trayendo datos de Mercado Publico'

    $local = LeerTicketLocal
    $ticket = ''
    $codigo = ''

    if ($local) {
        $ticket = [string] $local.Ticket
        $codigo = [string] $local.CodigoProveedor
    }

    if (-not $ticket -or $ticket.StartsWith('CAMBIAR-ESTE-VALOR')) {
        Malo 'No hay ticket en appsettings.Development.json, y sin ticket no se puede traer nada.'
        Malo 'Copia secrets\appsettings.Development.json.ejemplo y ponlo, o quita -Ingerir.'
        exit 1
    }

    if (-not $codigo) {
        Malo 'No hay CodigoProveedor en appsettings.Development.json.'
        exit 1
    }

    # EL RANGO. Con un mes concreto, ese mes entero. Sin mes, los ultimos 30
    # dias, que es la ventana de la aplicacion.
    #
    # El motivo de que el defecto NO sea el mes en curso: con el mes entero se
    # traeria tambien todo lo anterior al ultimo intento, gastando cuota en
    # dias que ya estan. Y el motivo por el que MinutosEntreIngestas vale 0 es
    # precisamente no gastar eso sin que nadie lo haya pedido.
    $hoy = Get-Date
    $fin = $hoy

    if ($Mes -ne 0) {
        if ($Anio -eq 0) { $Anio = $hoy.Year }
        if ($Mes -lt 1 -or $Mes -gt 12) { Malo "El mes $Mes no existe."; exit 1 }

        # A MEDIANOCHE. Get-Date -Day 1 conserva la hora del momento, y el
        # guion llegaba a imprimir
        #
        #     Rango: 04/01/2026 15:59:59 a 04/30/2026 15:59:59
        #
        # que parece un fallo. Al procedimiento solo le llega yyyyMMdd, asi que
        # la hora no cambia el resultado, pero es ruido que hace dudar.
        $inicio = Get-Date -Year $Anio -Month $Mes -Day 1 -Hour 0 -Minute 0 -Second 0

        # El '- 1' va DENTRO del AddDays. Fuera, restarle un entero a un date
        # da "Operand type clash: date is incompatible with int", y el mensaje
        # no menciona ni la aritmetica ni la fecha.
        $fin = $inicio.AddMonths(1).AddDays(-1)
        if ($fin -gt $hoy) { $fin = $hoy }

        $desdeTxt = $inicio.ToString('dd/MM/yyyy')
        $hastaTxt = $fin.ToString('dd/MM/yyyy')
        Info "Rango: $desdeTxt a $hastaTxt, el mes entero o hasta hoy si no ha terminado"
    }
    else {
        if ($Anio -ne 0) { Malo '-Anio sin -Mes no significa nada. Pon los dos, o ninguno.'; exit 1 }
        $inicio = $hoy.AddDays(-29)
        $desdeTxt = $inicio.ToString('dd/MM/yyyy')
        $hastaTxt = $fin.ToString('dd/MM/yyyy')
        Info 'Rango: los ultimos 30 dias, la ventana de la aplicacion'
    }

    $desdeApi = $inicio.ToString('yyyyMMdd')
    $hastaApi = $fin.ToString('yyyyMMdd')

    # Que el mes pedido este ya entero. No se PREGUNTA, se DICE, porque con -Si
    # no hay nadie a quien preguntarle.
    $mesCompleto = $false

    if ($Mes -ne 0) {
        # DIAS HABILES, y no de calendario. El procedimiento no pregunta los
        # fines de semana, asi que comparar contra los 30 dias del mes hacia que
        # este atajo no se activara NUNCA: 22 consultados nunca llegan a 30, y el
        # guion se ponia a preguntar dias que ya estaban.
        $diasDelMes = 0
        for ($d = $inicio.Date; $d -le $fin.Date; $d = $d.AddDays(1)) {
            if ($d.DayOfWeek -ne 'Saturday' -and $d.DayOfWeek -ne 'Sunday') { $diasDelMes++ }
        }

        # Los consultados TAMBIEN se filtran por dia habil, porque en MpConsulta
        # hay dias de fin de semana pointeros de cuando alguien.importo a mano o
        # de una epoca anterior. Contarlos todos daba "27 dias habiles
        # consultados de 22", que se contradice a si mismo y hace dudar de si
        # el atajo funciona.
        $desdeIso = $inicio.ToString('yyyy-MM-dd')
        $hastaIso = $fin.ToString('yyyy-MM-dd')

        $sqlCuenta = 'SELECT COUNT(DISTINCT FechaDia) FROM dbo.MpConsulta'
        $sqlCuenta += " WHERE Exito = 1 AND CodigoProveedor = N'$codigo'"
        $sqlCuenta += " AND FechaDia >= '$desdeIso' AND FechaDia <= '$hastaIso'"
        $sqlCuenta += ' AND DATEPART(WEEKDAY, FechaDia) NOT IN (1, 7)'

        $cuenta = @(SqlOrigen $sqlCuenta)[0]
        $consultados = [int] $cuenta

        if ($consultados -ge $diasDelMes) {
            $mesCompleto = $true
            Info "Ese mes ya tiene consultados $consultados de los $diasDelMes dias habiles. No se gasta nada."
            Info 'Se sigue a la copia con lo que haya.'
        }
    }

    if (-not $mesCompleto) {
        $conDetalle = 1
        if ($SinDetalle) {
            $conDetalle = 0
            Info 'Sin detalle: solo el listado, sin la ficha de cada licitacion.'
        }

        # @soloFaltantes = 1 para que el procedimiento se salte lo ya consultado.
        # Es lo que hace la aplicacion, y sin esto la pasada gastaria cuota en
        # dias que ya estan, que es justo lo que no se quiere en una tarea diaria.
        #
        # El ticket va por DECLARACION dentro del script y no en la linea de
        # comandos. Y se dobla la comilla por si acaso: un ticket es un UUID y
        # no lleva comillas, pero un token mal pegado no puede romper el script.
        $ticketSql = $ticket.Replace("'", "''")
        $codigoSql = $codigo.Replace("'", "''")

        $sql = 'SET NOCOUNT ON;' + "`n"
        $sql += 'SET QUOTED_IDENTIFIER ON;' + "`n"
        $sql += "DECLARE @t nvarchar(200) = N'$ticketSql';" + "`n"
        $sql += "DECLARE @p nvarchar(50)  = N'$codigoSql';" + "`n"
        $sql += 'DECLARE @r nvarchar(max);' + "`n"
        $sql += 'EXEC dbo.MpImportarRango' + "`n"
        $sql += "     @desde = '$desdeApi'," + "`n"
        $sql += "     @hasta = '$hastaApi'," + "`n"
        $sql += '     @codigoProveedor = @p,' + "`n"
        $sql += '     @ticket = @t,' + "`n"
        $sql += "     @conDetalle = $conDetalle," + "`n"
        $sql += '     @soloFaltantes = 1,' + "`n"
        $sql += '     @resultado = @r OUTPUT;' + "`n"
        $sql += 'SELECT @r;'

        $fichero = Join-Path $env:TEMP ('cbr-ingesta-{0}.sql' -f [guid]::NewGuid().ToString('N'))
        [System.IO.File]::WriteAllText($fichero, $sql, (New-Object System.Text.UTF8Encoding($false)))

        $salida = @()
        $codigoSalida = 0
        try {
            # -I por el mismo motivo que en SqlOrigen: sin QUOTED_IDENTIFIER ON el
            # procedimiento falla con el error de los indices filtrados.
            $a = @('-S', $Origen, '-d', $OrigenBase, '-b', '-W', '-h', '-1', '-f', '65001', '-I', '-i', $fichero)
            if ($OrigenUsuario) {
                $a += @('-U', $OrigenUsuario)
                if ($OrigenPassword) { $env:SQLCMDPASSWORD = $OrigenPassword }
            }
            else { $a += '-E' }

            $salida = & sqlcmd @a 2>&1
            $codigoSalida = $LASTEXITCODE
        }
        finally { Remove-Item $fichero -ErrorAction SilentlyContinue }

        if ($codigoSalida -ne 0) {
            Malo 'La ingesta ha fallado. No se copia: la base local puede quedarse a medias.'
            $salida | Select-Object -First 6 | ForEach-Object { Malo "   $_" }
            exit 1
        }

        $resumen = ($salida | Where-Object { "$_".Trim() -ne '' }) -join "`n"
        if ($resumen) {
            Info 'Resultado de la ingesta:'
            ($resumen -split "`n") | ForEach-Object { Write-Host "      $_" }
        }
        else {
            Info 'La ingesta no devolvio resumen.'
        }
    }

    Write-Host ''
}

# --- 2. Comprobar que se puede entrar, y en las dos bases -------------------

Titulo 'Comprobando el acceso'

try {
    $nombreDestino = @(SqlDestino 'SELECT @@SERVERNAME;')[0].Trim()
    if ($LASTEXITCODE -ne 0) { throw 'El destino no respondio.' }
    Info "Destino: $nombreDestino / $($kv['Database'])"
}
catch {
    Malo "No se pudo entrar en el destino: $_"
    exit 1
}

try {
    $nombreOrigen = @(SqlOrigen 'SELECT @@SERVERNAME;')[0].Trim()
    if ($LASTEXITCODE -ne 0) { throw 'El origen no respondio.' }
    Info "Origen:  $nombreOrigen / $OrigenBase"
}
catch {
    Malo "No se pudo entrar en el origen: $_"
    exit 1
}

if ($nombreOrigen -eq $nombreDestino -and $kv['Database'] -eq $OrigenBase) {
    Malo 'El origen y el destino son la misma base. Copiaria una base en si misma.'
    exit 1
}

# --- 3. El esquema tiene que casar, o la carga se parte por la mitad ---------

Titulo 'Comparando el esquema'

$consultaEsquema = @'
SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN c.max_length = -1 THEN '(max)' ELSE '(' + CAST(c.max_length AS VARCHAR) + ')' END + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
FROM sys.columns c
WHERE c.object_id = OBJECT_ID('{0}')
ORDER BY c.column_id;
'@

$desajustes = 0
foreach ($tabla in $tablas) {
    $enOrigen  = @(SqlOrigen  ($consultaEsquema -f $tabla)) | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $enDestino = @(SqlDestino ($consultaEsquema -f $tabla)) | ForEach-Object { $_.Trim() } | Where-Object { $_ }

    if (-not $enOrigen)  { Malo "$tabla no existe en el origen."; $desajustes++; continue }
    if (-not $enDestino) { Malo "$tabla no existe en el destino. Despliega 01-esquema-mercadopublico.sql ahi."; $desajustes++; continue }

    $soloOrigen  = $enOrigen  | Where-Object { $enDestino -notcontains $_ }
    $soloDestino = $enDestino | Where-Object { $enOrigen  -notcontains $_ }

    if ($soloOrigen -or $soloDestino) {
        Malo "$tabla NO coincide:"
        $soloOrigen  | ForEach-Object { Malo "   solo en origen:  $_" }
        $soloDestino | ForEach-Object { Malo "   solo en destino: $_" }

        # QUE HACER CON ESTO. Antes solo salia la lista de diferencias y se
        # quedaba en "algo no casa". El caso real: se anadio Descripcion a
        # MpLicitacionItem, se ejecuto 01 en la base de DESARROLLO y se lanzo
        # este guion. El destino no la tenia, porque 01 no se habia ejecutado
        # alla, y el mensaje decia:
        #
        #     solo en origen:  Descripcion nvarchar(4000) NULL
        #
        # que no dice nada de que hay que hacer. Se puede leer como "el destino
        # esta mal" y no como "el destino necesita el ALTER".
        #
        # Y es que el CREATE TABLE guardado de 01 no anade columnas a una tabla
        # que ya existe: hay un ALTER guardado aparte, con COL_LENGTH. Por eso
        # la pista dice "01 entero" y no "la columna".
        if ($soloOrigen) {
            Write-Host ''
            Malo '   El destino no tiene lo que tiene el origen.'
            Malo '   Suele ser que 01-esquema-mercadopublico.sql no se ha ejecutado'
            Malo '   en el destino DESPUES de anadir una columna al origen. Es un'
            Malo '   ALTER guardado con COL_LENGTH, no el CREATE TABLE: correr solo'
            Malo '   el CREATE no anade nada a una tabla que ya existe.'
            Malo '   Aplicar 01 entero en el destino y volver a lanzar este guion.'
        }
        $desajustes++
    }
    else {
        Info ("{0,-30} {1} columnas, todo igual" -f $tabla, $enOrigen.Count)
    }
}

if ($desajustes -gt 0) {
    Write-Host ''
    Malo "$desajustes tabla(s) no casan. No se copia nada: una carga a medias deja la base peor que vacia."
    exit 1
}

if ($SoloComprobar) {
    Write-Host ''
    Info 'SoloComprobar: el esquema casa. No se ha copiado nada.'
    Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
    return
}

# --- 4. Que hay en cada lado, antes de tocar nada ----------------------------

Titulo 'Lo que hay ahora mismo'

$antesDestino = @(SqlDestino 'SELECT CAST(COUNT(*) AS VARCHAR) FROM dbo.MpLicitacion;')[0]
$ahoraOrigen = @(SqlOrigen 'SELECT CAST(COUNT(*) AS VARCHAR) FROM dbo.MpLicitacion;')[0]
Info "Licitaciones  origen: $ahoraOrigen   destino: $antesDestino"

if (-not $Si) {
    Write-Host ''
    Write-Host '  Se va a BORRAR el contenido del destino y copiar el del origen.'
    $respuesta = Read-Host '  Van bien? (s/N)'
    if ($respuesta -ne 's') {
        Info 'Cancelado. No se ha tocado nada.'
        Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
        return
    }
}

# --- 5. Borrar el destino, en orden inverso ----------------------------------

if (-not $SinLimpiar) {
    Titulo 'Vaciando el destino'

    foreach ($tabla in $tablasParaBorrar) {
        $n = [int] @(SqlDestino ('SELECT CAST(COUNT(*) AS VARCHAR) FROM {0};' -f $tabla))[0]
        if ($n -eq 0) {
            Info ("{0,-30} ya estaba vacia" -f $tabla)
            continue
        }

        # El borrado se COMPRUEBA. Antes no se hacia, y era grave: el guion
        # anunciaba "N filas borradas" usando el recuento de antes, sin mirar si
        # el DELETE habia funcionado. Fallaba —por el QUOTED_IDENTIFIER— y el
        # guion seguia adelante diciendo que habia borrado. Luego la carga se
        # rompia con "duplicate key" y el aviso apuntaba al archivo de datos,
        # que era inocente.
        #
        # Es el fallo mas caro que ha tenido este guion: no hacia falta un
        # error para que los datos quedaran mal, solo hacia falta no mirar.
        $salidaBorrado = @(SqlDestino ('DELETE FROM {0};' -f $tabla))
        if ($LASTEXITCODE -ne 0) {
            Malo "${tabla}: el DELETE ha fallado y no se ha vaciado."
            $salidaBorrado | Select-Object -First 4 | ForEach-Object { Malo "   $_" }
            Malo 'No se copia nada encima de una tabla que no se ha vaciado: se mezclarian las dos copias.'
            exit 1
        }

        $restantes = [int] @(SqlDestino ('SELECT CAST(COUNT(*) AS VARCHAR) FROM {0};' -f $tabla))[0]
        if ($restantes -ne 0) {
            Malo "${tabla}: quedan $restantes filas tras el DELETE."
            exit 1
        }

        Info ("{0,-30} {1} filas borradas" -f $tabla, $n)
    }
}

# --- 6. Exportar e importar, tabla a tabla -----------------------------------

Titulo 'Copiando'

$fallos = 0

# -----------------------------------------------------------------------
# LOS TERMINADORES, que no son los de por defecto, y tienen que ser los mismos
# al exportar y al importar.
#
# bcp -c usa \t para separar CAMPOS y \r\n para separar FILAS. Y los dos pueden
# ESTAR DENTRO de un dato, porque hay columnas de texto libre y lo que manda es
# lo que escriba el comprador o la institucion. Se ha visto con datos de verdad,
# en dos tablas distintas:
#
#   - CR LF dentro de MpLicitacionItem.Descripcion. Con el terminador de por
#     defecto, bcp cree que ahi acaba la fila, parte el item en dos, y la
#     segunda mitad la intenta meter en Subtotal, que es decimal:
#
#         #@ Row 4, Column 1: Invalid character value for cast specification @#
#
#     Se perdieron seis items de seis. El resto de la tabla entra bien, asi que
#     la copia PARECE que funciona.
#
#   - Un TABULADOR dentro de MpLicitacionDetalle.Descripcion, que es
#     nvarchar(max) y traia un pliego entero. El TAB parte el CAMPO, y el trozo
#     largo que se queda delante cae en Tipo, que es nvarchar(20):
#
#         #@ Row 53, Column 6: String data, right truncation @#
#
#     Y con -r puesto pero sin -t seguia fallando, porque el TAB no es el
#     terminador de fila: es el de campo. Son dos cosas distintas y hacen falta
#     las dos.
#
# Por eso van los dos, y con valores que no aparecen en un pliego de condiciones
# de un municipio pero que se ven a la primera si alguna vez se colaran.
#
# MEDIDO, con la tabla de detalle real (53 filas, 1 con TAB, 9 con CR LF) y con
# la de items (58 filas, 6 con CR LF, 2 descripciones vacias):
#
#     con -t y -r  ->  todas las filas, y el hash IDENTICO entre origen y copia
#                      los TAB y los CR LF conservados dentro del texto
#                      y las descripciones vacias siguen siendo vacias, no NULL
#
# No se limpia el dato para que quepa en el formato: SE CAMBIA EL FORMATO. Un
# REPLACE de los saltos o de los tabuladores perderia informacion que escribio el
# comprador, que es justo lo que esta columna existe para guardar.
#
# OJO con lo que NO se toca: -k. Ese es el que convierte la cadena vacia en NULL.
# Con -c y sin -k, vacia y NULL siguen siendo distintas.
# -----------------------------------------------------------------------
$terminadorCampo = '%%F%%'
$terminadorFila = '%%R%%'

foreach ($tabla in $tablas) {
    $fichero = Join-Path $env:TEMP ("cbr-{0}.txt" -f [guid]::NewGuid().ToString('N'))
    $errores = "$fichero.err"

    $aOrigen = @("$OrigenBase.$tabla", 'out', $fichero,
        '-S', $Origen, '-c', '-C', '65001', '-E',
        '-t', $terminadorCampo, '-r', $terminadorFila)
    if ($OrigenUsuario) {
        # -P tambien aqui: bcp no lee la contrasena del entorno en ningun
        # sentido, ni para salir ni para entrar.
        $aOrigen += @('-U', $OrigenUsuario)
        if ($OrigenPassword) { $aOrigen += @('-P', $OrigenPassword) }
    }
    else { $aOrigen += '-T' }

    $salidaOut = & bcp @aOrigen 2>&1
    if ($LASTEXITCODE -ne 0) {
        Malo "${tabla}: no se pudo exportar."
        $salidaOut | Select-Object -First 4 | ForEach-Object { Malo "   $_" }
        $fallos++
        continue
    }

    # OJO: bcp NO lee la contrasena de SQLCMDPASSWORD. Eso solo es de sqlcmd.
    # Con -U y sin -P, bcp se la PIDE por consola: aparece un "Password:" que
    # espera seis veces seguidas, una por tabla, y en un guion no interactivo se
    # queda esperando indefinitely en vez de fallar. Medido.
    #
    # No hay forma de pasar la contrasena a bcp sin que salga en la linea de
    # comandos, porque bcp no acepta cadena de conexion completa como si hace
    # sqlcmd con -C. Asi que sale. La ventana de exposicion es la lista de
    # procesos de la maquina mientras dura el bcp, que dura segundos. No es
    # bonito y no hay alternativa.
    $aDestino = @("$($kv['Database']).$tabla", 'in', $fichero,
        '-S', $kv['Server'], '-c', '-C', '65001', '-E', '-q', '-e', $errores,
        '-t', $terminadorCampo, '-r', $terminadorFila)
    if ($destinoUsaSqlAuth) { $aDestino += @('-U', $destinoUsuario, '-P', $destinoPassword) }
    else { $aDestino += '-T' }   # -T es la conexion de confianza en bcp

    $salidaIn = & bcp @aDestino 2>&1
    $copiadas = 0
    $coincidencia = $salidaIn | Select-String '(\d+) rows copied'
    if ($coincidencia) { $copiadas = [int] $coincidencia.Matches[0].Groups[1].Value }

    if ($LASTEXITCODE -ne 0) {
        Malo "${tabla}: no se pudo importar. Esto se ha quedado a medias."
        $salidaIn | Select-Object -First 4 | ForEach-Object { Malo "   $_" }
        $fallos++
    }
    else {
        Info ("{0,-30} {1} filas" -f $tabla, $copiadas)
    }

    if ((Test-Path $errores) -and (Get-Item $errores).Length -gt 0) {
        Malo "${tabla}: avisos de bcp"
        Get-Content $errores | Select-Object -First 3 | ForEach-Object { Malo "   $_" }
    }

    Remove-Item $fichero, $errores -ErrorAction SilentlyContinue
}

# El identity hay que dejarlo por encima de lo que se ha cargado, y no basta con
# no tocarlo. Con la base vacia y bcp -E, SQL Server ya lo sube solo al maximo
# insertado. Pero si la base remota TENIA filas y las hemos borrado, el contador
# se queda donde estaba, y DBCC CHECKIDENT WITH RESEED, 1 —que es lo temptedo—
# lo rebajaria: con IDs cargados hasta 13, el siguiente INSERT que se cuelgue
# por donde sea intentaria el 1 y chocaria contra una fila de verdad.
#
# Asi que se pregunta el nombre de la columna identity y su maximo, y se reserva
# en ese mas uno. DBCC no admite variables, de ahi el texto armado.
$consultaIdentity = @'
SELECT c.name FROM sys.identity_columns c WHERE c.object_id = OBJECT_ID('{0}');
'@

$tablasConIdentity = @(
    'dbo.MpEmpresa'
    'dbo.MpConsulta'
    'dbo.MpLicitacion'
    'dbo.MpLicitacionDetalle'
    'dbo.MpLicitacionItem'
    'dbo.MpLicitacionEstadoHistorico'
)

foreach ($tabla in $tablasConIdentity) {
    $columna = @(SqlDestino ($consultaIdentity -f $tabla)) | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 1
    if (-not $columna) { continue }   # esta tabla no tiene identity

    $maximo = @(SqlDestino ("SELECT ISNULL(CAST(MAX([{0}]) AS VARCHAR), '0') FROM {1};" -f $columna, $tabla))[0]
    $siguiente = [int] $maximo + 1

    [void] (SqlDestino ("DBCC CHECKIDENT ('{0}') WITH RESEED, {1};" -f $tabla, $siguiente))
    Info ("{0,-30} identity reservado desde {1}" -f $tabla, $siguiente)
}

# --- 7. Comprobar que ha llegado todo ----------------------------------------

Titulo 'Comprobando'

$descuadre = 0
foreach ($tabla in $tablas) {
    $o = [int] @(SqlOrigen  ('SELECT CAST(COUNT(*) AS VARCHAR) FROM {0};' -f $tabla))[0]
    $d = [int] @(SqlDestino ('SELECT CAST(COUNT(*) AS VARCHAR) FROM {0};' -f $tabla))[0]

    if ($o -eq $d) { Info ("{0,-30} origen {1,4}  destino {2,4}" -f $tabla, $o, $d) }
    else { Malo ("{0,-30} origen {1,4}  destino {2,4}   NO CUADRA" -f $tabla, $o, $d); $descuadre++ }
}

Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
Write-Host ''

if ($descuadre -gt 0 -or $fallos -gt 0) {
    Malo "La copia no ha ido bien: $fallos fallo(s) de bcp y $descuadre tabla(s) que no cuadran."
    exit 1
}

Titulo 'Hecho'

Info 'La base remota ya tiene lo mismo que la local.'
Write-Host ''
Write-Host '  OJO: esto no se actualiza solo mientras nadie lo ejecute.' -ForegroundColor Yellow
Write-Host '  La ingesta vive en MpImportarRango, que es CLR, y el hosting no deja' -ForegroundColor Yellow
Write-Host '  registrar el ensamblado. Nadie lo hace desde ahi.' -ForegroundColor Yellow
Write-Host '  Con la tarea de Windows instalada -scripts\instalar-tarea.ps1- si que' -ForegroundColor Yellow
Write-Host '  pasa todos los dias, pero porque lo corre esta maquina, no el hosting.' -ForegroundColor Yellow
Write-Host ''
Info 'Para refrescar: .\scripts\cargar-base-remota.ps1 -Si'
