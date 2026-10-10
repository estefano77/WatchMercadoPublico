using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;
using WatchMercadoPublico.Server.Services;

namespace WatchMercadoPublico.Server.Endpoints;

/// <summary>
/// Endpoints que consume la aplicación Blazor.
///
/// La unidad es UNA SEMANA de una empresa fija: la pantalla elige año, mes y
/// semana, y con los tres juntos se consulta una semana, no un año entero. Sigo
/// sin filtro de palabras ni búsqueda por RUT, que se quitaron porque eran una
/// forma de equivocarse sin ayudar a responder la pregunta.
///
/// Aquí se hace el trabajo que la API no hace: insistir hasta que responda,
/// ordenar lo que llega, saltar los fines de semana y los días que todavía no
/// han llegado, y no inventar una lista vacía cuando falla.
/// </summary>
public static class LicitacionesEndpoints
{
    /// <summary>
    /// Cuántos intentos se hacen, contando el primero.
    ///
    /// NADA que ver con un "las primeras peticiones de cada sesión". Esa idea
    /// estaba aquí y era FALSA: el 500 constante que motivó subir los intentos
    /// desde 2 hasta 6 no era de Mercado Público, era nuestra URL con la fecha mal
    /// formada. Corregido eso, la primera petición responde bien a la primera.
    ///
    /// Lo que SÍ ocurre, medido: dos peticiones seguidas se ganan un <b>429</b>.
    /// Se vio repetidamente con `curl`, sin más. Es un límite de ritmo, no un
    /// calentamiento, y se nota también al barrer un mes: de cada diez
    /// peticiones, más o menos una vuelve con 429.
    ///
    /// Así que insistir tiene sentido por el 429, no por un 500 de apertura. Con
    /// esperas de 2, 4, 8, 16, 30 y 30 s son ~90 s en el peor caso, y en una
    /// semana son cinco peticiones seguidas donde alguna caerá.
    /// </summary>
    public const int IntentosPorDia = 6;

    /// <summary>
    /// Reintentos cuando la consulta NO la pidió nadie: el refresco automático.
    ///
    /// <para>
    /// Es menos a propósito, y por un motivo que se midió: una consulta en segundo
    /// plano retiene el candado de la semana mientras reintenta. Con seis
    /// intentos y esperas de 2 a 30 s, un día que falla puede retenerlo cuatro
    /// minutos, y quien esté mirando esa misma semana se queda esperando detrás
    /// sin poder ni cancelar.
    /// </para>
    ///
    /// <para>
    /// La diferencia entre las dos situaciones es que en una hay alguien mirando
    /// y en la otra no. Un refresco que falla no le cuesta nada a nadie: se pierde
    /// hasta el siguiente, cinco minutos después. En cambio, quitarle reintentos a
    /// quien PULSÓ "Actualizar" sí se nota, y ese es el que se los queda.
    /// </para>
    ///
    /// <para>
    /// Con tres intentos la espera máxima son 6 s por día, y el peor caso de una
    /// semana pasa de unos 20 minutos a unos 8. Sigue siendo historia para un
    /// proceso que nadie está mirando.
    /// </para>
    /// </summary>
    public const int IntentosPorDiaEnSegundoPlano = 3;

    /// <summary>Espera del primer reintento. Las siguientes son el doble, con tope.</summary>
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromSeconds(2);

    /// <summary>Tope de la espera entre reintentos.</summary>
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cuántos intentos se hacen al pedir el detalle de UNA licitación, contando
    /// el primero.
    ///
    /// SON MENOS QUE <see cref="IntentosPorDia"/> A PROPÓSITO, y el motivo es el
    /// reloj. Los días son el dato principal y pueden esperar 90 s; el detalle es
    /// un dato acessorio que solo va en la tarjeta. Con seis intentos y esperas
    /// de 2, 4, 8, 16, 30 y 30 s, una semana con varias tarjetas sin detalle se
    /// pasaría de los 120 s de IIS y se caería la página entera por un dato
    /// acessorio. Con tres intentos la espera máxima son 6 s por tarjeta.
    ///
    /// Antes de esto el detalle se pedía UNA vez y sin más. Como el 429 es un
    /// límite de ritmo y el detalle es la petición que va detrás de los días, es
    /// justo la que más cae: de cada diez detalles, más o menos uno se quedaba
    /// sin respuesta y la tarjeta salía sin organismo sin decir nada.
    /// </summary>
    public const int IntentosPorDetalle = 3;

    /// <summary>
    /// Espera antes del reintento número <paramref name="intento"/> del detalle.
    /// Doble cada vez, igual que los días, y con el mismo tope.
    /// </summary>
    public static TimeSpan EsperaDetalle(int intento) => TimeSpan.FromSeconds(Math.Min(
        EsperaInicial.TotalSeconds * Math.Pow(2, intento - 1),
        EsperaMaxima.TotalSeconds));

    public static IEndpointRouteBuilder MapLicitacionesEndpoints(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api").WithTags("Mercado Público");

        grupo.MapGet("/estado", ObtenerEstado);
        grupo.MapGet("/semana", ObtenerSemana);
        grupo.MapGet("/mes", ObtenerMes);
        grupo.MapGet("/licitaciones/{codigo}", ObtenerDetalle);
        grupo.MapPost("/refrescar", Refrescar);

        return rutas;
    }

    /// <summary>
    /// Estado de la configuración: qué empresa se mira y cada cuánto se refresca.
    /// La interfaz lo usa para la cabecera y para avisar de que falta el ticket o
    /// la empresa, en vez de dejar pulsar botones que no van a funcionar.
    ///
    /// OJO: se inyecta IOptions&lt;MercadoPublicoOpciones&gt;, NO la clase
    /// concreta. 'Configure&lt;T&gt;()' registra IOptions&lt;T&gt;, y un parámetro
    /// de tipo T en un minimal API no se reconoce como servicio: se infiere como
    /// cuerpo de la petición y la ruta peta al arrancar con "Body was inferred…",
    /// que tumba TODAS las rutas, incluida la SPA.
    /// </summary>
    /// <remarks>
    /// ES ASÍNCRONA POR LA EMPRESA, no por los datos. Antes leía todo de la
    /// configuración y podía ser <c>static</c>. Ahora tiene que preguntar a
    /// <see cref="EmpresaVigilada"/>, que va a la base de datos o a la API. No
    /// hace falta esperar de verdad —si ya está resuelta devuelve al instante—,
    /// pero la firma tiene que poder esperar porque hay un primer arranque en el
    /// que no lo está.
    /// </remarks>
    /// <param name="anio">Mes cuyas semanas se detalla. Por defecto, el actual.</param>
    /// <param name="mes">Mes cuyas semanas se detalla. Por defecto, el actual.</param>
    private static async Task<IResult> ObtenerEstado(
        IOptions<MercadoPublicoOpciones> opciones,
        EmpresaVigilada empresa,
        [FromQuery] int? anio = null,
        [FromQuery] int? mes = null,
        CancellationToken ct = default)
    {
        var config = opciones.Value;
        var hoy = DateOnly.FromDateTime(DateTime.Today);

        await empresa.ResolverAsync(ct);

        // El periodo pedido, ya saneado. Un año o un mes que no existen se
        // sustituyen por el actual en vez de propagar el error: un dato raro en
        // una ruta no puede ser razón de que la pantalla se quede en blanco.
        var anioPedido = anio is >= 1 and <= 9999 ? anio.Value : hoy.Year;
        var mesPedido = mes is >= 1 and <= 12 ? mes.Value : hoy.Month;

        return Results.Ok(new
        {
            // En modo demo no hace falta ticket: los datos son inventados.
            TicketConfigurado = config.Modo == "demo" || config.TieneTicket,
            EmpresaConfigurada = config.Modo == "demo" || empresa.TieneCodigoProveedor,
            Servible = config.Modo == "demo" || empresa.TieneCodigoProveedor,
            Modo = config.Modo,

            /* POR QUÉ "Servible" ES "QUE HAYA EMPRESA" Y NO LO DE MERCADOPUBLICO.Opciones.
               Antes, Servible miraba la configuración entera. Ahora lo que decide
               si se puede consultar es si se sabe a quién se consulta, y eso ya
               no está en la configuración: sale de MpEmpresa. La propiedad
               antigua se queda en las opciones para lo que sí depende solo de
               ellas, y aquí no se usa.

               Fijate en que NO aparece BaseDeDatosUtilizable: una conexión que
               falla y una empresa que no se encuentra son dos cosas distintas y
               el aviso tiene que decir cuál de las dos es. Eso lo cuenta
               EmpresaVigilada.Motivo, que lo sabe porque lo ha intentado. */

            // La fuente, y lo que se puede hacer con ella. El cliente lo
            // necesita para dos cosas: esconder el selector de semana en modo
            // base de datos, y no ofrecer "Dejar de esperar" cuando la consulta
            // es a un SQL Server local.
            //
            // BaseDeDatosUtilizable va aparte de UsaBaseDeDatos a propósito. Con
            // fuente "sql" y sin cadena de conexión, la pantalla tiene que
            // poder decir "falta configurar", no quedarse en blanco esperando.
            Fuente = config.Fuente,
            UsaBaseDeDatos = config.UsaBaseDeDatos,
            BaseDeDatosUtilizable = config.BaseDeDatosUtilizable,

            // El motivo, YA EN CASTELLANO Y EN UNA FRASE, porque aquí es donde se
            // sabe de verdad. Antes lo armaba el cliente con tres banderas
            // binarias y por eso solo podía decir dos cosas: "falta el ticket" o
            // "falta el código de proveedor". Con la empresa viniendo de la base
            // hay más motivos que eso y ninguno se parece a los otros:
            //
            //     - la tabla MpEmpresa está vacía para ese RUT
            //     - mp.LeeEmpresa no está instalado en la base
            //     - el RUT del appsettings no tiene el formato que acepta la API
            //     - el RUT devuelve más de una empresa y no se sabe cuál
            //
            // Y son cuatro arreglos distintos. Decir "falta el código" cuatro
            // veces sería un aviso que no dice nada, que es exactamente el
            // problema que había.
            //
            // Y EL `??` DE AQUÍ NO ES COSMÉTICO. EmpresaVigilada devuelve Motivo
            // nulo en un solo caso: que se esté resolviendo ahora mismo y esta
            // petición haya llegado antes. Es una carrera, dura milisegundos y
            // la siguiente petición la ve resuelta. Pero mientras dura, el
            // cliente recibía un motivo vacío y pintaba esto:
            //
            //     "En esta pantalla , así que no se puede consultar nada."
            //
            // con un hueco donde debería estar la explicación. Un aviso con un
            // hueco se lee como "no hay nada que explicar", que no es lo mismo
            // que "no lo sé", y quien lo lee no tiene nada con que empezar.
            FaltaConfiguracion = empresa.FaltaAlgo
                ? empresa.Motivo ?? "la empresa todavía no se ha podido determinar. Espera un segundo y vuelve a recargar."
                : null,

            Empresa = new
            {
                NombreEmpresa = empresa.Actual?.NombreEmpresa ?? "",
                RutEmpresa = empresa.Actual?.RutEmpresa ?? "",
                CodigoProveedor = empresa.Actual?.CodigoProveedor ?? "",

                // El enlace de la cabecera. Viaja vacío si la URL no es
                // utilizable, para que el cliente no tenga que decidir: pintar
                // un enlace es cosa del servidor, que es quien sabe si la URL
                // sirve.
                UrlMercadoPublico = empresa.Actual is { } e && e.TieneUrl
                    ? e.UrlMercadoPublico
                    : "",
            },

            // Lo que se está mirando, para que la cabecera lo diga.
            Anio = hoy.Year,
            Mes = hoy.Month,
            Semana = SemanasDelMes.SemanaDe(hoy.Year, hoy.Month, hoy),
            Fecha = $"{hoy:yyyy-MM-dd}",
            FechaLegible = EtiquetaDe(hoy),
            EsFinDeSemana = EsFinDeSemana(hoy),

            // Qué se puede elegir en los desplegables. Se calculan en el servidor
            // y no en el cliente para que los tres desplegables digan siempre lo
            // mismo: si el cliente calculara el número de semanas por su cuenta,
            // un cambio de formato en un sitio y no en el otro mostraría
            // semanas que no existen.
            AniosDisponibles = SemanasDelMes.Anios(hoy.Year),
            MesesDisponibles = MesesVisibles(hoy, anioPedido),

            // Los rangos de las semanas del mes pedido. Van aquí, y no en el
            // cliente, para que el cálculo de "de lunes a domingo" exista en UN
            // solo sitio. El cliente los pinta; no los calcula. Antes estaba
            // copiado en los dos lados, que es una forma segura de que un día
            // se desincronicen sin que nada avise.
            Semanas = DescribirSemanas(anioPedido, mesPedido, hoy),

            MinutosEntreRefrescos = config.MinutosRefresco,

            // Solo en demo: el RUT de ejemplo, para probar el flujo.
            RutDemo = config.Modo == "demo" ? DatosDemo.RutDemo : null,
        });
    }

    /// <summary>
    /// Rango y días hábiles de cada semana de un mes, para pintar el desplegable.
    ///
    /// El cliente recibe los datos ya hechos y no los calcula: es la única forma
    /// de que no haya dos copias de la regla "de lunes a domingo" que acaben
    /// discrepando sin que nada se note.
    /// </summary>
    /// <summary>
    /// Una semana del mes, tal como se la pasa al cliente para el desplegable.
    ///
    /// Es un tipo con nombre y no un <c>object</c> anónimo a propósito: sin él
    /// esta función no se puede probar, porque un tipo anónimo no se puede
    /// nombrar desde el proyecto de pruebas. Y lo que hay que probar aquí es
    /// justamente el CORTE de las semanas futuras.
    /// </summary>
    internal sealed record SemanaDescrita(
        int Numero,
        string Desde,
        string Hasta,
        string Texto,
        int DiasHabiles);

    internal static List<SemanaDescrita> DescribirSemanas(int anio, int mes, DateOnly hoy)
    {
        var resultado = new List<SemanaDescrita>();
        var total = SemanasDelMes.Cuantas(anio, mes);

        for (var s = 1; s <= total; s++)
        {
            var (desde, hasta) = SemanasDelMes.Rango(anio, mes, s);

            // Una semana que todavía NO HA EMPEZADO no se ofrece. Elegirla
            // gastaba una llamada al servidor para obtener semanas enteras
            // marcadas como pendientes, y devolvía una pantalla vacía que
            // parecía un fallo. La semana en curso sí se ofrece, y se muestra
            // con los días que faltan sin consultar.
            if (desde > hoy) break;

            resultado.Add(new SemanaDescrita(
                s,
                $"{desde:yyyy-MM-dd}",
                $"{hasta:yyyy-MM-dd}",

                // El texto YA escrito de cara al desplegable: "23 al 28 de
                // febrero". Viaja desde aquí para que el cliente no tenga que
                // nombrar meses: el cliente ya tuvo un array de días ordenado
                // al revés y por eso salió un día corrido en todas las fechas.
                TextosDeFecha.Rango(desde, hasta),

                SemanasDelMes.DiasHabiles(anio, mes, s).Count));
        }

        return resultado;
    }

    /// <summary>
    /// Los meses que se pueden elegir de un año, sin llegar al futuro.
    ///
    /// Un año pasado ofrece los doce. El año en curso, solo hasta el mes de
    /// hoy: en octubre no tiene sentido noviembre ni diciembre, y proponerlos
    /// llevaba a pantallas vacías.
    ///
    /// Un año futuro no ofrece ninguno, que es el caso que hace que la
    /// petición sea absurda y no una simple molestia.
    /// </summary>
    internal static List<string> MesesVisibles(DateOnly hoy, int anio)
    {
        if (anio < hoy.Year) return [.. CalendarioDelMes.TodosLosMeses()];

        if (anio > hoy.Year) return [];

        return CalendarioDelMes.TodosLosMeses().Take(hoy.Month).ToList();
    }

    // =====================================================================
    // Licitaciones de UNA SEMANA
    // =====================================================================

    /// <summary>
    /// Las licitaciones de una semana dentro de un mes: "semana 1 de septiembre
    /// de 2026".
    ///
    /// Los tres filtros van CONCATENADOS a propósito. La alternativa —filtrar
    /// solo por año— no es viable: la API devuelve UN día por consulta, así que
    /// un año son 261 peticiones y casi una hora, medido. Encadenando los tres,
    /// el rango máximo es una semana: cinco días hábiles, cinco peticiones, y
    /// se responden en menos de un minuto.
    ///
    /// <paramref name="refrescar"/> solo ignora la caché del DÍA DE HOY. Los
    /// días pasados no se vuelven a preguntar porque no cambian: repreguntarlos
    /// gastaría cupo para devolver exactamente lo mismo.
    /// </summary>
    /// Los tres van como [FromQuery] explícito porque son tipos simples: sin el
    /// atributo, el enlazador minimal API los busca en la RUTA —que no los tiene
    /// — y responde 400 con el cuerpo vacío. Un error que no dice nada.
    /// </summary>
    ///
    /// Van AL FINAL a propósito: C# exige que los parámetros opcionales vayan
    /// detrás de los obligatorios, y los servicios de DI no pueden llevar
    /// valores por defecto —dárselos los ocultaría una falta de registro.
    /// <summary>
    /// Licitaciones de UN MES, desde la base de datos.
    /// </summary>
    ///
    /// <para>
    /// La otra mitad del interruptor de <c>FuenteDatos</c>. Devuelve lo mismo
    /// que <c>/api/semana</c> pero cambia la unidad de la semana al mes, para
    /// que la lectura contra la base sea una consulta y no cinco seguidas.
    /// </para>
    ///
    /// <para>
    /// Y DEVUELVE TAMBIÉN LOS DÍAS QUE NO SE PUDIERON COMPROBAR, con su motivo.
    /// No es un añadido: es lo que impide que la pantalla diga "no hay nada"
    /// cuando en realidad no se preguntó. Pasó de verdad el 8 de octubre de
    /// 2026, cuando tres días se importaron con un ticket caducado: sin esto,
    /// ese mes habría salido vacío y con la etiqueta de "no se publicó nada".
    /// </para>
    private static async Task<IResult> ObtenerMes(
        LectorMercadoPublico lector,
        IOptions<MercadoPublicoOpciones> opciones,
        EmpresaVigilada empresa,
        ILoggerFactory registros,
        CancellationToken ct,
        [FromQuery] int anio,
        [FromQuery] int mes)
    {
        var config = opciones.Value;
        var log = registros.CreateLogger("Mes");

        await empresa.ResolverAsync(ct);

        if (config.UsaBaseDeDatos && !config.BaseDeDatosUtilizable)
            return Results.Problem(
                "La fuente de datos es la base de datos pero falta la cadena de "
                + "conexion en MercadoPublico__CadenaConexionSql.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Base de datos sin configurar");

        if (anio is < 1900 or > 9999 || mes is < 1 or > 12)
            return Results.BadRequest(new { error = "El anio o el mes no son validos." });

        try
        {
            var leido = await lector.LeerMesAsync(empresa.CodigoProveedor, anio, mes, ct);

            return Results.Ok(new
            {
                Anio = leido.Anio,
                Mes = leido.Mes,
                DiasHabiles = leido.DiasHabiles,
                DiasConsultados = leido.DiasConsultados,
                DiasFallidos = leido.DiasFallidos,
                DiasPendientes = leido.DiasPendientes,

                // Los dos juntos: la lista para el mensaje y el diccionario
                // para poder decir QUE paso con cada uno. Con solo los nombres
                // no se puede distinguir "no habia nada" de "el ticket caduco",
                // que es justo lo que el usuario necesita para decidir si
                // insistir tiene sentido.
                DiasSinRespuesta = leido.DiasSinRespuesta
                    .Select(d => $"{d.Fecha:yyyy-MM-dd}").ToList(),
                MotivosSinRespuesta = leido.DiasSinRespuesta
                    .ToDictionary(d => $"{d.Fecha:yyyy-MM-dd}", d => d.Motivo),

                Total = leido.Items.Count,
                Items = leido.Items.Select(ConTextoDePublicacion).ToList(),
                DesdeCache = false,
                Periodo = TextosDeFecha.PeriodoDelMes(leido.Anio, leido.Mes),
                /* El nombre del mes CON ANO, y con mayuscula:
                   "el mes de Junio de 2026".

                   De CalendarioDelMes.NombreMes y no de TextosDeFecha.NombreMes,
                   porque el segundo lo devuelve en minuscula: va dentro de frases
                   como "Del 1 al 30 de junio de 2026", donde en minuscula es lo
                   correcto. Un titulo de panel empieza con mayuscula.

                   Y lo compone el servidor, no el cliente: el nombre del mes no
                   esta en ningun sitio del cliente salvo el desplegable, y ese
                   llega filtrado al mes en curso. Depender de el para escribir
                   un texto es la clase de acoplamiento que ya rompio una vez el
                   array de dias. */
                Consultado = leido.Consultado,
                PeriodoDelMes = $"el mes de {CalendarioDelMes.NombreMes(leido.Mes)} de {leido.Anio}",
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un fallo leyendo la base NO es "no hay licitaciones". Se responde
            // con un error y se dice en el log, porque una lista vacia por no
            // haber podido leer es la misma mentira que la del listado por API.
            log.LogError(ex, "No se pudo leer el mes {Anio}-{Mes:00} de la base", anio, mes);

            return Results.Json(
                new { error = "No se pudo leer la base de datos." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ObtenerSemana(
        MercadoPublicoCliente api,
        CacheMercadoPublico cache,
        IOptions<MercadoPublicoOpciones> opciones,
        EmpresaVigilada empresa,
        ILoggerFactory registros,
        CancellationToken ct,
        [FromQuery] int anio,
        [FromQuery] int mes,
        [FromQuery] int semana,
        [FromQuery] bool refrescar = false,
        [FromQuery] bool fondo = false)
    {
        var config = opciones.Value;
        var log = registros.CreateLogger("Semana");

        await empresa.ResolverAsync(ct);

        /* El mensaje sale de EmpresaVigilada y no de tres banderas de la
           configuración. Antes podía decir dos cosas —"falta el ticket" o "falta
           el código"—, y ahora hay motivos que no son ninguno de esos dos: que
           la tabla MpEmpresa esté vacía, que mp.LeeEmpresa no esté instalado en
           el hosting, que el RUT no tenga el formato, o que devuelva más de una
           empresa. Todos se resuelven con una acción distinta, así que un
           mensaje único para los cuatro dejaría a quien lo lee sin saber qué
           hacer. */
        if (config.Modo != "demo" && !empresa.TieneCodigoProveedor)
            return Results.Problem(
                empresa.Motivo ?? "No se sabe a qué empresa se está mirando.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "API no configurada");

        if (anio is < 2015 or > 2100 || mes is < 1 or > 12)
            return Results.BadRequest(new { error = "El año o el mes no son válidos." });

        var totalSemanas = SemanasDelMes.Cuantas(anio, mes);
        if (semana < 1 || semana > totalSemanas)
            return Results.BadRequest(new
            {
                error = $"El mes {mes:00}/{anio} no tiene semana {semana}. Tiene {totalSemanas}.",
            });

        var dias = SemanasDelMes.DiasHabiles(anio, mes, semana);
        var hoy = DateOnly.FromDateTime(DateTime.Today);

        // Días que YA EXISTEN: los pasados y el de hoy. Los siguientes se
        // separan aquí y no se consultan.
        //
        // No es una optimización, es un bug caro: la API no tiene nada que
        // devolver de un día que aún no ha llegado, así que responde 500 y el
        // cliente insiste seis veces con esperas de 2 a 30 s. Una semana que
        // empieza en el día 4 del mes tiene tres días futuros, y eso son cuatro
        // minutos y medio de espera para nada. Además, contarlos como "fallidos"
        // sería mentira: no falló nada, es que todavía no hay día.
        var consultables = dias.Where(d => d <= hoy).ToList();
        var pendientes = dias.Where(d => d > hoy).ToList();

        if (consultables.Count == 0)
            return Results.Ok(ConstruirRespuesta(
                anio, mes, semana, 0, [], [], pendientes.Count, desdeCache: true));

        try
        {
            // El candado es único y sigue siéndolo. Se probó ponerlo por semana,
            // para que dos personas mirando semanas distintas no se estorbaran, y
            // la medición salió al revés: dos consultas en paralelo tardaron 23,5 s
            // donde en serie tardan 13,2 s. Mercado Público no avisa con un 429,
            // se limita callando. Ver el comentario del campo en la caché.

            // Cuánto se espera por el candado antes de rendirse. Antes se esperaba
            // sin límite, y el que llegaba segundo se quedaba ahí sin explicación
            // hasta que el primero acababa: hasta dos minutos de pantalla quieta,
            // contando como si fuera una consulta lenta.
            //
            // El límite es de 30 s, que es más que una semana en frío medida desde
            // MonsterASP (13 s para cinco días). Pasado ese tiempo, mejor un
            // mensaje claro que un silencio.
            var esperaCandidata = TimeSpan.FromSeconds(30);

            if (!await cache.Candado.WaitAsync(esperaCandidata, ct))
            {
                log.LogInformation(
                    "La semana {Anio}-{Mes:00}-{Semana} ya se estaba consultando y no se esperó",
                    anio, mes, semana);

                return Results.Json(
                    new
                    {
                        error = "Esa misma semana se está consultando ahora mismo. " +
                                "Espera unos segundos y vuelve a intentarlo.",
                        anio, mes, semana,
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                var todas = new List<Licitacion>();
                var sinRespuesta = new List<string>();
                var desdeCache = true;

                // El refresco automático se conforma con menos reintentos. Ver
                // IntentosPorDiaEnSegundoPlano: en segundo plano no hay nadie
                // esperando, y retener el candado cuatro minutos sí se nota.
                var intentos = fondo ? IntentosPorDiaEnSegundoPlano : IntentosPorDia;

                foreach (var dia in consultables)
                {
                    var forzarConsulta = refrescar && dia == hoy;

                    // Un fallo que se acaba de producir NO vuelve a preguntarse
                    // enseguida. El motivo está medido: el día que fallaba costaba
                    // 60 de los 65 segundos de cada consulta, no porque la API
                    // tardara, sino porque las esperas entre reintentos suman
                    // 2+4+8+16+30 s y ese día fallaba siempre y deprisa. Como el
                    // fallo no se guardaba en ninguna parte, la escalera entera se
                    // subía otra vez en cada petición, para siempre.
                    //
                    // Da igual que hoy sea el día que falla o uno pasado: el
                    // plazo es el mismo. La escalera entera se paga igual.
                    //
                    // Y el botón "Actualizar" sí salta el plazo, con
                    // refrescar = true. Es la única manera de hacerlo y por eso
                    // no puede quedarse sin efecto justo en el día que más
                    // urge reintentar.
                    if (!refrescar && cache.DiaFallidoReciente(empresa.CodigoProveedor, dia))
                    {
                        // Ojo con lo que esto NO es: el día se sigue anotando como
                        // sin respuesta, igual que si se hubiera preguntado. Y
                        // desdeCache NO se toca, porque aquí no se consultó nada.
                        // Recordar un fallo no autoriza a decir que no hay nada.
                        sinRespuesta.Add($"{dia:yyyy-MM-dd}");
                        log.LogInformation(
                            "El día {Dia} falló hace poco y no se vuelve a preguntar hasta que pase el plazo",
                            dia);
                        continue;
                    }

                    var lote = forzarConsulta ? null : cache.ObtenerDia(empresa.CodigoProveedor, dia);

                    if (lote is null)
                    {
                        desdeCache = false;

                        lote = await ConsultarDiaAsync(config, api, cache, empresa, dia, log, intentos, ct);

                        if (lote is null)
                        {
                            // Un día que falla NO tira el resto de la semana. Antes
                            // sí lo hacía, y un solo fallo dejaba la semana entera
                            // en error.
                            sinRespuesta.Add($"{dia:yyyy-MM-dd}");

                            // Y el fallo se guarda, que es lo que hace que la
                            // próxima consulta no vuelva a pagar los 60 s.
                            cache.GuardarDiaFallido(empresa.CodigoProveedor, dia);

                            log.LogWarning("El día {Dia} de la semana no se pudo consultar", dia);
                            continue;
                        }
                    }

                    todas.AddRange(lote);
                }

                // Si NO se pudo comprobar ningún día que existiera, sí es un fallo entero: no
                // se devuelve una lista vacía por no haber preguntado.
                if (sinRespuesta.Count == consultables.Count)
                {
                    log.LogWarning(
                        "No se pudo consultar ningún día de la semana {Anio}-{Mes:00}-{Semana}",
                        anio, mes, semana);

                    return Results.Json(
                        new
                        {
                            error = "Mercado Público no respondió. Se intentó varias veces durante " +
                                    "un minuto. Puede estar con problemas en este momento; " +
                                    "vuelve a intentarlo en unos segundos.",
                            anio, mes, semana,
                        },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                var ordenadas = AplicarOrden(todas);

                // El organismo comprador solo viene en el detalle, no en el
                // listado. Va en la tarjeta, así que hay que traerlo aquí.
                await AdjuntarDetallesAsync(ordenadas, api, cache, config, log, ct);

                return Results.Ok(ConstruirRespuesta(
                    anio, mes, semana, consultables.Count, sinRespuesta, ordenadas,
                    pendientes.Count, desdeCache));
            }
            finally
            {
                cache.Candado.Release();
            }
        }
        catch (MercadoPublicoException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.CodigoHttp);
        }
    }

    /// <summary>
    /// Pide el detalle de cada licitación para poder pintar el organismo
    /// comprador en la tarjeta.
    ///
    /// COSTE: una consulta a la API por licitación que no esté ya en caché. El
    /// listado diario solo trae cuatro campos —código, nombre, estado y fecha de
    /// cierre—, así que no hay manera de sacarlo de ahí. Para una semana son las
    /// mismas licitaciones que se están mostrando, que suelen ser cero o una.
    ///
    /// UN DETALLE QUE FALLA NO TIRA LA SEMANA. Es la misma regla que se aplica a
    /// los días: si un día falla, se marca y se sigue con los demás. Aquí si un
    /// detalle falla, esa tarjeta se queda sin organismo y las demás salen
    /// igual. La alternativa —fallar la semana entera— perdería datos que sí se
    /// pudieron traer, y sería mentir por un dato acessorio.
    ///
    /// INSISTE, y antes no lo hacía. El detalle se pedía una sola vez, sin
    /// reintentos, mientras que los días insisten hasta seis. Con el 429 medido
    /// —de cada diez peticiones, más o menos una— el detalle era la petición con
    /// más probabilidades de caerse, porque es la que va detrás de los días, y
    /// encima caerse no se veía: la tarjeta salía sin la línea del organismo y
    /// sin decir por qué. Ahora son <see cref="IntentosPorDetalle"/> intentos con
    /// espera creciente.
    ///
    /// Lo que NO se reintenta es la cancelación: si el navegador se fue o el
    /// servidor se para, no tiene sentido seguir. Un <c>TaskCanceledException</c>
    /// con el token propio sin cancelar sí se reintenta, porque ese es el
    /// timeout de la petición y no una orden de parar.
    ///
    /// Se pide de uno en uno, sin paralelismo: pedirlos a la vez dispara el
    /// límite de ritmo, que es el <c>429</c> del que ya se habla en el README.
    /// </summary>
    private static async Task AdjuntarDetallesAsync(
        List<Licitacion> items,
        MercadoPublicoCliente api,
        CacheMercadoPublico cache,
        MercadoPublicoOpciones config,
        ILogger log,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (item.Detalle is not null) continue;
            if (string.IsNullOrWhiteSpace(item.CodigoExterno)) continue;

            var cacheado = cache.ObtenerDetalle(item.CodigoExterno, out var hayCache);
            if (hayCache)
            {
                item.Detalle = cacheado;
                continue;
            }

            for (var intento = 1; ; intento++)
            {
                try
                {
                    var detalle = config.Modo == "demo"
                        ? DatosDemo.Detalle(item.CodigoExterno)
                        : await api.ObtenerDetalleAsync(item.CodigoExterno, ct);

                    // Se cachea también el "no hay detalle": si la API dice que no
                    // existe, no se vuelve a preguntar en cada carga.
                    cache.GuardarDetalle(item.CodigoExterno, detalle);
                    item.Detalle = detalle;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // El navegador se fue o el servidor se para: no se reintenta.
                    throw;
                }
                catch (Exception ex) when (ex is MercadoPublicoException
                                           or TaskCanceledException
                                           or HttpRequestException)
                {
                    if (intento >= IntentosPorDetalle)
                    {
                        log.LogWarning(
                            ex,
                            "Se agotaron {Intentos} intentos para el detalle de {Codigo}; " +
                            "la tarjeta saldrá sin organismo: {Motivo}",
                            IntentosPorDetalle, item.CodigoExterno, ex.Message);
                        break;
                    }

                    var espera = EsperaDetalle(intento);

                    log.LogInformation(
                        "Detalle de {Codigo} intento {Intento} de {Total} fallido ({Motivo}). " +
                        "Reintento en {Espera:F0} s.",
                        item.CodigoExterno, intento, IntentosPorDetalle, ex.Message, espera.TotalSeconds);

                    await Task.Delay(espera, ct);
                }
            }
        }
    }

    /// <summary>
    /// Respuesta de una semana. Un tipo explícito en vez de un objeto anónimo
    /// compuesto: el mensaje se armaba "con la respuesta vacía y luego se le
    /// añadían campos", y con <c>object</c> no se pueden leer. Además obliga a
    /// que todas las respuestas —venga vacía o no— lleven los mismos campos.
    /// </summary>
    private sealed record RespuestaSemana(
        int Anio,
        int Mes,
        int Semana,
        string Desde,
        string Hasta,
        int DiasHabiles,
        int DiasConsultados,
        int DiasFallidos,
        List<string> DiasSinRespuesta,
        int DiasPendientes,
        int Total,
        List<Licitacion> Items,
        bool DesdeCache,

        /// <summary>
        /// El periodo ya escrito: "Del 23 de febrero al 28 de febrero".
        ///
        /// Viaja escrito para que el cliente no componga fechas. Es lo que se
        /// pinta junto al contador de novedades.
        /// </summary>
        string Periodo,

        DateTimeOffset Consultado);

    private static RespuestaSemana ConstruirRespuesta(
        int anio,
        int mes,
        int semana,
        int diasConsultados,
        List<string> sinRespuesta,
        List<Licitacion> items,
        int diasPendientes,
        bool desdeCache)
    {
        // El rango se muestra completo, fines de semana incluidos: el lunes y el
        // domingo de la semana son los dos extremos de lo que se está mirando,
        // aunque solo se consulten los días hábiles de en medio.
        var (desde, hasta) = SemanasDelMes.Rango(anio, mes, semana);

        return new RespuestaSemana(
            anio,
            mes,
            semana,
            $"{desde:yyyy-MM-dd}",
            $"{hasta:yyyy-MM-dd}",
            diasConsultados + diasPendientes,
            diasConsultados,
            sinRespuesta.Count,
            sinRespuesta,
            diasPendientes,
            items.Count,
            items.Select(AñadirDetalleCache).Select(ConTextoDePublicacion).ToList(),
            desdeCache,
            TextosDeFecha.Periodo(desde, hasta),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Le pone a cada licitación el texto de su día de publicación: "viernes 27
    /// de febrero".
    ///
    /// Se hace aquí, en un solo punto, y no en los tres sitios donde se arma la
    /// licitación: el cliente lo compunía por su cuenta con su propio array de
    /// días, y por eso una vez un viernes salió pintado como "sábado".
    ///
    /// Va DESPUÉS de <see cref="AñadirDetalleCache"/> porque esta copia la
    /// licitación: si se invirtiera el orden, el texto se perdería.
    /// </summary>
    private static Licitacion ConTextoDePublicacion(Licitacion l)
    {
        // OJO: en la clase de la LISTA, FechaPublicacion es DateOnly?, no
        // DateTimeOffset? como en el detalle. Por eso aquí no hay ninguna
        // conversión: si algún día cambian los tipos, esto deja de compilar en
        // vez de fallar en pantalla.
        if (l.FechaPublicacion is not { } fecha) return l;

        l.PublicadoTexto = TextosDeFecha.DiaEnPalabras(fecha);
        return l;
    }

    // =====================================================================
    // Detalle de una licitación
    // =====================================================================

    private static async Task<IResult> ObtenerDetalle(
        string codigo,
        MercadoPublicoCliente api,
        LectorMercadoPublico lector,
        CacheMercadoPublico cache,
        IOptions<MercadoPublicoOpciones> opciones,
        EmpresaVigilada empresa,
        CancellationToken ct)
    {
        var config = opciones.Value;

        await empresa.ResolverAsync(ct);

        if (config.Modo != "demo" && !empresa.TieneCodigoProveedor)
            return Results.Problem(
                empresa.Motivo ?? "No se sabe a qué empresa se está mirando.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "API no configurada");

        if (string.IsNullOrWhiteSpace(codigo))
            return Results.BadRequest(new { error = "Falta el código de licitación." });

        // Si ya se pidió, no se vuelve a pedir: el detalle es de los pocos datos
        // que sí cambian la pantalla, así que no vale la pena repetirlo.
        var detalle = cache.ObtenerDetalle(codigo, out var hayCache);
        if (hayCache)
        {
            return Results.Ok(new
            {
                codigo,
                detalle,
                desdeCache = true,
            });
        }

        try
        {
            if (config.UsaBaseDeDatos)
            {
                // La ficha también sale de la base, por mp.LeeDetalle. NO es un
                // detalle menor: dejarlo solo en la ruta de la API hacía que
                // abrir una ficha en modo base de datos dijera "Mercado Público
                // no devolvió el detalle" en una instalación sin ticket, con la
                // ficha ahí al lado en el listado.
                //
                // El 404 es un resultado honesto y no un error: de esa licitacion
                // no se ha importado la ficha todavía.
                detalle = await lector.LeerDetalleAsync(empresa.CodigoProveedor, codigo, ct);

                if (detalle is null)
                    return Results.NotFound(new
                    {
                        error = "Esa licitación no está importada en la base de datos.",
                    });
            }
            else if (config.Modo == "demo")
            {
                detalle = DatosDemo.Detalle(codigo);
            }
            else
            {
                detalle = await api.ObtenerDetalleAsync(codigo, ct);
            }

            // Se cachea también el "no hay detalle": si la API dice que no
            // existe, no se vuelve a preguntar en cada apertura de la ficha.
            cache.GuardarDetalle(codigo, detalle);

            return Results.Ok(new
            {
                codigo,
                detalle,
                desdeCache = false,
            });
        }
        catch (MercadoPublicoException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.CodigoHttp);
        }
    }

    // =====================================================================
    // Utilidades
    // =====================================================================

    /// <summary>
    /// Consulta UN día insistiendo hasta que la API responda.
    ///
    /// Devuelve null si se agotaron los intentos; no lanza, para que quien llama
    /// pueda seguir con los demás días y marcar solo este como desconocido.
    ///
    /// La espera crece por dos motivos: un 500 puntual se recupera solo con
    /// esperar, y ante un 429 el problema es el ritmo, no un fallo concreto. El
    /// tope de 30 s evita que una espera se alargue hasta perder el sentido.
    ///
    /// Antes esta rutina se llamaba ConsultarHoyAsync y solo había un día. Ahora
    /// hay hasta cinco en una semana, y por eso devuelve null en vez de Fallar:
    /// un día sin respuesta no puede ser motivo para perder los otros cuatro.
    ///
    /// El código de proveedor entra con <paramref name="empresa"/> y no sale de
    /// <paramref name="config"/>, porque ya no está en la configuración. Se pasa
    /// el resolvedor entero y no el string porque el que llama ya lo tiene, y
    /// porque un string suelto se podría cambiar por error sin que nadie lo vea.
    /// </summary>
    private static async Task<List<Licitacion>?> ConsultarDiaAsync(
        MercadoPublicoOpciones config,
        MercadoPublicoCliente api,
        CacheMercadoPublico cache,
        EmpresaVigilada empresa,
        DateOnly dia,
        ILogger log,
        int intentos,
        CancellationToken ct)
    {
        var espera = EsperaInicial;

        for (var intento = 1; ; intento++)
        {
            try
            {
                var lote = config.Modo == "demo"
                    ? DatosDemo.Dia(dia)
                    : await api.ListarLicitacionesDelDiaAsync(empresa.CodigoProveedor, dia, ct);

                cache.GuardarDia(empresa.CodigoProveedor, dia, lote);
                return lote;
            }
            catch (OperationCanceledException)
            {
                // El navegador se fue o el servidor se para: no se reintenta.
                throw;
            }
            catch (MercadoPublicoException ex)
            {
                if (intento >= intentos)
                {
                    log.LogWarning(
                        "Se agotaron {Intentos} intentos para el {Dia}: {Motivo}",
                        intentos, $"{dia:yyyy-MM-dd}", ex.Message);
                    return null;
                }

                log.LogInformation(
                    "{Dia} intento {Intento} de {Total} fallido ({Motivo}). Reintento en {Espera:F0} s.",
                    $"{dia:yyyy-MM-dd}", intento, intentos, ex.Message, espera.TotalSeconds);

                await Task.Delay(espera, ct);

                // Crece al doble, con tope.
                espera = TimeSpan.FromSeconds(Math.Min(espera.TotalSeconds * 2, EsperaMaxima.TotalSeconds));
            }
        }
    }

    /// <summary>Si el día es sábado o domingo.</summary>
    private static bool EsFinDeSemana(DateOnly dia) =>
        dia.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>"3 de Octubre de 2026".</summary>
    private static string EtiquetaDe(DateOnly dia) =>
        $"{dia.Day} de {CalendarioDelMes.NombreMes(dia.Month)} de {dia.Year}";

    /// <summary>
    /// Si la ficha ya trae el detalle cacheado, lo viaja con ella: abrirla otra
    /// vez no muestra el icono de cargando.
    /// </summary>
    private static Licitacion AñadirDetalleCache(Licitacion l) =>
        new()
        {
            CodigoExterno = l.CodigoExterno,
            Nombre = l.Nombre,
            CodigoEstado = l.CodigoEstado,
            FechaCierre = l.FechaCierre,
            FechaPublicacion = l.FechaPublicacion,
            Detalle = l.Detalle,
        };

    /// <summary>Vacía la caché del proveedor para forzar consulta nueva.</summary>
    /// <remarks>
    /// <c>EmpresaVigilada</c> entra aquí por el código de proveedor, que ya no
    /// está en la configuración. No hace falta <c>ResolverAsync</c>: con la
    /// empresa sin resolver el código sale vacío, y vaciar la caché de un
    /// proveedor que no existe es lo mismo que no hacer nada.
    /// </remarks>
    private static IResult Refrescar(CacheMercadoPublico cache, EmpresaVigilada empresa)
    {
        cache.Invalidar(empresa.CodigoProveedor);
        return Results.Ok(new { mensaje = "Caché actualizada." });
    }

    /// <summary>
    /// Ordena el listado: primero lo que sigue vigente, por fecha de cierre
    /// ascendente; lo ya vencido al final.
    ///
    /// Ordenar solo por fecha ascendente mete lo vencido PRIMERO, porque su
    /// fecha es la más antigua, y la pantalla se abre con un muro de "Cerró
    /// hace 20 días". Lo primero, que es lo que se mira, tiene que enseñar lo
    /// que todavía está a tiempo.
    /// </summary>
    private static List<Licitacion> AplicarOrden(List<Licitacion> datos) =>
        datos
            .OrderBy(l => YaVencio(l) ? 1 : 0)
            .ThenBy(l => l.FechaCierre ?? DateTimeOffset.MaxValue)
            .ThenBy(l => l.Nombre, StringComparer.CurrentCulture)
            .ToList();

    private static bool YaVencio(Licitacion l) =>
        l.FechaCierre is { } cierre && cierre.Date < DateTime.Today;
}
