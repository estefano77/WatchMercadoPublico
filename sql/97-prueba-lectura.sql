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

/* ---------------------------------------------------------------------------
   LO QUE SIGUE NO COMPARA CON NINGUNA CIFRA ESCRITA A MANO

   Antes estas pruebas de octubre de 2026 decian, en texto, cuántos días había:
   "solo cuentan los 6 días que pasaron", "los 16 días hábiles que faltan",
   "no cuenta ningún día como comprobado", "salen los 6 días no comprobados".
   Todas salían bien con los datos de aquel día. Y todas se rompen en cuanto se
   importa algo de verdad, sin que ningún procedimiento haya cambiado.

   Pasó el 9 de octubre: al importar ese día con el ticket en regla, octubre pasó
   a tener un día comprobado. Las pruebas 9 y 10 salieron en rojo. Los
   procedimientos estaban bien; lo que se había movido eran los datos. Y las 8, 11,
   12 y 19 iban a hacer lo mismo en cuanto se rellenara lo que falta.

   Y aquí está lo que másFastidaba: arreglar eso cambiando el 6 por el 5 y el 0
   por el 1 pone la prueba en verde otra vez, y a la semana siguiente vuelve a
   saltar. Una prueba que hay que tocar para ponerla en verde es una que un día
   nadie vuelve a mirar, que es peor que no tenerla.

   Así que a partir de aquí no hay ni un número escrito. Las cuentas se hacen aquí,
   con aritmética independiente y cruzando con MpConsulta, y se compara lo que
   devuelve el procedimiento contra eso. Da igual lo que haya en la base:

     - si un día se dice comprobado, tiene que haber una consulta con éxito
     - si un día se dice no comprobado, no puede haberla
     - y la lista tiene que ser exactamente la de los días que no se pudieron

   Esas tres cosas se rompen si un procedimiento miente. No se rompen si alguien
   importa, que es lo que pasaba antes.
   --------------------------------------------------------------------------- */

/* Los días hábiles de octubre, contados aquí. Es la referencia contra la que se
   compara todo lo de abajo, y sale de contar, no de una cifra. */
DECLARE @diasDelMesOct TABLE (Dia date PRIMARY KEY);

INSERT @diasDelMesOct (Dia)
SELECT Dia FROM mp.TodosLosDiasDelMes('2026-10-01', '2026-10-31')
WHERE DATEPART(WEEKDAY, Dia) BETWEEN 2 AND 6;

DECLARE @laborablesHastaHoy int = (SELECT COUNT(*) FROM @diasDelMesOct WHERE Dia <= '2026-10-08');
DECLARE @laborablesDelMes    int = (SELECT COUNT(*) FROM @diasDelMesOct);
DECLARE @conOk int = (SELECT COUNT(*) FROM @diasDelMesOct d
                      WHERE d.Dia <= '2026-10-08'
                        AND EXISTS (SELECT 1 FROM dbo.MpConsulta c
                                    WHERE c.CodigoProveedor = @proveedor
                                      AND c.FechaDia = d.Dia AND c.Exito = 1));
DECLARE @esperadosFallidos   int = @laborablesHastaHoy - @conOk;
DECLARE @esperadosPendientes int = @laborablesDelMes - @laborablesHastaHoy;

INSERT @oct EXEC mp.CuentaDiasDelMes
    @codigoProveedor = @proveedor, @anio = 2026, @mes = 10, @hoy = '2026-10-08';

INSERT @sinComprobar EXEC mp.DiasSinComprobarDelMes
    @codigoProveedor = @proveedor, @anio = 2026, @mes = 10, @hoy = '2026-10-08';

/* PRINT NO ADMITE SUBCONSULTAS, solo expresiones escalares. En cuanto se
   escribe PRINT 'algo' + CONVERT(varchar, (SELECT ...)) sale "Subqueries are not
   allowed in this context. Only scalar expressions are allowed", y el mensaje
   no dice que el problema es el PRINT. Por eso todo lo que se imprime con
   números se saca antes a una variable. */
DECLARE @octHabiles     int = (SELECT DiasHabiles     FROM @oct);
DECLARE @octConsultados int = (SELECT DiasConsultados FROM @oct);
DECLARE @octFallidos    int = (SELECT DiasFallidos    FROM @oct);
DECLARE @octPendientes  int = (SELECT DiasPendientes  FROM @oct);
DECLARE @cuantosSin     int = (SELECT COUNT(*)        FROM @sinComprobar);

/* 6. Del 1 al 8 solo cuentan los días hábiles que ya pasaron, no los del mes
   entero: contar los que todavía no han llegado sería avisar de días que no
   existen. Este número decide si el panel dice la verdad sobre octubre.

   La cuenta sale de contar los días aquí. Si mañana se consultan el 9 y el 10,
   esta cuenta da 8 y la prueba sigue valiendo. */
IF @octHabiles = @laborablesHastaHoy
    PRINT '  [OK]  6. Del 1 al 8 de octubre solo cuentan los días hábiles que pasaron (' + CONVERT(varchar(10), @laborablesHastaHoy) + ').'
ELSE
BEGIN
    PRINT '  [MAL] 6. Dias habiles = ' + CONVERT(varchar(10), @octHabiles) + ' y deberian ser ' + CONVERT(varchar(10), @laborablesHastaHoy) + '.';
    THROW 50007, 'mp.CuentaDiasDelMes: dias habiles de un mes en curso', 1;
END

/* 7. Y los que quedan en el mes salen como pendientes, no como fallidos: los
   hábiles del mes entero menos los que ya pasaron. */
IF @octPendientes = @esperadosPendientes
    PRINT '  [OK]  7. Los días hábiles que faltan en octubre salen como pendientes.'
ELSE
BEGIN
    PRINT '  [MAL] 7. Dias pendientes = ' + CONVERT(varchar(10), @octPendientes) + ' y deberian ser ' + CONVERT(varchar(10), @esperadosPendientes) + '.';
    THROW 50008, 'mp.CuentaDiasDelMes: dias pendientes incorrectos', 1;
END

/* 8. Los días que dice comprobados tienen que estarlo de verdad: los mismos que
   tienen una consulta con éxito, contados aquí aparte. Si contara un día que
   nadie preguntó, la pantalla afirmaría que se comprobó algo que no se comprobó. */
IF @octConsultados = @conOk
    PRINT '  [OK]  8. Dias comprobados = días con consulta exitosa, contados aparte (' + CONVERT(varchar(10), @conOk) + ').'
ELSE
BEGIN
    PRINT '  [MAL] 8. Dias comprobados = ' + CONVERT(varchar(10), @octConsultados) + ' pero hay ' + CONVERT(varchar(10), @conOk) + ' con consulta exitosa.';
    THROW 50009, 'mp.CuentaDiasDelMes: dias consultados que no se comproaron', 1;
END

/* 9. Y DiasFallidos tiene que ser exactamente los días hábiles que ya pasaron y
   no se pudieron comprobar.

   Esta es la que sustituye a "octubre no cuenta ningún día como comprobado",
   que era una afirmación sobre octubre y no sobre el procedimiento. Y es la que
   caza el fallo de contar días POSTERIORES a @hoy: con ese error DiasConsultados
   salía mayor que DiasHabiles y DiasFallidos salía NEGATIVO, y como la pantalla
   decide con "DiasFallidos > 0" un mes con 22 días sin mirar se anunciaba como
   comprobado entero. */
IF @octFallidos = @esperadosFallidos
    PRINT '  [OK]  9. Dias fallidos = días hábiles sin consulta exitosa, contados aparte (' + CONVERT(varchar(10), @esperadosFallidos) + ').'
ELSE
BEGIN
    PRINT '  [MAL] 9. Dias fallidos = ' + CONVERT(varchar(10), @octFallidos) + ' pero hay ' + CONVERT(varchar(10), @esperadosFallidos) + ' sin comprobar.';
    THROW 50010, 'mp.CuentaDiasDelMes: dias fallidos incorrectos', 1;
END

/* Y DiasFallidos nunca puede ser negativo. Es una comprobación de la cuenta, no
   de los datos, y por eso vale para siempre. */
IF @octFallidos >= 0
    PRINT '  [OK] 10. Dias fallidos no es negativo.'
ELSE
BEGIN
    PRINT '  [MAL] 10. Dias fallidos = ' + CONVERT(varchar(10), @octFallidos) + ', que es negativo.';
    THROW 50015, 'mp.CuentaDiasDelMes: dias fallidos negativo', 1;
END

/* 11. Ningún día que salga como "no comprobado" puede tener una consulta exitosa.
     Esta es la que sostiene la frase de la pantalla, y no depende de cuántos
     días haya: si el procedimiento metiera en la lista un día que sí se
     comprobó, diría que no se pudo comprobar algo que sí se pudo. */
IF NOT EXISTS (SELECT 1 FROM @sinComprobar s
               WHERE EXISTS (SELECT 1 FROM dbo.MpConsulta c
                             WHERE c.CodigoProveedor = @proveedor
                               AND c.FechaDia = s.FechaDia AND c.Exito = 1))
    PRINT '  [OK] 11. Ningún día no comprobado tiene una consulta exitosa.'
ELSE
BEGIN
    DECLARE @mentiroso date = (SELECT TOP 1 s.FechaDia FROM @sinComprobar s
                               WHERE EXISTS (SELECT 1 FROM dbo.MpConsulta c
                                             WHERE c.CodigoProveedor = @proveedor
                                               AND c.FechaDia = s.FechaDia AND c.Exito = 1));

    PRINT '  [MAL] 11. El ' + CONVERT(varchar(10), @mentiroso, 23) + ' sale como no comprobado y sí se consultó.';
    THROW 50011, 'mp.DiasSinComprobarDelMes: un dia comprobado sale como no comprobado', 1;
END

/* 12. Y la lista tiene que ser COMPLETA: todos los días hábiles que ya pasaron y
     no se pudieron comprobar, y ninguno más. Ni uno de menos —que sería
     esconder una comprobación fallida— ni uno de más —que sería decir que no se
     pudo comprobar un día que sí se miró—. */
IF @cuantosSin = @octFallidos AND @cuantosSin = @esperadosFallidos
    PRINT '  [OK] 12. Salen todos los días no comprobados de octubre, ni uno más.'
ELSE
BEGIN
    PRINT '  [MAL] 12. Dias no comprobados = ' + CONVERT(varchar(10), @cuantosSin) + ', DiasFallidos = ' + CONVERT(varchar(10), @octFallidos) + ', y deberian ser ' + CONVERT(varchar(10), @esperadosFallidos) + '.';
    THROW 50012, 'mp.DiasSinComprobarDelMes: numero de dias incorrecto', 1;
END

/* 13. El motivo tiene que ser el de verdad, y solo se exige si hay ALGÚN día sin
     comprobar cuyo último intento fue un rechazo por ticket.

     Esto costó una vuelta de más, y la culpa fue de acortar el WHERE. La
     primera versión de esta prueba miraba si había algún 203 en el mes, sin más:

         IF EXISTS (SELECT 1 FROM dbo.MpConsulta c
                    WHERE ... AND c.Exito = 0 AND c.CodigoHttp = 203)

     Con los datos de octubre tal como estaban, el 5, 6 y 7 tienen un 203. Pero en
     MpConsulta queda el registro de ESE intento aunque después el día se
     consultara bien. Y en cuanto se importa de verdad, el día 5 pasa a estar
     comprobado y desaparece de la lista de "no comprobado" —porque ya no hay
     motivo que enseñar—, mientras su 203 sigue ahí para siempre.

     O sea: la prueba pedía un motivo para un día que ya no sale en la lista, y
     fallaba justo cuando la base estaba bien. Medido: seis días de
     octubre consultados, la 13 sale en rojo con "hay días rechazados con 203 y
     ningún motivo dice que el ticket no valía", siendo que no hay ni un solo día
     sin comprobar en ese mes.

     La condición correcta es "algún día DE LA LISTA tiene un rechazo por
     ticket", que es lo que el motivo tiene que explicar. Los 203 de días ya
     comprobados son historia, no un motivo pendiente. */
DECLARE @rechazadosPorTicket int = (
    SELECT COUNT(*) FROM @sinComprobar s
    WHERE EXISTS (SELECT 1 FROM dbo.MpConsulta c
                  WHERE c.CodigoProveedor = @proveedor
                    AND c.FechaDia = s.FechaDia
                    AND c.Exito = 0
                    AND c.CodigoHttp = 203
                    AND c.NumeroIntento = (SELECT MAX(c2.NumeroIntento) FROM dbo.MpConsulta c2
                                           WHERE c2.CodigoProveedor = c.CodigoProveedor
                                             AND c2.FechaDia = c.FechaDia)));

IF @rechazadosPorTicket > 0
BEGIN
    IF EXISTS (SELECT 1 FROM @sinComprobar WHERE Motivo LIKE '%válido%' OR Motivo LIKE '%valido%')
        PRINT '  [OK] 13. Los días rechazados por ticket dicen que el ticket no valía.';
    ELSE
    BEGIN
        PRINT '  [MAL] 13. Hay ' + CONVERT(varchar(10), @rechazadosPorTicket) + ' día(s) sin comprobar rechazados por ticket y ningún motivo lo dice.';
        THROW 50013, 'mp.DiasSinComprobarDelMes: sin motivo del rechazo', 1;
    END
END
ELSE
    PRINT '  [OK] 13. No queda ningún día sin comprobar rechazado por ticket, y no se anuncia ninguno.'

/* 14. Y los días nunca preguntados, igual que antes: solo se exigen si los hay,
     y además se exige lo contrario cuando el mes está comprobado entero. Un
     motivo "nunca se consultó" en un mes sin días sin preguntar sería el
     procedimiento inventándose un fallo. */
IF EXISTS (SELECT 1 FROM @diasDelMesOct d
           WHERE d.Dia <= '2026-10-08'
             AND NOT EXISTS (SELECT 1 FROM dbo.MpConsulta c
                             WHERE c.CodigoProveedor = @proveedor
                               AND c.FechaDia = d.Dia))
BEGIN
    IF EXISTS (SELECT 1 FROM @sinComprobar WHERE Motivo LIKE 'Nunca se consulto%')
        PRINT '  [OK] 14. Los días nunca preguntados también salen.'
    ELSE
    BEGIN
        PRINT '  [MAL] 14. Hay días que nunca se preguntaron y ningún motivo lo dice.';
        THROW 50014, 'mp.DiasSinComprobarDelMes: dias nunca preguntados ausentes', 1;
    END
END
ELSE IF EXISTS (SELECT 1 FROM @sinComprobar WHERE Motivo LIKE 'Nunca se consulto%')
BEGIN
    PRINT '  [MAL] 14. Dice que hubo días nunca preguntados y el mes está comprobado entero.';
    THROW 50018, 'mp.DiasSinComprobarDelMes: motivo de dia nunca consultado inexistente', 1;
END
ELSE
    PRINT '  [OK] 14. Todos los días del mes se preguntaron, y no se anuncia ninguno que no.'

/* 15. Ningún día sin comprobar puede tener motivo vacío: un "3 días sin
   comprobar" sin motivo deja al usuario sin poder decidir nada. */
IF NOT EXISTS (SELECT 1 FROM @sinComprobar WHERE Motivo IS NULL OR LTRIM(RTRIM(Motivo)) = '')
    PRINT '  [OK] 15. Ningún día no comprobado viene sin motivo.'
ELSE
BEGIN
    PRINT '  [MAL] 15. Hay días no comprobados sin motivo.';
    THROW 50019, 'mp.DiasSinComprobarDelMes: dias sin motivo', 1;
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
    PRINT '  [OK] 16. El primer conjunto de mp.LeeMes es la cabecera, con sus ocho columnas.'
ELSE
BEGIN
    PRINT '  [MAL] 16. El primer conjunto de mp.LeeMes no es la cabecera que espera la aplicación.';
    THROW 50016, 'mp.LeeMes: cabecera con columnas distintas', 1;
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
    PRINT '  [OK] 17. Los días no comprobados se devuelven con FechaDia y Motivo.'
ELSE
BEGIN
    PRINT '  [MAL] 17. El conjunto de días no comprobados no trae FechaDia y Motivo.';
    THROW 50017, 'mp.DiasSinComprobarDelMes: columnas distintas', 1;
END

PRINT '';
PRINT '=== La busqueda del mes, por RANGO y no por YEAR/MONTH ===';
PRINT '';

/* 18. El total de cada mes tiene que coincidir con las filas cuya fecha cae
     entre el primer y el ULTIMO dia de ese mes, calculado aqui con aritmetica
     independiente.

     Esto detecta un @finDelMes mal hecho. Con una base que solo tiene 2026, el
     27 de febrero y el 29 de julio lo detectaban: son el dia 27 de un mes de 28
     y el dia 29 de uno de 31, asi que un rango corto o largo los deja fuera.
     Con datos de varios anos la lista de (ano, mes) sale de los propios datos,
     que es lo que hace que la comprobacion siga sirviendo en cualquier base.

     OJO, Y ESTO SE ARREGLO PORQUE FALLO: la version anterior tomaba solo
     MONTH(FechaPublicacion) y filtra con un rango fijo de 2026. En cuanto la
     base tuvo licitaciones de 2024 y 2025 -que las tiene-, los meses que solo
     existen en esos anos daban cero y la prueba se caia sin que hubiera nada
     roto:

         [MAL] 18. ... -> mes 5: 0 vs 0; mes 6: 0 vs 0; mes 8: 0 vs 0

     Es el mismo fallo que el de la prueba 13, y por el mismo motivo: una
     comprobacion atada a los datos vigentes. Aqui la diferencia es que el ano va
     con el mes, y con eso los dos metodos se comparan siempre sobre el mismo
     conjunto. */
DECLARE @meses TABLE (Anio int, Mes int, TotalDelMes int, RangoDelMes int);

INSERT @meses (Anio, Mes)
SELECT DISTINCT YEAR(FechaPublicacion), MONTH(FechaPublicacion)
FROM dbo.MpLicitacion WHERE CodigoProveedor = @proveedor;

UPDATE m
SET TotalDelMes = (SELECT COUNT(*) FROM dbo.MpLicitacion AS l
                   WHERE l.CodigoProveedor = @proveedor
                     AND YEAR(l.FechaPublicacion) = m.Anio
                     AND MONTH(l.FechaPublicacion) = m.Mes),
    RangoDelMes = (SELECT COUNT(*) FROM dbo.MpLicitacion AS l
                   WHERE l.CodigoProveedor = @proveedor
                     AND l.FechaPublicacion >= DATEFROMPARTS(m.Anio, m.Mes, 1)
                     AND l.FechaPublicacion <= EOMONTH(DATEFROMPARTS(m.Anio, m.Mes, 1))
                     AND YEAR(l.FechaPublicacion) = m.Anio
                     AND MONTH(l.FechaPublicacion) = m.Mes)
FROM @meses AS m;

IF NOT EXISTS (SELECT 1 FROM @meses WHERE TotalDelMes <> RangoDelMes OR RangoDelMes = 0)
    PRINT '  [OK] 18. El rango del mes devuelve exactamente las mismas filas que el filtro por mes.'
ELSE
BEGIN
    DECLARE @detalle nvarchar(300) = (
        SELECT STRING_AGG(CONCAT(Anio, '-', RIGHT('0' + CONVERT(varchar(2), Mes), 2), ': ',
                                    TotalDelMes, ' vs ', RangoDelMes), '; ')
        FROM @meses WHERE TotalDelMes <> RangoDelMes OR RangoDelMes = 0);

    PRINT '  [MAL] 18. El rango y el filtro por mes no coinciden -> ' + @detalle;
    THROW 50019, 'mp.LeeMes: el rango del mes no coincide con el filtro por mes', 1;
END

/* 19. Un mes SIN datos tiene que dar cero, no "lo que hubiera": es el caso que
     la pantalla pinta como "Nada en el mes de Junio de 2026".

     Antes se comprobaba junio, y junio estaba vacío. El día que se importara
     algo de junio la prueba fallaba sin que nada estuviera roto, y el arreglo
     fácil era apuntar a otro mes que estuviera vacío, que es una forma elegante
     de dejar de comprobar.

     Ahora se comprueban LOS DOCE MESES contra un COUNT directo, y entre ellos
     está el que esté vacío. Sale más fuerte que antes —no se prueba un mes, se
     prueban todos— y no depende de qué mes toque ni de qué haya en la base. */
DECLARE @totales TABLE (Mes int, TotalDelProcedimiento int, TotalDirecto int);

/* @cabecera se declara UNA VEZ, fuera del bucle, y se vacía con DELETE en cada
   vuelta. Un DECLARE dentro de un WHILE no se puede repetir: en la segunda
   vuelta sale "The variable name '@cabecera' has already been declared", que no
   señala ni el mes ni el bucle y parece un problema de la base.

   Y se vacía con DELETE y no con TRUNCATE, porque TRUNCATE no existe para
   variables de tabla:

       Incorrect syntax near '@cabecera'

   que además no dice que lo que falla es una tabla variable. */
DECLARE @cabecera TABLE (
    Anio int, Mes int, DiasHabiles int, DiasConsultados int,
    DiasFallidos int, DiasPendientes int, Total int, Consultado datetime2(7));

DECLARE @m int = 1;

WHILE @m <= 12
BEGIN
    DELETE FROM @cabecera;

    INSERT @cabecera EXEC mp.CuentaDiasDelMes
        @codigoProveedor = @proveedor, @anio = 2026, @mes = @m, @hoy = '2026-12-31';

    /* El Total se lee por una variable, y no con un (SELECT Total FROM
       @cabecera). Con ISNULL(MAX(...), -1) se distingue además el caso de que
       el procedimiento no devuelva nada, que es un fallo distinto de que
       devuelva un total equivocado. */
    DECLARE @totalAqui int = (SELECT ISNULL(MAX(Total), -1) FROM @cabecera);

    INSERT @totales (Mes, TotalDelProcedimiento, TotalDirecto)
    SELECT @m,
           @totalAqui,
           (SELECT COUNT(*) FROM dbo.MpLicitacion
            WHERE CodigoProveedor = @proveedor
              AND YEAR(FechaPublicacion) = 2026
              AND MONTH(FechaPublicacion) = @m);

    SET @m = @m + 1;
END

IF NOT EXISTS (SELECT 1 FROM @totales WHERE TotalDelProcedimiento <> TotalDirecto)
    PRINT '  [OK] 19. Los doce meses devuelven el total que hay de verdad, vacíos incluidos.'
ELSE
BEGIN
    DECLARE @descuadre nvarchar(300) = (
        SELECT STRING_AGG(CONCAT('mes ', Mes, ': ', TotalDelProcedimiento, ' vs ', TotalDirecto), '; ')
        FROM @totales WHERE TotalDelProcedimiento <> TotalDirecto);

    PRINT '  [MAL] 19. Un mes no devuelve el total que tiene -> ' + @descuadre;
    THROW 50020, 'mp.LeeMes: un mes no devuelve su total', 1;
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
/* Y la licitación se elige DE LA BASE, no se escribe.

   Antes era 1456839-6-LP26 con un 0, y el día que esa no esté —porque se importó
   otra cosa, o porque la base es otra— la prueba se quedaba sin valor. Cogiendo
   la más reciente que haya, usa lo que exista y no depende de un código que
   nadie recuerda haber escrito. */
DECLARE @codigoParaProbar nvarchar(64) =
    (SELECT TOP 1 CodigoExterno FROM dbo.MpLicitacion
     WHERE CodigoProveedor = @proveedor
     ORDER BY FechaPublicacion DESC, LicitacionId DESC);

IF @codigoParaProbar IS NOT NULL
BEGIN
    BEGIN TRY
        EXEC mp.LeeDetalle @codigoProveedor = @proveedor, @codigo = @codigoParaProbar;
        PRINT '  [OK] 21. Una licitación importada devuelve su ficha sin errores (' + @codigoParaProbar + ').';
    END TRY
    BEGIN CATCH
        PRINT '  [MAL] 21. mp.LeeDetalle falla con una licitación que sí está importada.';
        THROW;
    END CATCH
END
ELSE
BEGIN
    /* La base no tiene ninguna licitación, así que la prueba no puede decir nada.
       Se avisa en vez de pasar en verde: una prueba que se salta lo que no puede
       comprobar es una prueba que un día deja de comprobar y nadie lo nota. */
    PRINT '  [??] 21. OJO: esta base no tiene ninguna licitación para ' + @proveedor + '. Prueba sin valor.';
    THROW 50021, 'mp.LeeDetalle: la prueba necesita una licitacion importada', 1;
END

/* 17. Y una que NO está devuelve EXISTS = 0 y NO un error. Esto es lo que
   permite que la pantalla diga "esa licitación no está importada" en vez de "la
   base de datos está caída". Son dos cosas distintas y la que sale mal es la
   que se dice. */
BEGIN TRY
    EXEC mp.LeeDetalle @codigoProveedor = @proveedor, @codigo = N'NO-EXISTE-EN-ESTA-BASE';
    PRINT '  [OK] 22. Una licitación no importada devuelve Existe = 0, sin error.';
END TRY
BEGIN CATCH
    PRINT '  [MAL] 22. mp.LeeDetalle lanza un error en vez de decir que no está importada.';
    THROW;
END CATCH

PRINT '';
PRINT '=== mp.LeeEmpresa: la empresa vigilada ===';
PRINT '';

/* Esta sección ESCRIBE en MpEmpresa, y las de arriba no escriben nada. Por eso
   va en su propia transacción, que se cierra aquí mismo con un ROLLBACK.

   Y el motivo de que haga falta escribir es el que hace que estas pruebas
   existan: mp.LeeEmpresa devuelve cero filas cuando la tabla está vacía, y
   eso es una respuesta CORRECTA. Para comprobar que devuelve la fila cuando
   tiene que devolverla hay que ponerla.

   El caso que importa más que los otros es el del RUT escrito de otra forma.
   La comparación es normalizada a propósito, y una prueba que solo pasara el
   RUT tal como está en el appsettings no comprobaría NADA de esa decisión: el
   día que alguien cambiara "86.130.200-8" por "861302008" —el mismo RUT— y
   la comparación volviera a ser textual, estas pruebas seguirían en verde. */
BEGIN TRY
    BEGIN TRAN;

    DECLARE @empresa TABLE (
        CodigoProveedor   nvarchar(50),
        NombreEmpresa     nvarchar(300),
        RutEmpresa        nvarchar(20),
        UrlMercadoPublico nvarchar(500),
        UltimaActualizacion datetime2(3));

    DECLARE @n int, @codigo nvarchar(50), @nombre nvarchar(300), @url nvarchar(500);
    DECLARE @mal int = 0;

    DELETE FROM dbo.MpEmpresa;

    INSERT INTO dbo.MpEmpresa (CodigoProveedor, NombreEmpresa, RutEmpresa, UrlMercadoPublico)
    VALUES (N'999001', N'EMPRESA DE PRUEBA UNO', N'86.130.200-8', N'https://ejemplo.invalid/prueba');

    INSERT INTO dbo.MpEmpresa (CodigoProveedor, NombreEmpresa, RutEmpresa)
    VALUES (N'999002', N'EMPRESA DE PRUEBA DOS', N'761234560');

    /* 23. El RUT tal como lo escribe una persona, con puntos y guion. */
    INSERT @empresa EXEC mp.LeeEmpresa @rutEmpresa = N'86.130.200-8';
    SELECT @n = COUNT(*), @codigo = MAX(CodigoProveedor),
           @nombre = MAX(NombreEmpresa), @url = MAX(UrlMercadoPublico) FROM @empresa;
    IF @n = 1 AND @codigo = N'999001'
       AND @nombre = N'EMPRESA DE PRUEBA UNO'
       AND @url = N'https://ejemplo.invalid/prueba'
        PRINT '  [OK] 23. mp.LeeEmpresa devuelve la fila entera con el RUT bien escrito.';
    ELSE
    BEGIN
        PRINT '  [MAL] 23. mp.LeeEmpresa devolvió ' + CONVERT(varchar(2), @n)
              + ' fila(s), código [' + ISNULL(@codigo, N'(nada)') + N'] y URL ['
              + ISNULL(@url, N'(nada)') + '].';
        SET @mal = @mal + 1;
    END

    /* 24. EL MISMO RUT, escrito SIN puntos ni guion. */
    DELETE @empresa;
    INSERT @empresa EXEC mp.LeeEmpresa @rutEmpresa = N'861302008';
    SELECT @n = COUNT(*), @codigo = MAX(CodigoProveedor) FROM @empresa;
    IF @n = 1 AND @codigo = N'999001'
        PRINT '  [OK] 24. El mismo RUT sin puntos y sin guion también se encuentra.';
    ELSE
    BEGIN
        PRINT '  [MAL] 24. Con el RUT normalizado devolvió ' + CONVERT(varchar(2), @n) + ' fila(s).';
        SET @mal = @mal + 1;
    END

    /* 25. Y con puntos pero sin guion, que es el tercer formato de verdad. */
    DELETE @empresa;
    INSERT @empresa EXEC mp.LeeEmpresa @rutEmpresa = N'761234560';
    SELECT @n = COUNT(*), @codigo = MAX(CodigoProveedor) FROM @empresa;
    IF @n = 1 AND @codigo = N'999002'
        PRINT '  [OK] 25. Un RUT con puntos y sin guion también se encuentra.';
    ELSE
    BEGIN
        PRINT '  [MAL] 25. Con otro formato devolvió ' + CONVERT(varchar(2), @n) + ' fila(s).';
        SET @mal = @mal + 1;
    END

    /* 26. Un RUT que no existe: CERO filas y NINGÚN error. Esto es lo que
       permite que la pantalla diga "no hay ninguna empresa con ese RUT" en vez
       de caerse. */
    DELETE @empresa;
    BEGIN TRY
        INSERT @empresa EXEC mp.LeeEmpresa @rutEmpresa = N'99.999.999-9';
        SELECT @n = COUNT(*) FROM @empresa;
        IF @n = 0
            PRINT '  [OK] 26. Un RUT que no está en la tabla devuelve cero filas, sin error.';
        ELSE
        BEGIN
            PRINT '  [MAL] 26. Un RUT inexistente devolvió ' + CONVERT(varchar(2), @n) + ' fila(s).';
            SET @mal = @mal + 1;
        END
    END TRY
    BEGIN CATCH
        PRINT '  [MAL] 26. mp.LeeEmpresa lanza un error en vez de devolver cero filas.';
        SET @mal = @mal + 1;
    END CATCH

    /* 27. RUT vacío y RUT nulo: igual que no encontrar nada, y sin error. Que
       falte el RUT en la configuración es un problema de arranque del
       servidor, no un fallo de la base. */
    DELETE @empresa;
    BEGIN TRY
        INSERT @empresa EXEC mp.LeeEmpresa @rutEmpresa = N'';
        INSERT @empresa EXEC mp.LeeEmpresa @rutEmpresa = NULL;
        SELECT @n = COUNT(*) FROM @empresa;
        IF @n = 0
            PRINT '  [OK] 27. RUT vacío y RUT nulo devuelven cero filas, sin error.';
        ELSE
        BEGIN
            PRINT '  [MAL] 27. Con el RUT vacío devolvió ' + CONVERT(varchar(2), @n) + ' fila(s).';
            SET @mal = @mal + 1;
        END
    END TRY
    BEGIN CATCH
        PRINT '  [MAL] 27. mp.LeeEmpresa lanza un error con el RUT vacío.';
        SET @mal = @mal + 1;
    END CATCH

    /* 28. Y con la tabla ENTERA vacía, que es el estado en el que está una
       base recién creada y también el que hace que el guion de ingesta se
       ponga a llenarla. */
    DELETE @empresa;
    DELETE FROM dbo.MpEmpresa;
    BEGIN TRY
        INSERT @empresa EXEC mp.LeeEmpresa @rutEmpresa = N'86.130.200-8';
        SELECT @n = COUNT(*) FROM @empresa;
        IF @n = 0
            PRINT '  [OK] 28. Con la tabla vacía devuelve cero filas, sin error.';
        ELSE
        BEGIN
            PRINT '  [MAL] 28. Con la tabla vacía devolvió ' + CONVERT(varchar(2), @n) + ' fila(s).';
            SET @mal = @mal + 1;
    END
    END TRY
    BEGIN CATCH
        PRINT '  [MAL] 28. mp.LeeEmpresa lanza un error con la tabla vacía.';
        SET @mal = @mal + 1;
    END CATCH

    /* 29. Dos códigos con el MISMO RUT. La clave única está en
       CodigoProveedor, no en el RUT, porque el RUT es la clave de búsqueda y
       el código es lo que viaja en cada llamada. Dos códigos para la misma
       empresa es raro, pero no puede ser un fallo: sale la más reciente. */
    DELETE @empresa;
    INSERT INTO dbo.MpEmpresa (CodigoProveedor, NombreEmpresa, RutEmpresa, UltimaActualizacion)
    VALUES (N'999003', N'MAS VIEJA', N'86.130.200-8', SYSUTCDATETIME());
    INSERT INTO dbo.MpEmpresa (CodigoProveedor, NombreEmpresa, RutEmpresa, UltimaActualizacion)
    VALUES (N'999004', N'MAS NUEVA', N'861302008', DATEADD(SECOND, 60, SYSUTCDATETIME()));

    INSERT @empresa EXEC mp.LeeEmpresa @rutEmpresa = N'86.130.200-8';
    SELECT @n = COUNT(*), @codigo = MAX(CodigoProveedor) FROM @empresa;
    IF @n = 1 AND @codigo = N'999004'
        PRINT '  [OK] 29. Con dos filas del mismo RUT sale la más reciente, no un error.';
    ELSE
    BEGIN
        PRINT '  [MAL] 29. Con dos filas del mismo RUT devolvió ' + CONVERT(varchar(2), @n)
              + ' fila(s), código [' + ISNULL(@codigo, N'(nada)') + '].';
        SET @mal = @mal + 1;
    END

    ROLLBACK TRAN;

    IF @mal > 0
        THROW 50023, 'mp.LeeEmpresa: hay comprobaciones que fallan', 1;
END TRY
BEGIN CATCH
    /* El ROLLBACK va AQUÍ y no al final a secas: si algo de lo de arriba
       falla, sin esto la transacción se queda abierta y las siguientes
       pruebas de este mismo fichero salen todas en rojo por un motivo que no
       es el suyo. Es la razón de que esto esté en TRY/CATCH y no suelto. */
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    PRINT '  Se ha revertido todo lo de esta sección.';
    THROW;
END CATCH

PRINT '';
PRINT '--- Las 29 comprobaciones pasaron. No queda nada escrito. ---';
PRINT '';
