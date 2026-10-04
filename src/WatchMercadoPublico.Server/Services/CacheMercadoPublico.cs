using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;

namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Caché en memoria de loslistados diarios y de los detalles.
///
/// Existe por una razón práctica: el ticket de Mercado Público tiene un tope de
/// 10.000 consultas al día, y la API v1 devuelve UN día por consulta. Un mes son
/// ~30 peticiones; si cada vez que se abre la pantalla se repitieran, en pocas
/// visitas se agotaría el cupo.
///
/// Se guarda por DÍA, no por mes, porque es la unidad que devuelve la API: al
/// elegir otro mes solo se piden los días que aún no están en caché.
/// </summary>
public sealed class CacheMercadoPublico
{
    /// <summary>Listado de un día concreto para un proveedor.</summary>
    private sealed class EntradaDia(DateTimeOffset vence, List<Licitacion> datos)
    {
        public DateTimeOffset Vence { get; } = vence;
        public List<Licitacion> Datos { get; } = datos;
    }

    /// <summary>Detalle de una licitación.</summary>
    private sealed class EntradaDetalle(DateTimeOffset vence, DetalleLicitacion? datos)
    {
        public DateTimeOffset Vence { get; } = vence;
        public DetalleLicitacion? Datos { get; } = datos;
    }

    private readonly Dictionary<string, EntradaDia> porDia = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntradaDetalle> porDetalle = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim candado = new(1, 1);
    private readonly TimeSpan caducidad;
    private readonly TimeSpan caducidadHistorica;
    private readonly ILogger<CacheMercadoPublico> log;

    public CacheMercadoPublico(
        ILogger<CacheMercadoPublico> log,
        TimeSpan caducidad,
        TimeSpan? caducidadHistorica = null)
    {
        this.log = log;
        this.caducidad = caducidad <= TimeSpan.Zero ? TimeSpan.FromMinutes(15) : caducidad;

        // Un día PASADO no cambia: una licitación publicada el 14 de enero sigue
        // siendo la misma en diciembre. Caducarla a los 4 minutos obligaría a
        // volver a preguntar por semanas ya consultadas, que es justo lo que
        // se cachea para no gastar el cupo del ticket. Por eso los días
        // anteriores a hoy viven mucho más.
        this.caducidadHistorica = caducidadHistorica is { } h && h > TimeSpan.Zero
            ? h
            : TimeSpan.FromDays(30);
    }

    private static string ClaveDia(string proveedor, DateOnly fecha) => $"{proveedor}|{fecha:yyyyMMdd}";

    // ------------------------------------------------------------------
    // Listados por día
    // ------------------------------------------------------------------

    /// <summary>¿Está este día ya en caché y vigente?</summary>
    public bool TieneDia(string proveedor, DateOnly fecha) =>
        porDia.TryGetValue(ClaveDia(proveedor, fecha), out var e) && e.Vence > DateTimeOffset.UtcNow;

    /// <summary>Listado cacheado de un día, o null si no lo hay.</summary>
    public List<Licitacion>? ObtenerDia(string proveedor, DateOnly fecha) =>
        TieneDia(proveedor, fecha) ? porDia[ClaveDia(proveedor, fecha)].Datos : null;

    /// <summary>
    /// Guarda el listado de un día.
    ///
    /// El plazo depende de si el día ya pasó: hoy caduca en minutos para que
    /// aparezca lo que se publica durante la jornada; un día pasado dura mucho
    /// más porque no va a cambiar.
    /// </summary>
    public void GuardarDia(string proveedor, DateOnly fecha, List<Licitacion> datos)
    {
        var esHoy = fecha == DateOnly.FromDateTime(DateTime.Today);
        var plazo = esHoy ? caducidad : caducidadHistorica;

        porDia[ClaveDia(proveedor, fecha)] =
            new EntradaDia(DateTimeOffset.UtcNow.Add(plazo), datos);
    }

    // ------------------------------------------------------------------
    // Detalles
    // ------------------------------------------------------------------

    /// <summary>
    /// Detalle cacheado. Se distingue "no está" de "está y salió vacío": un
    /// 404 se cachea unos minutos para no repreguntar en cada apertura.
    /// </summary>
    public DetalleLicitacion? ObtenerDetalle(string codigo, out bool hayCache)
    {
        hayCache = porDetalle.TryGetValue(codigo, out var e) && e.Vence > DateTimeOffset.UtcNow;
        return hayCache ? porDetalle[codigo].Datos : null;
    }

    public void GuardarDetalle(string codigo, DetalleLicitacion? detalle) =>
        porDetalle[codigo] = new EntradaDetalle(DateTimeOffset.UtcNow.Add(caducidad), detalle);

    // ------------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------------

    /// <summary>Purga lo caducado. La ejecuta un temporizador del servidor.</summary>
    public int Podar()
    {
        var ahora = DateTimeOffset.UtcNow;
        var dias = porDia.Where(kv => kv.Value.Vence <= ahora).Select(kv => kv.Key).ToList();
        var detalles = porDetalle.Where(kv => kv.Value.Vence <= ahora).Select(kv => kv.Key).ToList();

        foreach (var clave in dias) porDia.Remove(clave);
        foreach (var clave in detalles) porDetalle.Remove(clave);

        if (dias.Count + detalles.Count > 0)
            log.LogInformation(
                "Caché podada: {Dias} días y {Detalles} detalles caducados",
                dias.Count, detalles.Count);

        return dias.Count + detalles.Count;
    }

    /// <summary>Vacía todo, o solo lo de un proveedor (botón "Actualizar").</summary>
    public void Invalidar(string? codigoProveedor = null)
    {
        if (codigoProveedor is null)
        {
            porDia.Clear();
            porDetalle.Clear();
            return;
        }

        // Los detalles no llevan el proveedor, así que al refrescar un proveedor
        // se descartan también: sus fichas volverían a pedir detalle.
        foreach (var clave in porDia.Keys.Where(k => k.StartsWith(codigoProveedor + "|", StringComparison.Ordinal)).ToList())
            porDia.Remove(clave);
        porDetalle.Clear();
    }

    public SemaphoreSlim Candado => candado;
}

/// <summary>Registro de la caché.</summary>
public static class CacheMercadoPublicoExtensions
{
    public static IServiceCollection AddCacheMercadoPublico(
        this IServiceCollection servicios, IConfiguration configuracion)
    {
        var minutos = configuracion.GetValue<int?>(
            $"{MercadoPublicoOpciones.Seccion}:MinutosDeCache") ?? 15;

        // Días ya pasados: 30 días por defecto, o lo que diga la configuración.
        var minutosHistoricos = configuracion.GetValue<int?>(
            $"{MercadoPublicoOpciones.Seccion}:DiasDeCacheHistorico") ?? 30;

        servicios.AddSingleton(sp => new CacheMercadoPublico(
            sp.GetRequiredService<ILogger<CacheMercadoPublico>>(),
            TimeSpan.FromMinutes(Math.Max(1, minutos)),
            TimeSpan.FromDays(Math.Max(1, minutosHistoricos))));

        return servicios;
    }
}