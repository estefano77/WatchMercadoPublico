using System.Globalization;
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
        grupo.MapGet("/hoy", ObtenerHoy);
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
    private static IResult ObtenerEstado(IOptions<MercadoPublicoOpciones> opciones)
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

            // El día que se está mirando, para que la cabecera lo diga y quede
            // claro que no hay nada más que elegir.
            Fecha = $"{hoy:yyyy-MM-dd}",
            FechaLegible = EtiquetaDe(hoy),
            EsFinDeSemana = EsFinDeSemana(hoy),

            MinutosEntreRefrescos = config.MinutosRefresco,

            // Solo en demo: el RUT de ejemplo, para probar el flujo.
            RutDemo = config.Modo == "demo" ? DatosDemo.RutDemo : null,
        });
    }

    // =====================================================================
    // Licitaciones de HOY
    // =====================================================================

    private static async Task<IResult> ObtenerHoy(
        MercadoPublicoCliente api,
        CacheMercadoPublico cache,
        IOptions<MercadoPublicoOpciones> opciones,
        ILoggerFactory registros,
        CancellationToken ct)
    {
        var config = opciones.Value;
        var log = registros.CreateLogger("Hoy");

        if (!config.Servible)
            return Results.Problem(
                config.TieneTicket
                    ? "Falta el código de proveedor de la empresa en la configuración del servidor."
                    : "Falta el ticket de Mercado Público en la configuración del servidor.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "API no configurada");

        var hoy = DateOnly.FromDateTime(DateTime.Today);

        try
        {
            // El candado evita que dos pestañas abiertas disparen la misma
            // consulta. Con reintentos de hasta un minuto, esperar aquí es
            // preferible a machacar la API entre las dos.
            await cache.Candado.WaitAsync(ct);
            try
            {
                var todas = cache.ObtenerDia(config.CodigoProveedor, hoy);
                var desdeCache = todas is not null;

                if (todas is null)
                {
                    todas = await ConsultarHoyAsync(config, api, cache, hoy, log, ct);

                    if (todas is null)
                    {
                        // No se devuelve una lista vacía: se devuelve el fallo.
                        // Decir "0 licitaciones" cuando no se pudo preguntar es
                        // mentir, y el usuario no tiene forma de enterarse.
                        log.LogWarning(
                            "No se pudo consultar el día de hoy para {Proveedor}", config.CodigoProveedor);

                        return Results.Json(
                            new
                            {
                                error = "Mercado Público no respondió. Se intentó varias veces durante " +
                                        "un minuto. Puede estar con problemas en este momento; " +
                                        "vuelve a intentarlo en unos segundos.",
                                fecha = $"{hoy:yyyy-MM-dd}",
                            },
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                var ordenadas = AplicarOrden(todas);

                return Results.Ok(new
                {
                    items = ordenadas.Select(AñadirDetalleCache).ToList(),
                    total = ordenadas.Count,
                    fecha = $"{hoy:yyyy-MM-dd}",
                    fechaLegible = EtiquetaDe(hoy),
                    esFinDeSemana = EsFinDeSemana(hoy),
                    desdeCache,
                    consultado = DateTimeOffset.UtcNow,
                });
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
    /// Consulta el día de HOY insistiendo hasta que la API responda.
    ///
    /// Devuelve null si se agotaron los intentos; no lanza, para que el endpoint
    /// pueda responder con un error en vez de con una lista vacía.
    ///
    /// La espera crece por dos motivos: un 500 puntual se recupera solo con
    /// esperar, y ante un 429 el problema es el ritmo, no un fallo concreto. El
    /// tope de 30 s evita que una espera se alargue hasta perder el sentido.
    /// </summary>
    private static async Task<List<Licitacion>?> ConsultarHoyAsync(
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
                        "Se agotaron {Intentos} intentos para el día de hoy: {Motivo}",
                        IntentosPorDia, ex.Message);
                    return null;
                }

                log.LogInformation(
                    "Intento {Intento} de {Total} fallido ({Motivo}). Reintento en {Espera:F0} s.",
                    intento, IntentosPorDia, ex.Message, espera.TotalSeconds);

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