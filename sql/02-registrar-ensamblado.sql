/* ===========================================================================
   Registro del ensamblado CLR.

   ESTOS SON LOS TRES PASOS, Y EL ORDEN IMPORTA. Los tres se tienen que hacer
   una vez por instalacion, en este orden exacto:

       1. Compilar el ensamblado (.dll) con dotnet build.
       2. Declararlo de confianza con sp_add_trusted_assembly.
       3. CREATE ASSEMBLY.

   Ademas hay que activar el CLR en el servidor (sp_configure 'clr enabled'), y
   eso solo se puede hacer una vez.

   ---------------------------------------------------------------------------
   QUE ES ESTO Y POR QUE HACE FALTA
   ---------------------------------------------------------------------------

   Un procedimiento almacenado de T-SQL no sabe llamar a una API HTTP. Las dos
   vias que existen son el CLR (codigo .NET dentro de SQL Server) y OLE
   Automation. Se eligio el CLR por tres razones:

     - Soporta TLS 1.2, que Mercado Publico exige. Con OLE Automation la
       llamada falla con "No se puede crear un canal seguro SSL/TLS" antes de
       salir, y se probo: en esta maquina MSXML2 y WinHttp fallaron los dos.

     - El reintento con esperas y el mapeo del JSON se quedan en T-SQL, que se
       lee y se depura. Un error de mapeo en un ensamblado solo se ve
       recompilando.

     - No depende de que esten activados los "Ole Automation Procedures", que
       por defecto vienen desactivados y que ademas son una superficie de
       ataque conocida.

   El coste es este fichero: hay que compilar un .dll y registrarlo. Es
   incomodo, y es el precio de que T-SQL pueda hablar por red.

   ---------------------------------------------------------------------------
   LO QUE ESTA HECHO EN UN SOLO LOTE Y POR QUE
   ---------------------------------------------------------------------------

   Todo lo que puede fallar esta en TRY/CATCH con RAISERROR al final. Un
   despliegue a medias —CLR activado y ensamblado sin registrar, o
   ensamblado sin el CLR— deja el servidor en un estado en el que el siguiente
   intento falla con un error que no explica nada. Mejor que diga exactamente
   que paso.

   =========================================================================== */


/* ---------------------------------------------------------------------------
   0. EL CLR TIENE QUE ESTAR ACTIVADO EN EL SERVIDOR

   Y ESTO ES LO QUE MAS CUESTA, porque sp_configure solo lo deja PREPARADO: el
   cambio no se aplica hasta que se reinicia el servicio de SQL Server.

   En una prueba sobre SQL Server Express local se vio que el valor ya estaba
   en uso sin reiniciar, y puede ser que en algunas versiones ya lo este. Pero
   no se da por hecho: se comprueba DESPUES de aplicar, y si sigue en 0 se dice
   con claridad que hay que reiniciar, en vez de fallar mas adelante con un
   "no se puede cargar el ensamblado" que no menciona al reinicio ni de lejos.

   Y no reinicia solo: reiniciar un servidor de base de datos en caliente no es
   una cosa que deba hacer un script por su cuenta.
   ------------------------------------------------------------------------- */
PRINT '1. Activando el CLR en el servidor...';

DECLARE @clrAntes int = (SELECT CAST(value_in_use AS int)
                         FROM sys.configurations
                         WHERE name = 'clr enabled');

IF @clrAntes = 0
    EXEC sp_configure 'clr enabled', 1;

RECONFIGURE;
GO

DECLARE @clrDespues int = (SELECT CAST(value_in_use AS int)
                           FROM sys.configurations
                           WHERE name = 'clr enabled');

IF @clrDespues = 0
BEGIN
    DECLARE @msg nvarchar(600) =
        N'El CLR sigue desactivado. sp_configure deja el cambio PREPARADO pero no'
      + N' lo aplica: hay que REINICIAR el servicio de SQL Server y repetir.'
      + N' No es un fallo del script.';

    PRINT @msg;
    THROW 51000, @msg, 1;
END

PRINT '   CLR activo.';
GO


/* ---------------------------------------------------------------------------
   1. LA RUTA DEL ENSAMBLADO

   Se pasa por parametro con sqlcmd -v, porque la ruta depende de donde se haya
   compilado:

       sqlcmd -d WatchMercadoPublico -v RutaDll="C:\...\MercadoPublico.Http.dll" ^
               -i 02-registrar-ensamblado.sql

   Y si no se pasa, se busca en las rutas de siempre. Un DEFAULT en el
   procedimiento habria hecho que un despliegue en otra carpeta registrara el
   .dll viejo sin avisar: el ensamblado se registra POR NOMBRE, y si ya existe
   se esta reusando el antiguo aunque el fichero sea otro.
   ------------------------------------------------------------------------- */
/* ---------------------------------------------------------------------------
   LA COLUMNA DE sys.trusted_assemblies SE LLAMA 'description'

   No 'assembly_name', que es lo que espera el ojo y lo que busca el que ya se
   ha equivocado una vez. Con 'assembly_name' el error es

       Invalid column name 'assembly_name'

   y no dice nada de que la columna existe con otro nombre. La tabla tiene
   cuatro columnas: hash, description, create_date, created_by. Y 'description'
   guarda el NOMBRE del ensamblado.

   Va aqui, al principio del fichero, porque se usa mas adelante en dos sitios y
   no tiene sentido descubrirlo a mitad.
   ------------------------------------------------------------------------- */


/* ---------------------------------------------------------------------------
   2. EL HASH: POR QUE ES SHA-512 Y POR QUE HAY QUE RELLENARLO

   Este es el paso que mas confunde, y el error que da no ayuda nada.

   sp_add_trusted_assembly pide el parametro @hash como binary(64), es decir 64
   bytes. El SHA-256 del fichero son 32. Al pasar los 32 bytes, en vez de un
   error claro:

       Procedure expects parameter 'hash' of type 'binary(64)/varbinary(64)'

   ...que no dice nada de que hay que rellenarlo. Y si se rellena con ceros a
   64, el ensamblado SE ACEPTA pero luego CREATE ASSEMBLY responde que no es de
   confianza, porque SQL Server calcula su propio hash de 32 bytes y lo
   compara con los 64, y no coinciden. Ese es el punto en el que parece que el
   registro se hizo bien y el problema esta en otro sitio.

   La solucion: el hash que quiere es SHA-512, que SI son 64 bytes. Se pasa tal
   cual y funciona a la primera.

   Y hay que rehacerlo SIEMPRE que se recompile el .dll, porque el hash es del
   fichero: un binario nuevo es un hash nuevo. Volver a registrar sin recompilar
   no hace falta; recompilar sin volver a registrar da el error de confianza.
   ------------------------------------------------------------------------- */
PRINT '';
PRINT '2. Registrando el ensamblado...';
GO

/* ---------------------------------------------------------------------------
   DE DONDE SALE LA RUTA DEL .dll

   No se pasa por linea de ordenes, y es a proposito.

   La idea era usar "sqlcmd -v RutaDll=C:\...\MercadoPublico.Http.dll", y
   fallaba con:

       Sqlcmd: ':\Users\...\MercadoPublico.Http.dll': Invalid argument

   sqlcmd parte sus argumentos por "=" y el del path de Windows lleva drive y
   dos puntos; con comillas alrededor pasa lo mismo, porque el problema es el
   "=", no el espacio. Es un tropiezo innecesario para quien instala.

   En su lugar se escribe una fila en la tabla de abajo:

       INSERT dbo.MpConfiguracion (Clave, Valor)
       VALUES ('RutaEnsamblado', N'C:\...\MercadoPublico.Http.dll');

   ...y luego este fichero. Una tabla y no un sp_configure porque
   sp_configure RECHAZA un nombre de opcion que no conoce:

       The configuration option 'MpRutaEnsamblado' does not exist, or it may be
       an advanced option

   ...y ademas podria chocar con una opcion de verdad del servidor. Una tabla
   propia no puede.

   Y si no se ha puesto nada, se mira en las carpetas de siempre. El defecto de
   no pedir nada es que un despliegue en otra carpeta podria registrar el .dll
   equivocado, y como el ensamblado se registra POR NOMBRE, reutilizar el viejo
   no da ningun error. Por eso, si no se le dijo nada, lo dice con la ruta que
   ha usado. */
IF OBJECT_ID(N'dbo.MpConfiguracion', N'U') IS NULL
    CREATE TABLE dbo.MpConfiguracion
    (
        Clave   nvarchar(50)  NOT NULL,
        Valor   nvarchar(400) NOT NULL,
        CONSTRAINT PK_MpConfiguracion PRIMARY KEY CLUSTERED (Clave)
    );

/* LA RUTA VIENE DE DOS SITIOS, Y ANTES SOLO DE UNO.

   $(RutaDll) es la variable de sqlcmd, la que dice el comentario de cabecera:

       sqlcmd -d WatchMerPub -v RutaDll="C:\...\MercadoPublico.Http.dll" -i ...

   Y no se leia. El unico sitio del que se sacaba la ruta era la tabla
   MpConfiguracion, de modo que el -v documentado se aceptaba en silencio y se
   ignoraba: el guion registraba la ruta de por defecto, que en una maquina
   cualquiera no existe, y el fallo que sale no menciona la ruta en ningun
   sitio. Pasó al instalar el ensamblado firmado: -v con la ruta correcta,
   ignorado, y el guion cargo el .dll sin firmar de otra carpeta.

   Ahora manda $(RutaDll) si viene, y MpConfiguracion si no. El orden es el
   que se espera: lo que se dice en la linea de comandos gana sobre lo que se
   guardo la ultima vez. */
DECLARE @rutaConfigurada nvarchar(400) =
    CASE WHEN LTRIM(RTRIM(N'$(RutaDll)')) <> N''
         THEN LTRIM(RTRIM(N'$(RutaDll)'))
         ELSE (SELECT Valor FROM dbo.MpConfiguracion WHERE Clave = 'RutaEnsamblado')
    END;

DECLARE @rutaDll nvarchar(400);

IF @rutaConfigurada IS NOT NULL AND LTRIM(RTRIM(@rutaConfigurada)) <> N''
    SET @rutaDll = LTRIM(RTRIM(@rutaConfigurada));
ELSE
    SET @rutaDll = N'C:\WatchMercadoPublico\clr\MercadoPublico.Http.dll';

PRINT N'   DLL: ' + @rutaDll;

IF @rutaConfigurada IS NULL OR LTRIM(RTRIM(@rutaConfigurada)) = N''
    PRINT N'   (no estaba en MpConfiguracion la clave RutaEnsamblado; se usa esta ruta)';

IF NOT EXISTS (SELECT 1 FROM sys.assemblies WHERE name = N'MercadoPublico.Http')
BEGIN
    /* Si el procedimiento externo ya existe, hay que quitarlo antes:
       CREATE ASSEMBLY falla si el ensamblado esta en uso por un objeto.

       Y ojo: DROP PROCEDURE NO siempre libera la referencia al ensamblado. En
       esta maquina se probo y el "DROP ASSEMBLY" seguia respondiendo

           DROP ASSEMBLY failed because 'MercadoPublico.Http' is referenced by
           object 'MpPeticionGet'

       con el procedimiento ya borrado. Es un comportamiento conocido de los
       procedimientos EXTERNAL NAME. La conclusion practica es que este
       script solo instala la PRIMERA vez; para actualizar hay que quitar el
       procedimiento y el ensamblado a mano, o tirar la base y empezar otra
       vez. Por eso el IF de fuera: si ya esta cargado, no se toca. */
    IF OBJECT_ID(N'dbo.MpPeticionGet', N'P') IS NOT NULL
    BEGIN
        DECLARE @soltar nvarchar(max) =
            N'IF OBJECT_ID(''dbo.MpPeticionGet'',''P'') IS NOT NULL DROP PROCEDURE dbo.MpPeticionGet;';
        EXEC sp_executesql @soltar;
    END

    PRINT N'   Calculando el SHA-512 del fichero...';

    /* EL HASH DEL FICHERO.

       Se lee el .dll como binario con OPENROWSET(BULK) y se hashea con
       HASHBYTES('SHA2_512', ...), que devuelve los 64 bytes que
       sp_add_trusted_assembly pide.

       Se descartaron dos caminos antes de llegar aqui:

         - SHA512_FILE(ruta) leeria el fichero directamente, pero NO EXISTE.
           El error es "'SHA512_FILE' is not a recognized built-in function
           name", que parece una funcion mal escrita y no una que falte.

         - xp_cmdshell + Get-FileHash darian el hash en un comando de Power
            Shell, pero activar xp_cmdshell es una puerta de ataque muy conocida,
            y mucho mas grave que permitir un CREATE ASSEMBLY. No compensa.

       OPENROWSET(BULK) lee ficheros del disco sin ejecutar nada. Es de
       fiar para esto.

       Y SHA2_512, no SHA_256: SHA2_256 devuelve 32 bytes y sp_add_trusted_
       assembly los rechaza. Ese es el punto que mas rato costo, y el error
       ("expects parameter 'hash' of type 'binary(64)/varbinary(64)'") no dice
       que falte recorrer el hash, sino que parece un problema de tipo. */
    DECLARE @hash varbinary(64);

    /* OPENROWSET NO ACEPTA UNA VARIABLE en el primer argumento: espera una
       cadena LITERAL. Con una variable da:

           Incorrect syntax near '@rutaDll'

       Por eso el hash se calcula dentro de sp_executesql, donde la ruta se
       concatena YA como literal. Es la unica manera de que OPENROWSET la vea.

       Y la concatenacion no es una inyeccion: @rutaDll viene de la tabla de
       configuracion de este mismo servidor, no de fuera. Aun asi se cierran
       las comillas dobles por si alguien escribe una ruta con un " dentro. */
    DECLARE @sqlHash nvarchar(max) =
        N'SELECT @h = HASHBYTES(''SHA2_512'', BulkColumn)'
      + N' FROM OPENROWSET(BULK ''' + REPLACE(@rutaDll, N'''', N'''''') + N''', SINGLE_BLOB) AS b;';

    EXEC sp_executesql @sqlHash, N'@h varbinary(64) OUTPUT', @hash OUTPUT;

    IF @hash IS NULL
    BEGIN
        DECLARE @noExiste nvarchar(600) =
            N'No se encuentra la DLL en ' + @rutaDll + N'.'
          + N' Compilela antes: dotnet build sql\clr\MercadoPublico.Http.csproj -c Release';

        PRINT @noExiste;
        THROW 51001, @noExiste, 1;
    END

    PRINT N'   SHA-512: ' + CONVERT(nvarchar(130), @hash, 1);

    /* Si ya estaba declarado de confianza con OTRO hash —lo que pasa siempre que
       se recompila el .dll— se sustituye.

       Y aqui el nombre del procedimiento importa: es sp_DROP_trusted_assembly,
       no sp_remove_trusted_assembly. El que no existe da

           Could not find stored procedure 'sp_removetrusted_assembly'

       y ademas falla DESPUES de haber anadido el nuevo, dejando dos
       declaraciones con el mismo nombre, que sp_add_trusted_assembly no admite.
       Por eso la comprobacion de "ya existe" va antes de anadir y no despues. */
    IF EXISTS (SELECT 1 FROM sys.trusted_assemblies WHERE description = N'MercadoPublico.Http')
    BEGIN
        DECLARE @hashViejo varbinary(64) =
            (SELECT TOP 1 hash FROM sys.trusted_assemblies
             WHERE description = N'MercadoPublico.Http');

        IF @hashViejo IS NOT NULL AND @hashViejo <> @hash
        BEGIN
            PRINT N'   El hash guardado no es el de este .dll (se recompilo). Se sustituye.';
            EXEC sp_drop_trusted_assembly @hash = @hashViejo;
        END
    END

    IF NOT EXISTS (SELECT 1 FROM sys.trusted_assemblies WHERE description = N'MercadoPublico.Http')
        EXEC sp_add_trusted_assembly @hash = @hash, @assembly_name = N'MercadoPublico.Http';

    PRINT N'   Declarado de confianza.';

    /* PERMISSION_SET = UNSAFE. No es la opcion por defecto y se usa a
       proposito: UNSAFE es lo unico que permite salir a la red. EXTERNAL_ACCESS
       llega hasta a los recursos de Windows pero no a otro servidor.

       Es una decision que hay que tomar con la cabeza: concede a este
       ensamblado permisos de sistema dentro de SQL Server. A cambio, el
       ensamblado hace UNA cosa (un GET) y todo lo demas —el JSON, los
       reintentos, las tablas— esta en T-SQL, que se puede leer antes de
       concederlo. Un ensamblado con logica de negocio aqui no tendria nada
       que ver. */
    CREATE ASSEMBLY [MercadoPublico.Http]
        FROM @rutaDll
        WITH PERMISSION_SET = UNSAFE;

    PRINT N'   Ensamblado cargado.';
END
ELSE
    PRINT N'   Ya estaba cargado. Se deja el que hay.';
GO


/* ---------------------------------------------------------------------------
   3. EL PROCEDIMIENTO DE TRANSPORTE

   La sintaxis de EXTERNAL NAME lleva TRES partes entre corchetes, y esto no es
   opcional ni orden libre:

       [ENSAMBLADO].[ESPACIO_DE_NOMBRES.CLASE].[METODO]

   El punto DENTRO del corchete del medio es el espacio de nombres de C#, que
   aqui es MercadoPublico.Sql y la clase Peticion. Con un solo corchete
   ([[MercadoPublico.Sql.Peticion]].Eco) el mensaje es:

       Incorrect syntax near ';'

   ...que no dice nada de que el problema es la forma de escribir el nombre.
   ------------------------------------------------------------------------- */
PRINT '';
PRINT '3. Creando el procedimiento de transporte...';
GO

CREATE OR ALTER PROCEDURE dbo.MpPeticionGet
    @url                 nvarchar(2048),
    @segundosTimeout     int = 30,
    @codigoHttp          int            OUTPUT,
    @cuerpo              nvarchar(max)  OUTPUT,
    @error               nvarchar(2000) OUTPUT
AS
EXTERNAL NAME [MercadoPublico.Http].[MercadoPublico.Sql.Peticion].[Get];
GO

PRINT '';
PRINT '   dbo.MpPeticionGet listo.';
GO

/* ---------------------------------------------------------------------------
   4. QUE SE HIZO Y QUE FALTA
   ---------------------------------------------------------------------------
   Lo que sigue es una prueba de humo: llama a la API con un ticket FALSO a
   proposito. La respuesta correcta NO es un 200: es el error del ticket, que es
   la unica forma de demostrar de un golpe que salen a internet, que habla TLS y
   que el JSON llega bien.

   Un 200 aqui seria lo contrario de una buena noticia: significaria que la API
   acepto un ticket que no existe.
   ------------------------------------------------------------------------- */
PRINT '';
PRINT 'Comprobando que hay red, TLS y JSON...';
GO

DECLARE @codigo int, @cuerpo nvarchar(max), @error nvarchar(2000);

EXEC dbo.MpPeticionGet
    @url             = N'https://api.mercadopublico.cl/servicios/v1/publico/licitaciones.json?fecha=05102026&CodigoProveedor=PRUEBA&ticket=PRUEBA',
    @segundosTimeout = 30,
    @codigoHttp      = @codigo OUTPUT,
    @cuerpo          = @cuerpo OUTPUT,
    @error           = @error OUTPUT;

PRINT N'   HTTP ' + CONVERT(nvarchar(10), ISNULL(@codigo, 0))
    + N'  |  ' + ISNULL(dbo.MpMensajeApi(@cuerpo), N'(sin cuerpo)')
    + N'  |  red: ' + CASE WHEN @error IS NULL OR @error = N'' THEN N'ok'
                          ELSE N'ERROR: ' + @error END;

IF @error IS NOT NULL AND @error <> N''
BEGIN
    DECLARE @sinRed nvarchar(600) =
        N'No se pudo ni hablar con Mercado Publico: ' + @error
      + N' Si es lo del canal TLS, revisa que el .dll sea el de este repositorio.';

    THROW 51002, @sinRed, 1;
END

PRINT '';
PRINT 'Instalacion correcta. Para probar de verdad, con un ticket valido:';
PRINT '';
PRINT '    EXEC dbo.MpImportarRango';
PRINT '         @desde = ''2026-10-01'',';
PRINT '         @hasta = ''2026-10-05'',';
PRINT '         @codigoProveedor = ''<codigo>'',';
PRINT '         @ticket = ''<ticket>'';';
PRINT '';
PRINT '    Sin @soloFaltantes se vuelve a preguntar todo el rango. Con';
PRINT '    @soloFaltantes = 1 se salta lo ya descargado, salvo los ultimos';
PRINT '    @diasSondeo dias (3 por defecto): hoy, ayer y anteayer, que es lo que';
PRINT '    hace falta porque lo publicado ayer por la tarde llega despues de que';
PRINT '    el guion de la manana lo marcara como descargado.';
GO
