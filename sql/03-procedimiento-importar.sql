/* ===========================================================================
   Procedimiento de importacion: de un rango de fechas a las tablas.

   Hay DOS procedimientos, y la division es deliberada:

     MpPeticionGet      el transporte. Un GET y poco mas. Es el unico que es
                        CLR, y vive en 02-registrar-ensamblado.sql.

     MpImportarRango    todo lo demas: que dias se preguntan, con que esperas,
                        cuando se reintenta, que se guarda en cada tabla y que se
                        escribe en el historico. Es T-SQL de principio a fin.

   Por que no meterse el JSON en el CLR: OPENJSON de SQL Server 2022 llega de
   sobra al formato de Mercado Publico, y deja el mapeo de campo a campo a la
   vista en este fichero, donde se lee y se corrige sin recompilar nada. Un
   error de mapeo en un ensamblado solo se ve recompilando; aqui se ve leyendo.

   =========================================================================== */


/* ESTAS DOS LINEAS, ANTES DEL CREATE, SON OBLIGATORIAS.

   El problema que resuelven: este procedimiento inserta en tablas con indice
   filtrado (los "WHERE Exito = 0" y "WHERE FechaDia IS NOT NULL" de MpConsulta)
   y en una tabla con columna calculada PERSISTED (Subtotal en MpLicitacionItem).
   SQL Server exige que esas opciones SET esten activas no solo al EJECUTAR el
   procedimiento, sino al COMPILARLO.

   Ponerlas solo dentro (con SET QUOTED_IDENTIFIER ON en el cuerpo) NO BASTA, y
   se probo: el primer INSERT falla igual con

       INSERT failed because the following SET options have incorrect
       settings: 'QUOTED_IDENTIFIER'

   Ni con el script que lo llama abriendo el lote con un SET, porque al llamar a
   un procedimiento almacenado la sesion crea un ambito nuevo y no hereda el del
   que llama.

   Con esto delante, la compilacion del modulo ocurre con la opcion correcta y
   el INSERT funciona. El mismo SET se repite dentro del procedimiento para que
   siga siendo valido aunque alguien lo llame desde otro contexto. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO


CREATE OR ALTER PROCEDURE dbo.MpImportarRango
    @desde                  date,
    @hasta                  date,
    @codigoProveedor        nvarchar(50),
    @ticket                 nvarchar(200),
    @conDetalle             bit          = 1,   -- ademas del listado, la ficha
    @soloFaltantes          bit          = 0,   -- saltar dias ya descargados
    @maxIntentosDia         int          = 6,
    @maxIntentosDetalle     int          = 3,
    @minutosEsperaInicial   int          = 2,
    @segundosEsperaMaxima   int          = 30,
    @segundosEntreLlamadas  int          = 2,
    @segundosTimeout        int          = 30,
    @modoRegistro           varchar(10)  = 'v1',
    @resultado              nvarchar(max) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* ESTAS TRES COSAS TIENEN QUE ESTAR DENTRO DEL PROCEDIMIENTO, y no ponerlas
       solo en el script que lo llama.

       Cuando se llama a un procedimiento almacenado, la sesion crea un
       AMBITO NUEVO con los valores por defecto de SET, y el del que llama no se
       heredan. Es decir: por muy bien que el cliente haga

           SET QUOTED_IDENTIFIER ON
           EXEC dbo.MpImportarRango ...

       dentro del procedimiento sigue estando en OFF, y el primer INSERT falla
       con

           INSERT failed because the following SET options have incorrect
           settings: 'QUOTED_IDENTIFIER'

       ...que ademas no dice NADA de que la causa sea el ambito del
       procedimiento, y hace pensar en las columnas calculadas o en los indices
       filtrados, que en realidad estan bien.

       Hace falta porque hay dos cosas en este procedimiento que lo exigen:
       la columna Subtotal (calculada, PERSISTED) y los indices filtrados de
       MpConsulta. */
    SET ANSI_NULLS ON;
    SET QUOTED_IDENTIFIER ON;

    DECLARE @baseUrl   nvarchar(300) =
        N'https://api.mercadopublico.cl/servicios/v1/publico/licitaciones.json';
    /* @hoy ES LA FECHA LOCAL, NO LA UTC. Y hay una razon.

       SYSUTCDATETIME() devuelve la hora de Greenwich, y Chile esta en UTC-3.
       Entre las 21:00 y las 24:00 la fecha UTC ya es la de MAÑANA, de modo que
       un @hoy hecho con UTC vale un dia de mas durante tres horas de la noche.

       Con eso pasaban dos cosas, las dos malas:

         - El "un dia que aun no ha llegado no se pregunta" comparaba contra un
           mañana que no habia llegado, asi que ENTRE LAS 21:00 Y LAS 24:00 se
           preguntaba por el dia siguiente. La API responde 500 a un dia que no
           ha ocurrido, y ese 500 se guardaba como dia fallido del futuro. Justo
           cuando se importa de noche, que es cuando se pensaba hacer.

         - Y al deciding que un dia ya consultado se salta, "hoy" tambien era
           manana, asi que el dia de HOY se saltaba y sus publicaciones nunca
           llegaban.

       Las marcas de tiempo de las tablas SI van en UTC, y a proposito: se
       guardan todas en el mismo reloj y comparar entre ellas es directo. Lo que
       tiene que ser local es la FECHA, porque "hoy" es una pregunta de la
       persona que mira la pantalla, no del reloj de Greenwich. La aplicacion
       tampoco lo duda: usa DateTime.Today, que es local. */
    DECLARE @hoy       date = CONVERT(date, GETDATE());
    DECLARE @inicio    datetime2(3) = SYSUTCDATETIME();

    DECLARE @lineas nvarchar(max) = N'';
    DECLARE @errores int = 0;
    DECLARE @diasOk int = 0, @diasFallidos int = 0, @diasSaltados int = 0;
    DECLARE @detallesOk int = 0, @detallesFallidos int = 0;

    IF @desde IS NULL OR @hasta IS NULL
    BEGIN
        SET @resultado = 'Faltan las fechas.';
        RAISERROR('Faltan @desde o @hasta.', 16, 1);
        RETURN;
    END

    IF @hasta < @desde
    BEGIN
        SET @resultado = 'La fecha final es anterior a la inicial.';

        /* A CONVERT antes, porque RAISERROR NO admite date como parametro de
           sustitucion:

               Cannot specify date data type (parameter 4) as a substitution
               parameter

           ...y el mensaje no dice que el problema es el TIPO, sino que parece
           que se han pasado demasiados parametros. La fecha va como texto
           ISO, que ademas es como la lee mejor quien lo lea. */
        /* Y tampoco admite una EXPRESION como argumento de sustitucion, solo una
           variable. Por eso las dos fechas van a variables de texto antes de
           llamarlo; poner el CONVERT dentro de los parentesis de RAISERROR da
           "Incorrect syntax near the keyword 'CONVERT'". */
        DECLARE @txtHasta nvarchar(10) = CONVERT(nvarchar(10), @hasta, 23);
        DECLARE @txtDesde nvarchar(10) = CONVERT(nvarchar(10), @desde, 23);

        RAISERROR('@hasta (%s) es anterior a @desde (%s).', 16, 1, @txtHasta, @txtDesde);
        RETURN;
    END

    IF DATEDIFF(DAY, @desde, @hasta) > 400
    BEGIN
        -- Un rango de 400 dias son unas 250 peticiones. A ritmo de 1,5 s son
        -- mas de seis minutos, y si se equivoca una fecha el gasto ya esta
        -- hecho. Se corta aqui y no mas adelante, porque negarse a continuar
        -- para siempre por un error de tecleo es peor que tener que subir el
        -- limite a mano cuando de verdad haga falta.
        SET @resultado = 'El rango es demasiado grande.';
        RAISERROR('El rango supera los 400 dias. Pide los anos por partes.', 16, 1);
        RETURN;
    END

    IF @codigoProveedor IS NULL OR LTRIM(RTRIM(@codigoProveedor)) = ''
    BEGIN
        SET @resultado = 'Falta el codigo de proveedor.';
        RAISERROR('Falta @codigoProveedor.', 16, 1);
        RETURN;
    END

    IF @ticket IS NULL OR LTRIM(RTRIM(@ticket)) = ''
    BEGIN
        SET @resultado = 'Falta el ticket.';
        RAISERROR('Falta @ticket.', 16, 1);
        RETURN;
    END

    SET @lineas = @lineas + N'Desde ' + CONVERT(nvarchar(10), @desde, 23)
                + N' hasta ' + CONVERT(nvarchar(10), @hasta, 23) + N'.' + NCHAR(10);

    ------------------------------------------------------------------
    -- El ritmo, medido y no improvisado
    ------------------------------------------------------------------
    --
    -- Dos peticiones seguidas se ganan un 429. Medido con curl contra la API:
    -- sin pausa, una de cada tres falla; con 400 ms, casi todas; con 800 ms, la
    -- mitad; con 1500 ms, diez de doce pasan.
    --
    -- Se mide el intervalo entre el PRINCIPIO de una peticion y el principio de
    -- la siguiente, no una espera despues de cada respuesta. Contra la API de
    -- verdad las respuestas tardan mas de 1,4 s, asi que el intervalo ya se
    -- cumple solo y la espera sale a coste cero. Solo se paga cuando algo vuelve
    -- mas rapido de lo debido.
    --
    -- Y por eso NO se serializa con un candado como hace la aplicacion. Alli hay
    -- varios usuarios mirando a la vez; aqui corre un solo lote y por eso no hay
    -- nadie con quien chocarse. Meter un candado añadiria complejidad sin
    -- evitar nada.
    --
    DECLARE @ultimaLlamada datetime2(3) = NULL;

    ------------------------------------------------------------------
    -- El bucle de dias
    ------------------------------------------------------------------
    DECLARE @dia date = @desde;

    WHILE @dia <= @hasta
    BEGIN
        -- Los fines de semana se saltan sin preguntar.
        --
        -- No es una optimizacion: la API responde 500 a un dia que no ha
        -- un dia que no existe. Y contarlos como fallidos seria mentira. La aplicacion
        -- hace lo mismo en SemanasDelMes.DiasHabiles.
        IF DATEPART(WEEKDAY, @dia) IN (1, 7)
        BEGIN
            SET @diasSaltados = @diasSaltados + 1;
            SET @dia = DATEADD(DAY, 1, @dia);
            CONTINUE;
        END

        -- Un dia que aun no ha llegado no se pregunta. Por lo mismo que arriba:
        -- contarlo como fallido seria mentira, y ademas la API responde 500.
        IF @dia > @hoy
        BEGIN
            SET @diasSaltados = @diasSaltados + 1;
            SET @dia = DATEADD(DAY, 1, @dia);
            CONTINUE;
        END

        -- Con @soloFaltantes, un dia YA CONSULTADO con exito no se vuelve a
        -- preguntar. El gasto de cupo es real, asi que la opcion existe; por
        -- defecto no se salta nada, porque un dia ya descargado puede tener
        -- novedades de ese mismo dia.
        --
        -- MIRA EN MpConsulta Y NO EN MpLicitacion. Antes miraba en MpLicitacion,
        -- y eso hacia que un dia consultado que habia salido VACIO no contara
        -- como descargado: se volvia a preguntar en cada ejecucion, para
        -- siempre. Medido en la base local: de 19 dias consultados con exito,
        -- 13 tienen licitaciones y 7 estaban vacios, y los 7 se gastaban otra
        -- vez en cada pasada sin devolver nada nuevo.
        --
        -- No se puede mirar "si hay licitaciones" porque "no hay ninguna" y "no
        -- se ha preguntado" son cosas distintas, y confundirlas es justo el
        -- fallo que el panel de resultado vacio de la pantalla ya distingue. Un
        -- dia consultado y vacio es un dia que ya se sabe que esta vacio, y es
        -- JUSTO el que mas caro sale de volver a preguntar.
        --
        -- Y HOY NO SE SALTA, porque un dia en curso todavia puede recibir
        -- publicaciones. Las de ayer ya no cambian; las de hoy si. Por eso la
        -- comparacion es estricta, @dia < @hoy.
        --
        -- Un dia que salio FALLIDO tampoco se salta, que es lo que quiere decir
        -- "faltantes": se reintenta en esta pasada y, si vuelve a fallar, en la
        -- siguiente.
        IF @soloFaltantes = 1
           AND @dia < @hoy
           AND EXISTS (SELECT 1 FROM dbo.MpConsulta
                       WHERE CodigoProveedor = @codigoProveedor
                         AND FechaDia = @dia
                         AND Exito = 1)
        BEGIN
            SET @diasSaltados = @diasSaltados + 1;
            SET @dia = DATEADD(DAY, 1, @dia);
            CONTINUE;
        END

        DECLARE @urlDia nvarchar(1000);
        DECLARE @cuerpo nvarchar(max), @error nvarchar(2000);
        DECLARE @codigo int, @salioAlgo bit = 0;

        -- LA FECHA VA EN DDMMAAAA CON LOS DOS CAMPOS RELLENOS. Este es el bug
        -- que ya se cometio en la aplicacion y que costo dias de diagnostico:
        -- el 4 de octubre se mandaba "4102026", siete digitos, y la API
        -- respondia {"Codigo":10300,"Mensaje":"El formato del parametro fechas
        -- es incorrecto"}.
        --
        -- Lo que lo escondia es que solo se rompia en los dias del 1 al 9: a
        -- partir del 10 el dia ya tiene dos cifras y el fallo desaparece solo.
        -- Por eso parecia intermitente.
        --
        -- FORMAT con 'ddMMyyyy' es correcto, pero FORMAT es LENTO y depende de
        -- la configuracion regional de la sesion. Se arma a mano con RIGHT y
        -- CONVERT, que no dependen de ninguna de las dos cosas.
        DECLARE @fechaApi nvarchar(8) =
            RIGHT('0' + CONVERT(nvarchar(2), DAY(@dia)), 2)
          + RIGHT('0' + CONVERT(nvarchar(2), MONTH(@dia)), 2)
          + CONVERT(nvarchar(4), YEAR(@dia));

        SET @urlDia = @baseUrl
                   + N'?fecha=' + @fechaApi
                   + N'&CodigoProveedor=' + @codigoProveedor
                   + N'&ticket=' + @ticket;

        SET @cuerpo = NULL;
        SET @error = NULL;

        -- --------------------------------------------------------------
        -- Los reintentos, con la escalera de esperas de la aplicacion
        -- --------------------------------------------------------------
        DECLARE @intento int = 1;
        DECLARE @espera int = @minutosEsperaInicial;

        WHILE @intento <= @maxIntentosDia
        BEGIN
            -- El ritmo va ANTES de cada intento, incluido el primero: entre el
            -- fin de un dia y el principio del siguiente tambien hay que esperar,
            -- o el 429 llega igual.
            IF @ultimaLlamada IS NOT NULL
            BEGIN
                DECLARE @transcurrido int = DATEDIFF(SECOND, @ultimaLlamada, SYSUTCDATETIME());
                DECLARE @falta int = @segundosEntreLlamadas - @transcurrido;
                IF @falta > 0
                BEGIN
                    -- WAITFOR DELAY NO admite una expresion: solo una constante
                    -- o una variable. Por eso @falta (un entero) se pasa tal
                    -- cual. Ponerlo como "WAITFOR DELAY DATEADD(SECOND, @falta, 0)"
                    -- da "Incorrect syntax near 'DATEADD'", y el mensaje no
                    -- dice que el problema es el DATEADD sino que WAITFOR no
                    -- admite expresiones.
                    WAITFOR DELAY @falta;
                END
            END

            SET @ultimaLlamada = SYSUTCDATETIME();

            EXEC dbo.MpPeticionGet
                @url             = @urlDia,
                @segundosTimeout = @segundosTimeout,
                @codigoHttp      = @codigo OUTPUT,
                @cuerpo          = @cuerpo OUTPUT,
                @error           = @error OUTPUT;

            -- Una fila por INTENTO, no una por dia. Con seis intentos, seis
            -- filas. Es lo que hace que "este dia fallo 3 veces" y "este dia
            -- costo 18 peticiones" sean la misma informacion.
            INSERT dbo.MpConsulta
                (FechaHora, TipoConsulta, Origen, ModoConsulta, CodigoProveedor,
                 FechaDia, NumeroIntento, Exito, CodigoHttp, CodigoApi,
                 Mensaje, DuracionMs)
            VALUES
                (SYSUTCDATETIME(), 0, 0, @modoRegistro, @codigoProveedor,
                 @dia, @intento,
                  CASE WHEN @codigo = 200 THEN 1 ELSE 0 END,
                 @codigo,
                 dbo.MpCodigoApi(@cuerpo),
                 LEFT(ISNULL(NULLIF(@error, N''), ISNULL(dbo.MpMensajeApi(@cuerpo), N'')), 1000),
                 NULL);

            /* SOLO UN 200 EXACTO ES UNA RESPUESTA VALIDA. No "entre 200 y 299".

               Es la correccion mas importante del procedimiento, y la encontro la
               prueba, no el ojo.

               Mercado Publico contesta HTTP 203 ("Non-Authoritative
               Information") a un ticket que no vale, en lugar de un 401. Es un
               2xx, asi que un BETWEEN 200 AND 299 lo daba por bueno: el
               procedimiento informaba de

                   Dias preguntados con exito : 3

               ...con un ticket inventado y sin haberMirado ni una licitacion. El
               cuerpo de esa respuesta es {"Codigo":203,"Mensaje":"Ticket no
               válido."}, que es un error disfrazado de exito. Un 203 es
               cualquier cosa menos un listado, de ahi que se compruebe el 200
               exacto.

               Lo mismo con el cuerpo: se exige que traiga algo. Un 200 con cuerpo
               vacio no es una respuesta de la API. */
            IF @codigo = 200 AND @cuerpo IS NOT NULL AND LEN(@cuerpo) > 0
            BEGIN
                SET @salioAlgo = 1;
                BREAK;
            END

            /* EL 203 ES UN RECHAZO DE TICKET, Y NO SE REINTENTA.

               Es lo mismo que un 401 pero con otro numero: Mercado Publico
               responde 203 {"Codigo":203,"Mensaje":"Ticket no válido."} en
               lugar de un 401 cuando el ticket no existe. Con la correccion de
               arriba ya no se confunde con un exito, pero si se dejara aqui
               pasaria por el camino de los reintentos: seis intentos con
               esperas de 2, 4, 8, 16, 30 y 30 segundos por cada dia.

               En la prueba de 3 dias eso fueron 185 segundos, y 18 filas en
               MpConsulta, para obtener 18 veces el mismo "no". Un ticket
               invalido no mejora con insistir: lo unico que se consigue es
               tardar tres minutos en enterarse de algo que ya se sabia en el
               primer intento.

               Por eso el 203 se trata EXACTAMENTE como un 401, aqui abajo. */
            IF @codigo IN (203, 401, 403)
            BEGIN
                SET @lineas = @lineas + N'  ' + CONVERT(nvarchar(10), @dia, 23)
                            + N': ticket rechazado (HTTP ' + CONVERT(nvarchar(10), @codigo)
                            + N'). No se reintenta: no mejoraria con insistir.' + NCHAR(10);
                BREAK;
            END

            IF @intento < @maxIntentosDia
            BEGIN
                WAITFOR DELAY @espera;

                -- Crece al doble, con tope. El tope evita que una espera se
                -- alargue hasta perder el sentido.
                SET @espera = CASE WHEN @espera * 2 > @segundosEsperaMaxima
                                   THEN @segundosEsperaMaxima
                                   ELSE @espera * 2 END;
            END

            SET @intento = @intento + 1;
        END

        IF @salioAlgo = 0
        BEGIN
            -- Un dia que falla NO tira el resto del rango. Se anota y se sigue,
            -- que es la misma regla que sigue la aplicacion con los dias de una
            -- semana: perder un dia no puede costar los otros cuatro.
            SET @diasFallidos = @diasFallidos + 1;
            SET @errores = @errores + 1;

            SET @lineas = @lineas + N'  ' + CONVERT(nvarchar(10), @dia, 23)
                        + N': no se pudo consultar.' + NCHAR(10);

            SET @dia = DATEADD(DAY, 1, @dia);
            CONTINUE;
        END

        SET @diasOk = @diasOk + 1;

        --------------------------------------------------------------
        -- El listado del dia, guardado en MpLicitacion
        --------------------------------------------------------------
        DECLARE @cantidad int = dbo.MpCantidadApi(@cuerpo);

        /* NO SE INSERTA UNA FILA DE EXITO AQUI.

           Este INSERT estaba antes, "para dejar constancia del dia bueno". Es
           una fila de mas, y hacia dos cosas malas:

             - El dia ya queda registrado por el INSERT de cada intento, dentro
               del bucle. Con esta fila, cada dia salia DOS veces en MpConsulta.

             - Y la segunda salia con CantidadDevuelta y sin Mensaje, que es la
               firma de un registro a medias. En la prueba se veia:

                   2026-10-05  1  1  203  203  Ticket no valido.
                   2026-10-05  1  1  203  NULL NULL

               Dos filas del mismo dia, la segunda sin mensaje. Con eso, contar
               consultas por dia daria el doble de las que hubo de verdad.

           Para dejar constancia del exito con su cantidad, lo que se hace es
           ACTUALIZAR la fila del ultimo intento, no insertar otra. */
        UPDATE dbo.MpConsulta
        SET CantidadDevuelta = @cantidad
        WHERE ConsultaId = (SELECT MAX(ConsultaId) FROM dbo.MpConsulta
                            WHERE FechaDia = @dia AND Exito = 1
                              AND TipoConsulta = 0
                              AND CodigoProveedor = @codigoProveedor);

        -- MERGE y no un DELETE+INSERT: se pierde el UltimaVezVista y el numero
        -- de veces vista, que es justamente lo que hace util la tabla.
        MERGE dbo.MpLicitacion AS destino
        USING
        (
            SELECT
                JSON_VALUE(j.[value], '$.CodigoExterno')        AS CodigoExterno,
                JSON_VALUE(j.[value], '$.Nombre')               AS Nombre,
                TRY_CONVERT(int, JSON_VALUE(j.[value], '$.CodigoEstado')) AS CodigoEstado,
                TRY_CONVERT(datetimeoffset(3), JSON_VALUE(j.[value], '$.FechaCierre')) AS FechaCierre
            FROM OPENJSON(@cuerpo, '$.Listado') AS j
            WHERE JSON_VALUE(j.[value], '$.CodigoExterno') IS NOT NULL
        ) AS origen
           ON destino.CodigoProveedor = @codigoProveedor
          AND destino.CodigoExterno   = origen.CodigoExterno
        WHEN MATCHED THEN
            UPDATE SET
                Nombre            = origen.Nombre,
                CodigoEstado      = origen.CodigoEstado,
                FechaCierre       = origen.FechaCierre,
                UltimaVezVista    = SYSUTCDATETIME(),
                NumeroVecesVista  = destino.NumeroVecesVista + 1,
                ConsultaUltimaId  = NULL
        WHEN NOT MATCHED THEN
            INSERT (CodigoProveedor, CodigoExterno, Nombre, CodigoEstado,
                    FechaCierre, FechaPublicacion, ModoConsulta)
            VALUES (@codigoProveedor, origen.CodigoExterno, origen.Nombre,
                    origen.CodigoEstado, origen.FechaCierre, @dia, @modoRegistro);

        --------------------------------------------------------------
        -- El detalle de cada una, si se pidio
        --------------------------------------------------------------
        IF @conDetalle = 1
        BEGIN
            DECLARE @codigosLicitacion TABLE
            (
                Secuencia     int IDENTITY(1,1) PRIMARY KEY,
                LicitacionId  bigint NOT NULL,
                CodigoExterno nvarchar(64) NOT NULL
            );

            INSERT @codigosLicitacion (LicitacionId, CodigoExterno)
            SELECT l.LicitacionId, l.CodigoExterno
            FROM dbo.MpLicitacion AS l
            WHERE l.CodigoProveedor = @codigoProveedor
              AND l.FechaPublicacion = @dia
            ORDER BY l.CodigoExterno;

            /* SIN CURSOR, Y NO POR ESTILO.

               Un DECLARE CURSOR dentro de un CREATE PROCEDURE no compila:

                   Incorrect syntax near 'LOCAL'

               ...que no dice nada de que el problema sea el cursor, sino que
               parece un problema de sintaxis en la palabra LOCAL. Con
               "SET @cursorLic CURSOR ..." el error es "Must declare the scalar
               variable @cursorLic", que apunta a una variable que no existe y
               no a la causa.

               Se recorre con un WHILE sobre el numero de fila, que es lo que
               haria el cursor igualmente pero sin dejar un cursor abierto si
               algo lanza por el camino. Un cursor abierto en un procedimiento
               que falla se queda abierto hasta que se cierre la sesion, y desde
               ahi bloquea filas. */
            DECLARE @n int = 0;
            DECLARE @totalDetalle int = (SELECT COUNT(*) FROM @codigosLicitacion);

            WHILE @n < @totalDetalle
            BEGIN
                SET @n = @n + 1;

                DECLARE @licitacionId bigint, @codigoLic nvarchar(64);

                SELECT @licitacionId = LicitacionId, @codigoLic = CodigoExterno
                FROM @codigosLicitacion
                WHERE Secuencia = @n;

                IF @codigoLic IS NULL
                    BREAK;
                DECLARE @urlDet nvarchar(1000);
                DECLARE @cuerpoDet nvarchar(max), @errorDet nvarchar(2000);
                DECLARE @codigoDet int, @salioDetalle bit = 0;

                SET @urlDet = @baseUrl
                            + N'?codigo=' + @codigoLic
                            + N'&ticket=' + @ticket;

                SET @intento = 1;
                SET @espera = @minutosEsperaInicial;
                SET @cuerpoDet = NULL;

                WHILE @intento <= @maxIntentosDetalle
                BEGIN
                    IF @ultimaLlamada IS NOT NULL
                    BEGIN
                        DECLARE @t2 int = DATEDIFF(SECOND, @ultimaLlamada, SYSUTCDATETIME());
                        DECLARE @f2 int = @segundosEntreLlamadas - @t2;
                        IF @f2 > 0
                            WAITFOR DELAY @f2;
                    END

                    SET @ultimaLlamada = SYSUTCDATETIME();

                    EXEC dbo.MpPeticionGet
                        @url             = @urlDet,
                        @segundosTimeout = @segundosTimeout,
                        @codigoHttp      = @codigoDet OUTPUT,
                        @cuerpo          = @cuerpoDet OUTPUT,
                        @error           = @errorDet OUTPUT;

                    INSERT dbo.MpConsulta
                        (FechaHora, TipoConsulta, Origen, ModoConsulta, CodigoProveedor,
                         CodigoLicitacion, NumeroIntento, Exito, CodigoHttp,
                         CodigoApi, Mensaje)
                    VALUES
                        (SYSUTCDATETIME(), 1, 0, @modoRegistro, @codigoProveedor,
                         @codigoLic, @intento,
                          CASE WHEN @codigoDet = 200 THEN 1 ELSE 0 END,
                         @codigoDet,
                         dbo.MpCodigoApi(@cuerpoDet),
                         LEFT(ISNULL(NULLIF(@errorDet, N''), ISNULL(dbo.MpMensajeApi(@cuerpoDet), N'')), 1000));

                    IF @codigoDet = 200 AND @cuerpoDet IS NOT NULL AND LEN(@cuerpoDet) > 0
                    BEGIN
                        SET @salioDetalle = 1;
                        BREAK;
                    END

                    /* El 203 tambien aqui. El detalle de una licitacion va con el mismo
                       ticket, asi que si el ticket no vale, el detalle tampoco.
                       Y por el mismo motivo que en el listado: insistir no
                       arregla un ticket invalido, solo tarda mas. */
                    IF @codigoDet IN (203, 401, 403)
                        BREAK;

                    IF @intento < @maxIntentosDetalle
                    BEGIN
                        WAITFOR DELAY @espera;
                        SET @espera = CASE WHEN @espera * 2 > @segundosEsperaMaxima
                                           THEN @segundosEsperaMaxima
                                           ELSE @espera * 2 END;
                    END

                    SET @intento = @intento + 1;
                END

                -- Un detalle que falla NO tira el dia. La tarjeta sale sin
                -- organismo, igual que hace la aplicacion, y perder un dato
                -- acessorio no puede costar el listado entero.
                IF @salioDetalle = 1
                BEGIN
                    SET @detallesOk = @detallesOk + 1;

                    IF EXISTS (SELECT 1 FROM OPENJSON(@cuerpoDet, '$.Listado'))
                    BEGIN
                        -- ----------------------------------------------------------
                        -- La ficha, en MpLicitacionDetalle
                        -- ----------------------------------------------------------
                        --
                        -- "Comprador" y "Fechas" son objetos ANIDADOS. OPENJSON lo
                        -- resuelve con dos pasos: uno para sacar el texto con
                        -- JSON_VALUE y otro para los objetos, porque JSON_VALUE
                        -- sobre un objeto devuelve NULL.
                        --
                        -- Y "Adjudicacion" NO lleva ningun monto: es el ACTA
                        -- (fecha, numero, oferentes, enlace). El monto esta dentro
                        -- de cada item. Confundir los dos es un error que da 0
                        -- adjudicado sin avisar.
                        MERGE dbo.MpLicitacionDetalle AS destino
                        USING
                        (
                            SELECT
                                @licitacionId AS LicitacionId,
                                JSON_VALUE(j.[value], '$.Nombre')     AS Nombre,
                                JSON_VALUE(j.[value], '$.Estado')    AS Estado,
                                TRY_CONVERT(int, JSON_VALUE(j.[value], '$.CodigoEstado')) AS CodigoEstado,
                                JSON_VALUE(j.[value], '$.Descripcion') AS Descripcion,
                                JSON_VALUE(j.[value], '$.Tipo')      AS Tipo,

                                JSON_VALUE(j.[value], '$.Comprador.NombreOrganismo') AS NombreOrganismo,
                                JSON_VALUE(j.[value], '$.Comprador.RutUnidad')       AS RutOrganismo,
                                TRY_CONVERT(int, JSON_VALUE(j.[value], '$.Comprador.CodigoOrganismo')) AS CodigoOrganismo,
                                JSON_VALUE(j.[value], '$.Comprador.RegionUnidad')   AS RegionOrganismo,
                                JSON_VALUE(j.[value], '$.Comprador.ComunaUnidad')   AS ComunaOrganismo,

                                -- decimal(19,4), no float: ver el comentario del
                                -- esquema. JSON_VALUE devuelve texto y TRY_CONVERT
                                -- con punto decimal SIEMPRE, porque el punto es
                                -- el separador del JSON con independencia de la
                                -- configuracion regional de la sesion.
                                TRY_CONVERT(decimal(19,4), JSON_VALUE(j.[value], '$.MontoEstimado')) AS MontoEstimado,
                                JSON_VALUE(j.[value], '$.Moneda')    AS Moneda,
                                TRY_CONVERT(int, JSON_VALUE(j.[value], '$.Estimacion')) AS Estimacion,

                                TRY_CONVERT(datetimeoffset(3), JSON_VALUE(j.[value], '$.Fechas.FechaCreacion'))          AS FechaCreacion,
                                TRY_CONVERT(datetimeoffset(3), JSON_VALUE(j.[value], '$.Fechas.FechaCierre'))            AS FechaCierre,
                                TRY_CONVERT(datetimeoffset(3), JSON_VALUE(j.[value], '$.Fechas.FechaPublicacion'))       AS FechaPublicacion,
                                TRY_CONVERT(datetimeoffset(3), JSON_VALUE(j.[value], '$.Fechas.FechaActoAperturaTecnica'))   AS FechaAperturaTecnica,
                                TRY_CONVERT(datetimeoffset(3), JSON_VALUE(j.[value], '$.Fechas.FechaActoAperturaEconomica')) AS FechaAperturaEconomica,
                                TRY_CONVERT(datetimeoffset(3), JSON_VALUE(j.[value], '$.Adjudicacion.Fecha'))           AS FechaAdjudicacion,
                                TRY_CONVERT(datetimeoffset(3), JSON_VALUE(j.[value], '$.Fechas.FechaFinal'))           AS FechaFinal,

                                TRY_CONVERT(int, JSON_VALUE(j.[value], '$.Adjudicacion.NumeroOferentes')) AS NumeroOferentes,
                                JSON_VALUE(j.[value], '$.Adjudicacion.Numero')  AS NumeroAdjudicacion,
                                JSON_VALUE(j.[value], '$.Adjudicacion.UrlActa') AS UrlActa,

                                TRY_CONVERT(int, JSON_VALUE(j.[value], '$.Items.Cantidad')) AS NumeroItems,
                                TRY_CONVERT(int, JSON_VALUE(j.[value], '$.DiasCierreLicitacion')) AS DiasCierreLicitacion
                            FROM OPENJSON(@cuerpoDet, '$.Listado') AS j
                        ) AS origen
                           ON destino.LicitacionId = origen.LicitacionId
                        WHEN MATCHED THEN
                            UPDATE SET
                                Nombre = origen.Nombre, Estado = origen.Estado,
                                CodigoEstado = origen.CodigoEstado,
                                Descripcion = origen.Descripcion, Tipo = origen.Tipo,
                                NombreOrganismo = origen.NombreOrganismo,
                                RutOrganismo = origen.RutOrganismo,
                                CodigoOrganismo = origen.CodigoOrganismo,
                                RegionOrganismo = origen.RegionOrganismo,
                                ComunaOrganismo = origen.ComunaOrganismo,
                                MontoEstimado = origen.MontoEstimado,
                                Moneda = origen.Moneda, Estimacion = origen.Estimacion,
                                FechaCreacion = origen.FechaCreacion,
                                FechaCierre = origen.FechaCierre,
                                FechaPublicacion = origen.FechaPublicacion,
                                FechaAperturaTecnica = origen.FechaAperturaTecnica,
                                FechaAperturaEconomica = origen.FechaAperturaEconomica,
                                FechaAdjudicacion = origen.FechaAdjudicacion,
                                FechaFinal = origen.FechaFinal,
                                NumeroOferentes = origen.NumeroOferentes,
                                NumeroAdjudicacion = origen.NumeroAdjudicacion,
                                UrlActa = origen.UrlActa,
                                NumeroItems = origen.NumeroItems,
                                DiasCierreLicitacion = origen.DiasCierreLicitacion,
                                UltimaLectura = SYSUTCDATETIME(),
                                NumeroLecturas = destino.NumeroLecturas + 1
                        WHEN NOT MATCHED THEN
                            INSERT (LicitacionId, Nombre, Estado, CodigoEstado,
                                    Descripcion, Tipo, NombreOrganismo, RutOrganismo,
                                    CodigoOrganismo, RegionOrganismo, ComunaOrganismo,
                                    MontoEstimado, Moneda, Estimacion, FechaCreacion,
                                    FechaCierre, FechaPublicacion, FechaAperturaTecnica,
                                    FechaAperturaEconomica, FechaAdjudicacion, FechaFinal,
                                    NumeroOferentes, NumeroAdjudicacion, UrlActa,
                                    NumeroItems, DiasCierreLicitacion)
                            VALUES
                                (origen.LicitacionId, origen.Nombre, origen.Estado,
                                 origen.CodigoEstado, origen.Descripcion, origen.Tipo,
                                 origen.NombreOrganismo, origen.RutOrganismo,
                                 origen.CodigoOrganismo, origen.RegionOrganismo,
                                 origen.ComunaOrganismo, origen.MontoEstimado,
                                 origen.Moneda, origen.Estimacion, origen.FechaCreacion,
                                 origen.FechaCierre, origen.FechaPublicacion,
                                 origen.FechaAperturaTecnica, origen.FechaAperturaEconomica,
                                 origen.FechaAdjudicacion, origen.FechaFinal,
                                 origen.NumeroOferentes, origen.NumeroAdjudicacion,
                                 origen.UrlActa, origen.NumeroItems,
                                 origen.DiasCierreLicitacion);

                        ----------------------------------------------------------
                        -- Los items adjudicados
                        ----------------------------------------------------------
                        --
                        -- ESTAN ANIDADOS en Items.Listado[].Adjudicacion, no en el
                        -- Adjudicacion de primer nivel.
                        --
                        -- Y se reemplaza la lista entera en vez de hacer MERGE
                        -- item a item: la API manda SIEMPRE la lista completa, y
                        -- un item que ya no aparece es que la API lo quito. Con
                        -- MERGE parcial se quedarian items que ya no existen y el
                        -- total saldria mas alto que la realidad. Es un borrado
                        -- seguido de insercion DENTRO de la misma transaccion, no
                        -- un DELETE a pelo: si algo falla antes del COMMIT, se
                        -- deshace todo junto.
                        DELETE FROM dbo.MpLicitacionItem WHERE LicitacionId = @licitacionId;

                        INSERT dbo.MpLicitacionItem
                            (LicitacionId, Correlativo, NombreProducto, UnidadMedida,
                             Cantidad, CantidadAdjudicada, MontoUnitario,
                             RutProveedor, NombreProveedor)
                        SELECT
                            @licitacionId,
                            COALESCE(TRY_CONVERT(int, JSON_VALUE(it.[value], '$.Correlativo')),
                                     ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
                            JSON_VALUE(it.[value], '$.NombreProducto'),
                            JSON_VALUE(it.[value], '$.UnidadMedida'),
                            TRY_CONVERT(decimal(19,4), JSON_VALUE(it.[value], '$.Cantidad')),
                            TRY_CONVERT(decimal(19,4), JSON_VALUE(it.[value], '$.Adjudicacion.Cantidad')),
                            TRY_CONVERT(decimal(19,4), JSON_VALUE(it.[value], '$.Adjudicacion.MontoUnitario')),
                            JSON_VALUE(it.[value], '$.Adjudicacion.RutProveedor'),
                            JSON_VALUE(it.[value], '$.Adjudicacion.NombreProveedor')
                        FROM OPENJSON(@cuerpoDet, '$.Listado') AS j
                        CROSS APPLY OPENJSON(j.[value], '$.Items.Listado') AS it
                        WHERE TRY_CONVERT(decimal(19,4),
                                          JSON_VALUE(it.[value], '$.Adjudicacion.MontoUnitario')) IS NOT NULL;
                    END
                END
                ELSE
                BEGIN
                    -- Se anota el fallo del detalle y se sigue con la siguiente.
                    -- El listado ya esta guardado y no se tira: perder lo que SI
                    -- se pudo traer por un dato acessorio seria peor.
                    SET @detallesFallidos = @detallesFallidos + 1;
                END
            END
        END

        -- El historico de estados se escribe DESPUES de todo, y solo cuando el
        -- estado ha cambiado de verdad.
        --
        -- Se compara contra el estado ANTERIOR, que ya esta en la tabla. Por eso
        -- va al final del dia y no dentro del MERGE: si se comparase con el
        -- estado recien escrito, nunca habria cambio y el historico estaria
        -- vacio siempre.
        INSERT dbo.MpLicitacionEstadoHistorico
            (LicitacionId, CodigoExterno, CodigoEstado, Estado)
        SELECT
            h.LicitacionId, h.CodigoExterno, h.CodigoEstado, h.Estado
        FROM
        (
            SELECT
                l.LicitacionId,
                l.CodigoExterno,
                l.CodigoEstado,
                d.Estado,
                LAG(l.CodigoEstado) OVER (PARTITION BY l.LicitacionId ORDER BY l.CodigoExterno) AS Anterior
            FROM dbo.MpLicitacion AS l
            LEFT JOIN dbo.MpLicitacionDetalle AS d ON d.LicitacionId = l.LicitacionId
            WHERE l.CodigoProveedor = @codigoProveedor
              AND l.FechaPublicacion = @dia
        ) AS h
        WHERE h.Anterior IS NOT NULL
          AND h.Anterior <> h.CodigoEstado;

        SET @dia = DATEADD(DAY, 1, @dia);
    END

    ------------------------------------------------------------------
    -- El resumen
    ------------------------------------------------------------------
    SET @resultado =
        N'Dias preguntados con exito : ' + CONVERT(nvarchar(10), @diasOk) + NCHAR(10)
      + N'Dias que fallaron         : ' + CONVERT(nvarchar(10), @diasFallidos) + NCHAR(10)
      + N'Dias saltados (fin de semana, futuros o ya descargados): '
      + CONVERT(nvarchar(10), @diasSaltados) + NCHAR(10)
      + N'Detalles guardados       : ' + CONVERT(nvarchar(10), @detallesOk) + NCHAR(10)
      + N'Detalles que fallaron    : ' + CONVERT(nvarchar(10), @detallesFallidos) + NCHAR(10)
      + N'Duracion total           : '
      + CONVERT(nvarchar(10), DATEDIFF(SECOND, @inicio, SYSUTCDATETIME())) + N' s' + NCHAR(10)
      + N'' + NCHAR(10);

    IF @errores > 0
        SET @resultado = @resultado
            + N'ATENCION: ' + CONVERT(nvarchar(10), @errores)
            + N' dia(s) no se pudieron consultar. Los dias afectados estan en'
            + N' MpConsulta con Exito = 0. Un dia sin respuesta NO significa que'
            + N' no hubiera licitaciones: significa que no se pudo comprobar.' + NCHAR(10);

    IF @detallesFallidos > 0
        SET @resultado = @resultado
            + N'Advertencia: ' + CONVERT(nvarchar(10), @detallesFallidos)
            + N' ficha(s) se quedaron sin detalle. El listado de esos dias esta'
            + N' completo; lo que falta es el organismo y los montos.' + NCHAR(10);
END
GO
