namespace WatchMercadoPublico.Server.Models;

/// <summary>
/// Una licitación del LISTADO diario. Solo trae cuatro campos (medido): el
/// código, el nombre, el estado y la fecha de cierre. Los campos "ricos"
/// (organismo, monto, fechas de cada etapa) solo salen en el detalle, que es
/// una petición aparte por licitación.
/// </summary>
public sealed class Licitacion
{
    /// <summary>
    /// Código de la licitación. La API lo llama <c>CodigoExterno</c>, aunque
    /// la documentación no lo menciona (sí menciona "CodigoLicitacion").
    /// </summary>
    public string? CodigoExterno { get; set; }

    public string? Nombre { get; set; }

    /// <summary>5 Publicada, 6 Cerrada, 7 Desierta, 8 Adjudicada, 18 Revocada, 19 Suspendida.</summary>
    public int? CodigoEstado { get; set; }

    public DateTimeOffset? FechaCierre { get; set; }

    /// <summary>Fecha del barrido en la que salió esta licitación.</summary>
    public DateOnly? FechaPublicacion { get; set; }

    /// <summary>
    /// El detalle ya se pidió: la ficha lo muestra aunque se vuelva a abrir,
    /// sin gastar otra consulta.
    /// </summary>
    public DetalleLicitacion? Detalle { get; set; }
}

/// <summary>
/// Detalle completo de una licitación (<c>?codigo=…</c>). Trae mucha más
/// información que el listado: organismo comprador, fechas de cada etapa,
/// monto, adjudicación e ítems.
/// </summary>
public sealed class DetalleLicitacion
{
    public string? CodigoExterno { get; set; }
    public string? Nombre { get; set; }

    /// <summary>Estado en texto: "Publicada", "Adjudicada"…</summary>
    public string? Estado { get; set; }

    public int? CodigoEstado { get; set; }

    public string? Descripcion { get; set; }

    /// <summary>Tipo abreviado: "LP", "LE", "L1"…</summary>
    public string? Tipo { get; set; }

    /// <summary>Nombre del organismo comprador.</summary>
    public string? NombreOrganismo { get; set; }

    public string? RutOrganismo { get; set; }
    public int? CodigoOrganismo { get; set; }
    public string? RegionOrganismo { get; set; }
    public string? ComunaOrganismo { get; set; }

    public decimal? MontoEstimado { get; set; }
    public string? Moneda { get; set; }

    /// <summary>1 = Presupuesto Disponible, 2 = Precio Referencial.</summary>
    public int? Estimacion { get; set; }

    /// <summary>Fecha de creación de la licitación.</summary>
    public DateTimeOffset? FechaCreacion { get; set; }

    public DateTimeOffset? FechaCierre { get; set; }

    /// <summary>Fecha de publicación en la plataforma.</summary>
    public DateTimeOffset? FechaPublicacion { get; set; }

    public DateTimeOffset? FechaAperturaTecnica { get; set; }
    public DateTimeOffset? FechaAperturaEconomica { get; set; }
    public DateTimeOffset? FechaAdjudicacion { get; set; }
    public DateTimeOffset? FechaFinal { get; set; }

    /// <summary>Número de oferentes que seaw adjudicó.</summary>
    public int? NumeroOferentes { get; set; }

    /// <summary>Número de acto de adjudicación.</summary>
    public string? NumeroAdjudicacion { get; set; }

    /// <summary>Enlace al acta de adjudicación.</summary>
    public string? UrlActa { get; set; }

    public int? NumeroItems { get; set; }

    /// <summary>
    /// Ítems adjudicados, uno por producto.
    ///
    /// Vienen anidados en <c>Listado[].Items.Listado[].Adjudicacion</c>, NO en el
    /// detalle principal: el <c>Adjudicacion</c> de primer nivel es el ACTA
    /// (fecha, número, oferentes, enlace) y no lleva ningún monto. Solo hay
    /// ítems cuando la licitación ya está adjudicada; si está publicada,
    /// desierta o revocada, la lista viene vacía.
    /// </summary>
    public List<ItemAdjudicado> Items { get; set; } = [];

    /// <summary>
    /// Suma de lo adjudicado (unitario × cantidad de cada ítem).
    ///
    /// No coincide con <see cref="MontoEstimado"/>: ese es el presupuesto que se
    /// licitó y este es lo que se contrató. Con la licitación de prueba:
    /// 192.000.000 estimados frente a 147.432.000 adjudicados.
    /// </summary>
    public decimal? TotalAdjudicado
    {
        get
        {
            var conMonto = Items.Where(i => i.MontoUnitario is not null).ToList();
            if (conMonto.Count == 0) return null;

            decimal total = 0;
            foreach (var i in conMonto)
                total += i.MontoUnitario!.Value * (i.CantidadAdjudicada ?? 1m);

            return total;
        }
    }

    /// <summary>Días que faltaron entre el cierre y la apertura.</summary>
    public int? DiasCierreLicitacion { get; set; }
}

/// <summary>
/// Un ítem adjudicado: producto, quién lo ganó, por cuánto la unidad y cuántas.
/// </summary>
public sealed class ItemAdjudicado
{
    public int Correlativo { get; set; }

    public string? NombreProducto { get; set; }

    /// <summary>"Unidad", "Metro", "Kg"…</summary>
    public string? UnidadMedida { get; set; }

    /// <summary>Cantidad pedida en la licitación.</summary>
    public decimal? Cantidad { get; set; }

    /// <summary>
    /// Cantidad adjudicada. Puede diferir de la pedida, y es la que multiplica
    /// al unitario: si no viniera, se asume que se adjudicó lo pedido.
    /// </summary>
    public decimal? CantidadAdjudicada { get; set; }

    /// <summary>Precio por unidad adjudicado. Es el dato que se pide mostrar.</summary>
    public decimal? MontoUnitario { get; set; }

    public string? RutProveedor { get; set; }
    public string? NombreProveedor { get; set; }

    /// <summary>Unitario × cantidad adjudicada.</summary>
    public decimal? Subtotal => MontoUnitario is null
        ? null
        : MontoUnitario.Value * (CantidadAdjudicada ?? Cantidad ?? 1m);
}

/// <summary>
/// Envoltura estándar de licitaciones.json.
/// </summary>
public sealed class RespuestaLicitaciones
{
    public int Cantidad { get; set; }
    public List<Licitacion> Listado { get; set; } = [];
}