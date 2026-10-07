/* ===========================================================================
   WatchMercadoPublico · esquema de persistencia en SQL Server
   ===========================================================================

   QUE HACE ESTE SCRIPT

   Deja las tablas donde queda escrito TODO lo que la aplicacion leyo de
   Mercado Publico, para poder consultarlo despues con SQL en vez de con la
   pantalla:

     1. Una fila por CONSULTA hecha a la API, con su resultado o su fallo.
        Eso incluye los reintentos, porque cada intento es una peticion.
     2. Una fila por LICITACION vista, con su estado actual.
     3. Una fila por DETALLE de una licitacion (organismo, montos, fechas).
     4. Una fila por ITEM adjudicado (producto, proveedor, precio).
     5. Una fila por CAMBIO DE ESTADO, que despues no se puede reconstruir.
     6. La empresa vigilada, que es una sola pero puede cambiar con el tiempo.

   Ademas crea dos vistas ya hechas (vwMpLicitacion y vwMpDetalle) para no
   tener que escribir los JOIN a mano cada vez.


   QUE NO HACE ESTE SCRIPT

   NO conecta la aplicacion a la base. Esto es solo el sitio donde guardar los
   datos; que la aplicacion escriba therein es un cambio aparte en el codigo
   (añadir el paquete de SqlClient, una cadena de conexion y las llamadas de
   INSERT/UPSERT). La aplicacion hoy funciona sin base de datos y sigue
   funcionando sin ella.


   COMO EJECUTARLO

   sqlcmd -S localhost\SQLEXPRESS -E -d <basedatos> -b -f 65001 ^
           -i 01-esquema-mercadopublico.sql

   O bien pegarlo entero en una ventana de SQL Server Management Studio.

   Se puede volver a ejecutar las veces que haga falta: todo esta guardado con
   "IF OBJECT_ID ... IS NULL", asi que una segunda pasada no rompe nada ni
   borra los datos que ya esten ahi.

   El "-f 65001" no es adorno: sin el, sqlcmd lee el fichero con la pagina de
   codigos del sistema y los acentos de los comentarios se descuelgan. En
   Management Studio no hace falta.

   OJO CON EL QUOTED_IDENTIFIER, que sale en el script unas catorce veces y
   parece redundante. No lo es, y la razon es que sqlcmd arranca con
   QUOTED_IDENTIFIER en OFF, cosa que el Management Studio no hace. Los
   indices filtrados (los "WHERE Exito = 0"), las columnas calculadas
   PERSISTED y las vistas indexadas exigen que la opcion este en ON en el
   momento de crearlos, y sin el SET en cada lote sale:

     Msg 1934: CREATE INDEX failed because the following SET options have
     incorrect settings: 'QUOTED_IDENTIFIER'

   El SET va DESPUES de cada GO porque el GO corta el lote, y las opciones
   regionally no se heredan de uno al siguiente.


   DOS DECISIONES QUE PARECEN DETALLES Y NO LO SON
   ===========================================================================

   A. LAS FECHAS DE LA API SON datetimeoffset, NO datetime.

   El codigo lee los campos "Fechas" con DateTimeOffset y los pasa a hora local,
   asi que traen la hora de Chile (-03:00, -04:00 en horario de verano). Si se
   guardaran en datetime2 se perderia ese desfase: dos licitaciones que cerraron
   con una hora de diferencia entre dia de verano e invierno aparecerian con la
   misma hora local, que es justo el dato que hace falta para saber si algo
   alcanzo a cerrarse a tiempo.

   En cambio las fechas de CALENDARIO (FechaDia, FechaPublicacion) son date, sin
   hora: el dia que se consulto y el dia que se publico. No tienen zona y no
   deberian inventarsela.

   Las marcas de auditoria (FechaHora, PrimeraVezVista, UltimaVezVista) si son
   datetime2 en UTC, porque no son datos de la API sino_INSTANTES de esta
   maquina, y para ordenarlos no hace falta el desfase.


   B. LOS MONTOS SON decimal, NUNCA float.

   "MontoEstimado": 192000000.0 es un numero JSON entero con decimales, y en
   double un monto asi acaba en 191999999.99999997. Con una diferencia de un
   centimo entre lo que dice la API y lo que dice la base, la base ya no es
   una copia de la API. En decimal(19,4) cabe y sale exacto.
   =========================================================================== */

/* El QUOTED_IDENTIFIER tiene que estar en ON para crear los indices filtrados,
   las columnas calculadas PERSISTED y las vistas. sqlcmd arranca con esa opcion
   en OFF —a diferencia del Management Studio, que la pone en ON— y sin el SET
   sale:

     Msg 1934: CREATE INDEX failed because the following SET options have
     incorrect settings: 'QUOTED_IDENTIFIER'

   El GO va DESPUES del SET, no al reves. Es lo unico que hay que recordar al
   anadir un lote nuevo: cualquier CREATE TABLE, indice o vista que necesite
   esas opciones tiene que ir en un lote que empiece con el SET. Por eso el
   "-I" de sqlcmd tambien vale, pero el SET esta aqui para que el script
   funcione sin depender de como se llame. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO


/* ---------------------------------------------------------------------------
   EMPRESA VIGILADA

   Es UNA sola, viene de la configuracion y no se elige en pantalla. Se guarda
   igual porque si manana se cambia de empresa, los datos de la anterior tienen
   que seguir siendo legibles: sin esta tabla, las licitaciones antiguas
   apuntarian a un CodigoProveedor que ya no esta en ningun sitio.

   La clave es CodigoProveedor y no un id numerico, porque es lo que viaja en
   cada consulta a la API. Si se usara un id, habria que traducirlo en cada
   llamada y un fallo de esa traduccion escribiria las licitaciones en la
   empresa equivocada sin dar ningun error.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.MpEmpresa', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MpEmpresa
    (
        EmpresaId         int IDENTITY(1,1) NOT NULL,
        CodigoProveedor   nvarchar(50)  NOT NULL,
        NombreEmpresa     nvarchar(300) NOT NULL CONSTRAINT DF_MpEmpresa_Nombre      DEFAULT (''),
        RutEmpresa        nvarchar(20)  NULL,
        UrlMercadoPublico nvarchar(500) NULL,

        AltaEn            datetime2(3)  NOT NULL CONSTRAINT DF_MpEmpresa_AltaEn       DEFAULT (SYSUTCDATETIME()),
        UltimaActualizacion datetime2(3) NOT NULL CONSTRAINT DF_MpEmpresa_Act         DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_MpEmpresa PRIMARY KEY CLUSTERED (EmpresaId),
        CONSTRAINT UQ_MpEmpresa_Codigo UNIQUE (CodigoProveedor)
    );
END
GO



/* ---------------------------------------------------------------------------
   CONSULTA: una fila por peticion a la API

   Esta es la tabla que mas veces se consulta para responder "¿que se leyo y
   cuando?", asi que lleva indices pensados para eso.

   UN REGISTRO POR INTENTO, no uno por barrido. La aplicacion reintenta hasta
   seis veces un dia que falla, con esperas de 2, 4, 8, 16, 30 y 30 segundos. Si
   se guardara solo el resultado final se perderia justo lo que mas cuesta
   entender despues: que diasFallidos == 3 son 18 peticiones, no 3.

   Origen = 1 significa que la respuesta salio de la cache en memoria y NO se
   llamo a la API. Se guarda tambien, porque es la unica forma de saber cuanto
   cupo del ticket se gasto de verdad: el ticket tiene tope diario y esa
   pregunta no tiene otra respuesta.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.MpConsulta', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MpConsulta
    (
        ConsultaId        bigint IDENTITY(1,1) NOT NULL,

        -- Instante de la peticion, en UTC. Es de esta maquina, no de la API.
        FechaHora         datetime2(3)   NOT NULL CONSTRAINT DF_MpConsulta_Fecha DEFAULT (SYSUTCDATETIME()),

        -- 0 = listado de un dia (publico/licitaciones.json?fecha=...)
        -- 1 = detalle de una licitacion (publico/licitaciones.json?codigo=...)
        TipoConsulta      tinyint       NOT NULL,

        -- 0 = llamada real a la API    1 = servida desde la cache (0 cupo)
        Origen            tinyint       NOT NULL CONSTRAINT DF_MpConsulta_Origen DEFAULT (0),

        -- 'v1', 'c2' o 'demo'. Con 'demo' los datos son INVENTADOS y esta
        -- marcada para poder borrarlos sin borrar los reales.
        ModoConsulta      varchar(10)   NOT NULL,

        CodigoProveedor   nvarchar(50)  NOT NULL CONSTRAINT DF_MpConsulta_Prov DEFAULT (''),

        -- Solo en TipoConsulta = 0. Que dia se pregunto.
        FechaDia          date          NULL,

        -- Solo en TipoConsulta = 1. Que licitacion se pregunto.
        CodigoLicitacion  nvarchar(64)  NULL,

        -- 1..6 para el listado, 1..3 para el detalle (ver IntentosPorDia y
        -- IntentosPorDetalle en LicitacionesEndpoints.cs).
        NumeroIntento     tinyint       NOT NULL CONSTRAINT DF_MpConsulta_Intento DEFAULT (1),

        -- false = la peticion se lanzo y fallo, true = devolvio algo utilizable.
        Exito             bit           NOT NULL,

        -- Lo que devolvio la API, o null si no hubo respuesta.
        CodigoHttp        smallint      NULL,

        -- El "Codigo" del JSON de error de Mercado Publico. El 10300 es el
        -- "el formato del parametro fechas es incorrecto", que paso de verdad:
        -- ver ConstruirUrlDia en MercadoPublicoCliente.cs.
        CodigoApi         int           NULL,

        Mensaje           nvarchar(1000) NULL,

        -- Cuantas filas trae la respuesta. Para el detalle, cuantos items.
        CantidadDevuelta  int           NULL,

        -- Milisegundos que tardo la peticion. Sirve para ver cuando se dispara
        -- el limite de ritmo: la API admite del orden de una peticion cada
        -- 1,5 s y avisa con un 429.
        DuracionMs        int           NULL,

        -- Lo que esta usando el servidor (nombre de la instancia), para cuando
        -- se mire la tabla desde otra maquina.
        OrigenServidor    nvarchar(128) NULL,

        /* La tabla cumple su propia regla: un listado siempre trae dia y nunca
           codigo de licitacion, y un detalle al reves. Si la aplicacion se
           conecta mal y guarda las dos columnas a la vez, o ninguna, esta
           restriccion lo dice al insertar y no tres meses despues al contar. */
        CONSTRAINT CK_MpConsulta_Tipo CHECK
        (
            (TipoConsulta = 0 AND FechaDia IS NOT NULL AND CodigoLicitacion IS NULL)
            OR
            (TipoConsulta = 1 AND CodigoLicitacion IS NOT NULL AND FechaDia IS NULL)
        ),

        CONSTRAINT CK_MpConsulta_Origen CHECK (Origen IN (0, 1)),
        CONSTRAINT CK_MpConsulta_TipoOk CHECK (TipoConsulta IN (0, 1)),

        CONSTRAINT PK_MpConsulta PRIMARY KEY CLUSTERED (ConsultaId)
    );
END
GO


IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpConsulta_FechaHora' AND object_id = OBJECT_ID(N'dbo.MpConsulta'))
    CREATE INDEX IX_MpConsulta_FechaHora
        ON dbo.MpConsulta (FechaHora DESC);

-- "¿Que se leyo del dia X?" es la pregunta mas frecuente del diagnostico.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpConsulta_FechaDia' AND object_id = OBJECT_ID(N'dbo.MpConsulta'))
    CREATE INDEX IX_MpConsulta_FechaDia
        ON dbo.MpConsulta (FechaDia)
        WHERE FechaDia IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpConsulta_CodigoLicitacion' AND object_id = OBJECT_ID(N'dbo.MpConsulta'))
    CREATE INDEX IX_MpConsulta_CodigoLicitacion
        ON dbo.MpConsulta (CodigoLicitacion)
        WHERE CodigoLicitacion IS NOT NULL;

-- Indice filtrado SOLO con los fallos. Es diminuto comparado con la tabla
-- entera, porque los 429 y los 500 son la excepcion, y con el se responde
-- "¿que se ha estado rechazando?" sin recorrer millones de filas buenas.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpConsulta_Fallos' AND object_id = OBJECT_ID(N'dbo.MpConsulta'))
    CREATE INDEX IX_MpConsulta_Fallos
        ON dbo.MpConsulta (CodigoHttp, FechaHora DESC)
        WHERE Exito = 0;
GO



/* ---------------------------------------------------------------------------
   LICITACION: una fila por licitacion vista

   El listado diario de la API trae solo cuatro campos (codigo, nombre, estado y
   fecha de cierre); todo lo demás viene en el detalle. Por eso esta tabla es
   corta a proposito y la info que falta esta en MpLicitacionDetalle.

   Los datos se ACTUALIZAN en cada lectura (upsert), no se duplican. Se
   guardan la primera y la ultima vez que se vio, y cuantas veces, porque una
   licitacion aparece en varios dias de la semana y sin eso no se sabe si se
   vio 1 o 5 veces.

   Clave natural (CodigoProveedor, CodigoExterno): es lo unico que la API
   garantiza estable. Un id numerico propio no sirve para emparejar con la API
   y obliga a inventar una correspondencia.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.MpLicitacion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MpLicitacion
    (
        LicitacionId      bigint IDENTITY(1,1) NOT NULL,

        CodigoProveedor   nvarchar(50)   NOT NULL,
        CodigoExterno     nvarchar(64)   NOT NULL,
        Nombre            nvarchar(1000) NULL,

        -- 5 Publicada, 6 Cerrada, 7 Desierta, 8 Adjudicada, 18 Revocada,
        -- 19 Suspendida. Se guarda el numero y no el texto porque el texto es
        -- traducido y podria cambiar; el codigo no.
        CodigoEstado      tinyint        NULL,

        FechaCierre       datetimeoffset(3) NULL,

        -- El dia del barrido en el que aparecio. date, no datetimeoffset: es
        -- un dia del calendario y no lleva hora.
        FechaPublicacion  date           NULL,

        ModoConsulta      varchar(10)    NOT NULL,

        PrimeraVezVista   datetime2(3)   NOT NULL CONSTRAINT DF_MpLicitacion_Primera DEFAULT (SYSUTCDATETIME()),
        UltimaVezVista    datetime2(3)   NOT NULL CONSTRAINT DF_MpLicitacion_Ultima  DEFAULT (SYSUTCDATETIME()),
        NumeroVecesVista  int            NOT NULL CONSTRAINT DF_MpLicitacion_Veces   DEFAULT (1),

        -- Que consulta trajo la ultima lectura. NULL si se importaron los datos
        -- a mano o desde un fichero.
        ConsultaUltimaId  bigint         NULL,

        CONSTRAINT UQ_MpLicitacion_Codigo UNIQUE (CodigoProveedor, CodigoExterno),
        CONSTRAINT PK_MpLicitacion PRIMARY KEY CLUSTERED (LicitacionId)
    );
END
GO


IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpLicitacion_FechaPublicacion' AND object_id = OBJECT_ID(N'dbo.MpLicitacion'))
    CREATE INDEX IX_MpLicitacion_FechaPublicacion
        ON dbo.MpLicitacion (FechaPublicacion DESC)
        WHERE FechaPublicacion IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpLicitacion_Estado' AND object_id = OBJECT_ID(N'dbo.MpLicitacion'))
    CREATE INDEX IX_MpLicitacion_Estado
        ON dbo.MpLicitacion (CodigoEstado, FechaCierre);

/* SIN indice por Nombre, y es una decision y no un olvido.

   Nombre es nvarchar(1000), o sea 2.000 bytes, y un indice no clustered de
   SQL Server admite 1.700. Al crear el indice sale el aviso

     "The maximum key length for a nonclustered index is 1700 bytes"

   que no es cosmetico: el limite es real y con un nombre largo el INSERT
   falla. Seria un fallo que solo aparece con los datos que mas se parecen a los
   reales, que es la peor forma de que falle.

   Y ademas un indice sobre Nombre no ayudaria a la busqueda que de verdad se
   haria ("%adquisicion%"), porque los indices solo aceleran el principio de la
   cadena. Si mas adelante hace falta buscar por texto, lo que corresponde es
   un indice de texto completo con
     CREATE FULLTEXT INDEX ... ON dbo.MpLicitacion (Nombre)
   y no un indice normal. */
GO



/* ---------------------------------------------------------------------------
   LICITACIONDETALLE: la ficha completa

   Una fila por licitacion, actualizada en cada lectura del detalle.

   FechaCierre aparece TAMBIEN en MpLicitacion. No es una copia sin querer: son
   dos fuentes distintas y no siempre coinciden. El listado trae el cierre en la
   raiz; el detalle lo trae dentro de "Fechas" y a veces viene vacio, y el
   codigo usa el del listado como respaldo (ver ObtenerDetalleAsync). Guardar
   los dos por separado deja ver cuando la API se contradice, que es un dato
   sobre la API y no un error de la base.

   LA CLAVE ES UNA CLAVE FORANEA A MpLicitacion, NO UN IDENTITY PROPIO. Y esto
   no es una cuestion de estilo: es un fallo que ya se cometio aqui.

   La primera version tenia "LicitacionId bigint IDENTITY" en las tres tablas,
   y la vista emparejaba MpLicitacion.LicitacionId con
   MpLicitacionItem.LicitacionId. Son dos columnas que se llaman igual pero que
   cuentan por separado. Las tres filas se insertaron en el mismo orden, asi que
   los dos juegos de identificadores salieron 1, 2 y 3, y los items quedaron
   atribuidos a la licitacion equivocada sin dar ni un error: cada numero era
   valido en su tabla, la consulta no protestaba y el resultado era
   heelsimplemente incorrecto.

   Un fallo asi no se ve al mirar los datos de una tabla, porque cada tabla esta
   bien. Solo se ve al cruzarlas, y para entonces ya hay Informes con numeros
   equivocados que nadie va a repasar. Con la clave foranea, el mismo cruce es
   imposible: no hay dos identificadores que puedan confundirse porque solo hay
   uno.

   Por eso aqui no se repite CodigoProveedor ni CodigoExterno: se heredan de
   MpLicitacion por la clave foranea. Duplicarlos invitaba a que se actualizaran
   uno si y otro no.

   Sin clave foranea desde MpConsulta a proposito: la consulta que trajo el
   detalle es informacion de traza, no parte del dato. Si se perdiera esa
   fila, el detalle deberia seguir siendo valido.

   Un dato que NO se guarda: PublicadoTexto ("viernes 27 de febrero"). Es texto
   que compone la aplicacion con TextosDeFecha. Guardarlo seria guardar dos
   veces la misma fecha en dos formatos y acabar preguntandose cual es el
   bueno.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.MpLicitacionDetalle', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MpLicitacionDetalle
    (
        -- CLAVE FORANEA, no IDENTITY. Ver el comentario de este bloque: este
        -- identificador tiene que ser el MISMO que en MpLicitacion, y la unica
        -- forma de garantizarlo es no inventarlo aqui.
        LicitacionId      bigint         NOT NULL,

        Nombre            nvarchar(1000) NULL,
        Estado            nvarchar(50)   NULL,
        CodigoEstado      tinyint        NULL,
        Descripcion       nvarchar(max)  NULL,

        -- "LP", "LE", "L1"… abreviado, tal como lo manda la API.
        Tipo              nvarchar(10)   NULL,

        -- Organismo comprador. Viene en "Comprador", dentro del detalle, y no
        -- en el listado diario.
        NombreOrganismo   nvarchar(300)  NULL,
        RutOrganismo      nvarchar(20)   NULL,
        CodigoOrganismo   int            NULL,
        RegionOrganismo   nvarchar(100)  NULL,
        ComunaOrganismo   nvarchar(100)  NULL,

        -- Lo que se estimo que costaba. decimal(19,4) porque el JSON los manda
        -- como numero y en double pierden precision.
        MontoEstimado     decimal(19,4)   NULL,
        Moneda            nvarchar(10)   NULL,

        -- 1 = Presupuesto Disponible, 2 = Precio Referencial.
        Estimacion        tinyint        NULL,

        -- Todas con zona horaria: las trae dentro de "Fechas" y las horas
        -- importan (ver la nota A de la cabecera).
        FechaCreacion           datetimeoffset(3) NULL,
        FechaCierre             datetimeoffset(3) NULL,
        FechaPublicacion        datetimeoffset(3) NULL,
        FechaAperturaTecnica    datetimeoffset(3) NULL,
        FechaAperturaEconomica  datetimeoffset(3) NULL,
        FechaAdjudicacion       datetimeoffset(3) NULL,
        FechaFinal              datetimeoffset(3) NULL,

        -- Adjudicacion: numero de oferentes, numero de acto y enlace al acta.
        NumeroOferentes   int            NULL,
        NumeroAdjudicacion nvarchar(50)  NULL,
        UrlActa           nvarchar(500)  NULL,

        NumeroItems       int            NULL,

        -- Dias que faltaron entre el cierre y la apertura.
        DiasCierreLicitacion int         NULL,

        PrimeraLectura    datetime2(3)   NOT NULL CONSTRAINT DF_MpDetalle_Primera DEFAULT (SYSUTCDATETIME()),
        UltimaLectura     datetime2(3)   NOT NULL CONSTRAINT DF_MpDetalle_Ultima  DEFAULT (SYSUTCDATETIME()),
        NumeroLecturas    int            NOT NULL CONSTRAINT DF_MpDetalle_Veces   DEFAULT (1),

        ConsultaUltimaId  bigint         NULL,

        /* La clave primaria ES la clave foranea: una ficha por licitacion y
           como mucho. El indice unico sobra, porque CodigoProveedor y
           CodigoExterno ya son unicos en MpLicitacion y la clave foranea no
           admite duplicados. */
        CONSTRAINT PK_MpLicitacionDetalle PRIMARY KEY CLUSTERED (LicitacionId),

        CONSTRAINT FK_MpDetalle_Licitacion FOREIGN KEY (LicitacionId)
            REFERENCES dbo.MpLicitacion (LicitacionId)
    );
END
GO


IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpDetalle_Organismo' AND object_id = OBJECT_ID(N'dbo.MpLicitacionDetalle'))
    CREATE INDEX IX_MpDetalle_Organismo
        ON dbo.MpLicitacionDetalle (CodigoOrganismo)
        WHERE CodigoOrganismo IS NOT NULL;

-- Busqueda por nombre de organismo, que es como se pregunta "¿que le compro
-- a la municipalidad X?". Un indice normal no ayuda a un LIKE '%texto%', asi
-- que este sirve para igualdad o coincidencia por el principio del texto. Si
-- mas adelante hace falta buscar por texto libre en cualquier parte, lo que
-- corresponde es un indice de texto completo, no este.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpDetalle_NombreOrganismo' AND object_id = OBJECT_ID(N'dbo.MpLicitacionDetalle'))
    CREATE INDEX IX_MpDetalle_NombreOrganismo
        ON dbo.MpLicitacionDetalle (NombreOrganismo)
        WHERE NombreOrganismo IS NOT NULL;
GO



/* ---------------------------------------------------------------------------
   LICITACIONITEM: los items adjudicados

   Vienen anidados en Items.Listado[].Adjudicacion, NO en el adjudication de
   primer nivel: ese es el ACTA (fecha, numero, oferentes, enlace) y no lleva
   ningun monto. Solo hay items cuando la licitacion ya esta adjudicada.

   La clave es (LicitacionId, Correlativo) y no solo CodigoExterno, porque los
   correlativos son los que trae la API y son los que identifican el producto.

   Cantidad y CantidadAdjudicada se guardan las dos, y no una sola, porque
   pueden diferir: se pidio una cantidad y se adjudico otra. Para el total se
   usa la adjudicada, con la pedida como respaldo.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.MpLicitacionItem', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MpLicitacionItem
    (
        ItemId            bigint IDENTITY(1,1) NOT NULL,

        LicitacionId      bigint         NOT NULL,
        Correlativo       int            NOT NULL,

        NombreProducto    nvarchar(500)  NULL,

        -- "Unidad", "Metro", "Kg"…
        UnidadMedida      nvarchar(50)   NULL,

        Cantidad          decimal(19,4)  NULL,
        CantidadAdjudicada decimal(19,4) NULL,

        -- El precio por unidad adjudicado: el dato que se muestra.
        MontoUnitario     decimal(19,4)  NULL,

        -- Unitario x cantidad. Es una COLUMNA CALCULADA, no un numero guardado.
        -- Si se guardara el resultado, el dia que se corrigiera una cantidad el
        -- subtotal se quedaria viejo y el total no cuadraria con los items.
        -- PERSISTED para no recalcularla en cada lectura de la vista.
        Subtotal AS (CONVERT(decimal(19,4),
                    MontoUnitario * COALESCE(CantidadAdjudicada, Cantidad, 1)))
                   PERSISTED,

        RutProveedor      nvarchar(20)   NULL,
        NombreProveedor   nvarchar(300)  NULL,

        UltimaLectura     datetime2(3)   NOT NULL CONSTRAINT DF_MpItem_Ultima DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_MpLicitacionItem PRIMARY KEY CLUSTERED (ItemId),

        -- Va DENTRO del CREATE TABLE y no como un ALTER TABLE suelto: la
        -- sentencia suelta "CONSTRAINT ... UNIQUE" despues del parentesis de
        -- cierre no es SQL valido, y el error sale como "Incorrect syntax near
        -- the keyword 'CONSTRAINT'" en la linea 63, dos lineas mas abajo de
        -- donde esta el problema.
        CONSTRAINT UQ_MpItem_Correlativo UNIQUE (LicitacionId, Correlativo),

        /* Un item sin precio unitario no se guarda. Es la misma regla del
           codigo (ver LeerItems en MercadoPublicoCliente.cs): sin monto no
           aporta nada al total y solo llenaria la tabla de ruido. Por eso los
           correlativos guardados pueden no ser seguidos, y eso es correcto. */
        CONSTRAINT CK_MpItem_Monto CHECK (MontoUnitario IS NOT NULL),

        -- Apunta a MpLicitacionDetalle, cuya clave primaria ES el
        -- LicitacionId de MpLicitacion. Por eso aqui no puede haber confusion
        -- de identificadores: la cadena de claves foraneas no se rompe en
        -- ningun punto.
        --
        -- Sin ON DELETE CASCADE a proposito: los items son el dato y se borran
        -- a mano cuando se quiere, no por sorpresa al borrar una fila padre.
        CONSTRAINT FK_MpItem_Licitacion FOREIGN KEY (LicitacionId)
            REFERENCES dbo.MpLicitacionDetalle (LicitacionId)
    );
END
GO


IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpItem_Proveedor' AND object_id = OBJECT_ID(N'dbo.MpLicitacionItem'))
    CREATE INDEX IX_MpItem_Proveedor
        ON dbo.MpLicitacionItem (RutProveedor)
        WHERE RutProveedor IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpItem_Licitacion' AND object_id = OBJECT_ID(N'dbo.MpLicitacionItem'))
    CREATE INDEX IX_MpItem_Licitacion
        ON dbo.MpLicitacionItem (LicitacionId);
GO



/* ---------------------------------------------------------------------------
   LICITACIONESTADOHISTORICO: los cambios de estado

   MpLicitacion guarda el estado ACTUAL. Esta guarda la secuencia de cambios,
   y es la unica forma de responder "¿cuando paso de Publicada a Adjudicada?"
   dentro del periodo en que la aplicacion estuvo mirando.

   Es lo que hace que merezca la pena: los datos de Mercado Publico de hoy no
   dicen nada sobre los de marzo, y esta tabla es la memoria de lo que se vio.

   Se escribe SOLO cuando el estado cambia, no en cada lectura. Si se guardara
   una fila por lectura serian miles de filas que dicen "sigue publicada",
   que es justamente lo que no hay que almacenar.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.MpLicitacionEstadoHistorico', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MpLicitacionEstadoHistorico
    (
        HistoricoId       bigint IDENTITY(1,1) NOT NULL,

        LicitacionId      bigint         NOT NULL,
        CodigoExterno     nvarchar(64)   NOT NULL,

        CodigoEstado      tinyint        NOT NULL,
        Estado            nvarchar(50)   NULL,

        -- Cuando sevio el cambio. Instante de esta maquina, en UTC.
        VistoEn           datetime2(3)   NOT NULL CONSTRAINT DF_MpHist_Visto DEFAULT (SYSUTCDATETIME()),

        ConsultaId        bigint         NULL,

        CONSTRAINT PK_MpLicitacionEstadoHistorico PRIMARY KEY CLUSTERED (HistoricoId),

        CONSTRAINT FK_MpHist_Licitacion FOREIGN KEY (LicitacionId)
            REFERENCES dbo.MpLicitacion (LicitacionId)
    );
END
GO


IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_MpHist_Licitacion' AND object_id = OBJECT_ID(N'dbo.MpLicitacionEstadoHistorico'))
    CREATE INDEX IX_MpHist_Licitacion
        ON dbo.MpLicitacionEstadoHistorico (LicitacionId, VistoEn);
GO



/* ---------------------------------------------------------------------------
   VISTAS
   --------------------------------------------------------------------------- */

/* vwMpLicitacion: una fila por licitacion, con lo basico del listado y lo
   importante del detalle junto, mas el estado en texto.

   El CASE del estado es la MISMA regla que EstadoLegible en
   Licitaciones.cs del cliente: 5 Publicada, 6 Cerrada, 7 Desierta,
   8 Adjudicada, 18 Revocada, 19 Suspendida. Se decide por el codigo y no por
   el texto de la API, porque el texto es traducido y podria venir en otro
   idioma o cambiar de un dia para otro. */
CREATE OR ALTER VIEW dbo.vwMpLicitacion
AS
SELECT
    l.LicitacionId,
    l.CodigoProveedor,
    l.CodigoExterno,
    l.Nombre,
    l.CodigoEstado,
    CASE l.CodigoEstado
        WHEN 5  THEN N'Publicada'
        WHEN 6  THEN N'Cerrada'
        WHEN 7  THEN N'Desierta'
        WHEN 8  THEN N'Adjudicada'
        WHEN 18 THEN N'Revocada'
        WHEN 19 THEN N'Suspendida'
        ELSE N'Sin estado'
    END                                     AS Estado,
    -- El codigo de la API manda; el del listado es el respaldo (nota del
    -- bloque MpLicitacionDetalle).
    COALESCE(d.FechaCierre, l.FechaCierre) AS FechaCierre,
    l.FechaPublicacion,

    d.NombreOrganismo,
    d.RegionOrganismo,
    d.ComunaOrganismo,
    d.MontoEstimado,
    d.Moneda,
    d.NumeroOferentes,
    d.FechaAdjudicacion,

    agg.TotalAdjudicado,
    agg.CantidadItems,

    l.PrimeraVezVista,
    l.UltimaVezVista,
    l.NumeroVecesVista,
    l.ModoConsulta
FROM dbo.MpLicitacion AS l
LEFT JOIN dbo.MpLicitacionDetalle AS d
       ON d.LicitacionId = l.LicitacionId
OUTER APPLY
(
    -- El total se calcula AQUI, no se guarda. Es la unica manera de que nunca
    -- pueda desajustarse de los items: si se guardara el total como columna,
    -- bastaria con que un item cambiara para que dejara de cuadrar, y no
    -- habria forma de saber cual de los dos numeros era el bueno.
    --
    -- NULL cuando no hay items con monto, que es lo que hace el codigo, y no
    -- un 0: "no se adjudico nada todavia" y "se adjudico cero" no son lo mismo.
    SELECT
        CASE WHEN COUNT(i.MontoUnitario) = 0 THEN NULL
             ELSE SUM(i.Subtotal)
        END AS TotalAdjudicado,
        COUNT(i.ItemId) AS CantidadItems
    FROM dbo.MpLicitacionItem AS i
    WHERE i.LicitacionId = l.LicitacionId
) AS agg;
GO



/* vwMpDetalle: la ficha completa de una licitacion, con sus items en filas.

   De nada sirve tener los items en otra tabla si luego hay que(exportsarlos a
   mano para verlos: esta vista los trae a la vista. */
CREATE OR ALTER VIEW dbo.vwMpDetalle
AS
SELECT
    l.LicitacionId,
    l.CodigoProveedor,
    l.CodigoExterno,
    l.Nombre,
    l.CodigoEstado,
    l.FechaPublicacion,
    d.Estado,
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
    d.FechaPublicacion          AS FechaPublicacionDetalle,
    d.FechaAperturaTecnica,
    d.FechaAperturaEconomica,
    d.FechaAdjudicacion,
    d.FechaFinal,
    d.NumeroOferentes,
    d.NumeroAdjudicacion,
    d.UrlActa,
    d.DiasCierreLicitacion,
    i.Correlativo,
    i.NombreProducto,
    i.UnidadMedida,
    i.Cantidad,
    i.CantidadAdjudicada,
    i.MontoUnitario,
    i.Subtotal,
    i.RutProveedor,
    i.NombreProveedor
FROM dbo.MpLicitacion AS l
JOIN dbo.MpLicitacionDetalle AS d
       ON d.LicitacionId = l.LicitacionId
LEFT JOIN dbo.MpLicitacionItem AS i
       ON i.LicitacionId = d.LicitacionId;
GO



PRINT 'Esquema creado.';
PRINT '  Tablas : MpEmpresa, MpConsulta, MpLicitacion, MpLicitacionDetalle,';
PRINT '           MpLicitacionItem, MpLicitacionEstadoHistorico';
PRINT '  Vistas : vwMpLicitacion, vwMpDetalle';
PRINT '';
PRINT 'Recordatorio: esto crea las tablas. La aplicacion sigue sin tocar la';
PRINT 'base. Para que escriba, hay que anadir la conexion en el servidor.';
GO
