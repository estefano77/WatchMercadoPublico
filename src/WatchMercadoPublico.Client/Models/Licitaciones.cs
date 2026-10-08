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

    /// <summary>
    /// El dia de la publicacion ya escrito: "viernes 27 de febrero". Lo compone
    /// el servidor. El cliente lo pintaba con su propio array de dias, y por eso
    /// una vez un viernes aparecia como "sabado".
    /// </summary>
    public string PublicadoTexto { get; set; } = "";

    /// <summary>Detalle ya en caché, si se pidió antes.</summary>
    public DetalleLicitacion? Detalle { get; set; }

    public string? Codigo => CodigoExterno;

    /// <summary>¿Sigue abierta? 5 Publicada es el único estado con ofertas en curso.</summary>
    public bool Activa => CodigoEstado is null or 5;

    /// <summary>
    /// ¿Se adjudicó? Estado 8. Es el único que lleva una decisión y un
    /// documento que la respalda, que es el acta: por eso lleva insignia propia
    /// en vez de compartir la gris de los estados cerrados.
    ///
    /// Se decide por el código y no por el texto de <see cref="EstadoLegible"/>,
    /// que es una cadena localizada y podría cambiar sin que nadie se entere.
    /// </summary>
    public bool EsAdjudicada => CodigoEstado is 8;

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
/// Una semana de licitaciones de la empresa configurada.
///
/// Sin paginación: una semana son cinco días hábiles y una empresa publica pocas
/// cosas en ellos. Y sin "días pendientes" porque un día se consulta entero o no
/// se consulta: lo que no se pudo comprobar viene en DiasSinRespuesta, que es
/// distinto de "no había nada".
/// </summary>
public sealed class SemanaLicitaciones
{
    public List<Licitacion> Items { get; set; } = [];

    /// <summary>Cuántas se han publicado en la semana.</summary>
    public int Total { get; set; }

    public int Anio { get; set; }
    public int Mes { get; set; }
    public int Semana { get; set; }

    /// <summary>Primer día de la semana, en ISO. Puede ser sábado.</summary>
    public string? Desde { get; set; }

    /// <summary>Último día de la semana, en ISO. Puede ser domingo.</summary>
    public string? Hasta { get; set; }

    /// <summary>Cuántos días hábiles tenía la semana (lunes a viernes).</summary>
    public int DiasHabiles { get; set; }
    /// <summary>
    /// Cuántos de esos días se preguntaron DE VERDAD. Es menor que DiasHabiles
    /// cuando la semana todavía no ha terminado: los días futuros no se
    /// consultan porque no hay nada que preguntar, y no sería honesto contarlos
    /// como fallidos.
    /// </summary>
    public int DiasConsultados { get; set; }

    /// <summary>Días hábiles que todavía no han llegado. No es un fallo.</summary>
    public int DiasPendientes { get; set; }

    /// <summary>
    /// Cuántos días NO se pudieron consultar.
    ///
    /// Es lo que permite no mentir: si es mayor que cero, lo que se ve está
    /// incompleto y la pantalla tiene que decirlo. Un 0 aquí significa que se
    /// preguntaron todos los días de verdad.
    /// </summary>
    public int DiasFallidos { get; set; }

    /// <summary>Los días concretos que fallaron, en ISO.</summary>
    public List<string> DiasSinRespuesta { get; set; } = [];

    /// <summary>Sale de la caché del servidor sin gastar consulta.</summary>
    public bool DesdeCache { get; set; }


    /// <summary>
    /// El periodo ya escrito: "Del 23 de febrero al 28 de febrero". Lo compone
    /// el servidor, por lo mismo que <see cref="SemanaInfo.Texto"/>.
    /// </summary>
    public string Periodo { get; set; } = "";
    public DateTimeOffset Consultado { get; set; }

    /// <summary>
    /// El mes escrito y con año, cuando la búsqueda es por mes.
    /// </summary>
    /// <remarks>
    /// Solo se rellena en modo base de datos. En modo API queda vacía y el
    /// título del panel de cero resultados usa el texto de la semana, porque no
    /// hay mes detrás.
    /// </remarks>
    public string PeriodoDelMes { get; set; } = "";

    /// <summary>¿Se pudo comprobar la semana entera?</summary>
    public bool Completa => DiasFallidos == 0;

    /// <summary>¿La semana todavía no ha terminado?</summary>
    public bool AFuturo => DiasPendientes > 0;

    /// <summary>Las licitaciones agrupadas por día, para no repetir el día en cada ficha.</summary>
    public IEnumerable<IGrouping<DateOnly, Licitacion>> PorDia =>
        Items
            .Where(l => l.FechaPublicacion is not null)
            .GroupBy(l => DateOnly.FromDateTime(l.FechaPublicacion!.Value.DateTime))
            .OrderBy(g => g.Key);
}

/// <summary>
/// Un mes entero de licitaciones, cuando los datos vienen de la base de datos.
///
/// <para>
/// Es la misma forma que <see cref="SemanaLicitaciones"/> con la unidad cambiada,
/// y no un tipo con otro nombre: la pantalla dibuja las mismas tarjetas en los
/// dos modos, y si los tipos se parecieran solo un poco acabaría dibujando una
/// cosa en modo semana y otra en modo mes sin que se note.
/// </para>
/// </summary>
public sealed class MesLicitaciones
{
    public List<Licitacion> Items { get; set; } = [];

    public int Total { get; set; }

    public int Anio { get; set; }
    public int Mes { get; set; }

    /// <summary>Días hábiles del mes que ya han ocurrido.</summary>
    public int DiasHabiles { get; set; }

    /// <summary>De esos, cuántos se preguntaron DE VERDAD.</summary>
    public int DiasConsultados { get; set; }

    /// <summary>Días hábiles que todavía no han llegado.</summary>
    public int DiasPendientes { get; set; }

    /// <summary>De los que ya pasaron, cuántos NO se pudieron comprobar.</summary>
    public int DiasFallidos { get; set; }

    /// <summary>Los días concretos que fallaron, en ISO.</summary>
    public List<string> DiasSinRespuesta { get; set; } = [];

    public bool DesdeCache { get; set; }

    public string Periodo { get; set; } = "";

    /// <summary>
    /// El mes escrito y con año: "el mes de Junio de 2026".
    /// </summary>
    /// <remarks>
    /// Va aparte de <see cref="Periodo"/> porque son dos textos para dos sitios:
    /// <c>Periodo</c> es el rango del mes entero ("Del 1 al 30 de junio de 2026"),
    /// que va junto al contador, y este es el nombre del mes, que va en el titulo
    /// del panel de cero resultados.
    /// </remarks>
    public string PeriodoDelMes { get; set; } = "";
    public DateTimeOffset Consultado { get; set; }

    /// <summary>¿Se pudo comprobar el mes entero?</summary>
    public bool Completo => DiasFallidos == 0;

    /// <summary>¿El mes todavía no ha terminado?</summary>
    public bool AFuturo => DiasPendientes > 0;

    /// <summary>Las licitaciones agrupadas por día, para no repetir el día.</summary>
    public IEnumerable<IGrouping<DateOnly, Licitacion>> PorDia =>
        Items
            .Where(l => l.FechaPublicacion is not null)
            .GroupBy(l => DateOnly.FromDateTime(l.FechaPublicacion!.Value.DateTime))
            .OrderBy(g => g.Key);
}
/// <summary>La empresa vigilada, tal como viene de la configuración del servidor.</summary>
public sealed class EmpresaInfo
{
    public string? NombreEmpresa { get; set; }
    public string? RutEmpresa { get; set; }
    public string? CodigoProveedor { get; set; }

    /// <summary>
    /// A dónde lleva el enlace "Ir a Mercado Público" de la cabecera. Viene
    /// escrito desde la configuración del servidor; vacío si la URL no es
    /// utilizable, y entonces el enlace no se pinta.
    /// </summary>
    public string? UrlMercadoPublico { get; set; }

    /// <summary>¿Hay nombre para poner en la cabecera?</summary>
    public bool TieneNombre => !string.IsNullOrWhiteSpace(NombreEmpresa);

    public string Nombre => TieneNombre ? NombreEmpresa! : "Empresa sin configurar";

    /// <summary>¿Se puede pintar el enlace a Mercado Público?</summary>
    public bool TieneUrlMercadoPublico => !string.IsNullOrWhiteSpace(UrlMercadoPublico);
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

    // --- Qué se puede elegir en los desplegables ---
    // Los calcula el servidor para que el cliente no tenga su propia copia que
    // pueda desincronizarse: el número de semanas de un mes depende de cuántos
    // días tiene, y cada sitio que lo calculara por su cuenta daría un número
    // distinto.

    /// <summary>La semana en que está hoy, dentro de su mes.</summary>
    public int Semana { get; set; } = 1;

    public int Anio { get; set; } = DateTime.Today.Year;
    public int Mes { get; set; } = DateTime.Today.Month;

    /// <summary>Años a elegir: el actual y cinco antes.</summary>
    public List<int> AniosDisponibles { get; set; } = [];

    /// <summary>Los doce nombres de mes, en español.</summary>
    public List<string> MesesDisponibles { get; set; } = [];

    /// <summary>Cuántas semanas tiene el mes en que está hoy.</summary>
    /// <summary>
    /// Rangos y días hábiles de cada semana del mes pedido. Los CALCULA el
    /// servidor: el cliente no repite la regla de lunes a domingo, y eso es lo
    /// que garantiza que el rótulo y los datos consultados digan lo mismo.
    /// </summary>
    public List<SemanaInfo> Semanas { get; set; } = [];

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
    /// <summary>
    /// De dónde salen los datos: <c>api</c> o <c>sql</c>.
    ///
    /// <para>
    /// Con <c>sql</c> el filtro es por año y mes, no por semana, y el texto del
    /// overlay cambia. Es lo que hace que la misma pantalla sirva para las dos
    /// fuentes sin que quede a medias ninguna.
    /// </para>
    /// </summary>
    public string Fuente { get; set; } = "api";

    /// <summary>¿Se leen los datos de la base de datos?</summary>
    public bool UsaBaseDeDatos => Fuente == "sql";

    /// <summary>
    /// ¿Se puede leer de la base de verdad, o falta la cadena de conexión?
    ///
    /// <para>
    /// Con fuente "sql" y sin cadena, el aviso tiene que decir ESO y no "faltan
    /// datos": son dos estados distintos y el usuario solo puede arreglar uno.
    /// </para>
    /// </summary>
    public bool BaseDeDatosUtilizable { get; set; } = true;

    /// <summary>
    /// Qué falta para poder consultar, o null si no falta nada.
    ///
    /// <para>
    /// El caso de la base de datos va PRIMERO y por un motivo concreto: en modo
    /// "sql" no hace falta ticket para leer, porque no se pregunta a Mercado
    /// Público. Si se mirara primero el ticket, con fuente "sql" y sin ticket
    /// aparecería un aviso pidiendo un ticket que no hace falta para nada, y
    /// quien lo leyera pensaría que la aplicación está mal configurada.
    /// </para>
    /// </summary>
    public string? FaltaConfiguracion
    {
        get
        {
            if (UsaBaseDeDatos)
            {
                // Sin ticket no pasa nada: aquí no se pregunta a Mercado Público.
                // Pero el CÓDIGO DE PROVEEDOR sí sigue haciendo falta, porque lo
                // que hay en la base está etiquetado por empresa y sin él no se
                // sabe qué parte mirar.
                if (!BaseDeDatosUtilizable)
                    return "falta la cadena de conexión a la base de datos " +
                           "(MercadoPublico__CadenaConexionSql)";

                return EmpresaConfigurada
                    ? null
                    : "falta el código de proveedor de la empresa";
            }

            return Servible ? null
                : !TicketConfigurado ? "falta el ticket de Mercado Público"
                : !EmpresaConfigurada ? "falta el código de proveedor de la empresa"
                : null;
        }
    }
}

/// <summary>Una semana del mes, tal como la calcula el servidor.</summary>
public sealed class SemanaInfo
{
    public int Numero { get; set; }

    /// <summary>Primer día, en ISO. Puede no ser lunes si la semana está recortada.</summary>
    public string? Desde { get; set; }

    /// <summary>Último día, en ISO. Puede no ser domingo si la semana está recortada.</summary>
    public string? Hasta { get; set; }

    /// <summary>Cuántos de esos días se consultan: los de lunes a viernes.</summary>
    public int DiasHabiles { get; set; }

    /// <summary>
    /// El rango ya escrito, tal como va en el desplegable: "23 al 28 de
    /// febrero". Lo compone el servidor.
    ///
    /// Antes el cliente lo armaba con las fechas y su propia lista de meses.
    /// Dos copias de una regla se desincronizan solas en cuanto se toca una,
    /// asi que aqui no se calcula nada: solo se pinta lo que llega.
    /// </summary>
    public string Texto { get; set; } = "";
}
