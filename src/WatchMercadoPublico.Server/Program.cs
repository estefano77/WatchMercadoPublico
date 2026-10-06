using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using WatchMercadoPublico.Server.Endpoints;
using WatchMercadoPublico.Server.Models;
using WatchMercadoPublico.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuración
// ---------------------------------------------------------------------------

builder.Services.Configure<MercadoPublicoOpciones>(
    builder.Configuration.GetSection(MercadoPublicoOpciones.Seccion));

// El cliente de Mercado Público no usa "Bearer": el ticket viaja en la URL (v1)
// o en una cabecera (Compra Ágil), tal como lo exige cada endpoint.
builder.Services.AddHttpClient("mercadopublico", cliente =>
{
    cliente.DefaultRequestHeaders.UserAgent.ParseAdd("WatchMercadoPublico/1.0");
    cliente.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});

builder.Services.AddCacheMercadoPublico(builder.Configuration);

// OJO: el cliente tiene que estar REGISTRADO. Sin esta línea, un minimal API no
// lo reconoce como servicio y lo infiere como cuerpo de la petición, y la ruta
// peta al arrancar con "Body was inferred but the method does not allow inferred
// body parameters". El síntoma engaña: el error menciona el parámetro, no la
// llamada a AddScoped que falta, y tumba TODAS las rutas, incluida la SPA.
builder.Services.AddScoped<MercadoPublicoCliente>();

builder.Services.ConfigureHttpJsonOptions(opciones =>
{
    // El cliente Blazor usa los mismos nombres en C# y en el JSON, así que la
    // correspondencia se hace sin atributos: es el contrato interno de la app.
    opciones.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    opciones.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Comprobaciones al arrancar: fallar en voz alta, no en silencio
// ---------------------------------------------------------------------------

var opciones = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<MercadoPublicoOpciones>>().Value;

if (opciones.Modo == "demo")
{
    app.Logger.LogWarning(
        "ATENCIÓN: ModoConsulta = 'demo'. Se están mostrando DATOS INVENTADOS, no " +
        "licitaciones reales. Sirve para probar la interfaz; en producción hay que " +
        "ponerlo en 'v1' y configurar el ticket.");
}

if (!opciones.Servible)
{
    app.Logger.LogWarning(
        """
        ============================================================
        FALTA CONFIGURAR LA CONSULTA A MERCADO PUBLICO.
        La aplicación arranca y la interfaz funciona, pero cualquier
        consulta a la API fallará. En la sección "MercadoPublico" del
        appsettings hacen falta DOS cosas:

          - Ticket: se pide en mercadopublico.cl y llega al correo.
          - CodigoProveedor: el código de la empresa en Mercado Público.

        Para una instalación normal, copia la plantilla y rellénala:

          Copy-Item secrets\appsettings.Development.json.ejemplo `
                  src\WatchMercadoPublico.Server\appsettings.Development.json

        Para probar la interfaz SIN nada de eso: "ModoConsulta": "demo".
        ============================================================
        """);
}
else if (!opciones.TieneTicket)
{
    app.Logger.LogWarning(
        "No hay ticket de Mercado Público. La empresa se puede mostrar, " +
        "pero las consultas fallarán.");
}
else if (!opciones.TieneCodigoProveedor)
{
    app.Logger.LogWarning(
        "No hay CodigoProveedor en la sección MercadoPublico. Sin el código " +
        "de la empresa no hay a quién consultar.");
}
else if (opciones.Modo == "c2")
{
    app.Logger.LogWarning(
        """
        Modo 'c2' (Compra Ágil) seleccionado. OJO: Compra Ágil es OTRA API, con
        otro modelo de datos y otro flujo (órdenes de compra, no licitaciones).
        El flujo de mes + detalle de esta aplicación está pensado para la v1;
        con 'c2' solo funciona el listado.
        """);
}

// ---------------------------------------------------------------------------
// Que los buscadores no se enteren de que esto existe
//
// Esta herramienta es INTERNA de SMC. No hay nada aqui que deba indexarse, y el
// nombre de la empresa que se vigila tampoco es asunto publico.
//
// Van tres capas, porque cada buscador se lee una y ninguna sirve sola:
//
//   robots.txt    el que va a por el fichero antes de mirar nada mas
//   meta robots   dentro del HTML, para el que ya esta dentro
//   X-Robots-Tag  cabecera, que es la que respetan los que comparten enlaces
//
// OJO CON LO QUE NO ES ESTO: aqui no se toca Cache-Control. "Que no se cacheen"
// en este caso quiere decir que no se INDEXEN ni se saquen capturas, no que el
// navegador no guarde los assets. Los modulos de _framework llevan huella en el
// nombre y van immutable a proposito; poner no-store encima haria que la
// aplicacion los descargara enteros en cada visita, que es justo lo que el
// cache de mas abajo evita.
//
// La cabecera va aqui, en middleware, y no en el <customHeaders> del web.config
// porque ese solo sirve si el hosting respeta el fichero: en MonsterASP, que
// gestiona su propia configuracion, no es una apuesta segura. En el codigo si,
// porque el codigo lo ejecuta la aplicacion.
// ---------------------------------------------------------------------------

app.Use(async (context, siguiente) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive, nosnippet";
        return Task.CompletedTask;
    });

    await siguiente();
});

// ---------------------------------------------------------------------------
// Caché: qué se cachea y qué no
//
//  1. El HTML de la SPA lleva no-store. Si el navegador guardara un index.html
//     antiguo, Blazor pediría módulos de _framework con la huella anterior,
//     que ya no existen tras publicar.
//  2. Los errores (>= 400) llevan no-store. Un 404 cacheado durante un
//     despliegue a medias contaminaba al visitante para siempre, porque los
//     assets se cachean un año.
//  3. Solo _framework/ se cachea un año e immutable: son los módulos de Blazor
//     y llevan la huella del contenido en el nombre (kvp6r5x4nb2fs.js), así que
//     un despliegue nuevo cambia el nombre y no hay nada que invalidar.
//  4. El resto de archivos con extensión (css/app.css, imágenes...) NO llevan
//     huella: se sirven con max-age=0 y must-revalidate. El navegador los guarda
//     pero revalida en cada carga, y si no cambiaron responde 304, que es
//     barato. Con "un año e immutable" para todo lo que tenga extensión, un
//     cambio de estilos llegaría a los usuarios un año después: durante el
//     desarrollo de los <select> en modo oscuro, el navegador sirvió el app.css
//     viejo aunque el archivo en disco ya tenía la corrección.
// ---------------------------------------------------------------------------

app.Use(async (context, siguiente) =>
{
    context.Response.OnStarting(() =>
    {
        var ruta = context.Request.Path.Value ?? "";

        // Navegación = sin extensión (la SPA) o termina en .html.
        var esNavegacion = !Path.HasExtension(ruta)
                           || ruta.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

        // Solo los assets con huella en el nombre pueden ser inmutables.
        var llevaHuella = ruta.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase);

        context.Response.Headers.CacheControl =
            esNavegacion || context.Response.StatusCode >= 400
                ? "no-store, no-cache, must-revalidate"
                : llevaHuella
                    ? "public, max-age=31536000, immutable"
                    : "public, max-age=0, must-revalidate";

        return Task.CompletedTask;
    });

    await siguiente();
});

// ---------------------------------------------------------------------------
// Assets estáticos
//
// El manifiesto (.staticwebassets.endpoints.json) se valida ANTES de usarlo:
// MapStaticAssets() lo lee en el arranque y, si el hosting lo dejó corrupto o
// truncado, lanza JsonException y el proceso muere dejando la web sin assets.
// Con la comprobación previa se cae a UseStaticFiles() en su lugar.
// ---------------------------------------------------------------------------

if (ManifiestoEstaticoUtilizable(app))
    app.MapStaticAssets();
else
    app.Logger.LogError(
        "Manifiesto de assets estáticos no disponible o corrupto. " +
        "Se sirve con UseStaticFiles(); la SPA sigue funcionando, sin rutas con huella.");

app.UseStaticFiles();

// ---------------------------------------------------------------------------
// Purga de la caché
//
// La caché vive en memoria y solo se limpia por tiempo. Si el proceso lleva
// mucho tiempo con la misma memoria, los mapas de días y de detalles crecen
// sin límite (un mes por proveedor son ~30 entradas, pero con muchos
// proveedores y meses distintos son miles). Esta tarea los va vaciando.
// ---------------------------------------------------------------------------

var purga = new PeriodicTimer(TimeSpan.FromMinutes(30));
_ = Task.Run(async () =>
{
    while (await purga.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
    {
        try
        {
            app.Services.GetRequiredService<CacheMercadoPublico>().Podar();
        }
        catch (Exception ex)
        {
            app.Logger.LogError("Falló la purga de la caché: {Error}", ex.Message);
        }
    }
});

// ---------------------------------------------------------------------------
// API
// ---------------------------------------------------------------------------

app.MapLicitacionesEndpoints();

// Recursos ajenos devuelven 404 en JSON, ANTES del fallback: si no, la API
// devolvería el index.html de la SPA y el cliente intentaría parsearlo como JSON.
app.Map("/api/{**resto}", () => Results.NotFound(new { error = "Ese endpoint no existe." }));

app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// Comprueba que exista el manifiesto de assets estáticos y que sea JSON
/// legible. Devuelve false en cuanto algo no cuadra, para que el llamante use
/// UseStaticFiles() en lugar de dejar la aplicación sin assets.
/// </summary>
static bool ManifiestoEstaticoUtilizable(WebApplication app)
{
    var ensamblado = app.Environment.ApplicationName;
    var manifiesto = Path.Combine(AppContext.BaseDirectory, $"{ensamblado}.staticwebassets.endpoints.json");

    if (!File.Exists(manifiesto))
    {
        // En desarrollo el manifiesto lo sirve el host de desarrollo y no está
        // en disco junto al ejecutable: no es un error.
        app.Logger.LogDebug("Sin manifiesto de assets estáticos en disco (entorno normal en desarrollo).");
        return false;
    }

    try
    {
        using var documento = JsonDocument.Parse(File.ReadAllBytes(manifiesto));
        return documento.RootElement.ValueKind == JsonValueKind.Object;
    }
    catch (JsonException ex)
    {
        app.Logger.LogError("Manifiesto de assets estáticos corrupto ({Manifiesto}): {Error}",
            manifiesto, ex.Message);
        return false;
    }
    catch (IOException ex)
    {
        app.Logger.LogError("No se pudo leer el manifiesto de assets ({Manifiesto}): {Error}",
            manifiesto, ex.Message);
        return false;
    }
}

/// <summary>Marcador para que el archivo se pueda referenciar desde las pruebas.</summary>
public partial class Program;