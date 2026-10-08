using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WatchMercadoPublico.Client.Models;

namespace WatchMercadoPublico.Client.Services;

/// <summary>
/// Cliente de los endpoints de la aplicación.
///
/// Regla de la casa: un cliente que se come el error y devuelve una lista vacía
/// hace que la interfaz degrade en silencio y parezca que el usuario no tiene
/// datos. Por eso TODOS los métodos devuelven la tupla (datos, error): el error
/// se propaga siempre a la pantalla.
///
/// No usa CancellationToken en las consultas al día: si la pantalla se cancela
/// mientras el servidor insiste, la petición queda huérfana consumiendo cupo
/// del ticket. Mejor que el servidor termine y su resultado seignore.
/// </summary>
public sealed class MercadoPublicoApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Cuánto se espera al servidor antes de cortar, en segundos.
    ///
    /// ESTE NÚMERO ESTÁ MEDIDO, NO ELEGIDO POR GUSTO. El servidor reintenta seis
    /// veces cada día y cada intento a Mercado Público puede tardar 30 s, así que
    /// un día que va mal se queda 6 x 30 s de peticiones más 2, 4, 8, 16 y 30 s de
    /// esperas: unos cuatro minutos por día, y los días van uno tras otro.
    ///
    /// Por eso el corte del cliente NO cubre el peor caso del servidor, y a
    /// propósito. Ningún usuario mira un contador cuatro minutos. Y cortar no
    /// gasta nada: la petición no lleva token de cancelación, así que el servidor
    /// sigue su curso y guarda el día en la caché, de modo que el siguiente
    /// intento sale a la primera.
    ///
    /// Antes de esto el corte no existía: era el valor por defecto de .NET,
    /// 100 s, por casualidad y sin que nadie lo supiera. Y el problema no era el
    /// corte, sino lo que pasaba al dar: <see cref="OperationCanceledException"/>
    /// NO es <see cref="HttpRequestException"/>, así que se colaba por debajo del
    /// filtro de más abajo y salía como excepción sin manejar. La pantalla ponía
    /// Cargando a false, pero nadie repintaba, así que el usuario veía el contador
    /// congelado en el último número pintado y ningún error por ninguna parte.
    ///
    /// Lo que se arregla con esto es lo segundo, no el número.
    /// </summary>
    public const int SegundosEspera = 150;

    /// <summary>
    /// Configuración del servidor: qué empresa se mira, si hay ticket y cada
    /// cuánto se refresca.
    /// </summary>
    public async Task<(EstadoApi? Data, string? Error)> GetEstadoAsync(
        int? anio = null, int? mes = null, CancellationToken ct = default)
    {
        try
        {
            var url = "api/estado";

            if (anio is not null && mes is not null)
                url += $"?anio={anio}&mes={mes}";

            var estado = await http.GetFromJsonAsync<EstadoApi>(url, Json, ct);
            return estado is null ? (null, "El servidor no devolvió el estado.") : (estado, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Sin esta línea, un corte por tiempo de espera caería en el filtro de
            // más abajo, no encajaría en él, y saldría como excepción sin manejar.
            return (null, "El servidor tardó demasiado en responder.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return (null, "No se pudo conectar con el servidor.");
        }
    }

    /// <summary>
    /// Licitaciones de UNA semana: "semana 2 de septiembre de 2026".
    ///
    /// El cliente elige qué semana mirar, pero NO elige días: los días hábiles
    /// los calcula el servidor, que es quien sabe cuáles se saltan.
    ///
    /// El servidor insiste hasta un minuto antes de rendirse, y ahora son hasta
    /// cinco peticiones una detrás de otra, así que esta llamada puede tardar
    /// medio minuto. La pantalla muestra "Consultando…" mientras.
    ///
    /// <paramref name="refrescar"/> solo hace que se vuelva a preguntar el día
    /// de hoy; los días pasados ya están cacheados porque no cambian.
    /// </summary>
    public async Task<(SemanaLicitaciones? Data, string? Error)> GetSemanaAsync(
        int anio, int mes, int semana, bool refrescar = false,
        CancellationToken ct = default, bool enSegundoPlano = false)
    {
        try
        {
            var url =
                $"api/semana?anio={anio}&mes={mes}&semana={semana}&refrescar={(refrescar ? "true" : "false")}" +
                $"&fondo={(enSegundoPlano ? "true" : "false")}";

            using var respuesta = await http.GetAsync(url, ct);
            var cuerpo = await respuesta.Content.ReadAsStringAsync();

            if (respuesta.IsSuccessStatusCode)
            {
                var pagina = Deserializar<SemanaLicitaciones>(cuerpo);
                return pagina is null
                    ? (null, "El servidor devolvió una respuesta inesperada.")
                    : (pagina, null);
            }

            return (null, ExtraerError(cuerpo) ?? "No se pudieron cargar las licitaciones de esa semana.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // El corte por tiempo se convierte en error, pero una cancelación
            // pedida NO: esa sube como excepción, que es lo que espera quien llama
            // y lo que le permite distinguir "no aguanté más" de "el servidor no
            // respondió". El filtro con el token es lo que separa los dos casos.
            return (null,
                "El servidor tardó demasiado en responder y la consulta se cortó. " +
                "Puede que siga buscando en Mercado Público: inténtalo otra vez en un rato.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return (null, "No se pudo conectar con el servidor.");
        }
    }
    /// <summary>
    /// Licitaciones de UN MES, cuando los datos vienen de la base de datos.
    /// </summary>
    ///
    /// <para>
    /// Devuelve la tupla (datos, error) como los demás, por la misma razón: un
    /// cliente que se come el error deja que la pantalla degrade en silencio.
    /// </para>
    ///
    /// <para>
    /// NO lleva CancellationToken, y a diferencia de <see cref="GetSemanaAsync"/>
    /// aquí no es que no haga falta: es que no habría nada que cancelar. La
    /// consulta va a un SQL Server local y no llama a la API, así que no gasta
    /// cupo del ticket ni deja trabajo a medias si el navegador se va.
    /// Cancelarla solo añadiría un camino en el que la pantalla se queda
    /// esperando sobre datos que ya llegaron.
    /// </para>
    /// </summary>
    public async Task<(MesLicitaciones? Data, string? Error)> GetMesAsync(
        int anio, int mes)
    {
        try
        {
            using var respuesta = await http.GetAsync($"api/mes?anio={anio}&mes={mes}");
            var cuerpo = await respuesta.Content.ReadAsStringAsync();

            if (respuesta.IsSuccessStatusCode)
            {
                var pagina = Deserializar<MesLicitaciones>(cuerpo);
                return pagina is null
                    ? (null, "El servidor devolvió una respuesta inesperada.")
                    : (pagina, null);
            }

            return (null, ExtraerError(cuerpo) ?? "No se pudieron cargar las licitaciones de ese mes.");
        }
        catch (OperationCanceledException)
        {
            return (null, "La consulta al servidor se cortó antes de tiempo.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return (null, "No se pudo conectar con el servidor.");
        }
    }


    /// <summary>
    /// Detalle de una licitación. Se pide al abrir la ficha y queda cacheado en
    /// el servidor, así que abrirla otra vez no gasta consulta.
    /// </summary>
    public async Task<(DetalleLicitacion? Data, bool DesdeCache, string? Error)> GetDetalleAsync(
        string codigo)
    {
        try
        {
            using var respuesta = await http.GetAsync($"api/licitaciones/{Uri.EscapeDataString(codigo)}");
            var cuerpo = await respuesta.Content.ReadAsStringAsync();

            if (!respuesta.IsSuccessStatusCode)
                return (null, false, ExtraerError(cuerpo) ?? "No se pudo cargar el detalle.");

            var envoltura = Deserializar<RespuestaDetalle>(cuerpo);
            if (envoltura?.Detalle is null)
                return (null, false, "Mercado Público no devolvió el detalle de esta licitación.");

            return (envoltura.Detalle, envoltura.DesdeCache, null);
        }
        catch (OperationCanceledException)
        {
            // Mismo caso que en la semana, y con la misma consecuencia si se
            // deja pasar: el modal se quedaba cargando para siempre.
            return (null, false,
                "El detalle tardó demasiado en llegar y la consulta se cortó. " +
                "Vuelve a abrir la ficha en un rato.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return (null, false, "No se pudo conectar con el servidor.");
        }
    }

    /// <summary>Vacía la caché del servidor para forzar consulta nueva.</summary>
    public async Task<string?> RefrescarAsync()
    {
        try
        {
            using var respuesta = await http.PostAsync("api/refrescar", content: null);
            if (respuesta.IsSuccessStatusCode) return null;

            return ExtraerError(await respuesta.Content.ReadAsStringAsync())
                   ?? "No se pudo vaciar la caché del servidor.";
        }
        catch (OperationCanceledException)
        {
            return "El servidor tardó demasiado en responder y la caché no se vació.";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return "No se pudo conectar con el servidor.";
        }
    }

    // ------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------

    private static T? Deserializar<T>(string cuerpo) =>
        JsonSerializer.Deserialize<T>(cuerpo, Json);

    /// <summary>
    /// Saca el mensaje de error del cuerpo de la respuesta.
    ///
    /// Se prueban varias formas porque el servidor no siempre responde igual:
    /// <c>{ "error": "…" }</c> en los endpoints propios y el texto del
    /// ProblemDetails de <c>Results.Problem</c> en los de configuración.
    /// </summary>
    private static string? ExtraerError(string cuerpo)
    {
        if (string.IsNullOrWhiteSpace(cuerpo)) return null;

        try
        {
            using var doc = JsonDocument.Parse(cuerpo);
            var raiz = doc.RootElement;

            if (raiz.ValueKind == JsonValueKind.Object)
            {
                foreach (var clave in new[] { "error", "detail", "title", "mensaje" })
                {
                    if (raiz.TryGetProperty(clave, out var valor) &&
                        valor.ValueKind == JsonValueKind.String)
                    {
                        var texto = valor.GetString();
                        if (!string.IsNullOrWhiteSpace(texto)) return texto;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // No es JSON: se devuelve el texto plano, recortado.
        }

        var plano = System.Net.WebUtility.HtmlDecode(cuerpo.Trim());
        return plano.Length > 300 ? plano[..300] + "…" : plano;
    }
}
