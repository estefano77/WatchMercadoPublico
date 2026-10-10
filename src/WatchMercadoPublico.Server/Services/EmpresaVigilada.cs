using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using WatchMercadoPublico.Server.Models;

namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// La empresa vigilada, resuelta UNA vez y recordada.
/// </summary>
/// <remarks>
/// <para>
/// Antes de esto, el nombre, el RUT y el código de proveedor estaban en el
/// appsettings y se copiaban a cada respuesta. Ahora la empresa vive en
/// <c>MpEmpresa</c> y lo único que sigue en la configuración es el RUT, que es
/// la clave con la que se busca.
/// </para>
///
/// <para>
/// QUE SEA UN SINGLETON Y NO UNA CONSULTA POR PETICIÓN. La fila no cambia sola
/// sin que alguien escriba en la base, y <c>/api/estado</c> se llama al cargar
/// la pantalla y en cada refresco. Releerla en cada una sería una conexión al
/// servidor de base de datos para leer una fila que no ha cambiado.
/// </para>
///
/// <para>
/// Y QUE NUNCA LANZE. Este singleton lo consulta <c>/api/estado</c>, que es la
/// primera cosa que la pantalla pide. Si al resolver la empresa saltara una
/// excepción, el arranque se iría al carajo y la web quedaría en blanco sin
/// decir nada. con este diseño hay dos salidas y ninguna rompe: o se resuelve,
/// o se registra el motivo y la pantalla lo enseña. Que es justo lo que
/// distingue un fallo útil de una pantalla en blanco.
/// </para>
///
/// <para>
/// EL REINTENTO CON TOPE ES LO QUE EVITA UN CALLEJÓN SIN SALIDA. La razón más
/// normal para no resolver es que <c>MpEmpresa</c> está vacía, que se arregla
/// corriendo el guion de carga en otra máquina. Si la aplicación solo lo
/// intentara al arrancar, arreglarlo en la base no bastaría: habría que
/// republicar. Con un reintento cada minuto, en cuanto la fila aparece la
/// web se arregla sola.
/// </para>
/// </remarks>
public sealed class EmpresaVigilada
{
    /// <summary>
    /// Cuánto se espera entre reintentos cuando la resolución falló.
    /// </summary>
    /// <remarks>
    /// Un minuto. La razón normal para fallar no se arregla en segundos —hay que
    /// correr un guion en otra máquina— y un minuto es suficiente para que quien
    /// lo arregla vea que se arregló sin reiniciar nada.
    /// </remarks>
    private const int SegundosEntreReintentos = 60;

    private readonly IServiceScopeFactory _ambitos;
    private readonly IOptions<MercadoPublicoOpciones> _opciones;
    private readonly ILogger<EmpresaVigilada> _log;
    private readonly SemaphoreSlim _candado = new(1, 1);

    private EmpresaActual? _empresa;
    private string? _motivo;
    private bool _sinEmpresaPorDiseno;
    private DateTimeOffset _ultimoIntento = DateTimeOffset.MinValue;

    public EmpresaVigilada(
        IServiceScopeFactory ambitos,
        IOptions<MercadoPublicoOpciones> opciones,
        ILogger<EmpresaVigilada> log)
    {
        _ambitos = ambitos;
        _opciones = opciones;
        _log = log;
    }

    /// <summary>
    /// Constructor para pruebas: una empresa ya resuelta, sin tocar nada.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existe porque probar <c>ResolverAsync</c> de verdad exigiría una base de
    /// datos, o una API, o un <see cref="IServiceScopeFactory"/> con un
    /// <c>LectorMercadoPublico</c> que va a salir con una conexión abierta. Y lo
    /// que hay que probar en las otras clases —que la ingesta no corra sin código
    /// de proveedor, que el estado diga una cosa y no la contraria— no depende de
    /// cómo se resolvió, sino del valor resuelto.
    /// </para>
    ///
    /// <para>
    /// Con <c>null</c> queda como no resuelta y con el motivo que se le pase.
    /// Ponerlo es <c>internal</c>, no <c>public</c>: desde fuera de las pruebas
    /// esta clase tiene que resolver sola, que es lo que garantiza el resto del
    /// diseño.
    /// </para>
    /// </remarks>
    internal EmpresaVigilada(EmpresaActual? empresa, string? motivo = null)
    {
        _empresa = empresa;
        _motivo = motivo;
        _sinEmpresaPorDiseno = empresa is null && motivo is null;
        _opciones = Microsoft.Extensions.Options.Options.Create(
            new WatchMercadoPublico.Server.Models.MercadoPublicoOpciones());
        _log = Microsoft.Extensions.Logging.Abstractions.NullLogger<EmpresaVigilada>.Instance;
        _ambitos = null!;
    }

    private MercadoPublicoOpciones Opciones => _opciones.Value;

    /// <summary>La empresa vigilada, o <c>null</c> si no se ha podido resolver.</summary>
    public EmpresaActual? Actual => _empresa;

    /// <summary>
    /// Por qué no hay empresa, en una frase que se pueda poner en pantalla.
    /// </summary>
    /// <remarks>
    /// Es <c>null</c> cuando sí hay empresa, y también cuando no hace falta
    /// ninguna: en modo demostración no se vigila a nadie y no es un problema.
    /// Por eso hay <see cref="FaltaAlgo"/> para preguntar "¿hay que avisar?" y
    /// este para "¿qué digo?".
    /// </remarks>
    public string? Motivo => _motivo;

    /// <summary>¿Hay algo que avisar al usuario?</summary>
    public bool FaltaAlgo => !_sinEmpresaPorDiseno && _empresa is null;

    /// <summary>El RUT con el que se busca, que es lo único que hay de empresa en la configuración.</summary>
    public string RutEmpresa => Opciones.RutEmpresa;

    /// <summary>¿Se sabe a quién se está mirando?</summary>
    public bool TieneCodigoProveedor =>
        !string.IsNullOrWhiteSpace(_empresa?.CodigoProveedor);

    /// <summary>
    /// El código de proveedor, o la cadena vacía.
    /// </summary>
    /// <remarks>
    /// Cadena vacía y no excepción. Todos los usos son consultas que con el
    /// código vacío devuelven cero filas, que es un resultado que la pantalla ya
    /// sabe pintar. Lanzar aquí obligaría a comprobar en veinte sitios lo que se
    /// puede comprobar en uno.
    /// </remarks>
    public string CodigoProveedor => _empresa?.CodigoProveedor ?? "";

    /// <summary>
    /// Resuelve la empresa si hace falta. Se puede llamar tantas veces como
    /// quiera: si ya se resolvió no vuelve a hacer nada.
    /// </summary>
    /// <remarks>
    /// <b>NUNCA LANZA.</b> Ni por un error de red, ni por una tabla que no
    /// existe, ni por un procedimiento que no está instalado. Todo eso se
    /// convierte en un <see cref="Motivo"/> que la pantalla enseña. Es lo que
    /// hace que publicar la aplicación antes de correr
    /// <c>05-procedimientos-lectura.sql</c> en el hosting sea un aviso y no una
    /// caída.
    /// </remarks>
    public async Task ResolverAsync(CancellationToken ct)
    {
        // El candado, y no una comprobación de "ya estoy dentro": con
        // /api/estado y el arranque del programa llamando a la vez, la
        // comprobación tiene una ventana entre que se lee y se escribe y las dos
        // pueden acabar preguntando a la base a la vez.
        if (!await _candado.WaitAsync(0, ct))
            return;

        try
        {
            if (_empresa is not null)
                return;

            if (_sinEmpresaPorDiseno)
                return;

            // El tope. Si se acaba de fallar, no se vuelve a intentarlo en cada
            // /api/estado: eso sería una conexión al servidor de base de datos
            // por cada refresco de la pantalla, que es justo lo que se quería
            // evitar al cachear.
            if (DateTimeOffset.UtcNow - _ultimoIntento
                < TimeSpan.FromSeconds(SegundosEntreReintentos))
                return;

            _ultimoIntento = DateTimeOffset.UtcNow;
            _motivo = null;

            if (Opciones.Modo == "demo")
            {
                // En demostración no hay empresa que buscar y eso NO es un
                // problema. Se dice explícitamente para que el camino de "sin
                // empresa" siga siendo un aviso real en los otros modos.
                _sinEmpresaPorDiseno = true;
                return;
            }

            _empresa = Opciones.UsaBaseDeDatos
                ? await ResolverDesdeLaBaseAsync(ct)
                : await ResolverDesdeLaApiAsync(ct);

            if (_empresa is null)
                return;

            _log.LogInformation(
                "Empresa vigilada: {Nombre} (codigo {Codigo}, RUT {Rut}).",
                _empresa.NombreEmpresa, _empresa.CodigoProveedor, _empresa.RutEmpresa);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cualquier cosa, sin excepción. Si ResolverAsync se escapara, la
            // /api/estado se caería y la pantalla quedaría en blanco en vez de
            // decir qué falta.
            _empresa = null;
            _motivo = "no se pudo leer la empresa: " + ex.Message;
            _log.LogError(ex, "No se pudo resolver la empresa vigilada.");
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>
    /// Modo base de datos: la fila de <c>MpEmpresa</c>.
    /// </summary>
    private async Task<EmpresaActual?> ResolverDesdeLaBaseAsync(CancellationToken ct)
    {
        if (!MercadoPublicoCliente.RutBienFormado(Opciones.RutEmpresa))
        {
            _motivo = "el RUT de la sección MercadoPublico no tiene el formato "
                      + "que acepta la API (algo como 86.130.200-8).";
            return null;
        }

        /* Sin cadena de conexión no se intenta abrirla. Tres razones, y la
           última es la que de verdad importa: sin esto, una instalación a medio
           configurar se spende un segundo entero esperando un tiempo de espera de
           conexión antes de enterarse de que no tenía cadena. Y el motivo que se
           guarda es el de la conexión, no el del RUT: son dos problemas
           distintos y se arreglan de dos maneras distintas. */
        if (!Opciones.BaseDeDatosUtilizable)
        {
            _motivo = "falta la cadena de conexión a la base de datos "
                      + "(MercadoPublico__CadenaConexionSql).";
            return null;
        }

        var ambito = _ambitos.CreateScope();
        var lector = ambito.ServiceProvider.GetRequiredService<LectorMercadoPublico>();

        LectorMercadoPublico.EmpresaLeida? fila;
        try
        {
            fila = await lector.LeerEmpresaAsync(Opciones.RutEmpresa, ct);
        }
        catch (SqlException ex)
        {
            // 2812 es "no existe el procedimiento almacenado". Es el fallo de publicar
            // la aplicación antes de correr 05 en la base, y es el único caso en
            // que el arreglo NO es correr el guion de carga: es aplicar el
            // fichero SQL en el panel del hosting.
            if (ex.Number == 2812)
            {
                _motivo = "en la base no está el procedimiento mp.LeeEmpresa. Hay que "
                          + "correr 05-procedimientos-lectura.sql en el hosting.";
                _log.LogError(
                    "mp.LeeEmpresa no existe en la base. La aplicación se ha publicado sin "
                    + "aplicar 05-procedimientos-lectura.sql en el servidor.");
                return null;
            }

            throw;
        }

        if (fila is null)
        {
            _motivo = "la tabla MpEmpresa está vacía para el RUT "
                      + $"{Opciones.RutEmpresa}. Se llena sola: corre "
                      + "scripts\\cargar-base-remota.ps1 -Ingerir -Si en la máquina que "
                      + "carga la base.";
            return null;
        }

        return new EmpresaActual(
            fila.CodigoProveedor,
            fila.NombreEmpresa,
            fila.RutEmpresa,
            fila.UrlMercadoPublico);
    }

    /// <summary>
    /// Modo API: se le pregunta a Mercado Público por el RUT.
    /// </summary>
    /// <remarks>
    /// Sale de la API y no de la configuración porque en este modo no hay base
    /// de datos donde mirar. Pasa una vez al arrancar y no más: es el mismo dato
    /// durante toda la vida del proceso.
    /// </remarks>
    private async Task<EmpresaActual?> ResolverDesdeLaApiAsync(CancellationToken ct)
    {
        if (!Opciones.TieneTicket)
        {
            _motivo = "falta el ticket de Mercado Público.";
            return null;
        }

        var ambito = _ambitos.CreateScope();
        var cliente = ambito.ServiceProvider.GetRequiredService<MercadoPublicoCliente>();

        var empresas = await cliente.BuscarProveedorAsync(Opciones.RutEmpresa, ct);

        /* UNA O NADA. Esto no es desconfianza gratuita: BuscarProveedor con un
           RUT que no es de nadie devuelve empresas de verdad, no una lista
           vacía. Medido con 99.999.999-9, que devolvió dos. Con un RUT mal
           escrito, coger "la primera" no daría sin resultado: daría la empresa
           equivocada, y todo lo que viene después escribiría sus licitaciones.

           Con más de una, el mensaje enseña cuáles son. Puede que sean dos
           códigos de la misma empresa, que es un caso real y no un error; que lo
           decida alguien, que es quien sabe cuál es. */
        if (empresas.Count != 1)
        {
            var nombres = string.Join(
                ", ", empresas.Select(e => $"{e.NombreEmpresa} ({e.CodigoEmpresa})"));

            _motivo = empresas.Count == 0
                ? $"Mercado Público no devolvió ninguna empresa para el RUT {Opciones.RutEmpresa}."
                : $"el RUT {Opciones.RutEmpresa} devuelve {empresas.Count} empresas en "
                  + $"Mercado Público y no se sabe cuál es: {nombres}. Corregir el RUT en la "
                  + "sección MercadoPublico, o dejar en la tabla la fila correcta.";

            if (empresas.Count > 1)
                _log.LogWarning(
                    "El RUT {Rut} devuelve varias empresas: {Nombres}",
                    Opciones.RutEmpresa, nombres);

            return null;
        }

        var empresa = empresas[0];

        return new EmpresaActual(
            empresa.CodigoEmpresa,
            // La API devuelve el nombre REGISTRADO, que puede ser más largo que el
            // que se usa por dentro. Se pone el que viene, no el de la
            // configuración, porque el de la configuración ya no existe.
            string.IsNullOrWhiteSpace(empresa.NombreEmpresa)
                ? "Empresa sin nombre"
                : empresa.NombreEmpresa,
            Opciones.RutEmpresa,
            EmpresaActual.UrlPorDefecto);
    }
}
