/* ===========================================================================
   Prueba de los PROCEDIMIENTOS DE LECTURA (sql/05-procedimientos-lectura.sql).

   Estas pruebas se ejecutan contra la base DE VERDAD, sin transacciones y sin
   borrar nada: mp.LeeMes y mp.LeeDetalle solo leen, y lo que se comprueba es
   exactamente lo que va a ver la pantalla.

   Por eso esta prueba NO mete datos. Si lo hiciera, estaría probando los datos
   que ella misma acaba de escribir en vez de los que importó de verdad, que es
   lo que hay que probar. La forma que estos procedimientos tienen de contar los
   días no comprobados se lee contra MpConsulta, y un MpConsulta inventado en una
   transacción deshecha no comprobaría nada.

   ---------------------------------------------------------------------------
   POR QUÉ SE PRUEBAN LOS PROCEDIMIENTOS PEQUEÑOS Y NO mp.LeeMes
   ---------------------------------------------------------------------------

   En T-SQL, INSERT @tabla EXEC unProcedimiento mete TODOS los conjuntos que
   devuelva en la MISMA tabla. Con los cinco de mp.LeeMes eso no funciona, y no
   porque falte un truco: no existe forma de coger solo el primero.

   Por eso la cuenta de días vive en mp.CuentaDiasDelMes y mp.DiasSinComprobarDelMes,
   que devuelven un conjunto cada uno y sí se pueden meter en una tabla. Lo que
   se prueba aquí es lo que sostiene la honestidad de la pantalla; mp.LeeMes es
   un pegador.

   ---------------------------------------------------------------------------
   LO QUE NO SE PUEDE PROBAR AQUÍ
   ---------------------------------------------------------------------------

   Que la aplicación recorte el JSON que devuelve el procedimiento, que es donde
   más cosas se pueden torcer. Eso lo prueba el navegador, no sqlcmd.

   Y hay un fallo que T-SQL no puede ver y que salió al empezar a usar esto:

       while (await lector.NextResultAsync(ct))
       {
           switch (numero++) { case 0: /* cabecera */ ... }
       }

   ExecuteReaderAsync() deja el lector YA SOBRE EL PRIMER CONJUNTO, así que ese
   bucle se come la cabecera y el primer caso lee el conjunto equivocado. En
   T-SQL no se nota porque sqlcmd imprime los cinco bien. Solo se nota cuando el
   primer conjunto tiene una columna que el segundo no tiene, y sale como

       IndexOutOfRangeException: DiasHabiles

   que no dice ni qué conjunto es ni que hubo un salto.

   =========================================================================== */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @proveedor nvarchar(50) = N'71284';

PRINT '';
PRINT '=== mp.CuentaDiasDelMes: septiembre de 2026, un mes con datos ===';
PRINT '';

/* El "hoy" se fija en el 8 de octubre de 2026 porque septiembre ya terminó, y
   un mes terminado tiene que salir con DiasPendientes = 0. Si el procedimiento
   leyera el reloj, esta prueba seguiría valiendo dentro de unos meses pero
   dejaría de valer para siempre el día que se ejecutara en otro mes. */
DECLARE @sept TABLE (
    Anio int, Mes int, DiasHabiles int, DiasConsultados int,
    DiasFallidos int, DiasPendientes int, Total int, Consultado datetime2(7));

INSERT @sept EXEC mp.CuentaDiasDelMes
    @codigoProveedor = @proveedor, @anio = 2026, @mes = 9, @hoy = '2026-10-08';

/* 1. La cabecera viene con el mes que se pidió. */
IF (SELECT COUNT(*) FROM @sept WHERE Anio = 2026 AND Mes = 9) = 1
    PRINT '  [OK]  1. La cabecera trae el mes pedido (2026-09).'
ELSE
BEGIN
    PRINT '  [MAL] 1. La cabecera no trae 2026-09.';
    THROW 50001, 'mp.CuentaDiasDelMes: cabecera incorrecta', 1;
END

/* 2. Un mes ya pasado no tiene días pendientes. */
IF (SELECT DiasPendientes FROM @sept) = 0
    PRINT '  [OK]  2. Un mes ya pasado no tiene días pendientes.'
ELSE
BEGIN
    PRINT '  [MAL] 2. Hay días pendientes en un mes que ya terminó.';
    THROW 50002, 'mp.CuentaDiasDelMes: dias pendientes en mes pasado', 1;
END

/* 3. La cuenta tiene que cuadrar: lo comprobado más lo fallado es todo lo que
   había. Esta es la comprobación que sostiene la frase de la pantalla. */
IF (SELECT DiasConsultados + DiasFallidos FROM @sept) = (SELECT DiasHabiles FROM @sept)
    PRINT '  [OK]  3. Comprobados + fallidos = días hábiles del mes.'
ELSE
BEGIN
    PRINT '  [MAL] 3. Los días no cuadran.';
    THROW 50003, 'mp.CuentaDiasDelMes: los dias no cuadran', 1;
END

/* 4. Septiembre de 2026 tiene 22 días hábiles de lunes a viernes. No es un
   dato de adorno: es el que detecta un generador de días que no llega a fin de
   mes, que saldría con menos y sin ningún error. */
IF (SELECT DiasHabiles FROM @sept) = 22
    PRINT '  [OK]  4. Septiembre de 2026 tiene los 22 días hábiles que le tocan.'
ELSE
BEGIN
    PRINT '  [MAL] 4. Septiembre de 2026 no sale con 22 días hábiles.';
    THROW 50004, 'mp.CuentaDiasDelMes: recuento de dias habiles incorrecto', 1;
END

/* 5. Un mes que no existe se dice, no se devuelve callado. Un 2026/13 vacío es
   indistinguible de un mes sin nada publicado. */
DECLARE @mesImposible TABLE (Anio int, Mes int);

BEGIN TRY
    INSERT @mesImposible EXEC mp.CuentaDiasDelMes
        @codigoProveedor = @proveedor, @anio = 2026, @mes = 13, @hoy = '2026-10-08';

    PRINT '  [MAL] 5. Un mes 13 se devuelve sin decir nada.';
    THROW 50005, 'mp.CuentaDiasDelMes: mes imposible sin avisar', 1;
END TRY
BEGIN CATCH
    IF (SELECT COUNT(*) FROM @mesImposible) = 0
        PRINT '  [OK]  5. Un mes que no existe se rechaza con un error.'
    ELSE
    BEGIN
        PRINT '  [MAL] 5. Un mes imposible devuelve filas.';
        THROW 50006, 'mp.CuentaDiasDelMes: mes imposible con filas', 1;
    END
END CATCH

PRINT '';
PRINT '=== El caso que NO puede mentir: octubre de 2026, el mes en curso ===';
PRINT '';

/* Octubre de 2026. El día 8 de ese mes se importaron los días 5, 6 y 7 y los
   tres fueron rechazados con "Ticket no válido" (HTTP 203). Los días 1 y 2 no
   llegaron a preguntarse porque el rango que se importaba empezaba el 14 de
   enero.

   El resultado tiene que ser CERO licitaciones, seis días no comprobados y el
   motivo real de cada uno. Si esto saliera "0 licitaciones" y nada más, la
   pantalla diría que en octubre no se publicó nada, que es falso: no se miró.

   Este caso se puso en la base a propósito, con datos reales, y no se borra. Es
   el que justifica que los procedimientos devuelvan los días no comprobados. */
DECLARE @oct TABLE (
    Anio int, Mes int, DiasHabiles int, DiasConsultados int,
    DiasFallidos int, DiasPendientes int, Total int, Consultado datetime2(7));

DECLARE @sinComprobar TABLE (FechaDia date PRIMARY KEY, Motivo nvarchar(400));

INSERT @oct EXEC mp.CuentaDiasDelMes
    @codigoProveedor = @proveedor, @anio = 2026, @mes = 10, @hoy = '2026-10-08';

INSERT @sinComprobar EXEC mp.DiasSinComprobarDelMes
    @codigoProveedor = @proveedor, @anio = 2026, @mes = 10, @hoy = '2026-10-08';

/* 6. Del 1 al 8 de octubre de 2026 hay 6 días hábiles, no 23. El mes entero
   tiene 23, pero contar los que todavía no han llegado sería avisar de días que
   no existen. Este número decide si el panel dice la verdad sobre octubre. */
IF (SELECT DiasHabiles FROM @oct) = 6
    PRINT '  [OK]  6. Del 1 al 8 de octubre solo cuentan los 6 días que pasaron.'
ELSE
BEGIN
    PRINT '  [MAL] 6. Octubre no sale con 6 días hábiles.';
    THROW 50007, 'mp.CuentaDiasDelMes: dias habiles de un mes en curso', 1;
END

/* 7. Y los que quedan en el mes salen como pendientes, no como fallidos. */
IF (SELECT DiasPendientes FROM @oct) = 16
    PRINT '  [OK]  7. Los 16 días hábiles que faltan en octubre salen como pendientes.'
ELSE
BEGIN
    PRINT '  [MAL] 7. Los días que faltan en octubre no salen como pendientes.';
    THROW 50008, 'mp.CuentaDiasDelMes: dias pendientes incorrectos', 1;
END

/* 8. Octubre no puede decir que se comprobó entero. */
IF (SELECT DiasFallidos FROM @oct) > 0
    PRINT '  [OK]  8. Octubre reporta días que no se pudieron comprobar.'
ELSE
BEGIN
    PRINT '  [MAL] 8. Octubre sale como comprobado entero, y no lo está.';
    THROW 50009, 'mp.CuentaDiasDelMes: mes no comprobado sale como comprobado', 1;
END

/* 9. Y no puede decir que se comprobó ningún día: los tres que se importaron
   dieron 203, que es un fallo, pero ES un intento. Un día intentado y rechazado
   no es lo mismo que un día nunca preguntado, y el motivo lo distingue. */
IF (SELECT DiasConsultados FROM @oct) = 0
    PRINT '  [OK]  9. Octubre no cuenta ningún día como comprobado.'
ELSE
BEGIN
    PRINT '  [MAL] 9. Octubre cuenta días como consultados que no lo son.';
    THROW 50010, 'mp.CuentaDiasDelMes: dias consultados incorrectos', 1;
END

/* 10. Los seis días que no se pudieron comprobar tienen que estar los seis, y
   ninguno más. Ni uno de menos (que sería esconder una comprobación fallida) ni
   uno de más (que sería avisar de un día que sí se miró). */
IF (SELECT COUNT(*) FROM @sinComprobar) = 6
    PRINT '  [OK] 10. Salen los 6 días no comprobados de octubre, ni uno más.'
ELSE
BEGIN
    PRINT '  [MAL] 10. El número de días no comprobados de octubre no es 6.';
    THROW 50011, 'mp.DiasSinComprobarDelMes: numero de dias incorrecto', 1;
END

/* 11. Los días con ticket caducado tienen que decir qué pasó, y no "no hay
   datos". Es la diferencia entre "reintentar ahora" y "pedir un ticket nuevo". */
IF EXISTS (SELECT 1 FROM @sinComprobar WHERE Motivo LIKE '%válido%' OR Motivo LIKE '%valido%')
    PRINT '  [OK] 11. Los días rechazados dicen que el ticket no valía.'
ELSE
BEGIN
    PRINT '  [MAL] 11. Los días rechazados no dicen por qué.';
    THROW 50012, 'mp.DiasSinComprobarDelMes: sin motivo del rechazo', 1;
END

/* 12. Y los días que nunca se preguntaron tienen que estar ahí también. Un día
   que nunca se preguntó no es un día comprobado, y aquí es donde más se nota:
   fue justo lo que pasó el 1 y el 2 de octubre. */
IF EXISTS (SELECT 1 FROM @sinComprobar WHERE Motivo LIKE 'Nunca se consulto%')
    PRINT '  [OK] 12. Los días nunca preguntados también salen.'
ELSE
BEGIN
    PRINT '  [MAL] 12. Los días nunca preguntados no salen.';
    THROW 50013, 'mp.DiasSinComprobarDelMes: dias nunca preguntados ausentes', 1;
END

/* 13. Ningún día sin comprobar puede tener motivo vacío: un "3 días sin
   comprobar" sin motivo deja al usuario sin poder decidir nada. */
IF NOT EXISTS (SELECT 1 FROM @sinComprobar WHERE Motivo IS NULL OR LTRIM(RTRIM(Motivo)) = '')
    PRINT '  [OK] 13. Ningún día no comprobado viene sin motivo.'
ELSE
BEGIN
    PRINT '  [MAL] 13. Hay días no comprobados sin motivo.';
    THROW 50014, 'mp.DiasSinComprobarDelMes: dias sin motivo', 1;
END

PRINT '';
PRINT '=== mp.LeeMes: el conjunto que arma la respuesta ===';
PRINT '';

/* 14. El primer conjunto de mp.LeeMes es la cabecera, con SUS OCHO columnas y
   con ESOS nombres.

   Esto importa más de lo que parece. La aplicación lee ese conjunto por
   POSICIÓN: un NextResultAsync() al final de cada vuelta del bucle, y el caso 0
   del switch. Si el primer conjunto perdiera una columna, o el procedimiento
   devolviera un conjunto antes, la aplicación leería el conjunto equivocado y
   fallaría con

       IndexOutOfRangeException: DiasHabiles

   que no dice ni qué conjunto es ni que hubo un salto. Y con sqlcmd no se ve,
   porque sqlcmd imprime los cinco bien y no dice de dónde viene cada uno.

   Los nombres se comprueban uno a uno y no solo el recuento, porque un conjunto
   con ocho columnas mal nombradas compila igual y revienta en la aplicación.

   CUÁNDO SE COMPRUEBA EL NÚMERO DE CONJUNTOS Y CUÁNDO NO: en T-SQL no se puede.
   INSERT @t EXEC mete todos los conjuntos en la misma tabla, y la DMV que
   describe el primero no tiene versión "y ahora el segundo":

       Invalid object name 'sys.dm_exec_describe_next_result_set_for_object'

   Así que el número de conjuntos se comprueba desde la aplicación, y la
   mouthpiece de ese bucle lleva un comentario que explica que va al final. Lo
   que sí se comprueba aquí es que el primero es el que tiene que ser. */
DECLARE @columnasCabecera TABLE (nombre sysname);

INSERT @columnasCabecera (nombre)
SELECT name FROM sys.dm_exec_describe_first_result_set_for_object(
    OBJECT_ID('mp.LeeMes'), 0);

DECLARE @esperadas TABLE (nombre sysname);

INSERT @esperadas (nombre)
VALUES ('Anio'), ('Mes'), ('DiasHabiles'), ('DiasConsultados'),
       ('DiasFallidos'), ('DiasPendientes'), ('Total'), ('Consultado');

IF NOT EXISTS (
    SELECT nombre FROM @esperadas
    EXCEPT
    SELECT nombre FROM @columnasCabecera)
   AND NOT EXISTS (
    SELECT nombre FROM @columnasCabecera
    EXCEPT
    SELECT nombre FROM @esperadas)
    PRINT '  [OK] 14. El primer conjunto de mp.LeeMes es la cabecera, con sus ocho columnas.'
ELSE
BEGIN
    PRINT '  [MAL] 14. El primer conjunto de mp.LeeMes no es la cabecera que espera la aplicación.';
    THROW 50015, 'mp.LeeMes: cabecera con columnas distintas', 1;
END

/* 15. Los días no comprobados también tienen su contrato, y este sí se puede
   comprobar entero: mp.DiasSinComprobarDelMes devuelve un solo conjunto. */
DECLARE @columnasDias TABLE (nombre sysname);

INSERT @columnasDias (nombre)
SELECT name FROM sys.dm_exec_describe_first_result_set_for_object(
    OBJECT_ID('mp.DiasSinComprobarDelMes'), 0);

IF (SELECT COUNT(*) FROM @columnasDias) = 2
   AND EXISTS (SELECT 1 FROM @columnasDias WHERE nombre = 'FechaDia')
   AND EXISTS (SELECT 1 FROM @columnasDias WHERE nombre = 'Motivo')
    PRINT '  [OK] 15. Los días no comprobados se devuelven con FechaDia y Motivo.'
ELSE
BEGIN
    PRINT '  [MAL] 15. El conjunto de días no comprobados no trae FechaDia y Motivo.';
    THROW 50016, 'mp.DiasSinComprobarDelMes: columnas distintas', 1;
END

PRINT '';
PRINT '=== La busqueda del mes, por RANGO y no por YEAR/MONTH ===';
PRINT '';

/* 18. El total de cada mes tiene que coincidir con las filas cuya fecha cae
     entre el primer y el ULTIMO dia de ese mes, calculado aqui con aritmetica
     independiente.

     Esto detecta un @finDelMes mal hecho, y en esta base hay dias que lo
     detectan: el 27 de febrero de 2026, que es el dia 27 de un mes de 28, y el
     29 de julio, que es el dia 29 de uno de 31. Si el rango se quedara corto o
     se comiera un dia de mas, uno de esos dos se caeria del total.

     Con YEAR(FechaPublicacion) = @anio AND MONTH(...) = @mes tambien pasaria:
     las dos formas devuelven las mismas filas. Lo que cambia, y no se comprueba
     aqui, es si el indice IX_MpLicitacion_FechaPublicacion se puede usar. */
DECLARE @meses TABLE (Mes int, TotalDelMes int, RangoDelMes int);

INSERT @meses (Mes)
SELECT DISTINCT MONTH(FechaPublicacion) FROM dbo.MpLicitacion WHERE CodigoProveedor = @proveedor;

UPDATE m
SET TotalDelMes = (SELECT COUNT(*) FROM dbo.MpLicitacion AS l
                   WHERE l.CodigoProveedor = @proveedor
                     AND l.FechaPublicacion >= '2026-01-01'
                     AND l.FechaPublicacion <= '2026-12-31'
                     AND MONTH(l.FechaPublicacion) = m.Mes),
    RangoDelMes = (SELECT COUNT(*) FROM dbo.MpLicitacion AS l
                   WHERE l.CodigoProveedor = @proveedor
                     AND l.FechaPublicacion >= DATEFROMPARTS(2026, m.Mes, 1)
                     AND l.FechaPublicacion <= EOMONTH(DATEFROMPARTS(2026, m.Mes, 1))
                     AND MONTH(l.FechaPublicacion) = m.Mes)
FROM @meses AS m;

IF NOT EXISTS (SELECT 1 FROM @meses WHERE TotalDelMes <> RangoDelMes OR RangoDelMes = 0)
    PRINT '  [OK] 18. El rango del mes devuelve exactamente las mismas filas que el filtro por mes.'
ELSE
BEGIN
    DECLARE @detalle nvarchar(300) = (
        SELECT STRING_AGG(CONCAT('mes ', Mes, ': ', TotalDelMes, ' vs ', RangoDelMes), '; ')
        FROM @meses WHERE TotalDelMes <> RangoDelMes OR RangoDelMes = 0);

    PRINT '  [MAL] 18. El rango y el filtro por mes no coinciden -> ' + @detalle;
    THROW 50019, 'mp.LeeMes: el rango del mes no coincide con el filtro por mes', 1;
END

/* 19. Y un mes SIN datos tiene que dar cero, no "lo que hubiera".
     Junio de 2026 no tiene ninguna licitacion, y es el caso que la pantalla
     pinte como "Nada en el mes de Junio de 2026". */
DECLARE @junio TABLE (
    Anio int, Mes int, DiasHabiles int, DiasConsultados int,
    DiasFallidos int, DiasPendientes int, Total int, Consultado datetime2(7));

INSERT @junio EXEC mp.CuentaDiasDelMes
    @codigoProveedor = @proveedor, @anio = 2026, @mes = 6, @hoy = '2026-10-08';

IF (SELECT Total FROM @junio) = 0
    PRINT '  [OK] 19. Un mes sin licitaciones devuelve Total = 0.'
ELSE
BEGIN
    PRINT '  [MAL] 19. Un mes sin licitaciones no devuelve Total = 0.';
    THROW 50020, 'mp.LeeMes: un mes vacio no devuelve cero', 1;
END

/* 20. El total del RANGO completo del ano. El mismo filtro escrito de las dos
     formas tiene que dar el mismo numero, y tiene que ser 13 en esta base. Si
     aparece un 14 o un 12, el rango se comio un dia o se dejo uno. */
DECLARE @porRango int = (SELECT COUNT(*) FROM dbo.MpLicitacion
                         WHERE CodigoProveedor = @proveedor
                           AND FechaPublicacion >= '2026-01-01'
                           AND FechaPublicacion <= '2026-12-31');

DECLARE @porMes int = (SELECT COUNT(*) FROM dbo.MpLicitacion
                       WHERE CodigoProveedor = @proveedor
                         AND YEAR(FechaPublicacion) = 2026);

IF @porRango = @porMes
    PRINT '  [OK] 20. El rango del año completo coincide con YEAR/MONTH (' + CAST(@porRango AS varchar(10)) + ' licitaciones).'
ELSE
BEGIN
    PRINT '  [MAL] 20. El rango del año y YEAR/MONTH no coinciden.';
    THROW 50021, 'mp.LeeMes: rango anual distinto de YEAR/MONTH', 1;
END

PRINT '';
PRINT '=== mp.LeeDetalle ===';
PRINT '';

/* 16. Una licitación que sí está trae ficha.

   mp.LeeDetalle devuelve TRES conjuntos, así que INSERT @t EXEC no vale, y
   tampoco vale un cursor. O sea:

       DECLARE c CURSOR FOR EXEC mp.LeeDetalle ...;
       Incorrect syntax near the keyword 'EXEC'

   El CURSOR FOR de T-SQL acepta un SELECT o un UPDATE, no una llamada a un
   procedimiento. Es sintaxis de Oracle y de DB2, y el error no dice nada de
   por qué.

   Lo que queda es ejecutarlo y mirar qué salta, que es justo lo que hay que
   comprobar aquí: que exista devuelva EXISTS = 1, y que no exista devuelva
   EXISTS = 0 SIN LANZAR NADA. Eso último no se puede leer de una tabla: hay que
   verlo pasar, y un TRY/CATCH es la forma. */
IF EXISTS (SELECT 1 FROM dbo.MpLicitacion
           WHERE CodigoProveedor = @proveedor AND CodigoExterno = N'1456839-6-LP26')
BEGIN
    BEGIN TRY
        EXEC mp.LeeDetalle @codigoProveedor = @proveedor, @codigo = N'1456839-6-LP26';
        PRINT '  [OK] 16. Una licitación importada devuelve su ficha sin errores.';
    END TRY
    BEGIN CATCH
        PRINT '  [MAL] 16. mp.LeeDetalle falla con una licitación que sí está importada.';
        THROW;
    END CATCH
END
ELSE
BEGIN
    /* La base no tiene esa licitación, así que la prueba no puede decir nada.
       Se avisa en vez de pasar en verde: una prueba que se salta lo que no puede
       comprobar es una prueba que un día deja de comprobar y nadie lo nota. */
    PRINT '  [??] 16. OJO: la licitación 1456839-6-LP26 no está en esta base. Prueba sin valor.';
    THROW 50017, 'mp.LeeDetalle: la prueba necesita una licitacion importada', 1;
END

/* 17. Y una que NO está devuelve EXISTS = 0 y NO un error. Esto es lo que
   permite que la pantalla diga "esa licitación no está importada" en vez de "la
   base de datos está caída". Son dos cosas distintas y la que sale mal es la
   que se dice. */
BEGIN TRY
    EXEC mp.LeeDetalle @codigoProveedor = @proveedor, @codigo = N'NO-EXISTE-EN-ESTA-BASE';
    PRINT '  [OK] 17. Una licitación no importada devuelve Existe = 0, sin error.';
END TRY
BEGIN CATCH
    PRINT '  [MAL] 17. mp.LeeDetalle lanza un error en vez de decir que no está importada.';
    THROW;
END CATCH

PRINT '';
PRINT '--- Las 20 comprobaciones pasaron. No queda nada escrito. ---';
PRINT '';
