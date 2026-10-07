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

    /// <summary>
    /// Días que FALLARON, con la hora en que deja de importar recordarlo.
    ///
    /// <para>
    /// Aquí no hay datos, solo la memoria de que ya se preguntó y no respondió.
    /// Y existe por una medición: el día que falla estaba costando 60 de los 65
    /// segundos de cada consulta, para siempre, porque el fallo no se guardaba
    /// en ninguna parte y la escalera de seis intentos se volvía a subir entera
    /// en cada petición.
    /// </para>
    ///
    /// <para>
    /// Lo que se guarda NO es una mentira: el día sigue saliendo como fallido y
    /// la pantalla sigue diciendo que puede faltar algo de ese día. Lo único que
    /// cambia es que no se vuelve a preguntar hasta que pase el plazo.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> porDiaFallido = new(StringComparer.Ordinal);

    /// <summary>
    /// UN candado para toda la aplicación, a propósito, y con la medición que lo
    /// justifica.
    ///
    /// <para>
    /// Se probó la contraria: un candado por semana, para que dos personas
    /// mirando semanas distintas no se estorbaran. Con ese cambio, en esta misma
    /// máquina:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>Una semana en frío: <b>6,5 s</b></item>
    /// <item>Dos semanas, una detrás de otra: <b>13,2 s</b>, o sea la suma exacta</item>
    /// <item>Dos semanas, en paralelo: <b>23,5 s</b></item>
    /// </list>
    ///
    /// <para>
    /// Es decir: mandar dos consultas a la vez a Mercado Público desde la misma
    /// conexión es más de tres veces peor que hacerlas seguidas. La API no lo
    /// dice con un 429, se limita callando: cada llamada tarda mucho más, y el
    /// total sube por encima de la suma. Por eso los días de una semana van en
    /// serie, y por eso el candado es único.
    /// </para>
    ///
    /// <para>
    /// Entonces lo que había que arreglar NO era el candado, sino dos cosas
    /// alrededor: que se esperaba en él sin límite y sin decir nada, y que lo
    /// retuviera el refresco automático mientras reintentaba. Eso sí está
    /// arreglado, y sin tocar el candado.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim candado = new(1, 1);
    private readonly TimeSpan caducidad;
    private readonly TimeSpan caducidadHistorica;
    private readonly TimeSpan caducidadFallo;
    private readonly ILogger<CacheMercadoPublico> log;

    public CacheMercadoPublico(
        ILogger<CacheMercadoPublico> log,
        TimeSpan caducidad,
        TimeSpan? caducidadHistorica = null,
        TimeSpan? caducidadFallo = null)
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

        // El plazo del fallo tiene que ser MÁS LARGO que el refresco automático.
        // Si fuera igual, cada refresco llegaría justo cuando el fallo caduca y
        // volvería a subir la escalera entera, y no se arreglaría nada. Quince
        // minutos es más que los cinco del refresco, y bastante menos que los
        // cuatro de espera que costaba preguntar en balde.
        this.caducidadFallo = caducidadFallo is { } f && f > TimeSpan.Zero
            ? f
            : TimeSpan.FromMinutes(15);
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
    ///
    /// Y borra el fallo que hubiera: si el día acaba de responder, ya no hay nada
    /// que recordar, y dejarlo puesto haría que la próxima consulta lo saltara
    /// sin preguntarlo.
    /// </summary>
    public void GuardarDia(string proveedor, DateOnly fecha, List<Licitacion> datos)
    {
        var esHoy = fecha == DateOnly.FromDateTime(DateTime.Today);
        var plazo = esHoy ? caducidad : caducidadHistorica;

        porDia[ClaveDia(proveedor, fecha)] =
            new EntradaDia(DateTimeOffset.UtcNow.Add(plazo), datos);

        porDiaFallido.Remove(ClaveDia(proveedor, fecha));
    }

    // ------------------------------------------------------------------
    // Días que fallaron
    // ------------------------------------------------------------------

    /// <summary>
    /// Anota que este día se preguntó y no respondió, para no volver a preguntarlo
    /// hasta que pase el plazo.
    /// </summary>
    public void GuardarDiaFallido(string proveedor, DateOnly fecha) =>
        porDiaFallido[ClaveDia(proveedor, fecha)] = DateTimeOffset.UtcNow.Add(caducidadFallo);

    /// <summary>
    /// ¿Este día falló hace poco y aún se está procurando?
    ///
    /// Es la pregunta que evita la escalera de reintentos entera en cada petición.
    /// Quien la hace tiene que dejar el día en la lista de sin respuesta igual
    /// que si hubiera preguntado: recordarlo no autoriza a decir que no hay nada.
    /// </summary>
    public bool DiaFallidoReciente(string proveedor, DateOnly fecha) =>
        porDiaFallido.TryGetValue(ClaveDia(proveedor, fecha), out var vence)
        && vence > DateTimeOffset.UtcNow;

    /// <summary>El plazo que se recuerda un fallo. Solo para pruebas y para el log.</summary>
    public TimeSpan CaducidadFallo => caducidadFallo;

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
        var fallidos = porDiaFallido.Where(kv => kv.Value <= ahora).Select(kv => kv.Key).ToList();

        foreach (var clave in dias) porDia.Remove(clave);
        foreach (var clave in detalles) porDetalle.Remove(clave);
        foreach (var clave in fallidos) porDiaFallido.Remove(clave);

        if (dias.Count + detalles.Count + fallidos.Count > 0)
            log.LogInformation(
                "Caché podada: {Dias} días, {Fallidos} fallos y {Detalles} detalles caducados",
                dias.Count, fallidos.Count, detalles.Count);

        return dias.Count + detalles.Count + fallidos.Count;
    }

    /// <summary>Vacía todo, o solo lo de un proveedor (botón "Actualizar").</summary>
    public void Invalidar(string? codigoProveedor = null)
    {
        if (codigoProveedor is null)
        {
            porDia.Clear();
            porDetalle.Clear();
            porDiaFallido.Clear();
            return;
        }

        // Los detalles no llevan el proveedor, así que al refrescar un proveedor
        // se descartan también: sus fichas volverían a pedir detalle.
        foreach (var clave in porDia.Keys.Where(k => k.StartsWith(codigoProveedor + "|", StringComparison.Ordinal)).ToList())
            porDia.Remove(clave);

        // Y los fallos también se descartan. Si no, el botón "Actualizar" no
        // reintentaría un día que había fallado, y durante el plazo entero
        // seguiría apareciendo como sin respuesta sin que nadie lo volviera a
        // preguntar. El botón es la única manera de saltarse este plazo, y si no
        // se lo salta deja de servir para lo único que sirve.
        foreach (var clave in porDiaFallido.Keys.Where(k => k.StartsWith(codigoProveedor + "|", StringComparison.Ordinal)).ToList())
            porDiaFallido.Remove(clave);

        porDetalle.Clear();
    }

    /// Candado único de la aplicación. Ver el comentario del campo: hay
    /// medición detrás de que no se parta por semanas.
    /// </summary>
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

        // Días que fallaron: 15 minutos por defecto, o lo que diga la
        // configuración. Tiene que ser mayor que MinutosEntreRefrescos o el
        // refresco automático llegaría siempre con el fallo caducado.
        var minutosFallo = configuracion.GetValue<int?>(
            $"{MercadoPublicoOpciones.Seccion}:MinutosDeCacheFallo") ?? 15;

        servicios.AddSingleton(sp => new CacheMercadoPublico(
            sp.GetRequiredService<ILogger<CacheMercadoPublico>>(),
            TimeSpan.FromMinutes(Math.Max(1, minutos)),
            TimeSpan.FromDays(Math.Max(1, minutosHistoricos)),
            TimeSpan.FromMinutes(Math.Max(1, minutosFallo))));

        return servicios;
    }
}