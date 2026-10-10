/* ===========================================================================
   Procedimientos de LECTURA. El otro camino hacia los mismos datos.

   Estos procedimientos son la mitad "sql" del interruptor de FuenteDatos. La
   otra mitad, que no cambia, es MercadoPublicoCliente hablando con la API.

   Que existan los dos y devuelvan LO MISMO es lo que permite cambiar de fuente
   sin tocar la interfaz. Si un procedimiento devolviera una forma distinta,
   habría que tocar la pantalla y se perdería la garantía de que el interruptor
   es un cambio de una línea.

   ---------------------------------------------------------------------------
   POR QUÉ PROCEDIMIENTOS Y NO EF
   ---------------------------------------------------------------------------

   El acceso es solo por procedimiento almacenado. No hay mapeo objeto-relacional,
   ni migraciones, ni un modelo que se pueda desincronizar del esquema. El
   contrato entre el servidor y la base son estos procedimientos y nada más,
   y se puede leer entero en esta pantalla.

   Eso tiene un precio que conviene decir: si mañana cambia el esquema, hay que
   tocar estos procedimientos a mano. Con EF, el cambio se propagaría solo. Aquí
   se prefiere que el cambio sea visible y no que sea automático.

   ===========================================================================

   ---------------------------------------------------------------------------
   SON TRES, Y POR QUÉ NO UNO
   ---------------------------------------------------------------------------

   mp.LeeMes          Lo que usa la aplicación. Devuelve CINCO conjuntos.
   mp.CuentaDiasDelMes    Los ocho números del mes. UN conjunto.
   mp.DiasSinComprobarDelMes   Los días que no se pudieron mirar, con su motivo.
                              UN conjunto.

   Los dos últimos existen porque la cuenta de días no se puede probar dentro de
   mp.LeeMes. En T-SQL, "INSERT @tabla EXEC unProcedimiento" mete TODOS los
   conjuntos que devuelva en la MISMA tabla, así que una prueba no puede coger
   solo la cabecera de un procedimiento de cinco conjuntos. Y eso no es un
   capricho del lenguaje: es el mecanismo con el que se comprueba que la cuenta
   cuadra, que es justo lo que impide que la pantalla diga "no había nada" cuando
   lo que pasó fue que no se preguntó.

   Partiendo la cuenta en dos procedimientos de un solo conjunto, cada uno se
   puede meter en su tabla y comprobar. Y de paso mp.LeeMes se queda corto y se
   lee: llama a los dos y pega lo que devuelven.

   =========================================================================== */


SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* La conexión vive aquí y no en el código. Un servidor con la contraseña
   escrita dentro se puede leer de un vistazo, y un repositorio con una
   cadena de conexión encima es un repositorio filtrado. */
IF SCHEMA_ID('mp') IS NULL
    EXEC(N'CREATE SCHEMA mp;');
GO


/* -----------------------------------------------------------------------
   EL CONTRATO, Y LA PARTE QUE NO SE PUEDE OMITIR

   Un procedimiento que devolviera solo las licitaciones del mes obligaría a la
   pantalla a decir "no hay nada" siempre que el mes estuviera vacío. Y eso es
   una MENTIRA en el caso que de verdad importa.

   Ocurrió de verdad: el 8 de octubre de 2026 se importaron los días 5, 6 y 7 de
   octubre y los tres fueron rechazados con "Ticket no válido" (HTTP 203,
   CodigoApi 203). Con solo las filas, la pantalla habría puesto "Nada en el mes
   de Octubre" cuando en realidad no se comprobó ni un solo día.

   Por eso TODOS los procedimientos devuelven también qué días se pudieron
   comprobar y cuáles no. Es el mismo contrato que sostiene la lectura directa de
   la API, donde DiasSinRespuesta viaja al cliente, y no es un añadido: sin él,
   el modo base de datos no puede ser honesto.
   ------------------------------------------------------------------------- */


/* -----------------------------------------------------------------------
   mp.CuentaDiasDelMes

   Los ocho números que la pantalla necesita para no mentir, en UN conjunto y
   UNA fila.

   Uso:

       EXEC mp.CuentaDiasDelMes @codigoProveedor = N'71284', @anio = 2026, @mes = 10;

   El parámetro @hoy está para poder preguntar por meses ya terminados. Lo usa
   la prueba, y por eso no se fija dentro: un procedimiento que lee el reloj no
   se puede probar, porque el resultado cambiaría según el día en que se
   ejecutara la prueba.
   -------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE mp.CuentaDiasDelMes
    @codigoProveedor  nvarchar(50),
    @anio             int,
    @mes              int,
    @hoy              date = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* Si no se dice qué día es hoy, se toma el del servidor. Y un mes del año
       que no existe no se devuelve callado: se dice, porque un 2026/13 que
       devuelve vacío es indistinguible de un mes sin nada publicado.

       "Hoy" es LA FECHA LOCAL, y no es un detalle. Chile está en UTC-3, así que
       entre las 21:00 y las 24:00 la fecha UTC ya es la de mañana. Con "@hoy =
       mañana", esta pantalla contaría como un día de hoy uno que todavía no ha
       ocurrido, y lo haría por la noche, que es justo cuando se mira. El mismo
       error estaba en 03-procedimiento-importar.sql, en el otro lado.

       Las marcas de tiempo de las tablas sí van en UTC, y a propósito: se
       guardan todas en el mismo reloj y comparar entre ellas es directo. Lo que
       tiene que ser local es la FECHA, porque "hoy" es una pregunta de quien
       mira la pantalla, no del reloj de Greenwich. La aplicación tampoco lo
       duda: usa DateTime.Today, que es local. */
    IF @hoy IS NULL
        SET @hoy = CONVERT(date, GETDATE());

    IF @anio IS NULL OR @anio < 1900 OR @anio > 9999
    BEGIN
        RAISERROR('El anio %s no es valido.', 16, 1, @anio);
        RETURN;
    END

    IF @mes IS NULL OR @mes < 1 OR @mes > 12
    BEGIN
        RAISERROR('El mes %s no es valido.', 16, 1, @mes);
        RETURN;
    END

    IF @codigoProveedor IS NULL OR LTRIM(RTRIM(@codigoProveedor)) = ''
    BEGIN
        RAISERROR('Falta el codigo de proveedor.', 16, 1);
        RETURN;
    END

    DECLARE @primeroDelMes date = DATEFROMPARTS(@anio, @mes, 1);

    /* El "- 1" va DENTRO del DATEADD y no fuera. Fuera da:

           Operand type clash: date is incompatible with int

       porque SQL Server resuelve el DATEADD como date en vez de datetime, y
       restarle un entero a un date no es una operación válida. El mensaje no
       menciona el DATEADD ni la aritmética: parece un problema de tipos en
       general, y es fácil que alguien busque el DATEADD en otra parte.

       Es la forma canónica de obtener el último día del mes, y no es obvia. */
    DECLARE @finDelMes date = DATEADD(DAY, -1, DATEADD(MONTH, 1, @primeroDelMes));

    /* Los días del mes, materializados UNA VEZ.

       La primera versión generaba los días con un ROW_NUMBER() sobre
       sys.all_objects, que es un truco que aparece en blogs y que aquí no
       vale: depende de cuántas filas tenga esa vista del sistema, y en una
       instancia recién instalada puede no alcanzar para un mes entero. El
       resultado sería que faltan días y el recuento de días hábiles sale
       bajo, sin ningún error. Un fallo que solo depende del servidor donde
       corra.

       Con VALUES(0..31) la lista es la misma en todas partes y se lee sin
       effort. El 31 es porque el mes más largo tiene 31 días.

       Va en una tabla variable y no en un CTE porque hace falta tres veces en
       este procedimiento y dos veces en el otro, y un CTE solo se puede leer
       una vez. */
    DECLARE @diasDelMes TABLE (Dia date PRIMARY KEY);

    INSERT @diasDelMes (Dia)
    SELECT CONVERT(date, DATEADD(DAY, n, @primeroDelMes))
    FROM (VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9), (10),
                 (11), (12), (13), (14), (15), (16), (17), (18), (19), (20),
                 (21), (22), (23), (24), (25), (26), (27), (28), (29), (30),
                 (31)) AS v(n)
    WHERE n <= DATEDIFF(DAY, @primeroDelMes, @finDelMes);

    /* Días de lunes a viernes (WEEKDAY 2 a 6: lunes=2, viernes=6).

       Y dos cuentas, no una: el mes entero, y el parte que ya ha ocurrido. Un
       mes en curso todavía tiene días que no han llegado, y contarlos sería
       avisar de días que no existen. El 8 de octubre, DiasHabiles es 6 (los
       días 1 a 8 que ya pasaron) y no 23 (los del mes entero). */
    DECLARE @laborablesDelMes int = (SELECT COUNT(*) FROM @diasDelMes
                                     WHERE DATEPART(WEEKDAY, Dia) BETWEEN 2 AND 6);

    DECLARE @laborablesHastaHoy int = (SELECT COUNT(*) FROM @diasDelMes
                                       WHERE DATEPART(WEEKDAY, Dia) BETWEEN 2 AND 6
                                         AND Dia <= @hoy);

    /* Cuantos de esos días tienen AL MENOS UN intento que salió bien. Se
       cuenta por día, no por fila: un día con tres intentos y uno bueno está
       comprobado. Y un día que se preguntó y llegó con cero licitaciones SÍ
       está comprobado, porque está en MpConsulta con Exito = 1. Eso no hay que
       contarlo aparte: ya está en la tabla.

       Y ACOTADO POR @hoy, que antes no lo estaba, y eso hacía que DiasFallidos
       saliera NEGATIVO. Medido, con julio de 2026 y @hoy = 1 de julio:

           DiasHabiles = 1    DiasConsultados = 6    DiasFallidos = -5

       Seis días consultados y un solo día hábil el 1 de julio, porque el conteo
       cogía días de todo el mes mientras DiasHabiles solo contaba los que ya
       habían pasado. Las dos mitades de la resta no miraban el mismo periodo.

       Y un DiasFallidos negativo es peor que un número feo: la pantalla decide
       si un mes está comprobado entero con "DiasFallidos > 0", así que un -5
       hace que un mes con 22 días sin mirar se anuncie como comprobado entero.
       Es exactamente la mentira que mp.DiasSinComprobarDelMes existe para
       evitar, y llegaba por la puerta de atrás.

       Con el tope puesto, DiasConsultados nunca pasa de DiasHabiles y el -5 se
       convierte en 0, que es la verdad: el 1 de julio no se comprobó. */
    DECLARE @diasComprobados int =
    (
        SELECT COUNT(DISTINCT c.FechaDia)
        FROM dbo.MpConsulta AS c
        WHERE c.CodigoProveedor = @codigoProveedor
          AND c.FechaDia >= @primeroDelMes
          AND c.FechaDia <= @finDelMes
          AND c.FechaDia <= @hoy
          AND c.Exito = 1
    );

    SELECT
        @anio AS Anio,
        @mes AS Mes,
        @laborablesHastaHoy AS DiasHabiles,
        @diasComprobados AS DiasConsultados,
        @laborablesHastaHoy - @diasComprobados AS DiasFallidos,
        @laborablesDelMes - @laborablesHastaHoy AS DiasPendientes,
        /* UN RANGO DE FECHAS, y no YEAR(FechaPublicacion) = @anio AND
           MONTH(FechaPublicacion) = @mes.

           Las dos formas devuelven EXACTAMENTE las mismas filas, y esta base lo
           demuestra: 13 y 13. Por eso parece que da igual.

           No da igual por una cosa que se llama SARGABILIDAD: si el predicado
           deja que el indice se pueda usar. Comparar la columna desnuda
           (FechaPublicacion >= ...) si lo es; envolverla en YEAR(fecha) = ...
           no, porque el indice guarda los valores de la columna y no el
           resultado de la funcion, asi que no hay por donde entrar. Con anos de
           datos, la que recorra la tabla entera para devolver las de un mes es
           la de YEAR/MONTH.

           MEDIDO HOY NO HAY DIFERENCIA, y conviene no vender mas: con 13 filas y
           3 lecturas logicas, las dos hacen un recorrido de tabla y el
           optimizador acierta, porque buscar en un indice de una tabla de dos
           paginas cuesta MAS que recorrerla. El indice
           IX_MpLicitacion_FechaPublicacion existe y se aprovecha cuando la
           tabla crece. El rango es la forma correcta; hoy no se nota. */
        (SELECT COUNT(*) FROM dbo.MpLicitacion
          WHERE CodigoProveedor = @codigoProveedor
            AND FechaPublicacion >= @primeroDelMes
            AND FechaPublicacion <= @finDelMes) AS Total,
        SYSUTCDATETIME() AS Consultado;
END
GO


/* -----------------------------------------------------------------------
   mp.DiasSinComprobarDelMes

   Los días hábiles del mes que ya pasaron y que NO se pudieron comprobar, uno a
   uno y con su motivo.

   Uso:

       EXEC mp.DiasSinComprobarDelMes @codigoProveedor = N'71284', @anio = 2026, @mes = 10;

   El motivo sale del ÚLTIMO intento de cada día: si un día falló tres veces y
   la última fue un 429, el motivo es el 429 y no el primero. Un día que se
   preguntó dos veces con dos motivos distintos es raro, pero cuando pasa
   importa el último porque es el que explica por qué sigue fallando.

   Y se incluyen los días laborables que NO tienen NINGUNA consulta. Un día que
   nunca se preguntó no es un día comprobado, y aquí es donde más se nota: el 1
   y el 2 de octubre de 2026 no llegaron a preguntarse porque el rango que se
   importó empezaba el 14 de enero.

   NO lleva un recuento al principio. Va solo el detalle, y el recuento lo hace
   mp.CuentaDiasDelMes, para que no haya dos cifras del mismo número en dos
   sitios que puedan discrepar sin que nada avise.
   -------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE mp.DiasSinComprobarDelMes
    @codigoProveedor  nvarchar(50),
    @anio             int,
    @mes              int,
    @hoy              date = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* Tambien la fecha local, por el mismo motivo que en mp.CuentaDiasDelMes: un
       "hoy" en UTC vale un dia de mas entre las 21:00 y las 24:00, que es la
       franja en la que se mira. Y aqui el coste es mayor, porque este
       procedimiento es el que decide que dias sedice que no se pudieron
       comprobar. */
    IF @hoy IS NULL
        SET @hoy = CONVERT(date, GETDATE());

    IF @anio IS NULL OR @anio < 1900 OR @anio > 9999
    BEGIN
        RAISERROR('El anio %s no es valido.', 16, 1, @anio);
        RETURN;
    END

    IF @mes IS NULL OR @mes < 1 OR @mes > 12
    BEGIN
        RAISERROR('El mes %s no es valido.', 16, 1, @mes);
        RETURN;
    END

    IF @codigoProveedor IS NULL OR LTRIM(RTRIM(@codigoProveedor)) = ''
    BEGIN
        RAISERROR('Falta el codigo de proveedor.', 16, 1);
        RETURN;
    END

    DECLARE @primeroDelMes date = DATEFROMPARTS(@anio, @mes, 1);
    DECLARE @finDelMes     date = DATEADD(DAY, -1, DATEADD(MONTH, 1, @primeroDelMes));

    DECLARE @fallidos TABLE (FechaDia date PRIMARY KEY, Motivo nvarchar(400));

    INSERT @fallidos (FechaDia)
    SELECT dias.Dia
    FROM mp.TodosLosDiasDelMes(@primeroDelMes, @finDelMes) AS dias
    WHERE DATEPART(WEEKDAY, dias.Dia) BETWEEN 2 AND 6
      AND dias.Dia <= @hoy
      AND NOT EXISTS (SELECT 1 FROM dbo.MpConsulta AS c
                      WHERE c.CodigoProveedor = @codigoProveedor
                        AND c.FechaDia = dias.Dia AND c.Exito = 1);

    UPDATE f
    SET Motivo = ISNULL((
            SELECT TOP 1 ISNULL(c.Mensaje, N'Sin respuesta')
            FROM dbo.MpConsulta AS c
            WHERE c.CodigoProveedor = @codigoProveedor
              AND c.FechaDia = f.FechaDia
            ORDER BY c.FechaHora DESC), N'Nunca se consulto este dia')

    FROM @fallidos AS f;

    SELECT FechaDia, Motivo FROM @fallidos ORDER BY FechaDia;
END
GO


/* -----------------------------------------------------------------------
   mp.TodosLosDiasDelMes

   Función ESCALAR, no de tabla. Con TRY/CATCH —que es lo que se querría para
   validar el rango— una función no puede: T-SQL lo prohíbe dentro de funciones
   y da un error que no menciona las funciones. Aquí no hace falta, porque el
   rango lo construyen dos enteros ya validados.

   Los días van en una lista explícita y no con un generador del sistema. Un
   ROW_NUMBER() sobre sys.all_objects parece más elegante y depende de cuántas
   filas tenga esa vista del sistema: en una instancia recién instalada puede no
   alcanzar para un mes entero, y entonces el recuento de días hábiles sale
   bajo sin ningún error. Un fallo que solo depende del servidor donde corra.

   El 31 es porque el mes más largo tiene 31 días. Y n empieza en 0, para que el
   día 1 sea el primero del mes y no el segundo.
   -------------------------------------------------------------------- */
CREATE OR ALTER FUNCTION mp.TodosLosDiasDelMes (@primero date, @ultimo date)
RETURNS TABLE
AS
RETURN
(
    SELECT CONVERT(date, DATEADD(DAY, n, @primero)) AS Dia
    FROM (VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9), (10),
                 (11), (12), (13), (14), (15), (16), (17), (18), (19), (20),
                 (21), (22), (23), (24), (25), (26), (27), (28), (29), (30),
                 (31)) AS v(n)
    WHERE n <= DATEDIFF(DAY, @primero, @ultimo)
);
GO


/* -----------------------------------------------------------------------
   mp.LeeMes

   Las licitaciones publicadas en un mes, con su detalle y sus items, y el
   recuento de días comprobados.

   DEVUELVE CINCO CONJUNTOS y no uno plano. El detalle y los items se repetirían
   en cada fila del listado, y con veinte licitaciones eso serían veinte copias
   de lo mismo. Con cinco conjuntos, el servidor los lee una vez y los reparte.

   Este procedimiento no calcula nada: llama a los dos que sí calculan y pega lo
   que devuelven. Todo lo interesante está en mp.CuentaDiasDelMes y
   mp.DiasSinComprobarDelMes, que además se pueden probar solos.

   Uso:

       EXEC mp.LeeMes @codigoProveedor = N'71284', @anio = 2026, @mes = 10;
   -------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE mp.LeeMes
    @codigoProveedor  nvarchar(50),
    @anio             int,
    @mes              int,
    @hoy              date = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* Tambien la fecha local, por el mismo motivo que en mp.CuentaDiasDelMes: un
       "hoy" en UTC vale un dia de mas entre las 21:00 y las 24:00, que es la
       franja en la que se mira. Y aqui el coste es mayor, porque este
       procedimiento es el que decide que dias sedice que no se pudieron
       comprobar. */
    IF @hoy IS NULL
        SET @hoy = CONVERT(date, GETDATE());

    /* El mes entero, como rango. Las consultas de mas abajo filtran por esto y
       no por YEAR(FechaPublicacion) = @anio AND MONTH(FechaPublicacion) = @mes.

       Las dos formas devuelven las mismas filas. La diferencia es que el rango
       es SARGABLE —el indice IX_MpLicitacion_FechaPublicacion guarda los
       valores de la columna, no el resultado de YEAR(), asi que solo el rango
       deja que se use— y con la tabla grande eso es la diferencia entre leer
       un mes y leer la tabla entera.

       Con los datos de ahora no se nota: 3 lecturas logicas con las dos formas,
       porque la tabla cabe en dos paginas. Esta nota esta para que nadie lo
       "simplifique" de vuelta creyendo que es lo mismo, no para prometer una
       velocidad que todavia no existe. */
    DECLARE @primeroDelMes date = DATEFROMPARTS(@anio, @mes, 1);
    DECLARE @finDelMes     date = DATEADD(DAY, -1, DATEADD(MONTH, 1, @primeroDelMes));

    /* Las dos cuentas, en tablas variables.

       INSERT @t EXEC mete el resultado en la tabla, así que se puede usar
       aunque el procedimiento devuelva varios conjuntos. Lo que NO se puede es
       coger solo el primero, y por eso la cuenta está en otro procedimiento. */
    DECLARE @cuenta TABLE (
        Anio int, Mes int, DiasHabiles int, DiasConsultados int,
        DiasFallidos int, DiasPendientes int, Total int, Consultado datetime2(7));

    DECLARE @sinComprobar TABLE (FechaDia date PRIMARY KEY, Motivo nvarchar(400));

    INSERT @cuenta EXEC mp.CuentaDiasDelMes
        @codigoProveedor = @codigoProveedor, @anio = @anio, @mes = @mes, @hoy = @hoy;

    INSERT @sinComprobar EXEC mp.DiasSinComprobarDelMes
        @codigoProveedor = @codigoProveedor, @anio = @anio, @mes = @mes, @hoy = @hoy;

    ------------------------------------------------------------------
    -- 1. Cabecera: los números que la pantalla necesita para no mentir
    ------------------------------------------------------------------
    SELECT
        Anio, Mes, DiasHabiles, DiasConsultados, DiasFallidos,
        DiasPendientes, Total, Consultado
    FROM @cuenta;

    ------------------------------------------------------------------
    -- 2. Los días que NO se pudieron comprobar
    ------------------------------------------------------------------
    --
    -- Se devuelven uno a uno y con su motivo, para que la pantalla pueda decir
    -- qué pasó y no solo cuántos. Sin el motivo, un "3 días sin comprobar"
    -- deja al usuario sin saber si reintentar tiene sentido.
    SELECT FechaDia, Motivo FROM @sinComprobar ORDER BY FechaDia;

    ------------------------------------------------------------------
    -- 3. El listado del mes
    ------------------------------------------------------------------
    --
    -- El orden es el de la pantalla: primero lo que sigue vigente, por fecha de
    -- cierre ascendente, y lo ya vencido al final. Ordenar solo por fecha
    -- ascendente mete lo vencido PRIMERO, que es lo contrario de lo que se mira.
    --
    -- El CONVERT(datetime2, ...) al comparar con @hoy es lo que pasa de un
    -- datetimeoffset a la HORA LOCAL. Hacerlo con un AT TIME ZONE daría la hora
    -- de UTC y desplazaría el día entero cuando el desfase es negativo: una
    -- licitación que cerró a las 23:30 en Chile pasaría al día siguiente.
    SELECT
        l.CodigoExterno,
        l.Nombre,
        l.CodigoEstado,
        COALESCE(d.FechaCierre, l.FechaCierre) AS FechaCierre,
        l.FechaPublicacion,
        l.LicitacionId
    FROM dbo.MpLicitacion AS l
    LEFT JOIN dbo.MpLicitacionDetalle AS d ON d.LicitacionId = l.LicitacionId
    WHERE l.CodigoProveedor = @codigoProveedor
      AND l.FechaPublicacion >= @primeroDelMes
      AND l.FechaPublicacion <= @finDelMes
    ORDER BY CASE
                 WHEN COALESCE(d.FechaCierre, l.FechaCierre) IS NOT NULL
                  AND CONVERT(datetime2, COALESCE(d.FechaCierre, l.FechaCierre)) < @hoy
                 THEN 1 ELSE 0
             END,
             COALESCE(d.FechaCierre, l.FechaCierre),
             l.Nombre;

    ------------------------------------------------------------------
    -- 4. Los detalles de esas mismas licitaciones
    ------------------------------------------------------------------
    SELECT
        d.LicitacionId,
        d.Nombre,
        d.Estado,
        d.CodigoEstado,
        d.Descripcion,
        d.Tipo,
        d.NombreOrganismo,
        d.RutOrganismo,
        d.CodigoOrganismo,
        d.RegionOrganismo,
        d.ComunaOrganismo,
        d.MontoEstimado,
        d.Moneda,
        d.Estimacion,
        d.FechaCreacion,
        d.FechaCierre,
        d.FechaPublicacion,
        d.FechaAperturaTecnica,
        d.FechaAperturaEconomica,
        d.FechaAdjudicacion,
        d.FechaFinal,
        d.NumeroOferentes,
        d.NumeroAdjudicacion,
        d.UrlActa,
        d.NumeroItems,
        d.DiasCierreLicitacion
    FROM dbo.MpLicitacionDetalle AS d
    WHERE d.LicitacionId IN
    (
        SELECT l.LicitacionId
        FROM dbo.MpLicitacion AS l
        WHERE l.CodigoProveedor = @codigoProveedor
          AND l.FechaPublicacion >= @primeroDelMes
          AND l.FechaPublicacion <= @finDelMes
    );

    ------------------------------------------------------------------
    -- 5. Los items de esos detalles
    ------------------------------------------------------------------
    SELECT
        i.LicitacionId,
        i.Correlativo,
        i.NombreProducto,
        i.Descripcion,
        i.UnidadMedida,
        i.Cantidad,
        i.CantidadAdjudicada,
        i.MontoUnitario,
        i.Subtotal,
        i.RutProveedor,
        i.NombreProveedor
    FROM dbo.MpLicitacionItem AS i
    WHERE i.LicitacionId IN
    (
        SELECT l.LicitacionId
        FROM dbo.MpLicitacion AS l
        WHERE l.CodigoProveedor = @codigoProveedor
          AND l.FechaPublicacion >= @primeroDelMes
          AND l.FechaPublicacion <= @finDelMes
    )
    ORDER BY i.LicitacionId, i.Correlativo;
END
GO


/* -----------------------------------------------------------------------
   mp.LeeDetalle

   La ficha de UNA licitación, tal cual la devuelve hoy /api/licitaciones/{codigo}.

   Se consulta por (codigoProveedor, codigo) y no solo por código, porque el
   código existe en el contexto de una empresa: el mismo número puede aparecer
   en dos peticiones de dos proveedores distintos, y sin el empresa el segundo
   se llevaría la ficha del primero.
   -------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE mp.LeeDetalle
    @codigoProveedor  nvarchar(50),
    @codigo           nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @codigo IS NULL OR LTRIM(RTRIM(@codigo)) = ''
    BEGIN
        RAISERROR('Falta el codigo de licitacion.', 16, 1);
        RETURN;
    END

    DECLARE @licitacionId bigint =
        (SELECT l.LicitacionId
         FROM dbo.MpLicitacion AS l
         WHERE l.CodigoProveedor = @codigoProveedor
           AND l.CodigoExterno   = @codigo);

    IF @licitacionId IS NULL
    BEGIN
        /* No es un error de la base: es que de esa licitacion no se ha
           importado nada. La pantalla lo dice como "no hay detalle", que es
           distinto de "la base está caída". Por eso se devuelve Existe = 0 y
           no se lanza nada. */
        SELECT CAST(NULL AS bigint) AS LicitacionId, 0 AS Existe;
        RETURN;
    END

    SELECT @licitacionId AS LicitacionId, 1 AS Existe;

    SELECT
        d.Nombre, d.Estado, d.CodigoEstado, d.Descripcion, d.Tipo,
        d.NombreOrganismo, d.RutOrganismo, d.CodigoOrganismo,
        d.RegionOrganismo, d.ComunaOrganismo,
        d.MontoEstimado, d.Moneda, d.Estimacion,
        d.FechaCreacion, d.FechaCierre, d.FechaPublicacion,
        d.FechaAperturaTecnica, d.FechaAperturaEconomica,
        d.FechaAdjudicacion, d.FechaFinal,
        d.NumeroOferentes, d.NumeroAdjudicacion, d.UrlActa,
        d.NumeroItems, d.DiasCierreLicitacion
    FROM dbo.MpLicitacionDetalle AS d
    WHERE d.LicitacionId = @licitacionId;

    SELECT
        i.Correlativo, i.NombreProducto, i.Descripcion, i.UnidadMedida,
        i.Cantidad, i.CantidadAdjudicada, i.MontoUnitario, i.Subtotal,
        i.RutProveedor, i.NombreProveedor
    FROM dbo.MpLicitacionItem AS i
    WHERE i.LicitacionId = @licitacionId
    ORDER BY i.Correlativo;
END
GO


PRINT 'mp.LeeMes, mp.LeeDetalle, mp.CuentaDiasDelMes,';
PRINT 'mp.DiasSinComprobarDelMes y mp.TodosLosDiasDelMes creados.';
PRINT '';
PRINT 'Para probarlos contra la base actual:';
PRINT '';
PRINT '    EXEC mp.LeeMes @codigoProveedor = N''71284'', @anio = 2026, @mes = 10;';
PRINT '    EXEC mp.LeeDetalle @codigoProveedor = N''71284'', @codigo = N''1456839-6-LP26'';';
PRINT '';
PRINT 'Y las pruebas, que usan solo procedimientos de un conjunto:';
PRINT '';
PRINT '    sqlcmd -S localhost\SQLEXPRESS -E -d WatchMerPub -b -f 65001 -i sql\97-prueba-lectura.sql';
GO
