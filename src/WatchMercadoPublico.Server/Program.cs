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

// La otra fuente de datos. Scoped y NO singleton a propósito, aunque podría ser
// singleton: no guarda estado entre llamadas (la conexión se abre y se cierra
// cada vez), así que no hay nada que compartir. Y un singleton con un SqlConnection
// dentro acabaría con una conexión viva para siempre y sin cerrarla nunca.
builder.Services.AddScoped<LectorMercadoPublico>();

// La otra mitad de la base de datos: la que ESCRIBE. Va aparte del lector y no
// dentro de él porque no se usan nunca en el mismo camino. La pantalla solo lee;
// el temporizador solo ingiere. Juntarlos sería una clase con dos maneras
// totalmente distintas de abrir la conexion, una de las cuales ESCRIBE.
// Juntarlas daria una clase con un "estoy escribiendo" repartido por todos lados.
builder.Services.AddSingleton<IngestaMercadoPublico>();

// Singleton A PROPÓSITO: el ritmo hacia la API tiene que ser el mismo para todas
// las peticiones del proceso. Si fuera scoped, cada petición HTTP tendría el suyo
// y dos a la vez no se limitarían entre sí, que es el 429 que se quiere evitar.
builder.Services.AddSingleton<RitmoDeLlamadas>();

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
    /* La plantilla se busca con ruta ABSOLUTA, y no con una relativa.

       La relativa era "secrets\appsettings.Development.json.ejemplo", y
       funciona solo si el directorio actual es la raíz del repositorio. Con la
       aplicación arrancada desde dentro de src\WatchMercadoPublico.Server —que
       es lo normal con dotnet run --project, y lo que hace el IDE — el
       directorio actual es el proyecto y el comando falla:

           Copy-Item : No se encuentra la ruta de acceso
           '...\src\WatchMercadoPublico.Server\secrets\appsettings.Development.json.ejemplo'
           porque no existe.

       El aviso se leía, se copiaba tal cual y no funcionaba. Un aviso cuyo
       consejo no se puede seguir no vale como aviso.

       ContentRootPath es el directorio del proyecto, que la aplicación ya sabe
       sin preguntar. La plantilla vive dos niveles arriba, en la raíz del
       repositorio. Si algún día cambia de sitio, este es el único sitio que hay
       que tocar, y la comprobación de abajo avisa. */
    var plantilla = Path.GetFullPath(
        Path.Combine(app.Environment.ContentRootPath, "..", "..", "secrets",
            "appsettings.Development.json.ejemplo"));

    if (!File.Exists(plantilla))
    {
        app.Logger.LogWarning(
            """
            ============================================================
            FALTA CONFIGURAR LA CONSULTA A MERCADO PUBLICO.
            Y ADEMÁS: no se encuentra la plantilla de configuración en

              {Ruta}

            Si clonaste el repositorio, eso significa que la plantilla no se
            versionó. Créala copiando src\WatchMercadoPublico.Server\appsettings.json
            a src\WatchMercadoPublico.Server\appsettings.Development.json y
            añadiendo dentro la sección "MercadoPublico".
            ============================================================
            """, plantilla);
    }
    else
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

            Para una instalación normal, copia la plantilla y rellénala. Esta
            ruta es absoluta, así que el comando funciona desde cualquier
            directorio:

              Copy-Item '{Plantilla}' '{Destino}'

            Para probar la interfaz SIN nada de eso: "ModoConsulta": "demo".
            ============================================================
            """,
            plantilla,
            Path.Combine(app.Environment.ContentRootPath, "appsettings.Development.json"));
    }
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
// Ingesta automática a la base de datos
//
// Va AQUÍ, junto al temporizador de purga, y no en un SQL Server Agent. El
// Express no lo trae: el servicio SQLSERVERAGENT no existe en una instalación
// de Express, así que "programarlo en SQL Server" es, en esta máquina, no
// tener nada.
//
// Y va dentro del servidor por una razón más profunda que la del Agent: el
// ticket está en la configuración del servidor y no sale de ahí. Un trabajo de
// SQL Server Agent necesita las credenciales guardadas en el servidor de
// impersonalidades, y el ticket es una credencial.
//
// Con MinutosEntreIngestas = 0 no se programa ninguna, que es el valor por
// defecto a propósito.
// ---------------------------------------------------------------------------

var minutosIngesta = opciones.MinutosEntreIngestas;
var ingesta = app.Services.GetRequiredService<IngestaMercadoPublico>();

if (!opciones.UsaBaseDeDatos)
{
    app.Logger.LogInformation(
        "Fuente de datos: la API de Mercado Público. No se programa ninguna ingesta.");
}
else if (minutosIngesta <= 0)
{
    app.Logger.LogInformation(
        "Fuente de datos: la base de datos. MinutosEntreIngestas = 0, así que NO se " +
        "importa nada solo. La base se llena ejecutando sql/03-procedimiento-importar.sql " +
        "a mano, o poniendo MinutosEntreIngestas mayor que cero.");
}
else if (ingesta.FaltaParaIngerir is { } falta)
{
    // No se programa el temporizador. Es tentador programarlo igual y que cada
    // pasada avise, pero eso es un bucle que se despierta cada N minutos para
    // no hacer nada, y con el log lleno de líneas iguales nadie ve la que
    // importa.
    app.Logger.LogError(
        "Fuente de datos: la base de datos, con ingesta cada {Minutos} minutos, pero {Falta}. " +
        "No se programa ninguna ingesta.",
        minutosIngesta, falta);
}
else
{
    app.Logger.LogInformation(
        "Fuente de datos: la base de datos. Importando los días que falten cada {Minutos} minutos.",
        minutosIngesta);

    var reloj = new PeriodicTimer(TimeSpan.FromMinutes(minutosIngesta));
    _ = Task.Run(async () =>
    {
        while (await reloj.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            try
            {
                await ingesta.IngerirAsync(app.Lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Cada pasada se reintenta sola en el siguiente tictac. Si una
                // importa y la siguiente no, no hay nada que arreglar a mano:
                // los días que fallaron se vuelven a mirar porque el rango se
                // recalcula y @soloFaltantes se los salta si ya entraron.
                app.Logger.LogError(
                    "Falló la ingesta automática: {Error}", ex.Message);
            }
        }
    });
}

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
