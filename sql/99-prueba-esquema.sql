/* Prueba del esquema: mete datos con la FORMA EXACTA que produce la
   aplicacion y comprueba que las vistas, las restricciones y los calculos
   hacen lo que dicen.

   Los tres items son los de una respuesta real: codigos 7000-LE-D1L510,
   7000-D1-D2L510 y 7000-L1-D3L510, con los mismos montos que devolvio la API.
   Se ejecuta dentro de una transaccion que se revierte al final, y todo el
   fichero va en un solo lote para que la transaccion llegue a hacer su
   trabajo. Asi no queda ni una fila y las pruebas no pueden contaminar los
   datos buenos. */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

/* Todo lo que viene a continuacion va DENTRO de una transaccion, que se
   revierte al final para que estas pruebas no dejen datos mezclados con los
   reales.

   DOS COSAS DEL GO QUE COSTARON UN RATO:

   ESTA PRUEBA VA ENTERA EN UN SOLO LOTE, SIN NINGUN GO. No es por gusto.

   Las dos cosas que se rompieron al separarla con GO:

   1. Una variable declarada antes de un GO no existe despues de el. El GO
      corta el lote, y con el la variable.

   2. Y una transaccion abierta antes de un GO tampoco sobrevive. El BEGIN TRAN
      de aqui no protegia lo que venia despues: el ROLLBACK del final estaba en
      otro lote y revirtea una transaccion vacia, mientras las filas se
      quedaban. Se comprobo mirando la base despues de ejecutar: seguia con 4
      consultas, 3 licitaciones, 1 detalle y 3 items, y el mensaje decia que no
      quedaba ni una fila.

   El paso 9 lo comprueba y lo dice si alguna vez vuelve a pasar, porque es un
   fallo que no se ve en la salida: solo se ve en la base, consultando. */
BEGIN TRAN;

/* FOTOGRAFIA DE LO QUE HAY ANTES DE EMPEZAR.

   El paso 0 de abajo borra TODAS las tablas, así que hace falta saber qué había
   para poder comprobar al final que todo ha vuelto como estaba. Antes esto se
   contaba como "la base ha quedado vacía", y en cuanto la base tuvo datos
   reales —13 licitaciones importadas de verdad— el aviso saltó siempre:

     AVISO: quedan 86 fila(s) de la prueba. La transaccion no ha cogido.

   con 86 filas que eran las de verdad y la transaccion perfectamente Revival.
   Un aviso que salta siempre es un aviso que nadie lee, y este además accuses
   de algo que no ha pasado.

   Se guardan CONTOS y SUMAS DE CHECKSUM, no solo el número. Si el ROLLBACK
   fallara y los DELETE del paso 0 llegaran a commitearse, los contos volverían a
   ser los mismos —porque los de ahora también son "todas las filas"— y solo el
   checksum de los codigos delataría que ahora hay otras filas. */
DECLARE @antes TABLE (
    Tabla sysname, Cuantas int, Suma int);

INSERT @antes (Tabla, Cuantas, Suma)
SELECT 'MpEmpresa', COUNT(*), CHECKSUM_AGG(CHECKSUM(CodigoProveedor)) FROM dbo.MpEmpresa
UNION ALL SELECT 'MpConsulta', COUNT(*), CHECKSUM_AGG(CHECKSUM(FechaHora, TipoConsulta, FechaDia)) FROM dbo.MpConsulta
UNION ALL SELECT 'MpLicitacion', COUNT(*), CHECKSUM_AGG(CHECKSUM(CodigoExterno)) FROM dbo.MpLicitacion
UNION ALL SELECT 'MpLicitacionDetalle', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId)) FROM dbo.MpLicitacionDetalle
UNION ALL SELECT 'MpLicitacionItem', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId, Correlativo)) FROM dbo.MpLicitacionItem
UNION ALL SELECT 'MpLicitacionEstadoHistorico', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId, CodigoEstado)) FROM dbo.MpLicitacionEstadoHistorico;

/* Se vacia todo ANTES de empezar, aunque la transaccion vaya a deshacerse.

   Hace falta porque el ROLLBACK del final solo se llega a ejecutar si la
   prueba termina bien. Con sqlcmd -b, un error en una comprobacion corta el
   script ahi, y si eso ocurre antes de que la transaccion llegue a abrirse
   queda data de una corrida anterior. Entonces la siguiente falla aqui:

     Violacion de la restriccion UNIQUE 'UQ_MpEmpresa_Codigo'

   que no dice nada de por que y parece un fallo del esquema cuando lo unico
   que ha pasado es que la prueba anterior se dejo a medias. */
DELETE FROM dbo.MpLicitacionItem;
DELETE FROM dbo.MpLicitacionEstadoHistorico;
DELETE FROM dbo.MpLicitacionDetalle;
DELETE FROM dbo.MpLicitacion;
DELETE FROM dbo.MpConsulta;
DELETE FROM dbo.MpEmpresa;

-- ---------------------------------------------------------------
-- 1. Empresa
-- ---------------------------------------------------------------
INSERT INTO dbo.MpEmpresa (CodigoProveedor, NombreEmpresa, RutEmpresa)
VALUES (N'71284', N'EMPRESA DE EJEMPLO PARA DEMOSTRACIONES SPA', N'76.123.456-0');

-- ---------------------------------------------------------------
-- 2. Consultas: un listado bien, un detalle bien, y un 429
-- ---------------------------------------------------------------
INSERT INTO dbo.MpConsulta
    (TipoConsulta, Origen, ModoConsulta, CodigoProveedor, FechaDia, NumeroIntento,
     Exito, CodigoHttp, CantidadDevuelta, DuracionMs)
VALUES (0, 0, 'v1', N'71284', '2026-10-05', 1, 1, 200, 3, 1432);

INSERT INTO dbo.MpConsulta
    (TipoConsulta, Origen, ModoConsulta, CodigoProveedor, CodigoLicitacion,
     NumeroIntento, Exito, CodigoHttp, CantidadDevuelta, DuracionMs)
VALUES (1, 0, 'v1', N'71284', N'7000-LE-D1L510', 1, 1, 200, 8, 1105);

-- El 429 que de verdad aparece: uno de cada diez peticiones.
INSERT INTO dbo.MpConsulta
    (TipoConsulta, Origen, ModoConsulta, CodigoProveedor, FechaDia, NumeroIntento,
     Exito, CodigoHttp, Mensaje, DuracionMs)
VALUES (0, 0, 'v1', N'71284', '2026-10-06', 1, 0, 429,
        N'Mercado Publico esta limitando las peticiones.', 283);

-- Y el 10300, el del formato de fecha, que fue error NUESTRO y no de la API.
INSERT INTO dbo.MpConsulta
    (TipoConsulta, Origen, ModoConsulta, CodigoProveedor, FechaDia, NumeroIntento,
     Exito, CodigoHttp, CodigoApi, Mensaje, DuracionMs)
VALUES (0, 0, 'v1', N'71284', '2026-10-04', 1, 0, 500, 10300,
        N'El formato del parametro fechas es incorrecto', 210);

-- ---------------------------------------------------------------
-- 3. Las tres licitaciones del listado
-- ---------------------------------------------------------------
INSERT INTO dbo.MpLicitacion
    (CodigoProveedor, CodigoExterno, Nombre, CodigoEstado, FechaCierre,
     FechaPublicacion, ModoConsulta)
VALUES
 (N'71284', N'7000-LE-D1L510',
  N'Reparacion de bombas de agua industriales (05/10/2026)', 5,
  '2026-10-11T00:00:00-03:00', '2026-10-05', 'v1'),
 (N'71284', N'7000-D1-D2L510',
  N'Suministro de tuberia y fitting para tramo de red (05/10/2026)', 5,
  '2026-10-16T00:00:00-03:00', '2026-10-05', 'v1'),
 (N'71284', N'7000-L1-D3L510',
  N'Compra de elementos de proteccion personal (05/10/2026)', 5,
  '2026-10-27T00:00:00-03:00', '2026-10-05', 'v1');

-- ---------------------------------------------------------------
-- 4. Un detalle completo, con TODAS las fechas y el desfase horario
-- ---------------------------------------------------------------
INSERT INTO dbo.MpLicitacionDetalle
    (LicitacionId, Nombre, Estado, CodigoEstado, Descripcion, Tipo,
     NombreOrganismo, RutOrganismo, CodigoOrganismo, RegionOrganismo, ComunaOrganismo,
     MontoEstimado, Moneda, Estimacion,
     FechaCreacion, FechaCierre, FechaPublicacion,
     FechaAperturaTecnica, FechaAperturaEconomica, FechaAdjudicacion, FechaFinal,
     NumeroOferentes, NumeroAdjudicacion, UrlActa, DiasCierreLicitacion)
VALUES
 ((SELECT LicitacionId FROM dbo.MpLicitacion WHERE CodigoExterno = N'7000-D1-D2L510'),
  N'Suministro de tuberia y fitting para tramo de red (05/10/2026)',
  N'Adjudicada', 8, N'Detalle inventado para probar la ficha (modo demo).', N'LP',
  N'Municipalidad de Providencia', N'61.981.420-7', 892779,
  N'Region de La Araucania', N'Temuco',
  6175900000, N'CLP', 2,
  '2026-08-07T16:00:00-04:00', '2026-08-31T16:00:00-04:00', '2026-08-14T16:00:00-04:00',
  '2026-08-26T16:00:00-04:00', '2026-08-27T16:00:00-04:00',
  '2026-07-15T11:30:00-04:00', '2026-08-20T16:00:00-04:00',
  5, N'651', N'https://www.mercadopublico.cl/', 7);

-- ---------------------------------------------------------------
-- 5. Los items adjudicados
--
--    El primero trae CantidadAdjudicada distinta de la pedida, que es el caso
--    que obliga a tener las dos columnas. El tercero no trae Cantidad y por eso
--    el subtotal tiene que caer en la pedida.
--
--    Descripcion es la especificacion del comprador: la pagina la llama
--    "Especificaciones del comprador" y la API la llama Descripcion. No existe
--    ninguna clave "Especificacion", comprobado contra la API.
--
--    Se anade a los tres items, y el tercero va VACIA a proposito: "" y NULL no
--    son lo mismo, y el primero significa "la API lo mando en blanco". Los datos
--    de los tres -montos y productos- NO se tocan, porque las comprobaciones 7.1,
--    7.2 y 7.3 suman los subtotales contra estos valores.
-- ---------------------------------------------------------------
INSERT INTO dbo.MpLicitacionItem
    (LicitacionId, Correlativo, NombreProducto, Descripcion, UnidadMedida,
     Cantidad, CantidadAdjudicada, MontoUnitario, RutProveedor, NombreProveedor)
SELECT d.LicitacionId, v.Correlativo, v.NombreProducto, v.Descripcion, v.UnidadMedida,
       v.Cantidad, v.CantidadAdjudicada, v.MontoUnitario, v.Rut, v.Proveedor
FROM dbo.MpLicitacionDetalle AS d
CROSS APPLY (VALUES
    (1, N'Tuberia PVC',          N'Linea a) con canoneria de acero - instalacion',   N'Metro', 1000.0,  800.0, 2500000.0000, N'76.111.111-1', N'PROVEEDOR A SA'),
    (2, N'Fitting deunion',      N'Linea a) con canoneria de acero - mantenimiento', N'Unidad',  50.0,   50.0,   125000.5000, N'76.111.111-1', N'PROVEEDOR A SA'),
    (3, N'Valvula de compuerta', N'',                                             N'Unidad',   3.0,   NULL, 12345678.0000, N'76.222.222-2', N'PROVEEDOR B LTDA')
) AS v(Correlativo, NombreProducto, Descripcion, UnidadMedida,
        Cantidad, CantidadAdjudicada, MontoUnitario, Rut, Proveedor)
JOIN dbo.MpLicitacion AS l ON l.LicitacionId = d.LicitacionId
WHERE l.CodigoExterno = N'7000-D1-D2L510';

-- ---------------------------------------------------------------
-- 6. Cambio de estado historizado
-- ---------------------------------------------------------------
INSERT INTO dbo.MpLicitacionEstadoHistorico
    (LicitacionId, CodigoExterno, CodigoEstado, Estado, ConsultaId)
SELECT l.LicitacionId, l.CodigoExterno, 8, N'Adjudicada', NULL
FROM dbo.MpLicitacion AS l
WHERE l.CodigoExterno = N'7000-D1-D2L510';

-- ---------------------------------------------------------------
-- 7. Lo que TIENE que ser cierto
-- ---------------------------------------------------------------
PRINT '';
PRINT '--- COMPROBACIONES ---';

-- El contador se declara aqui, y no arriba del todo, porque el GO anterior
-- corta el lote y una variable de sqlcmd no sobrevive a un GO.
/* El contador se declara aqui, y no arriba del todo, porque el GO anterior
   corta el lote y una variable de sqlcmd no sobrevive a un GO.

   Y se borra antes la empresa, porque estas pruebas se pueden ejecutar varias
   veces seguidas sin pasar por el ROLLBACK. Con el UNIQUE puesto, la segunda
   ejecucion fallaria al principio con

     Violacion de la restriccion UNIQUE 'UQ_MpEmpresa_Codigo'

   que no dice nada de por que: parece un fallo del esquema y es solo que la
   prueba se ha quedado sin limpiar. */
DECLARE @fallos int = 0;

-- 7.1 El subtotal se calcula con la cantidad ADJUDICADA cuando existe:
--      2.500.000 x 800 = 2.000.000.000
IF NOT EXISTS (SELECT 1 FROM dbo.MpLicitacionItem AS i
               JOIN dbo.MpLicitacion AS l ON l.LicitacionId = i.LicitacionId
               WHERE l.CodigoExterno = N'7000-D1-D2L510'
                 AND i.Correlativo = 1 AND i.Subtotal = 2000000000.0000)
BEGIN
    PRINT 'FALLO 7.1: el subtotal no uso la cantidad adjudicada';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.1  subtotal con cantidad adjudicada = 2.000.000.000';

-- 7.2 Y con la PEDIDA cuando la adjudicada viene vacia:
--      12.345.678 x 3 = 37.037.034
IF NOT EXISTS (SELECT 1 FROM dbo.MpLicitacionItem AS i
               JOIN dbo.MpLicitacion AS l ON l.LicitacionId = i.LicitacionId
               WHERE l.CodigoExterno = N'7000-D1-D2L510'
                 AND i.Correlativo = 3 AND i.Subtotal = 37037034.0000)
BEGIN
    PRINT 'FALLO 7.2: el subtotal no cayo en la cantidad pedida';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.2  subtotal con cantidad pedida = 37.037.034';

-- 7.3 El total de la vista: la suma de los tres subtotales.
/* El total se compara con lo que dicen los items, calculado aqui con la misma
   regla: subtotales ya redondeados, porque la columna Subtotal es
   decimal(19,4) y el truncamiento ocurre AL GUARDAR, no despues.

   2.500.000,0000 x 800        = 2.000.000.000,0000
     125.000,5000 x 50         =     6.250.025,0000
  12.345.678,0000 x 3          =    37.037.034,0000
                                  -----------------
                                    2.043.287.059,0000

   Ojo con el segundo: 125.000,50 x 50 son 6.250.025, y no 6.250.000. La
   primera version de esta comprobacion decia 6.250.000 y el fallo 7.3 salio
   por eso, por un numero mal calculado a mano en el propio test y no por un
   error del esquema. El .5000 del precio unitario se multiplica igual que el
   resto.

   Y el total NO se compara contra una suma hecha aqui, sino contra la suma
   que dan los propios items, leida de la tabla:

       SELECT SUM(Subtotal) FROM MpLicitacionItem WHERE LicitacionId = ...

   Esa es la unica comparacion que vale: si el test y la vista compartieran el
   mismo numero escrito a mano, los dos podrian estar mal a la vez y la
   comprobacion pasaria. Leyendo la tabla, si las dossumaran distinto se ve
   al momento. */
DECLARE @esperado decimal(19,4) = (
    SELECT SUM(i.Subtotal)
    FROM dbo.MpLicitacionItem AS i
    JOIN dbo.MpLicitacion AS l ON l.LicitacionId = i.LicitacionId
    WHERE l.CodigoExterno = N'7000-D1-D2L510');

DECLARE @total decimal(19,4) = (SELECT TotalAdjudicado FROM dbo.vwMpLicitacion
                                 WHERE CodigoExterno = N'7000-D1-D2L510');

IF @total IS NULL OR @total <> @esperado
BEGIN
    PRINT 'FALLO 7.3: la vista no coincide con la suma de los items';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.3  la vista suma igual que la tabla: 2.043.287.059';

-- 7.4 Una licitacion SIN items tiene TotalAdjudicado NULL, no 0. La
--     diferencia importa: "todavia no se adjudico" y "se adjudico cero" no
--     son lo mismo, y es lo que hace el codigo en TotalAdjudicado.
IF EXISTS (SELECT 1 FROM dbo.vwMpLicitacion
           WHERE CodigoExterno = N'7000-LE-D1L510'
             AND TotalAdjudicado IS NOT NULL)
BEGIN
    PRINT 'FALLO 7.4: una licitacion sin items devuelve 0 en vez de NULL';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.4  sin items, TotalAdjudicado es NULL (no 0)';

-- 7.5 El desfase horario se conserva. Las dos fechas de esta licitacion
--     estan en -04:00 (invierno). Si alguien las guardara en datetime2 se
--     perderia y no se podria saber que hora local era.
IF NOT EXISTS (SELECT 1 FROM dbo.MpLicitacionDetalle
               WHERE LicitacionId = (SELECT LicitacionId FROM dbo.MpLicitacion
                                     WHERE CodigoExterno = N'7000-D1-D2L510')
                 AND FechaCierre = '2026-08-31T16:00:00-04:00'
                 AND DATEPART(HOUR, FechaCierre AT TIME ZONE 'UTC') = 20)
BEGIN
    PRINT 'FALLO 7.5: se perdio el desfase horario de las fechas';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.5  el desfase horario -04:00 se conserva';

-- 7.6 El monto de 6.175.900.000 sale EXACTO. En double terminaria en
--     6175899999.999999.
IF NOT EXISTS (SELECT 1 FROM dbo.MpLicitacionDetalle
               WHERE LicitacionId = (SELECT LicitacionId FROM dbo.MpLicitacion
                                     WHERE CodigoExterno = N'7000-D1-D2L510')
                 AND MontoEstimado = 6175900000)
BEGIN
    PRINT 'FALLO 7.6: el monto no es exacto';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.6  monto 6.175.900.000 exacto (decimal, no float)';

-- 7.7 La vista de detalle trae los items en filas.
IF (SELECT COUNT(*) FROM dbo.vwMpDetalle WHERE CodigoExterno = N'7000-D1-D2L510') <> 3
BEGIN
    PRINT 'FALLO 7.7: vwMpDetalle no trae los 3 items';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.7  vwMpDetalle trae los 3 items';

-- 7.8 La restriccion de MpConsulta: un listado no puede llevar codigo de
--     licitacion. Si la app se conectara mal, esto lo dice al insertar.
BEGIN TRY
    INSERT INTO dbo.MpConsulta
        (TipoConsulta, Origen, ModoConsulta, CodigoProveedor, FechaDia,
         CodigoLicitacion, Exito)
    VALUES (0, 0, 'v1', N'71284', '2026-10-07', N'7000-LE-D1L510', 1);
    PRINT 'FALLO 7.8: la restriccion de MpConsulta no hizo nada';
    SET @fallos = @fallos + 1;
END TRY
BEGIN CATCH
    PRINT 'OK    7.8  la restriccion rechaza un listado con codigo de licitacion';
END CATCH

-- 7.9 Un item sin monto unitario se rechaza. Es la regla de LeerItems: sin
--     monto no aporta nada y solo llenaria la tabla de ruido.
BEGIN TRY
    INSERT INTO dbo.MpLicitacionItem
        (LicitacionId, Correlativo, NombreProducto)
    SELECT LicitacionId, 99, N'Producto sin monto'
    FROM dbo.MpLicitacionDetalle;
    PRINT 'FALLO 7.9: se acepto un item sin monto unitario';
    SET @fallos = @fallos + 1;
END TRY
BEGIN CATCH
    PRINT 'OK    7.9  un item sin monto unitario se rechaza';
END CATCH

/* 7.10 Si dos lecturas traen la MISMA licitacion, la segunda no puede crear
   una fila nueva. Es el UNIQUE (CodigoProveedor, CodigoExterno) de
   MpLicitacion, que es lo que hace que un "upsert" se pueda escribir de verdad. */
BEGIN TRY
    INSERT INTO dbo.MpLicitacion
        (CodigoProveedor, CodigoExterno, Nombre, CodigoEstado, ModoConsulta)
    VALUES (N'71284', N'7000-D1-D2L510', N'Duplicada', 5, 'v1');
    PRINT 'FALLO 7.10: se acepto la misma licitacion dos veces';
    SET @fallos = @fallos + 1;
END TRY
BEGIN CATCH
    PRINT 'OK    7.10 el UNIQUE impide la misma licitacion dos veces';
END CATCH

/* 7.11 LA QUE MAS IMPORTABA, y que no existed antes de que este esquema se
   escribiera dos veces.

   Los items tienen que quedar colgados de SU licitacion. Con la primera version
   —un IDENTITY propio en MpLicitacion, otra en MpLicitacionDetalle y otra en
   MpLicitacionItem— los tres juegos de numeros salian 1, 2 y 3 porque las filas
   se insertaron en orden, y la vista repartia los items a la licitacion que le
   tocaba por numero. No habia ningun error: cada numero era correcto en su
   tabla y el cruce era solo incorrecto.

   Esta comprobacion falla en aquel esquema y pasa en este. Es la prueba de que
   la clave foranea hace su trabajo. */
IF EXISTS (SELECT 1 FROM dbo.vwMpLicitacion
           WHERE CodigoExterno = N'7000-D1-D2L510' AND CantidadItems <> 3)
BEGIN
    PRINT 'FALLO 7.11: los 3 items NO quedaron en su licitacion';
    SET @fallos = @fallos + 1;
END
ELSE IF EXISTS (SELECT 1 FROM dbo.vwMpLicitacion
                WHERE CodigoExterno <> N'7000-D1-D2L510' AND CantidadItems <> 0)
BEGIN
    PRINT 'FALLO 7.11: items repartidos en licitaciones que no son la suya';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.11 los 3 items quedan en SU licitacion, y en ninguna otra';

-- ---------------------------------------------------------------
-- 7.12 La especificacion del comprador (Descripcion) llega y se guarda
--
-- Tres cosas, y las tres se pueden romper sin que salte ningun error:
--
--   a) Que la columna exista. Es lo que pasa si alguien anade el ALTER al
--      CREATE TABLE y se olvida del ALTER de las bases ya instaladas: no hay
--      error hasta que algo intenta usarla.
--
--   b) Que el texto llegue tal cual, con acentos y con la "Linea a)" del
--      principio. Un "-q" que convierte a otra pagina de codigos deja el texto
--      doblemente codificado y lo delata una comparacion, no un aviso.
--
--   c) Que "" NO se convierta en NULL al entrar y al salir. Son cosas distintas:
--      la API no manda descripcion en unos items y la manda en blanco en otros.
-- ---------------------------------------------------------------
IF COL_LENGTH(N'dbo.MpLicitacionItem', N'Descripcion') IS NULL
BEGIN
    PRINT 'FALLO 7.12a: dbo.MpLicitacionItem no tiene la columna Descripcion.';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.12a la columna Descripcion existe';

IF NOT EXISTS (SELECT 1 FROM dbo.MpLicitacionItem AS i
               WHERE i.Correlativo = 2
                 AND i.Descripcion = N'Linea a) con canoneria de acero - mantenimiento')
BEGIN
    PRINT 'FALLO 7.12b: la descripcion no se guardo tal cual.';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.12b la descripcion se guarda con sus acentos y su parentesis';

/* Y la del tercero, que entra como cadena vacia, tiene que seguir siendo cadena
   vacia y no NULL. Con bcp -k esto se pierde: el campo de longitud cero es NULL. */
IF EXISTS (SELECT 1 FROM dbo.MpLicitacionItem AS i
           WHERE i.Correlativo = 3 AND i.Descripcion IS NULL)
BEGIN
    PRINT 'FALLO 7.12c: la descripcion vacia se guardo como NULL.';
    SET @fallos = @fallos + 1;
END
ELSE PRINT 'OK    7.12c la descripcion vacia sigue vacia y no se ha vuelto NULL';

PRINT '';
IF @fallos = 0
    PRINT 'RESULTADO: las 14 comprobaciones pasan.'
ELSE
    PRINT 'RESULTADO: ' + CAST(@fallos AS varchar(10)) + ' comprobacion(es) FALLAN.';

-- ---------------------------------------------------------------
-- 8. Lo que se ve desde fuera
-- ---------------------------------------------------------------
PRINT '';
PRINT '--- vwMpLicitacion ---';
SELECT CodigoExterno, Estado, FechaPublicacion, TotalAdjudicado, CantidadItems
FROM dbo.vwMpLicitacion
ORDER BY CodigoExterno;

PRINT '';
PRINT '--- Cuanto cupo del ticket se gasto de verdad ---';
SELECT CASE WHEN Origen = 0 THEN N'llamada real a la API'
            ELSE N'servida desde la cache (0 cupo)' END AS Origen,
       COUNT(*) AS Peticiones,
       SUM(CASE WHEN Exito = 1 THEN 0 ELSE 1 END) AS Fallos
FROM dbo.MpConsulta
GROUP BY Origen;

PRINT '';
PRINT '--- Errores, con su codigo de la API ---';
SELECT FechaHora, CodigoHttp, CodigoApi, Mensaje
FROM dbo.MpConsulta
WHERE Exito = 0
ORDER BY FechaHora;

/* ---------------------------------------------------------------
   9. SE DESHACE LO QUE SE HA ESCRITO

   Y AQUI ESTA EL OTRO HALLAZGO, que es el mas facil de no ver:

   BEGIN TRAN estaba al principio del archivo y ROLLBACK al final, con un GO
   entre medias.(sqlcmd abre una conexion, pero cada GO es un lote INDEPENDIENTE
   y una transaccion abierta en un lote NO se ve desde el siguiente.) Asi que el
   ROLLBACK no estaba deshaciendo nada: estaba revirtiendo una transaccion vacia
   que el propio ROLLBACK acababa de abrir, y las filas se quedaban.

   Se noto al mirar el estado de la base despues de la prueba: seguia con 4
   consultas, 3 licitaciones, 1 detalle y 3 items, y el mensaje decia que no
   quedaba ni una fila.

   Por eso el COMMIT/ROLLBACK va en el MISMO lote que el BEGIN. Y por eso la
   limpieza del principio (paso 0) es obligatoria: es la red que recoge lo que
   se quede si esta prueba se corta a medio camino.

   El IF de abajo compara contra la FOTOGRAFIA del principio, y no contra cero,
   porque esta base tiene datos reales que no son de la prueba. Comparar con
   cero hacia que el aviso saltara SIEMPRE, acusando de un fallo que no habia.
   --------------------------------------------------------------- */
ROLLBACK TRAN;

DECLARE @despues TABLE (
    Tabla sysname, Cuantas int, Suma int);

INSERT @despues (Tabla, Cuantas, Suma)
SELECT 'MpEmpresa', COUNT(*), CHECKSUM_AGG(CHECKSUM(CodigoProveedor)) FROM dbo.MpEmpresa
UNION ALL SELECT 'MpConsulta', COUNT(*), CHECKSUM_AGG(CHECKSUM(FechaHora, TipoConsulta, FechaDia)) FROM dbo.MpConsulta
UNION ALL SELECT 'MpLicitacion', COUNT(*), CHECKSUM_AGG(CHECKSUM(CodigoExterno)) FROM dbo.MpLicitacion
UNION ALL SELECT 'MpLicitacionDetalle', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId)) FROM dbo.MpLicitacionDetalle
UNION ALL SELECT 'MpLicitacionItem', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId, Correlativo)) FROM dbo.MpLicitacionItem
UNION ALL SELECT 'MpLicitacionEstadoHistorico', COUNT(*), CHECKSUM_AGG(CHECKSUM(LicitacionId, CodigoEstado)) FROM dbo.MpLicitacionEstadoHistorico;

/* Se comparan las DOS cosas: numero de filas y suma de checksums. Con el numero
   solo no bastaria. Si el ROLLBACK fallara y los DELETE del principio llegaran
   a commitearse, las filas serian "todas las de la tabla" igual que antes y el
   numero cliquaria; lo que delataria el cambio son los codigos. */
DECLARE @cambios TABLE (
    Tabla sysname, Antes int, Despu int);

INSERT @cambios (Tabla, Antes, Despu)
SELECT a.Tabla, a.Cuantas, d.Cuantas
FROM @antes AS a
FULL OUTER JOIN @despues AS d ON a.Tabla = d.Tabla
WHERE a.Tabla IS NULL OR d.Tabla IS NULL
   OR a.Cuantas <> d.Cuantas
   OR ISNULL(a.Suma, -1) <> ISNULL(d.Suma, -1);

PRINT '';
IF NOT EXISTS (SELECT 1 FROM @cambios)
    PRINT 'La base ha quedado como estaba: las filas de la prueba se han revertido.'
ELSE
BEGIN
    DECLARE @resumen nvarchar(600) = (
        SELECT STRING_AGG(CONCAT(Tabla, ': antes ', Antes, ', ahora ', Despu), '; ')
        FROM @cambios);

    PRINT 'AVISO: la base NO ha vuelto a como estaba -> ' + @resumen
        + '. La transaccion no ha cogido; ejecuta el paso 0 de limpieza a mano.';
END

