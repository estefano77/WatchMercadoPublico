using WatchMercadoPublico.Server.Models;

namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Datos de ejemplo para trabajar sin el ticket de Mercado Público.
///
/// NO forma parte de la aplicación: se activa con
/// "MercadoPublico:ModoConsulta": "demo" y existe por dos razones:
///
///  1. Para comprobar la paginación, el filtro, el barrido por mes y el detalle
///     sin gastar el cupo diario del ticket (10.000 consultas).
///  2. Para ver la herramienta funcionando antes de tener el ticket.
///
/// Los datos son INVENTADOS. Ninguna empresa, organismo, monto ni código existe
/// de verdad en Mercado Público.
/// </summary>
public static class DatosDemo
{
    private static readonly string[] Organismos =
    [
        "Municipalidad de Providencia",
        "Servicio de Salud Metropolitano Norte",
        "Corporación Municipal de Quinta Normal",
        "Ministerio de Obras Públicas",
        "Municipalidad de Las Condes",
        "Hospital Clínico Universidad de Chile",
        "Subsecretaría de Redes Asistenciales",
        "Servicio Local de Educación Pública Los Coopiues",
    ];

    private static readonly string[] Tipos = ["L1", "LE", "LP", "E1", "B2", "C2", "D1"];

    private static readonly string[] Zonas =
    [
        "Región Metropolitana|Providencia",
        "Región Metropolitana|Las Condes",
        "Región del Biobío|Concepción",
        "Región de Antofagasta|Antofagasta",
        "Región de Los Lagos|Puerto Montt",
        "Región del Maule|Talca",
        "Región de La Araucanía|Temuco",
    ];

    private static readonly string[] Temas =
    [
        "Suministro de señalización vial y demarcación",
        "Mantenimiento de equipos de aire comprimido",
        "Adquisición de herramientas neumáticas",
        "Servicio de limpieza y aseo de dependencias",
        "Compra de elementos de protección personal",
        "Reparación de bombas de agua industriales",
        "Suministro de tubería y fitting para tramo de red",
        "Arriendo de equipos de construcción",
        "Adquisición de sensores de presión y temperatura",
        "Servicio de mantención preventiva de ascensores",
        "Compra de válvulas y accesorios industriales",
        "Suministro de lubricantes y grasas",
    ];

    /// <summary>
    /// RUT de ejemplo, para probar el flujo. Ya no se escribe en ningún sitio:
    /// la empresa viene de la configuración. Se mantiene por si hace falta
    /// documentar un caso de prueba.
    /// </summary>
    public const string RutDemo = "76.123.456-0";

    /// <summary>Listado de un día. Reproduce lo que hace la API de verdad: la mayoría de
    /// los días trae POCAS licitaciones de una empresa (SMC tiene 0 o 1 según el
    /// día, según lo medido). Se generan entre 0 y 3 para que se vea el caso
    /// "este mes no tiene nada", que es el más habitual.
    ///
    /// La semilla sale de la fecha, así que un día da SIEMPRE lo mismo: sin eso
    /// la caché y las pruebas no significarían nada.
    /// </summary>
    public static List<Licitacion> Dia(DateOnly dia)
    {
        var semilla = dia.Day * 31 + dia.Month * 7 + dia.Year % 100;
        var aleatorio = new Random(semilla);

        var cuantas = aleatorio.Next(0, 4);
        var lista = new List<Licitacion>(cuantas);

        for (var i = 0; i < cuantas; i++)
        {
            var tema = Temas[aleatorio.Next(Temas.Length)];
            var tipo = Tipos[aleatorio.Next(Tipos.Length)];

            // La fecha de cierre va entre 3 y 25 días después de la fecha de
            // publicación: en su mayoría siguen vigentes, como en la realidad.
            var diasVista = aleatorio.Next(3, 26);

            lista.Add(new Licitacion
            {
                CodigoExterno = $"{7000 + i / 8}-{tipo}-D{i % 8 + 1}L{dia.Day}{dia.Month:00}",
                Nombre = $"{tema} ({dia:dd/MM/yyyy})",
                CodigoEstado = 5,
                FechaCierre = dia.AddDays(diasVista).ToDateTime(TimeOnly.MinValue),
                FechaPublicacion = dia,
            });
        }

        return lista;
    }

    /// <summary>
    /// Detalle inventado pero con la FORMA exacta del real, para que la ficha se
    /// pueda probar sin inventarse campos que la API no manda.
    /// </summary>
    public static DetalleLicitacion? Detalle(string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo)) return null;

        var aleatorio = new Random(codigo.GetHashCode());
        var zona = Zonas[aleatorio.Next(Zonas.Length)].Split('|');
        var organismo = Organismos[aleatorio.Next(Organismos.Length)];

        var tipo = Tipos[aleatorio.Next(Tipos.Length)];
        var monto = tipo switch
        {
            "L1" => aleatorio.Next(5, 60) * 100_000m,
            "LE" => aleatorio.Next(80, 900) * 100_000m,
            "LP" => aleatorio.Next(1_000, 90_000) * 100_000m,
            _ => aleatorio.Next(500_000, 120_000_000),
        };

        var publicada = new DateTimeOffset(
            DateTime.Today.AddDays(-aleatorio.Next(5, 60)).AddHours(-8));

        // Ítems adjudicados, para que se vea también la tabla y el subtotal.
        // El unitario sale por debajo del estimado a propósito: así se aprecia
        // que son cifras distintas, como pasa en las licitaciones reales.
        var cuantosItems = aleatorio.Next(1, 4);
        var itemsDemo = new List<ItemAdjudicado>();
        for (var n = 1; n <= cuantosItems; n++)
        {
            var cantidad = aleatorio.Next(1, 12);

            // El MISMO nombre de producto en todas las lineas y la descripcion
            // distinta, que es el caso real que hace falta este campo: sin ella
            // el demo enseña dos filas identicas sin explicar por que.
            var letra = (char)('a' + n - 1);

            itemsDemo.Add(new ItemAdjudicado
            {
                Correlativo = n,
                NombreProducto = $"Ítem de ejemplo para {codigo}",
                Descripcion = $"Línea {letra}) Ítem de ejemplo - implementación",
                UnidadMedida = "Unidad",
                Cantidad = cantidad,
                CantidadAdjudicada = cantidad,
                MontoUnitario = Math.Round(monto / (decimal)cuantosItems / cantidad, 0),
                RutProveedor = "76.123.456-0",
                NombreProveedor = "Proveedor de ejemplo SpA",
            });
        }

        return new DetalleLicitacion
        {
            CodigoExterno = codigo,
            Nombre = $"Licitación de ejemplo {codigo}",
            Estado = "Publicada",
            CodigoEstado = 5,
            Descripcion = "Detalle inventado para probar la ficha (modo demo).",
            Tipo = tipo,
            NombreOrganismo = organismo,
            RutOrganismo = "61.981.420-7",
            CodigoOrganismo = aleatorio.Next(1000, 999999),
            RegionOrganismo = zona[0],
            ComunaOrganismo = zona[1],
            MontoEstimado = monto,
            Moneda = "CLP",
            Estimacion = tipo == "L1" ? 1 : 2,
            NumeroItems = aleatorio.Next(1, 40),
            Items = itemsDemo,
            DiasCierreLicitacion = aleatorio.Next(3, 20),
            FechaCreacion = publicada.AddDays(-7),
            FechaPublicacion = publicada,
            FechaCierre = publicada.AddDays(aleatorio.Next(10, 30)),
            FechaAperturaTecnica = publicada.AddDays(12),
            FechaAperturaEconomica = publicada.AddDays(13),
            FechaFinal = publicada.AddDays(6),
            NumeroOferentes = aleatorio.Next(1, 9),
            NumeroAdjudicacion = aleatorio.Next(100, 999).ToString(),
            UrlActa = "https://www.mercadopublico.cl/",
        };
    }
}