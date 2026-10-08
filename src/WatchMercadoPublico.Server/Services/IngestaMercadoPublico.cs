using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;

namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Importa a la base los días que aún no se han descargado.
/// </summary>
/// <remarks>
/// <para>
/// Es la contraparte de <see cref="LectorMercadoPublico"/>. El lector trae lo que
/// ya está; este deja de traer lo que falta. Los dos van por procedimiento
/// almacenado, por la misma razón: el acceso a la base son dos procedimientos
/// de lectura y uno de escritura, y se pueden leer enteros.
/// </para>
///
/// <para>
/// NO se lanza por el botón "Actualizar" de la pantalla. Ese botón relee de la
/// base, y releer es instantáneo; si además descargara, la pantalla volvería a
/// ser lenta por la razón que motivó todo esto.
/// </para>
///
/// <para>
/// Solo se lanza con el temporizador y <c>MinutosEntreIngestas</c> mayor que
/// cero. Con cero no se programa nada: una tarea que sale sola descarga de la
/// API y gasta cupo del ticket sin que nadie lo haya pedido.
/// </para>
/// </remarks>
public sealed class IngestaMercadoPublico
{
    private readonly ILogger<IngestaMercadoPublico> _log;
    private readonly SemaphoreSlim _candado = new(1, 1);

    public IngestaMercadoPublico(
        IOptions<MercadoPublicoOpciones> opciones,
        ILogger<IngestaMercadoPublico> log)
    {
        Opciones = opciones.Value;
        _log = log;
    }

    private MercadoPublicoOpciones Opciones { get; }

    /// <summary>
    /// ¿Se puede lanzar una ingesta?
    /// </summary>
    /// <remarks>
    /// Exige las TRES cosas: fuente base de datos, cadena de conexión y ticket.
    /// Sin ticket no hay nada que traer, y el proceso de importación se
    /// quedaría gastando reintentos contra la API devolviendo 203 una y otra
    /// vez, que es lo que pasó el 8 de octubre de 2026.
    /// </remarks>
    public bool PuedeIngerir =>
        Opciones.BaseDeDatosUtilizable && Opciones.TieneTicket;

    /// <summary>
    /// Qué falta para poder ingerir, o null si no falta nada.
    /// </summary>
    /// <remarks>
    /// En modo API devuelve null aunque <see cref="PuedeIngerir"/> sea false: no
    /// le falta nada, es que no hay ingesta que hacer. Son dos preguntas
    /// distintas —"¿puede?" y "¿qué le falta?"— y confundirlas fue lo que hizo
    /// que la primera prueba de esta clase saliera roja asserting una
    /// equivalencia que no existe.
    /// </remarks>
    public string? FaltaParaIngerir =>
        !Opciones.UsaBaseDeDatos ? null
        : !Opciones.BaseDeDatosUtilizable
            ? "falta la cadena de conexión a la base de datos"
        : !Opciones.TieneTicket
            ? "falta el ticket de Mercado Público para poder descargar"
        : null;

    /// <summary>
    /// Descarga los días que falten, empezando por el más viejo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Solo días LABORABLES hasta hoy. Un sábado no tiene licitaciones que traer
    /// y preguntarlo es gastar una llamada para obtener un 200 con cero
    /// resultados. Y hasta HOY, no hasta el fin de mes: el 31 de diciembre
    /// todavía no pasó.
    /// </para>
    ///
    /// <para>
    /// <c>@soloFaltantes = 1</c> es lo que hace esto barato de repetir: un día
    /// que ya tiene al menos un intento exitoso no se vuelve a preguntar. Sin
    /// eso, cada pasada del temporizador reintentaría los días que fallaron y,
    /// con un ticket caducado, serían seis reintentos por día y por pasada.
    /// </para>
    /// </remarks>
    public async Task<string> IngerirAsync(CancellationToken ct)
    {
        // Un candado, no una comprobación de "ya estoy dentro". La comprobación
        // tiene una ventana entre que se lee y que se escribe el estado, y en
        // esa ventana un segundo paso se cuela. Con el temporizador y con el
        // arranque solapados eso no es una posibilidad teórica.
        if (!await _candado.WaitAsync(0, ct))
            return "Ya había una ingesta en marcha; esta se salta.";

        try
        {
            var hoy = DateOnly.FromDateTime(DateTime.Today);

            await using var conexion = new SqlConnection(Opciones.CadenaConexionSql);
            await conexion.OpenAsync(ct);

            await using var comando = conexion.CreateCommand();
            comando.CommandType = CommandType.StoredProcedure;
            comando.CommandText = "dbo.MpImportarRango";

            comando.Parameters.Add("@desde", SqlDbType.Date).Value = hoy.AddDays(-DiasMirandoAtras);
            comando.Parameters.Add("@hasta", SqlDbType.Date).Value = hoy;
            comando.Parameters.Add("@codigoProveedor", SqlDbType.NVarChar, 50).Value = Opciones.CodigoProveedor;
            comando.Parameters.Add("@ticket", SqlDbType.NVarChar, 200).Value = Opciones.Ticket;
            comando.Parameters.Add("@conDetalle", SqlDbType.Bit).Value = true;

            // Los días que ya dieron algo, no se vuelven a preguntar.
            comando.Parameters.Add("@soloFaltantes", SqlDbType.Bit).Value = true;

            comando.Parameters.Add("@modoRegistro", SqlDbType.VarChar, 10).Value = Opciones.ModoConsulta;
            comando.Parameters.Add("@resultado", SqlDbType.NVarChar, -1).Value = null;

            comando.CommandTimeout = 0;

            var resultado = await comando.ExecuteScalarAsync(ct) as string ?? "";

            _log.LogInformation("Ingesta terminada: {Resultado}", resultado);

            return resultado;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>
    /// Cuántos días hacia atrás mira cada pasada.
    /// </summary>
    /// <remarks>
    /// Treinta días, y no "los que falten": el rango se recalcula en cada pasada
    /// a partir de HOY, así que una caída de dos semanas no deja un hueco
    /// atrás, porque al volver a arrancar se mira hacia atrás otra vez desde el
    /// día de hoy. Lo que no se recupera es lo que quedó en medio mientras el
    /// proceso estaba apagado, y por eso treinta días es un colchón y no un
    /// olvido.
    /// </remarks>
    private const int DiasMirandoAtras = 30;
}
