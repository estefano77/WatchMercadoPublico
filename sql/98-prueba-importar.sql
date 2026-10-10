SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
/* ===========================================================================
   Prueba del procedimiento de importacion.

   NO USA UN TICKET REAL, y por eso hay que decirlo antes de nada: lo que se
   prueba aqui es el CAMINO DE ERROR, que es la mitad del producto.

   Con un ticket falso, Mercado Publico responde

       HTTP 203  {"Codigo":203,"Mensaje":"Ticket no válido."}

   ...que es exactamente lo que tiene que pasar. Si un dia esto devolviera un
   200 con datos, el ticket falso habria sido aceptado y habria que mirar por
   que.

   Con eso se comprueba, de punta a punta y contra el servidor de verdad:

     - Que la URL se arma con DDMMAAAA y no con D M M AAAA (el bug del dia sin
       cero, que solo falla entre el 1 y el 9).
     - Que un 4xx NO se reintenta, y por tanto no se generan seis esperas.
     - Que se anota en MpConsulta con el codigo HTTP y el CodigoApi.
     - Que un dia que falla NO tira el resto del rango.
     - Que el resumen distingue "no se pudo consultar" de "no habia nada".

   Y las comprobaciones de parseo del JSON (los items anidados, los montos, el
   desfase horario) NO se pueden probar aqui sin un ticket: la API no devuelve
   una ficha a un ticket falso. Esas estan en 99-prueba-esquema.sql, que mete
   datos con la forma exacta de una respuesta real.

   =========================================================================== */


/* ---------------------------------------------------------------------------
   1. La comprobacion de la fecha: un dia del 1 al 9
   ---------------------------------------------------------------------------
   Si la URL se armara con "dMMyyyy" en vez de "ddMMyyyy", el 4 de octubre
   mandaria "4102026", siete digitos, y la API responderia 500 con el CodigoApi
   10300. Ese fallo solo aparece entre el 1 y el 9, y por eso parece
   intermitente. Aqui se comprueba que el 4 SI se manda con cero.
   ------------------------------------------------------------------------- */
PRINT '=== 1. El formato de la fecha ===';

DECLARE @fecha date = '2026-10-04';
DECLARE @fechaApi nvarchar(8) =
      RIGHT('0' + CONVERT(nvarchar(2), DAY(@fecha)), 2)
    + RIGHT('0' + CONVERT(nvarchar(2), MONTH(@fecha)), 2)
    + CONVERT(nvarchar(4), YEAR(@fecha));

IF @fechaApi = N'04102026'
    PRINT 'OK    el 4 de octubre se manda como 04102026, con el cero del dia.';
ELSE
    PRINT 'FALLO se armaria como ' + @fechaApi + ', que son siete digitos.';

-- Y el resto de dias del ano, por si el caso raro se esconde en otro mes.
DECLARE @fallos int = 0, @d date = '2026-01-01';
WHILE @d <= '2026-12-31'
BEGIN
    DECLARE @esperado nvarchar(8) =
          RIGHT('0' + CONVERT(nvarchar(2), DAY(@d)), 2)
        + RIGHT('0' + CONVERT(nvarchar(2), MONTH(@d)), 2)
        + CONVERT(nvarchar(4), YEAR(@d));

    IF @esperado <> FORMAT(@d, 'ddMMyyyy')
        SET @fallos = @fallos + 1;

    SET @d = DATEADD(DAY, 1, @d);
END

IF @fallos = 0
    PRINT 'OK    los 365 dias del 2026 se arman bien contra FORMAT.';
ELSE
    PRINT 'FALLO ' + CONVERT(nvarchar(10), @fallos) + ' dia(s) no coinciden con FORMAT.';


/* ---------------------------------------------------------------------------
   2. La validacion de los parametros
   ---------------------------------------------------------------------------
   Cada caso tiene que fallar SOLO y con su mensaje. Un procedimiento que se
   pasa de largo con un @ticket vacio se pondria a preguntar y gastaria cupo
   sin poder hacer nada.
   ------------------------------------------------------------------------- */
PRINT '';
PRINT '=== 2. Las validaciones ===';

DECLARE @resultado nvarchar(max);
DECLARE @fallosValidacion int = 0;

-- 2.1 Rango invertido.
BEGIN TRY
    EXEC dbo.MpImportarRango @desde = '2026-10-10', @hasta = '2026-10-01',
                             @codigoProveedor = '1', @ticket = 'x',
                             @resultado = @resultado OUTPUT;
    PRINT 'FALLO 2.1 acepto un rango invertido.';
    SET @fallosValidacion = @fallosValidacion + 1;
END TRY
BEGIN CATCH
    IF ERROR_MESSAGE() LIKE '%anterior%'
        PRINT 'OK    2.1  rango invertido rechazado: ' + ERROR_MESSAGE();
    ELSE
    BEGIN
        PRINT 'FALLO 2.1 fallo por otra cosa: ' + ERROR_MESSAGE();
        SET @fallosValidacion = @fallosValidacion + 1;
    END
END CATCH

-- 2.2 Sin codigo de proveedor.
BEGIN TRY
    EXEC dbo.MpImportarRango @desde = '2026-10-01', @hasta = '2026-10-02',
                             @codigoProveedor = N'  ', @ticket = 'x',
                             @resultado = @resultado OUTPUT;
    PRINT 'FALLO 2.2 acepto un codigo de proveedor vacio.';
    SET @fallosValidacion = @fallosValidacion + 1;
END TRY
BEGIN CATCH
    IF ERROR_MESSAGE() LIKE '%proveedor%'
        PRINT 'OK    2.2  sin codigo de proveedor rechazado.';
    ELSE
    BEGIN
        PRINT 'FALLO 2.2 fallo por otra cosa: ' + ERROR_MESSAGE();
        SET @fallosValidacion = @fallosValidacion + 1;
    END
END CATCH

-- 2.3 Sin ticket.
BEGIN TRY
    EXEC dbo.MpImportarRango @desde = '2026-10-01', @hasta = '2026-10-02',
                             @codigoProveedor = '71284', @ticket = N'',
                             @resultado = @resultado OUTPUT;
    PRINT 'FALLO 2.3 acepto un ticket vacio.';
    SET @fallosValidacion = @fallosValidacion + 1;
END TRY
BEGIN CATCH
    IF ERROR_MESSAGE() LIKE '%ticket%'
        PRINT 'OK    2.3  sin ticket rechazado.';
    ELSE
    BEGIN
        PRINT 'FALLO 2.3 fallo por otra cosa: ' + ERROR_MESSAGE();
        SET @fallosValidacion = @fallosValidacion + 1;
    END
END CATCH

-- 2.4 Rango enorme.
BEGIN TRY
    EXEC dbo.MpImportarRango @desde = '2020-01-01', @hasta = '2026-12-31',
                             @codigoProveedor = '71284', @ticket = 'x',
                             @resultado = @resultado OUTPUT;
    PRINT 'FALLO 2.4 acepto un rango de siete años.';
    SET @fallosValidacion = @fallosValidacion + 1;
END TRY
BEGIN CATCH
    IF ERROR_MESSAGE() LIKE '%400%'
        PRINT 'OK    2.4  rango de mas de 400 dias rechazado.';
    ELSE
    BEGIN
        PRINT 'FALLO 2.4 fallo por otra cosa: ' + ERROR_MESSAGE();
        SET @fallosValidacion = @fallosValidacion + 1;
    END
END CATCH

-- 2.5 Ventana de sondeo negativa.
BEGIN TRY
    EXEC dbo.MpImportarRango @desde = '2026-10-01', @hasta = '2026-10-02',
                             @codigoProveedor = '71284', @ticket = 'x',
                             @diasSondeo = -1,
                             @resultado = @resultado OUTPUT;
    PRINT 'FALLO 2.5 acepto una ventana de sondeo negativa.';
    SET @fallosValidacion = @fallosValidacion + 1;
END TRY
BEGIN CATCH
    IF ERROR_MESSAGE() LIKE '%diasSondeo%'
        PRINT 'OK    2.5  ventana de sondeo negativa rechazada.';
    ELSE
    BEGIN
        PRINT 'FALLO 2.5 fallo por otra cosa: ' + ERROR_MESSAGE();
        SET @fallosValidacion = @fallosValidacion + 1;
    END
END CATCH

-- 2.6 Ventana de sondeo enorme.
--
-- El 30 es el tope porque el rango entero ya se limita a 400 dias: una ventana
-- mayor que eso no distinguiria nada, seria "vuelve a preguntar todo". Por eso
-- 31 tiene que rechazarse y no aceptarse como un numero sin sentido.
BEGIN TRY
    EXEC dbo.MpImportarRango @desde = '2026-10-01', @hasta = '2026-10-02',
                             @codigoProveedor = '71284', @ticket = 'x',
                             @diasSondeo = 31,
                             @resultado = @resultado OUTPUT;
    PRINT 'FALLO 2.6 acepto una ventana de sondeo de 31 dias.';
    SET @fallosValidacion = @fallosValidacion + 1;
END TRY
BEGIN CATCH
    IF ERROR_MESSAGE() LIKE '%diasSondeo%'
        PRINT 'OK    2.6  ventana de sondeo de mas de 30 dias rechazada.';
    ELSE
    BEGIN
        PRINT 'FALLO 2.6 fallo por otra cosa: ' + ERROR_MESSAGE();
        SET @fallosValidacion = @fallosValidacion + 1;
    END
END CATCH

-- 2.7 El parametro EXISTE y se llama exactamente asi.
--
-- Esto parece tonto, y no lo es. La regla de saltar dias ya descargados cambio
-- de "@dia < @hoy" a "@dia < @desdeSondeo", que depende de un parametro. Si un
-- dia se estropiase el nombre al desplegar, el procedimiento seguiria
-- COMPILANDO -porque el valor por defecto lo taparia- y no volveria a preguntar
-- ayer ni anteayer, que es justo lo que se quiere arreglar. Y no habria ningun
-- error: solo datos que faltan sin decir nada.
--
-- OJO CON EL NOMBRE: en sys.parameters el nombre lleva la @ delante
-- (@diasSondeo), al reves que en sys.columns, donde no la lleva. Buscar
-- 'diasSondeo' sin la arroba no encuentra nada y el EXISTS sale falso.
DECLARE @haySondeo bit = CASE WHEN EXISTS (
    SELECT 1 FROM sys.parameters
    WHERE object_id = OBJECT_ID(N'dbo.MpImportarRango')
      AND name = N'@diasSondeo')
    THEN 1 ELSE 0 END;

IF @haySondeo = 1
    PRINT 'OK    2.7  el procedimiento tiene el parametro @diasSondeo.'
ELSE
BEGIN
    PRINT 'FALLO 2.7 el procedimiento NO tiene @diasSondeo.';
    SET @fallosValidacion = @fallosValidacion + 1;
END

-- 2.8 Y ADEMAS TIENE QUE VALER 3 CUANDO NO SE LE PASA.
--
-- El primer intento de esta comprobacion fue mirar el valor por defecto en el
-- catalogo, y no se puede: en esta instancia (SQL Server 16.0.1190.2)
-- sys.parameters.has_default_value viene a 0 en TODOS los parametros, tambien
-- en los que llevan = 1 en el CREATE. No es que falte el valor; es que la
-- columna no se rellena para procedimientos. Asi que se comprueba LLAMANDO, que
-- ademas es lo que de verdad importa: que alguien que ejecute el EXEC a mano,
-- como el que imprime 02-registrar-ensamblado.sql, se lleve la ventana de tres
-- y no la de antes.
--
-- El rango es un sabado a proposito, el 3 de octubre de 2026: los fines de
-- semana se saltan antes de preguntar nada, asi que esta llamada NO gasta ni una
-- peticion de la API y se puede ejecutar en cualquier momento.
BEGIN TRY
    EXEC dbo.MpImportarRango @desde = '2026-10-03', @hasta = '2026-10-03',
                             @codigoProveedor = '71284', @ticket = 'x',
                             @resultado = @resultado OUTPUT;

    IF @resultado LIKE '%Ventana de sondeo en dias : 3%'
        PRINT 'OK    2.8  sin pasarle el parametro, la ventana por defecto es 3.'
    ELSE
    BEGIN
        PRINT 'FALLO 2.8 sin pasarle el parametro la ventana NO vale 3. El resumen fue:';
        PRINT @resultado;
        SET @fallosValidacion = @fallosValidacion + 1;
    END
END TRY
BEGIN CATCH
    PRINT 'FALLO 2.8 fallo por otra cosa: ' + ERROR_MESSAGE();
    SET @fallosValidacion = @fallosValidacion + 1;
END CATCH


/* ---------------------------------------------------------------------------
   3. El camino de error de verdad, contra la API
   ---------------------------------------------------------------------------
   Tres dias laborables de octubre de 2026 con un ticket que no existe.

   Lo que tiene que pasar:
     - Tres intentos en MpConsulta (uno por dia), NO dieciocho. Un 4xx no se
       reintenta, y eso se comprueba aqui.
     - Cada uno con CodigoHttp y CodigoApi.
     - Ni una licitacion guardada, porque no se pudo comprobar nada. Esto es lo
       importante: una lista vacia por no haber preguntado NO es lo mismo que
       una lista vacia porque no habia nada.
   ------------------------------------------------------------------------- */
PRINT '';
PRINT '=== 3. La consulta con ticket falso (contra la API de verdad) ===';

/* -----------------------------------------------------------------------
   ESTA PRUEBA BORRABA LA BASE DE VERDAD. YA NO.

   Todo lo que hay entre aqui y el final va DENTRO de una transaccion que se
   revierte al final. Antes no habia ninguna, y el paso 3 empezaba con seis
   DELETE FROM sin mas:

       DELETE FROM dbo.MpConsulta;
       DELETE FROM dbo.MpLicitacionEstadoHistorico;
       DELETE FROM dbo.MpLicitacionItem;
       DELETE FROM dbo.MpLicitacionDetalle;
       DELETE FROM dbo.MpLicitacion;
       DELETE FROM dbo.MpEmpresa;

   y se quedaban ahi. Pasa de verdad: se ejecuto este fichero contra la base de
   desarrollo y se llevo por delante 921 consultas, 48 licitaciones, 48 detalles
   y 57 items. Todo lo que habia. Para recuperar hubo que reimportar 2024-2026
   entero desde la API.

   Que sea una PRUEBA no la hace inocua, y esta es de las que de verdad borran.
   Que 99-prueba-esquema.sql estuviera protegida y esta no es justo el fallo que
   hace que la siguiente vez no se pise: las dos son, igual y llanamente, ficheros
   de pruebas que se ejecutan contra la base de desarrollo.

   Y NO HAY NINGUN GO EN ESTE FICHERO, a proposito. Una transaccion abierta
   antes de un GO no sobrevive al siguiente lote: el ROLLBACK del final estaria
   en otro lote y revirteria una transaccion vacia, mientras las filas se
   quedan. Es el mismo motivo por el que 99-prueba-esquema.sql va entero en un
   lote. Ver el paso final, que lo comprueba y avisa si alguna vez vuelve a
   pasar.

   FOTOGRAFIA DE LO QUE HAY ANTES DE EMPEZAR, para poder comprobar al final que
   todo ha vuelto como estaba. Se guardan CONTOS y SUMAS DE CHECKSUM, no solo el
   numero: si el ROLLBACK fallara, los numeros volverian a ser los mismos
   -porque los de ahora tambien serian "todas las filas"- y solo el checksum
   delataria que ahora hay otras filas. */
BEGIN TRAN;

DECLARE @antes98 TABLE (
    Tabla sysname, Cuantas int, Suma int);

INSERT @antes98 (Tabla, Cuantas, Suma)
SELECT 'MpEmpresa', COUNT(*), CHECKSUM_AGG(CHECKSUM(CodigoProveedor)) FROM dbo.MpEmpresa
UNION ALL SELECT 'MpConsulta', COUNT(*), CHECKSUM_AGG(CHECKSUM(FechaHora, TipoConsulta, FechaDia)) FROM dbo.MpConsulta
UNION ALL SELECT 'MpLicitacion', COUNT(*), CHECKSUM_AGG(CHECKSUM(CodigoExterno)) FROM dbo.MpLicitacion
UNION ALL SELECT 'MpLicitacionDetalle', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId)) FROM dbo.MpLicitacionDetalle
UNION ALL SELECT 'MpLicitacionItem', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId, Correlativo, NombreProducto)) FROM dbo.MpLicitacionItem
UNION ALL SELECT 'MpLicitacionEstadoHistorico', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId, CodigoEstado)) FROM dbo.MpLicitacionEstadoHistorico;

DELETE FROM dbo.MpConsulta;
DELETE FROM dbo.MpLicitacionEstadoHistorico;
DELETE FROM dbo.MpLicitacionItem;
DELETE FROM dbo.MpLicitacionDetalle;
DELETE FROM dbo.MpLicitacion;
DELETE FROM dbo.MpEmpresa;

DECLARE @antes bigint = (SELECT COUNT_BIG(*) FROM dbo.MpConsulta);

-- 2026-10-05, 06 y 07: lunes, martes y miercoles. Los tres laborables.
DECLARE @t0 datetime2(3) = SYSUTCDATETIME();

EXEC dbo.MpImportarRango
    @desde                = '2026-10-05',
    @hasta                = '2026-10-07',
    @codigoProveedor      = N'71284',
    @ticket               = N'TICKET-QUE-NO-EXISTE',
    @conDetalle           = 0,
    @maxIntentosDia       = 6,
    @minutosEsperaInicial = 2,
    @segundosEsperaMaxima = 30,
    @resultado            = @resultado OUTPUT;

PRINT @resultado;

DECLARE @nuevas bigint = (SELECT COUNT_BIG(*) FROM dbo.MpConsulta) - @antes;

/* Lo que se espera: UN intento por dia, porque el 203 es un rechazo del ticket
   y un rechazo del ticket no mejora con insistir.

   Y no son seis por dia. Esta comprobacion es la que mas valor tiene, y la que
   mas cosas destapa: cuando el procedimiento trataba el 203 como si fuera
   reintentable, el rango entero tardaba 185 s y MpConsulta acababa con 18 filas
   (6 por dia) para obtener 18 veces el mismo "no". Un ticket invalido se sabe
   en el primer intento; los dos minutos siguientes son espera pura. */
IF @nuevas = 3
    PRINT 'OK    3.1  tres intentos en total: el rechazo de ticket NO se reintenta.';
ELSE
    PRINT 'FALLO 3.1 ' + CONVERT(nvarchar(20), @nuevas) + ' intentos en vez de 3.';

/* Las tres filas tienen que llevar el codigo de la API, no solo el HTTP. */
IF EXISTS (SELECT 1 FROM dbo.MpConsulta
           WHERE FechaDia IS NOT NULL
             AND (CodigoHttp IS NULL OR CodigoApi IS NULL))
    PRINT 'FALLO 3.2 hay filas sin CodigoHttp o sin CodigoApi.';
ELSE
    PRINT 'OK    3.2  cada intento guardo su CodigoHttp y su CodigoApi.';

/* Y el mensaje tiene que ser el de la API, no un "(null)". */
IF EXISTS (SELECT 1 FROM dbo.MpConsulta
           WHERE Exito = 0 AND (Mensaje IS NULL OR Mensaje = N'(null)'))
    PRINT 'FALLO 3.3 hay un fallo sin mensaje util.';
ELSE
    PRINT 'OK    3.3  los fallos guardan el mensaje real de la API.';

/* Un dia que no se pudo comprobar NO debe haber creado licitaciones. Aqui no
   hay ninguna, y esa es la comprobacion: si apareciera alguna, el
   procedimiento estaria guardando datos que no ha conseguido. */
IF EXISTS (SELECT 1 FROM dbo.MpLicitacion)
    PRINT 'FALLO 3.4 se guardaron licitaciones sin haberlas conseguido.';
ELSE
    PRINT 'OK    3.4  no se guardo ninguna licitacion: no se pudo comprobar nada.';

/* 3.5 Y EL TIEMPO. Esta es la comprobacion que hace visible el bug.

   Con el rechazo de ticket reintentandose, tres dias tardaban 185 segundos:
   eran seis intentos por dia con esperas de 2, 4, 8, 16, 30 y 30 segundos, y
   todo para receber el mismo "no" seis veces.

   Un rechazo de ticket se sabe en el primer intento, asi que 3 dias tienen que
   tardar lo que tardan 3 peticiones: unos segundos. Se pone un techo de 30 s,
   que con la latencia de la API deja margen de sobra y aun asi atrapa el
   caso de que alguien vuelva a poner el 203 en el camino de los reintentos. */
DECLARE @segundos int = DATEDIFF(SECOND, @t0, SYSUTCDATETIME());

IF @segundos <= 30
    PRINT 'OK    3.5  tardo ' + CONVERT(nvarchar(10), @segundos)
        + ' s: no hay esperas de reintento que no sirvan para nada.';
ELSE
    PRINT 'FALLO 3.5 tardo ' + CONVERT(nvarchar(10), @segundos)
        + ' s. Con 30 s de techo; hay esperas de mas.';


/* ---------------------------------------------------------------------------
   4. Lo que ve alguien que mira la tabla despues
   --------------------------------------------------------------------------- */
PRINT '';
PRINT '=== 4. Lo que queda en MpConsulta ===';

SELECT FechaDia,
       NumeroIntento,
       Exito,
       CodigoHttp,
       CodigoApi,
       Mensaje
FROM dbo.MpConsulta
ORDER BY FechaDia;


/* ---------------------------------------------------------------------------
   5. El resumen
   --------------------------------------------------------------------------- */
PRINT '';

IF @fallosValidacion = 0
    PRINT 'Validaciones: todas pasan.'
ELSE
    PRINT 'Validaciones: ' + CONVERT(nvarchar(10), @fallosValidacion) + ' FALLAN.';

/* -----------------------------------------------------------------------
   6. Se deshace TODO lo del paso 3, y se comprueba que se ha deshacido.

   El ROLLBACK va en ESTE lote, que es el unico lote del fichero, y no en uno
   aparte. Ver la nota de mas arriba: separado con un GO, revertiria una
   transaccion vacia y las filas se quedarian, que es justo lo que pasaba antes
   de que esto existiera. */
ROLLBACK TRAN;

DECLARE @despues98 TABLE (
    Tabla sysname, Cuantas int, Suma int);

INSERT @despues98 (Tabla, Cuantas, Suma)
SELECT 'MpEmpresa', COUNT(*), CHECKSUM_AGG(CHECKSUM(CodigoProveedor)) FROM dbo.MpEmpresa
UNION ALL SELECT 'MpConsulta', COUNT(*), CHECKSUM_AGG(CHECKSUM(FechaHora, TipoConsulta, FechaDia)) FROM dbo.MpConsulta
UNION ALL SELECT 'MpLicitacion', COUNT(*), CHECKSUM_AGG(CHECKSUM(CodigoExterno)) FROM dbo.MpLicitacion
UNION ALL SELECT 'MpLicitacionDetalle', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId)) FROM dbo.MpLicitacionDetalle
UNION ALL SELECT 'MpLicitacionItem', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId, Correlativo, NombreProducto)) FROM dbo.MpLicitacionItem
UNION ALL SELECT 'MpLicitacionEstadoHistorico', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId, CodigoEstado)) FROM dbo.MpLicitacionEstadoHistorico;

DECLARE @cambios98 TABLE (
    Tabla sysname, Antes int, Despu int);

INSERT @cambios98 (Tabla, Antes, Despu)
SELECT a.Tabla, a.Cuantas, d.Cuantas
FROM @antes98 AS a
FULL OUTER JOIN @despues98 AS d ON a.Tabla = d.Tabla
WHERE a.Tabla IS NULL OR d.Tabla IS NULL
   OR a.Cuantas <> d.Cuantas
   OR ISNULL(a.Suma, -1) <> ISNULL(d.Suma, -1);

PRINT '';
IF NOT EXISTS (SELECT 1 FROM @cambios98)
    PRINT 'La base ha quedado como estaba: todo lo del paso 3 se ha revertido.'
ELSE
BEGIN
    DECLARE @resumen98 nvarchar(600) = (
        SELECT STRING_AGG(CONCAT(Tabla, ': antes ', Antes, ', ahora ', Despu), '; ')
        FROM @cambios98);

    PRINT 'AVISO: la base NO ha vuelto a como estaba -> ' + @resumen98
        + '. La transaccion no ha cogido; ejecuta el paso 0 de limpieza a mano.';
END

SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON;

PRINT '';
PRINT 'Lo que NO prueba este fichero:';
PRINT '  - El parseo de una ficha real (items anidados, montos, fechas), porque';
PRINT '    la API no devuelve nada a un ticket falso. Eso esta en';
PRINT '    99-prueba-esquema.sql, con datos con la forma exacta.';
PRINT '  - Que una consulta correcta se escriba de verdad. Para eso hace falta';
PRINT '    un ticket de verdad, y es lo que se ejecuta en produccion.';

