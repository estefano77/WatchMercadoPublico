using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;
using WatchMercadoPublico.Server.Services;

namespace WatchMercadoPublico.Server.Endpoints;

/// <summary>
/// Endpoints que consume la aplicación Blazor.
///
/// La pantalla es una sola: las licitaciones de HOY de una empresa fija. No hay
/// selector de día, ni de mes, ni filtro de palabras, ni búsqueda por RUT. Todo
/// eso se quitó porque cada opción era una forma de equivocarse y ninguna
/// ayudaba a responder la única pregunta que se hace: ¿ha salido algo hoy?
///
/// Aquí se hace el trabajo que la API no hace: insistir hasta que responda,
/// ordenar lo que llega, y no inventar una lista vacía cuando falla.
/// </summary>
public static class LicitacionesEndpoints
{
    /// <summary>
    /// Cuántos intentos se hacen, contando el primero.
    ///
    /// Mucho más alto que antes, a propósito. Mercado Público rechaza las
    /// primeras ~10-15 peticiones de cada sesión con 500 y 429; con dos
    /// intentos se acumulaban los "No se pudo consultar ese día". Ahora la
    /// consulta es automática y al cargar la página —nadie está esperando un
    /// clic— así que insistir un minuto no le cuesta nada al usuario.
    ///
    /// Con esperas de 2, 4, 8, 16, 30 y 30 s son ~90 s en el peor caso.
    /// </summary>
    private const int IntentosPorDia = 6;

    /// <summary>Espera del primer reintento. Las siguientes son el doble, con tope.</summary>
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromSeconds(2);

    /// <summary>Tope de la espera entre reintentos.</summary>
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapLicitacionesEndpoints(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api").WithTags("Mercado Público");

        grupo.MapGet("/estado", ObtenerEstado);
        grupo.MapGet("/semana", ObtenerSemana);
        grupo.MapGet("/licitaciones/{codigo}", ObtenerDetalle);
        grupo.MapPost("/refrescar", Refrescar);

        return rutas;
    }

    /// <summary>
    /// Estado de la configuración: qué empresa se mira y cada cuánto se refresca.
    /// La interfaz lo usa para la cabecera y para avisar de que falta el ticket o
    /// el código, en vez de dejar pulsar botones que no van a funcionar.
    ///
    /// OJO: se inyecta IOptions&lt;MercadoPublicoOpciones&gt;, NO la clase
    /// concreta. 'Configure&lt;T&gt;()' registra IOptions&lt;T&gt;, y un parámetro
    /// de tipo T en un minimal API no se reconoce como servicio: se infiere como
    /// cuerpo de la petición y la ruta peta al arrancar con "Body was inferred…",
    /// que tumba TODAS las rutas, incluida la SPA.
    /// </summary>
    /// <param name="anio">Mes cuyas semanas se detalla. Por defecto, el actual.</param>
    /// <param name="mes">Mes cuyas semanas se detalla. Por defecto, el actual.</param>
    private static IResult ObtenerEstado(
        IOptions<MercadoPublicoOpciones> opciones,
        [FromQuery] int? anio = null,
        [FromQuery] int? mes = null)
    {
        var config = opciones.Value;
        var hoy = DateOnly.FromDateTime(DateTime.Today);

        return Results.Ok(new
        {
            // En modo demo no hace falta ticket: los datos son inventados.
            TicketConfigurado = config.Modo == "demo" || config.TieneTicket,
            EmpresaConfigurada = config.Modo == "demo" || config.TieneCodigoProveedor,
            Servible = config.Servible,
            Modo = config.Modo,

            Empresa = new
            {
                config.NombreEmpresa,
                config.RutEmpresa,
                config.CodigoProveedor,
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
            MesesDisponibles = CalendarioDelMes.TodosLosMeses(),

            // Los rangos de las semanas del mes pedido. Van aquí, y no en el
            // cliente, para que el cálculo de "de lunes a domingo" exista en UN
            // solo sitio. El cliente los pinta; no los calcula. Antes estaba
            // copiado en los dos lados, que es una forma segura de que un día
            // se desincronicen sin que nada avise.
            Semanas = DescribirSemanas(
                anio is >= 1 and <= 9999 ? anio.Value : hoy.Year,
                mes is >= 1 and <= 12 ? mes.Value : hoy.Month),

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
    private static List<object> DescribirSemanas(int anio, int mes)
    {
        var resultado = new List<object>();
        var total = SemanasDelMes.Cuantas(anio, mes);

        for (var s = 1; s <= total; s++)
        {
            var (desde, hasta) = SemanasDelMes.Rango(anio, mes, s);

            resultado.Add(new
            {
                Numero = s,
                Desde = $"{desde:yyyy-MM-dd}",
                Hasta = $"{hasta:yyyy-MM-dd}",
                DiasHabiles = SemanasDelMes.DiasHabiles(anio, mes, s).Count,
            });
        }

        return resultado;
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
    ///
    /// Van AL FINAL a propósito: C# exige que los parámetros opcionales vayan
    /// detrás de los obligatorios, y los servicios de DI no pueden llevar
    /// valores por defecto —dárselos los ocultaría una falta de registro.
    private static async Task<IResult> ObtenerSemana(
        MercadoPublicoCliente api,
        CacheMercadoPublico cache,
        IOptions<MercadoPublicoOpciones> opciones,
        ILoggerFactory registros,
        CancellationToken ct,
        [FromQuery] int anio,
        [FromQuery] int mes,
        [FromQuery] int semana,
        [FromQuery] bool refrescar = false)
    {
        var config = opciones.Value;
        var log = registros.CreateLogger("Semana");

        if (!config.Servible)
            return Results.Problem(
                config.TieneTicket
                    ? "Falta el código de proveedor de la empresa en la configuración del servidor."
                    : "Falta el ticket de Mercado Público en la configuración del servidor.",
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
            // El candado evita que dos pestañas consulten lo mismo a la vez. Con
            // reintentos de hasta un minuto, esperar aquí es preferible a
            // machacar la API entre las dos.
            await cache.Candado.WaitAsync(ct);
            try
            {
                var todas = new List<Licitacion>();
                var sinRespuesta = new List<string>();
                var desdeCache = true;

                foreach (var dia in consultables)
                {
                    var forzarConsulta = refrescar && dia == hoy;

                    var lote = forzarConsulta ? null : cache.ObtenerDia(config.CodigoProveedor, dia);

                    if (lote is null)
                    {
                        desdeCache = false;

                        lote = await ConsultarDiaAsync(config, api, cache, dia, log, ct);

                        if (lote is null)
                        {
                            // Un día que falla NO tira el resto de la semana. Antes
                            // sí lo hacía, y un solo fallo dejaba la semana entera
                            // en error.
                            sinRespuesta.Add($"{dia:yyyy-MM-dd}");
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
            items.Select(AñadirDetalleCache).ToList(),
            desdeCache,
            DateTimeOffset.UtcNow);
    }

    // =====================================================================
    // Detalle de una licitación
    // =====================================================================

    private static async Task<IResult> ObtenerDetalle(
        string codigo,
        MercadoPublicoCliente api,
        CacheMercadoPublico cache,
        IOptions<MercadoPublicoOpciones> opciones,
        CancellationToken ct)
    {
        var config = opciones.Value;

        if (!config.Servible)
            return Results.Problem(
                "Falta la configuración de Mercado Público en el servidor.",
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
            if (config.Modo == "demo")
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
    /// </summary>
    private static async Task<List<Licitacion>?> ConsultarDiaAsync(
        MercadoPublicoOpciones config,
        MercadoPublicoCliente api,
        CacheMercadoPublico cache,
        DateOnly dia,
        ILogger log,
        CancellationToken ct)
    {
        var espera = EsperaInicial;

        for (var intento = 1; ; intento++)
        {
            try
            {
                var lote = config.Modo == "demo"
                    ? DatosDemo.Dia(dia)
                    : await api.ListarLicitacionesDelDiaAsync(config.CodigoProveedor, dia, ct);

                cache.GuardarDia(config.CodigoProveedor, dia, lote);
                return lote;
            }
            catch (OperationCanceledException)
            {
                // El navegador se fue o el servidor se para: no se reintenta.
                throw;
            }
            catch (MercadoPublicoException ex)
            {
                if (intento >= IntentosPorDia)
                {
                    log.LogWarning(
                        "Se agotaron {Intentos} intentos para el {Dia}: {Motivo}",
                        IntentosPorDia, $"{dia:yyyy-MM-dd}", ex.Message);
                    return null;
                }

                log.LogInformation(
                    "{Dia} intento {Intento} de {Total} fallido ({Motivo}). Reintento en {Espera:F0} s.",
                    $"{dia:yyyy-MM-dd}", intento, IntentosPorDia, ex.Message, espera.TotalSeconds);

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
    private static IResult Refrescar(CacheMercadoPublico cache, IOptions<MercadoPublicoOpciones> opciones)
    {
        cache.Invalidar(opciones.Value.CodigoProveedor);
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