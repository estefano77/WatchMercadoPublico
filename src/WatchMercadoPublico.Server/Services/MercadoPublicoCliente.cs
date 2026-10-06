using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;

namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Cliente de la API de Mercado Público. Vive SOLO en el servidor: el ticket es
/// una credencial y el cliente Blazor (WebAssembly) se descarga entero, con el
/// ticket dentro del .dll si se intentara.
/// </summary>
public sealed class MercadoPublicoCliente
{
    private const string Base = "https://api.mercadopublico.cl/servicios/v1";

    /// <summary>
    /// Propiedades del JSON tal como las manda la API: <c>CodigoExterno</c>,
    /// <c>FechaCierre</c>… System.Text.Json solo empareja por defecto los
    /// nombres EXACTOS, así que sin esto no se leería ni un campo.
    /// </summary>
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient http;
    private readonly MercadoPublicoOpciones opciones;
    private readonly ILogger<MercadoPublicoCliente> log;

    /// <summary>
    /// El ritmo de salida a la API. Vive en un singleton aparte porque si estuviera
    /// aquí, que es de ámbito por petición, cada petición HTTP tendría el suyo y
    /// dos a la vez no se limitarían entre sí. Ver <see cref="RitmoDeLlamadas"/>.
    /// </summary>
    private readonly RitmoDeLlamadas ritmo;

    public MercadoPublicoCliente(
        IHttpClientFactory factory,
        IOptions<MercadoPublicoOpciones> opciones,
        ILogger<MercadoPublicoCliente> log,
        RitmoDeLlamadas ritmo)
    {
        this.http = factory.CreateClient("mercadopublico");
        this.opciones = opciones.Value;
        this.log = log;
        this.ritmo = ritmo;

        this.http.BaseAddress = new Uri(Base + "/");
        this.http.Timeout = TimeSpan.FromSeconds(Math.Max(5, opciones.Value.SegundosTimeout));
    }

    // 1) Listado de un día, filtrado por proveedor
    //    /publico/licitaciones.json?fecha=DDMMAAAA&CodigoProveedor=…&ticket=…
    // =====================================================================

    /// <summary>
    /// Licitaciones publicadas en <paramref name="fecha"/> que tienen que ver con
    /// ese proveedor.
    ///
    /// Importante, medido contra la API: el filtro <c>CodigoProveedor</c> SOLO
    /// funciona cuando se manda <c>fecha</c>. Con <c>estado=activas</c> se
    /// ignora y devuelve el total del país (unos 4.600) con cualquier código,
    /// incluso uno inexistente.
    /// </summary>
    public async Task<List<Licitacion>> ListarLicitacionesDelDiaAsync(
        string codigoProveedor, DateOnly fecha, CancellationToken ct)
    {
        var url = ConstruirUrlDia(codigoProveedor, fecha, opciones.Ticket);

        using var doc = await LeerJsonAsync(url, ct);

        var lista = new List<Licitacion>();
        if (!doc.RootElement.TryGetProperty("Listado", out var listado) ||
            listado.ValueKind != JsonValueKind.Array)
            return lista;

        foreach (var item in listado.EnumerateArray())
        {
            lista.Add(new Licitacion
            {
                CodigoExterno = Leer(item, "CodigoExterno", "CodigoLicitacion"),
                Nombre = Leer(item, "Nombre"),
                CodigoEstado = LeerEntero(item, "CodigoEstado"),
                FechaCierre = LeerFecha(item, "FechaCierre"),
                FechaPublicacion = fecha,
            });
        }

        return lista;
    }

    // =====================================================================
    // 2) Detalle de una licitación
    //    /publico/licitaciones.json?codigo=…&ticket=…
    // =====================================================================

    /// <summary>
    /// Detalle completo de una licitación. Trae organismo comprador, fechas de
    /// cada etapa, monto estimado y adjudicación; el listado diario solo da
    /// cuatro campos.
    /// </summary>
    public async Task<DetalleLicitacion?> ObtenerDetalleAsync(string codigo, CancellationToken ct)
    {
        var url = $"publico/licitaciones.json" +
                  $"?codigo={Uri.EscapeDataString(codigo)}" +
                  $"&ticket={Uri.EscapeDataString(opciones.Ticket)}";

        using var doc = await LeerJsonAsync(url, ct);

        if (!doc.RootElement.TryGetProperty("Listado", out var listado) ||
            listado.ValueKind != JsonValueKind.Array ||
            listado.GetArrayLength() == 0)
            return null;

        var item = listado[0];

        // Comprador es un objeto con CodigoOrganismo, NombreOrganismo, Region…;
        // las fechas viven anidadas en "Fechas"; la adjudicación en "Adjudicacion".
        var fechas = item.TryGetProperty("Fechas", out var f) ? f : default;
        var adjudicacion = item.TryGetProperty("Adjudicacion", out var a) ? a : default;
        var items = item.TryGetProperty("Items", out var it) ? it : default;

        var detalle = new DetalleLicitacion
        {
            CodigoExterno = Leer(item, "CodigoExterno", "CodigoLicitacion"),
            Nombre = Leer(item, "Nombre"),
            Estado = Leer(item, "Estado"),
            CodigoEstado = LeerEntero(item, "CodigoEstado"),
            Descripcion = Leer(item, "Descripcion"),
            Tipo = Leer(item, "Tipo"),
            NombreOrganismo = LeerRuta(item, "Comprador", "NombreOrganismo"),
            RutOrganismo = LeerRuta(item, "Comprador", "RutUnidad"),
            CodigoOrganismo = LeerRutaEntero(item, "Comprador", "CodigoOrganismo"),
            RegionOrganismo = Limpiar(LeerRuta(item, "Comprador", "RegionUnidad")),
            ComunaOrganismo = Limpiar(LeerRuta(item, "Comprador", "ComunaUnidad")),
            MontoEstimado = LeerDecimal(item, "MontoEstimado"),
            Moneda = Leer(item, "Moneda"),
            Estimacion = LeerEntero(item, "Estimacion"),
            DiasCierreLicitacion = LeerEntero(item, "DiasCierreLicitacion"),
            NumeroOferentes = LeerRutaEntero(adjudicacion, "NumeroOferentes"),
            NumeroAdjudicacion = LeerRuta(adjudicacion, "Numero"),
            UrlActa = LeerRuta(adjudicacion, "UrlActa"),
            NumeroItems = LeerEntero(items, "Cantidad"),
            FechaCreacion = LeerFecha(fechas, "FechaCreacion"),
            FechaCierre = LeerFecha(fechas, "FechaCierre"),
            FechaPublicacion = LeerFecha(fechas, "FechaPublicacion"),
            FechaAperturaTecnica = LeerFecha(fechas, "FechaActoAperturaTecnica"),
            FechaAperturaEconomica = LeerFecha(fechas, "FechaActoAperturaEconomica"),
            FechaAdjudicacion = LeerFecha(adjudicacion, "Fecha"),
            FechaFinal = LeerFecha(fechas, "FechaFinal"),
            Items = LeerItems(items),
        };

        // La lista diaria trae FechaCierre en la raíz; el detalle lo trae anidado
        // y a veces vacío. Se usa el de la raíz como respaldo.
        if (detalle.FechaCierre is null)
            detalle.FechaCierre = LeerFecha(item, "FechaCierre");

        return detalle;
    }

    /// <summary>
    /// Lee los ítems adjudicados de <c>Items.Listado</c>.
    ///
    /// El monto unitario NO está en el <c>Adjudicacion</c> de primer nivel (ese
    /// es el acta: fecha, número, oferentes y enlace al documento), sino dentro
    /// del <c>Adjudicacion</c> de cada ítem. Es el dato que dice cuánto se
    /// contrató de verdad, y solo existe si la licitación está adjudicada.
    ///
    /// Se recorren todos los ítems, no solo el primero: una licitación puede
    /// tener varios productos y cada uno con su proveedor y su precio.
    /// </summary>
    private static List<ItemAdjudicado> LeerItems(JsonElement items)
    {
        var resultado = new List<ItemAdjudicado>();

        if (items.ValueKind != JsonValueKind.Object) return resultado;
        if (!items.TryGetProperty("Listado", out var listado)) return resultado;
        if (listado.ValueKind != JsonValueKind.Array) return resultado;

        foreach (var item in listado.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var adjudicacion = item.TryGetProperty("Adjudicacion", out var a) ? a : default;

            // Un ítem sin monto no aporta nada al total y solo llenaría la tabla
            // de ruido, así que se descarta.
            var unitario = LeerDecimal(adjudicacion, "MontoUnitario");
            if (unitario is null) continue;

            resultado.Add(new ItemAdjudicado
            {
                Correlativo = LeerEntero(item, "Correlativo") ?? resultado.Count + 1,
                NombreProducto = Limpiar(Leer(item, "NombreProducto")),
                UnidadMedida = Limpiar(Leer(item, "UnidadMedida")),
                Cantidad = LeerDecimal(item, "Cantidad"),
                CantidadAdjudicada = LeerDecimal(adjudicacion, "Cantidad"),
                MontoUnitario = unitario,
                RutProveedor = Limpiar(Leer(adjudicacion, "RutProveedor")),
                NombreProveedor = Limpiar(Leer(adjudicacion, "NombreProveedor")),
            });
        }

        return resultado;
    }

    // Utilidades puras, sin red: lo que se puede testear de verdad
    // =====================================================================

    /// <summary>
    /// URL del listado de un día.
    ///
    /// OJO con el formato de la fecha: la API pide <c>DDMMAAAA</c> con **ambos**
    /// campos rellenos. En .NET, <c>"d"</c> es el día SIN cero a la izquierda y
    /// <c>"dd"</c> el día CON cero. Con <c>"dMMyyyy"</c> el 4 de octubre se
    /// mandaba <c>4102026</c> —siete dígitos— y la API respondía 500 con
    /// <c>{"Codigo":10300,"Mensaje":"El formato del parametro fechas es incorrecto"}</c>.
    ///
    /// Lo que lo escondía es que <c>"d"</c> y <c>"dd"</c> solo se diferencian en
    /// los días 1 al 9: a partir del 10 el día ya tiene dos cifras y el fallo
    /// desaparece solo. Por eso parecía intermitente, y por qué no salió nunca
    /// en las fechas de prueba (28/09/2026 era día 28).
    ///
    /// El <c>InvariantCulture</c> no es adorno: sin él, <c>"y"</c> se interpreta
    /// con el <b>calendario de la cultura actual</c>, y hay culturas cuyo
    /// calendario no es el gregoriano —"ar-SA" usa el Umm al-Qura— de modo que la
    /// misma línea habría enviado un año equivocado según el idioma del servidor.
    ///
    /// Está en un método aparte y no en línea porque es justo el sitio donde un
    /// cambio de formato pasa desapercibido: nada en el código ni en la
    /// compilación avisa de que <c>"dMMyyyy"</c> vuelve a ser un bug. Lo
    /// comprueba <c>FormatoDeFechaTests</c> para los 365 días.
    /// </summary>
    internal static string ConstruirUrlDia(string codigoProveedor, DateOnly fecha, string ticket) =>
        "publico/licitaciones.json" +
        $"?fecha={fecha.ToString("ddMMyyyy", CultureInfo.InvariantCulture)}" +
        $"&CodigoProveedor={Uri.EscapeDataString(codigoProveedor)}" +
        $"&ticket={Uri.EscapeDataString(ticket)}";

    /// <summary>
    /// Traduce una respuesta que no es 2xx en una excepción con un mensaje que
    /// dice la verdad.
    ///
    /// La API responde sus errores en español: <c>{"Codigo":10300,"Mensaje":"…"}</c>.
    /// Antes solo se leía <c>error.message</c>, <c>Descripcion</c> y
    /// <c>Message</c>, así que un <c>{"Codigo":…,"Mensaje":…}</c> se perdía
    /// entero: el log ponía <c>(null)</c> y la pantalla culpaba a Mercado Público
    /// de estar con problemas cuando el fallo era de la petición que le
    /// mandábamos. Leer <c>Mensaje</c> es lo que permitió encontrar eso.
    /// </summary>
    internal static MercadoPublicoException ConstruirError(
        HttpStatusCode estado, string cuerpo, ILogger log)
    {
        string? mensaje = null;
        int codigoApi = 0;

        try
        {
            using var doc = JsonDocument.Parse(cuerpo);

            mensaje = LeerRuta(doc.RootElement, "error", "message")
                      ?? Leer(doc.RootElement, "Mensaje")
                      ?? Leer(doc.RootElement, "Descripcion")
                      ?? Leer(doc.RootElement, "Message");

            codigoApi = LeerEntero(doc.RootElement, "Codigo") ?? 0;
        }
        catch (JsonException)
        {
            mensaje = cuerpo.Length > 0 ? cuerpo[..Math.Min(300, cuerpo.Length)] : null;
        }

        var codigo = (int)estado;

        var explicacion = codigo switch
        {
            401 or 403 =>
                "El ticket no es válido, está inactivo o se agotó su cupo diario. Revísalo en Mercado Público.",
            429 =>
                "Mercado Público está limitando las peticiones. Espera un momento e inténtalo de nuevo.",
            // El 500 de esta API casi nunca es un problema de la API: lo que
            // llega es una queja sobre lo que le hemos mandado (una fecha mal
            // formada, un parámetro que no existe). Decir "está con problemas"
            // manda a mirar donde no está el fallo, así que se prioriza su
            // mensaje sobre nuestra interpretación del código.
            >= 500 when !string.IsNullOrWhiteSpace(mensaje) =>
                $"Mercado Público rechazó la consulta: {mensaje.Trim()}",
            >= 500 =>
                "Mercado Público está con problemas en este momento.",
            _ => "No se pudo completar la consulta a Mercado Público.",
        };

        if (!string.IsNullOrWhiteSpace(mensaje) && codigo < 500)
            explicacion += $" ({mensaje.Trim()})";

        if (codigoApi > 0)
            log.LogWarning(
                "Mercado Público respondió {Codigo} (código propio {CodigoApi}): {Detalle}",
                codigo, codigoApi, mensaje);
        else
            log.LogWarning("Mercado Público respondió {Codigo}: {Detalle}", codigo, mensaje);

        return new MercadoPublicoException(
            explicacion, null, codigo is 401 or 403 ? 502 : codigo);
    }

    // Lectura del JSON y errores
    // =====================================================================

    /// <summary>
/// Acompasa las peticiones a la API de Mercado Público.
///
/// <para>
/// Sale de medir el límite de verdad, con peticiones sin ticket para no gastar
/// cupo. Doce seguidas, con la aplicación sin espera propia:
///
/// </para>
///
/// <list type="bullet">
/// <item>Sin pausa: <c>203 429 203 429 203 429 429…</c>, o sea una de cada
/// tres</item>
/// <item>400 ms: casi todas 429</item>
/// <item>800 ms: la mitad 429</item>
/// <item>1500 ms: diez de doce bien</item>
/// </list>
///
/// <para>
/// Traducido: la API admite del orden de <b>una petición cada 1,5 s</b>. Antes no
/// había nada que la gobernara, y una semana son cinco días más los detalles de
/// cada licitación, todos seguidos. De ahí los 429 de los logs.
/// </para>
///
/// <para>
/// Lo que se mide es el intervalo entre el <b>principio</b> de una petición y el
/// principio de la siguiente, y no una espera después de cada respuesta. La
/// diferencia importa: contra la API de verdad las respuestas tardan 1,4 a 1,6 s,
/// así que el intervalo ya se cumple solo y la espera sale a coste cero. Solo se
/// paga cuando una respuesta vuelve más rápida de lo debido.
/// </para>
///
/// <para>
/// Va aquí y no en los bucles de días y de detalles por dos razones: hay un solo
/// sitio por el que sale todo, y así ningún bucle nuevo puede olvidarse de la
/// pausa.
/// </para>
/// </para>
/// </summary>
private async Task<JsonDocument> LeerJsonAsync(string urlRelativa, CancellationToken ct)
    {
        await ritmo.PedirTurnoAsync(ct);

        try
        {
            using var respuesta = await http.GetAsync(urlRelativa, ct);
            var cuerpo = await respuesta.Content.ReadAsStringAsync(ct);

            if (!respuesta.IsSuccessStatusCode)
                throw ConstruirError(respuesta.StatusCode, cuerpo, log);

            try
            {
                return JsonDocument.Parse(cuerpo);
            }
            catch (JsonException ex)
            {
                log.LogError("Mercado Público devolvió un JSON ilegible: {Error}", ex.Message);
                throw new MercadoPublicoException(
                    "Mercado Público devolvió una respuesta que no se pudo leer.", ex, 502);
            }
        }
        finally
        {
            // Siempre: si la respuesta falla, el turno se queda bloqueado para
            // siempre y ninguna petición más sale nunca.
            ritmo.Liberar();
        }
    }

    /// <summary>
    /// Primer valor encontrado entre varias CLAVES ALTERNATIVAS del mismo
    /// objeto.
    ///
    /// Para bajar por una jerarquía (<c>error → message</c>,
    /// <c>Comprador → NombreOrganismo</c>) está <see cref="LeerRuta"/>.
    /// Confundir ambas es un fallo silencioso: con una ruta,
    /// ("CodigoExterno","CodigoLicitacion") buscaría un "CodigoLicitacion"
    /// DENTRO de "CodigoExterno", no encontraría nada, y como la lista se
    /// deduplicaba por ese campo, las 4.631 licitaciones se descartaban todas
    /// con HTTP 200 y lista vacía.
    /// </summary>
    private static string? Leer(JsonElement el, params string[] claves)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        foreach (var clave in claves)
        {
            if (el.TryGetProperty(clave, out var valor))
            {
                var texto = ComoTexto(valor);
                if (texto is not null) return texto;
            }
        }

        return null;
    }

    /// <summary>Recorre un camino de propiedades anidadas.</summary>
    private static string? LeerRuta(JsonElement el, params string[] ruta)
    {
        var actual = el;
        foreach (var clave in ruta)
        {
            if (actual.ValueKind != JsonValueKind.Object) return null;
            if (!actual.TryGetProperty(clave, out actual)) return null;
        }

        return ComoTexto(actual);
    }

    private static string? ComoTexto(JsonElement valor) => valor.ValueKind switch
    {
        JsonValueKind.String => Limpiar(valor.GetString()),
        JsonValueKind.Number => valor.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object when valor.TryGetProperty("nombre", out var n)
                              && n.ValueKind == JsonValueKind.String => Limpiar(n.GetString()),
        _ => null,
    };

    private static int? LeerEntero(JsonElement el, params string[] claves) =>
        int.TryParse(Leer(el, claves), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    private static int? LeerRutaEntero(JsonElement el, params string[] ruta) =>
        int.TryParse(LeerRuta(el, ruta), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    /// <summary>
    /// Lee un decimal.
    ///
    /// OJO con el formato: el JSON de Mercado Público trae los montos como
    /// NÚMERO JSON con punto decimal —"MontoEstimado": 192000000.0— así que
    /// llegan como JsonValueKind.Number y se leen con GetDecimal(), que es
    /// exacto y no interpretable.
    ///
    /// Antes se pasaba todo por una cadena y se quitaban los puntos "porque en
    /// Chile son separador de miles". Con un número JSON eso convertía
    /// 192000000.0 en 1920000000: el precio salía **diez veces más alto**, y
    /// "Cantidad": 1.0 en 10. Solo tiene sentido normalizar cuando el valor
    /// viene como string, y ahí se prueban las dos convenciones antes de
    /// quitar nada.
    /// </summary>
    private static decimal? LeerDecimal(JsonElement el, params string[] claves)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        foreach (var clave in claves)
        {
            if (!el.TryGetProperty(clave, out var valor)) continue;

            // Número JSON: se lee tal cual, sin pasar por texto.
            if (valor.ValueKind == JsonValueKind.Number)
                return valor.TryGetDecimal(out var numero) ? numero : null;

            var texto = ComoTexto(valor);
            if (string.IsNullOrWhiteSpace(texto)) continue;

            if (decimal.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var directo))
                return directo;

            // String con formato chileno: "1.234.567,89".
            var normalizado = texto.Replace(".", string.Empty).Replace(",", ".");
            if (decimal.TryParse(normalizado, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;

            continue;
        }

        return null;
    }

    private static DateTimeOffset? LeerFecha(JsonElement el, params string[] claves)
    {
        var texto = Leer(el, claves);
        if (string.IsNullOrWhiteSpace(texto)) return null;

        return DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out var valor)
            ? valor.ToLocalTime()
            : null;
    }

    /// <summary>La API manda strings con espacios sobrantes.</summary>
    private static string? Limpiar(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}

/// <summary>Fallo hablando con Mercado Público, con un mensaje en español.</summary>
public sealed class MercadoPublicoException : Exception
{
    public MercadoPublicoException(string mensaje, Exception? interna = null, int codigoHttp = 502)
        : base(mensaje, interna)
        => CodigoHttp = codigoHttp;

    /// <summary>Código con el que responder al navegador.</summary>
    public int CodigoHttp { get; }
}
