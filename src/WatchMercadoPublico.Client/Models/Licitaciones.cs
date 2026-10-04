namespace WatchMercadoPublico.Client.Models;

/// <summary>
/// Los mismos nombres que usa el servidor, que los serializa en camelCase. Si un
/// nombre se cambia aquí, hay que cambiarlo también en el DTO del servidor: al no
/// haber atributos, el JSON y el C# tienen que coincidir.
/// </summary>
public sealed class Licitacion
{
    /// <summary>Código de la licitación. La API lo llama CodigoExterno.</summary>
    public string? CodigoExterno { get; set; }

    public string? Nombre { get; set; }

    /// <summary>5 Publicada, 6 Cerrada, 7 Desierta, 8 Adjudicada, 18 Revocada, 19 Suspendida.</summary>
    public int? CodigoEstado { get; set; }

    public DateTimeOffset? FechaCierre { get; set; }

    /// <summary>Día del barrido en el que apareció esta licitación.</summary>
    public DateTimeOffset? FechaPublicacion { get; set; }

    /// <summary>Detalle ya en caché, si se pidió antes.</summary>
    public DetalleLicitacion? Detalle { get; set; }

    public string? Codigo => CodigoExterno;

    /// <summary>¿Sigue abierta? 5 Publicada es el único estado con ofertas en curso.</summary>
    public bool Activa => CodigoEstado is null or 5;

    /// <summary>Texto del estado, con un nombre legible si la API no lo manda.</summary>
    public string EstadoLegible => CodigoEstado switch
    {
        5 => "Publicada",
        6 => "Cerrada",
        7 => "Desierta",
        8 => "Adjudicada",
        18 => "Revocada",
        19 => "Suspendida",
        _ => "Sin estado",
    };
}

/// <summary>
/// Detalle completo de una licitación. Solo llega al abrir la ficha, que es
/// cuando se consume el endpoint <c>?codigo=…</c>.
/// </summary>
public sealed class DetalleLicitacion
{
    public string? CodigoExterno { get; set; }
    public string? Nombre { get; set; }
    public string? Estado { get; set; }
    public int? CodigoEstado { get; set; }
    public string? Descripcion { get; set; }
    public string? Tipo { get; set; }

    public string? NombreOrganismo { get; set; }
    public string? RutOrganismo { get; set; }
    public int? CodigoOrganismo { get; set; }
    public string? RegionOrganismo { get; set; }
    public string? ComunaOrganismo { get; set; }

    public decimal? MontoEstimado { get; set; }
    public string? Moneda { get; set; }

    /// <summary>
    /// Precio por unidad adjudicado. Es el dato que se muestra como cifra
    /// principal: el estimado es el presupuesto que se licitó, este es lo que
    /// se contrató.
    /// </summary>
    public List<ItemAdjudicado> Items { get; set; } = [];

    /// <summary>
    /// Suma de lo adjudicado. Null si esta licitación no tiene ítems, que pasa
    /// siempre que aún no se adjudica.
    /// </summary>
    public decimal? TotalAdjudicado
    {
        get
        {
            var conMonto = Items.Where(i => i.MontoUnitario is not null).ToList();
            if (conMonto.Count == 0) return null;

            decimal total = 0;
            foreach (var i in conMonto)
                total += i.MontoUnitario!.Value * (i.CantidadAdjudicada ?? i.Cantidad ?? 1m);

            return total;
        }
    }

    /// <summary>¿Hay un único ítem? Entonces se muestra como tarjeta y no como tabla.</summary>
    public bool UnSoloItem => Items.Count == 1;

    /// <summary>1 = Presupuesto Disponible, 2 = Precio Referencial.</summary>
    public int? Estimacion { get; set; }

    public DateTimeOffset? FechaCreacion { get; set; }
    public DateTimeOffset? FechaCierre { get; set; }
    public DateTimeOffset? FechaPublicacion { get; set; }
    public DateTimeOffset? FechaAperturaTecnica { get; set; }
    public DateTimeOffset? FechaAperturaEconomica { get; set; }
    public DateTimeOffset? FechaAdjudicacion { get; set; }
    public DateTimeOffset? FechaFinal { get; set; }

    public int? NumeroOferentes { get; set; }
    public string? NumeroAdjudicacion { get; set; }
    public string? UrlActa { get; set; }
    public int? NumeroItems { get; set; }

    /// <summary>Proveedores distintos que se llevaron algún ítem.</summary>
    public int? ProveedoresDistintos => Items.Count == 0
        ? null
        : Items
            .Where(i => !string.IsNullOrWhiteSpace(i.RutProveedor))
            .Select(i => i.RutProveedor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    public int? DiasCierreLicitacion { get; set; }
}

/// <summary>
/// Un ítem adjudicado: producto, quién lo ganó, por cuánto la unidad y cuántas.
/// Viene anidado en el detalle, en <c>Items.Listado[].Adjudicacion</c>.
/// </summary>
public sealed class ItemAdjudicado
{
    public int Correlativo { get; set; }
    public string? NombreProducto { get; set; }

    /// <summary>"Unidad", "Metro", "Kg"…</summary>
    public string? UnidadMedida { get; set; }

    /// <summary>Cantidad que se pidió en la licitación.</summary>
    public decimal? Cantidad { get; set; }

    /// <summary>
    /// Cantidad adjudicada. Puede diferir de la pedida, y es la que multiplica
    /// al unitario; si no viniera, se usa <see cref="Cantidad"/>.
    /// </summary>
    public decimal? CantidadAdjudicada { get; set; }

    /// <summary>Precio por unidad adjudicado: el dato que se muestra.</summary>
    public decimal? MontoUnitario { get; set; }

    public string? RutProveedor { get; set; }
    public string? NombreProveedor { get; set; }

    /// <summary>Cantidad que multiplica al unitario, con respaldo en la pedida.</summary>
    public decimal CantidadEfectiva => CantidadAdjudicada ?? Cantidad ?? 1m;

    /// <summary>Unitario × cantidad: el subtotal de la fila.</summary>
    public decimal? Subtotal => MontoUnitario is null ? null : MontoUnitario.Value * CantidadEfectiva;
}

/// <summary>
/// Lo que hay publicado HOY para la empresa configurada.
///
/// No hay selector de día ni paginación: la pantalla solo mira el día actual, y
/// en un día una empresa publica pocas licitaciones. Sin "días pendientes",
/// porque un día es una petición y o sale o no sale.
/// </summary>
public sealed class PaginaLicitaciones
{
    public List<Licitacion> Items { get; set; } = [];

    /// <summary>Cuántas se han publicado hoy.</summary>
    public int Total { get; set; }

    /// <summary>El día consultado, en ISO (aaaa-mm-dd).</summary>
    public string? Fecha { get; set; }

    /// <summary>"3 de Octubre de 2026", para las cabeceras.</summary>
    public string? FechaLegible { get; set; }

    /// <summary>Hoy es sábado o domingo. No es un error: lo normal es que no haya nada.</summary>
    public bool EsFinDeSemana { get; set; }

    /// <summary>Salió de la caché del servidor sin gastar consulta.</summary>
    public bool DesdeCache { get; set; }

    public DateTimeOffset Consultado { get; set; }
}

/// <summary>La empresa vigilada, tal como viene de la configuración del servidor.</summary>
public sealed class EmpresaInfo
{
    public string? NombreEmpresa { get; set; }
    public string? RutEmpresa { get; set; }
    public string? CodigoProveedor { get; set; }

    /// <summary>¿Hay nombre para poner en la cabecera?</summary>
    public bool TieneNombre => !string.IsNullOrWhiteSpace(NombreEmpresa);

    public string Nombre => TieneNombre ? NombreEmpresa! : "Empresa sin configurar";
}

/// <summary>Respuesta del endpoint de detalle.</summary>
public sealed class RespuestaDetalle
{
    public string? Codigo { get; set; }
    public DetalleLicitacion? Detalle { get; set; }

    /// <summary>True si vino de la caché del servidor (no se gastó consulta).</summary>
    public bool DesdeCache { get; set; }
}

/// <summary>Estado de la configuración del servidor.</summary>
public sealed class EstadoApi
{
    public bool TicketConfigurado { get; set; }

    /// <summary>¿Hay código de proveedor configurado?</summary>
    public bool EmpresaConfigurada { get; set; }

    /// <summary>¿Se puede consultar de verdad, o solo falta configuración?</summary>
    public bool Servible { get; set; }

    public string Modo { get; set; } = "v1";

    /// <summary>La empresa vigilada, fija, de la configuración del servidor.</summary>
    public EmpresaInfo Empresa { get; set; } = new();

    /// <summary>El día que se está mirando, en ISO.</summary>
    public string? Fecha { get; set; }

    public string? FechaLegible { get; set; }
    public bool EsFinDeSemana { get; set; }

    /// <summary>Cada cuántos minutos se refresca sola la pantalla.</summary>
    public int MinutosEntreRefrescos { get; set; } = 5;

    /// <summary>Solo en modo demo: el RUT de ejemplo.</summary>
    public string? RutDemo { get; set; }

    /// <summary>¿Estamos viendo datos inventados?</summary>
    public bool EsDemo => Modo == "demo";

    /// <summary>
    /// ¿Falta algo para poder consultar? El mensaje cambia según lo que falte:
    /// sin ticket es distinto de sin código de proveedor, y el arreglo es
    /// distinto en cada caso.
    /// </summary>
    public string? FaltaConfiguracion =>
        Servible ? null
        : !TicketConfigurado ? "falta el ticket de Mercado Público"
        : !EmpresaConfigurada ? "falta el código de proveedor de la empresa"
        : null;
}
