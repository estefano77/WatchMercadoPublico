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

    /// <summary>
    /// Pausa entre peticiones cuando Mercado Público acaba de limitar el ritmo.
    ///
    /// <para>
    /// Sale de un log real de MonsterASP, del 6 de octubre de 2026. Una semana de
    /// cinco días va bien al principio y el QUINTO día responde:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>200 en 1570 ms</item>
    /// <item>200 en 1370 ms</item>
    /// <item>200 en 1008 ms</item>
    /// <item>200 en 612 ms</item>
    /// <item><b>429</b> en 280 ms: "Hemos detectado que existen peticiones
    /// simultáneas", código 10500</item>
    /// </list>
    ///
    /// <para>
    /// Los días ya iban de uno en uno, no en paralelo: lo que se disparaba era el
    /// RITMO, cinco peticiones en menos de cinco segundos. Por eso la pausa no es
    /// fija sino <b>adaptativa</b>: si un día entró al primer intento, no se pausa
    /// nada y la semana se consulta tan rápido como siempre. Si un día necesitó
    /// reintentar, es que la API está pidiendo más calma, y se espera antes del
    /// siguiente.
    /// </para>
    ///
    /// <para>
    /// Una pausa fija entre todos los días costaría más de 3 s en cada semana en
    /// frío, para algo que solo pasa de vez en cuando. Así se paga únicamente
    /// cuando hay un 429 de por medio, que es cuando de verdad hace falta.
    /// </para>
    /// </summary>
    private static readonly TimeSpan PausaTrasLimite = TimeSpan.FromMilliseconds(800);

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

        // El periodo pedido, ya saneado. Un año o un mes que no existen se
        // sustituyen por el actual en vez de propagar el error: un dato raro en
        // una ruta no puede ser razón de que la pantalla se quede en blanco.
        var anioPedido = anio is >= 1 and <= 9999 ? anio.Value : hoy.Year;
        var mesPedido = mes is >= 1 and <= 12 ? mes.Value : hoy.Month;

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

                // El enlace de la cabecera, ya escrito desde la configuración.
                // Viaja vacío si la URL no es utilizable, para que el cliente no
                // tenga que decidir: pintar un enlace es cosa del servidor, que
                // es quien sabe si la URL es válida.
                UrlMercadoPublico = config.TieneUrlMercadoPublico
                    ? config.UrlMercadoPublico
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
        [FromQuery] bool refrescar = false,
        [FromQuery] bool fondo = false)
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

                    var lote = forzarConsulta ? null : cache.ObtenerDia(config.CodigoProveedor, dia);

                    // 1 significa "entró al primer intento", o sea que este día vino de caché y no
                    // hubo ninguna petición que enviar. La pausa adaptativa de más
                    // abajo lo lee, y por eso vive fuera del if.
                    var intentosDia = 1;

                    if (lote is null)
                    {
                        desdeCache = false;

                        var (loteDia, intentosUsados) =
                            await ConsultarDiaAsync(config, api, cache, dia, log, intentos, ct);

                        lote = loteDia;
                        intentosDia = intentosUsados;

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

                    // Pausa ADAPTATIVA entre días. Si este día entró al primer
                    // intento, no se pausa nada. Si necesitó reintentar, es que la
                    // API acaba de decir que el ritmo va rápido, y el siguiente
                    // día espera un poco. Solo si queda otro día por delante.
                    if (intentosDia > 1 && dia != consultables[^1])
                        await Task.Delay(PausaTrasLimite, ct);
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

                    // Misma pausa adaptativa que en los días. Una semana puede
                    // traer más licitaciones que días, así que aquí es donde más
                    // veces se encadena una petición detrás de otra, y donde más
                    // fácil es que la API conteste con un 429.
                    if (intento > 1) await Task.Delay(PausaTrasLimite, ct);

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
    private static async Task<(List<Licitacion>? Datos, int Intentos)> ConsultarDiaAsync(
        MercadoPublicoOpciones config,
        MercadoPublicoCliente api,
        CacheMercadoPublico cache,
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
                    : await api.ListarLicitacionesDelDiaAsync(config.CodigoProveedor, dia, ct);

                cache.GuardarDia(config.CodigoProveedor, dia, lote);
                return (lote, intento);
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
                    return (null, intento);
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