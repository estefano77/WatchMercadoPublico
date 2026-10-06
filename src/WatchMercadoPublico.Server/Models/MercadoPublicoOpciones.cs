namespace WatchMercadoPublico.Server.Models;

/// <summary>
/// Configuración de la API de Mercado Público (sección "MercadoPublico").
///
/// La empresa vigilada es FIJA y viene de aquí: nombre, RUT y código de
/// proveedor. La aplicación dejó de preguntar el RUT porque casi siempre era la
/// misma, y pedirlo cada vez solo añadía un paso donde se podían equivocar.
/// </summary>
public sealed class MercadoPublicoOpciones
{
    public const string Seccion = "MercadoPublico";

    /// <summary>Ticket personal que envía ChileCompra por correo.</summary>
    public string Ticket { get; set; } = "";

    /// <summary>
    /// Código de proveedor en Mercado Público. Es lo único que hace falta para
    /// consultar; el nombre y el RUT son solo para que la pantalla diga a quién
    /// está mirando.
    ///
    /// Con "demo" se puede dejar vacío y la app funciona con datos inventados.
    /// </summary>
    public string CodigoProveedor { get; set; } = "";

    /// <summary>Nombre de la empresa, para la cabecera.</summary>
    public string NombreEmpresa { get; set; } = "";

    /// <summary>RUT de la empresa, para la cabecera.</summary>
    public string RutEmpresa { get; set; } = "";

    /// <summary>
    /// A dónde lleva el enlace "Ir a Mercado Público" de la cabecera.
    ///
    /// Va en la configuración y no en el marcado por la misma razón que la
    /// empresa: es un dato del entorno, no de la pantalla. Si algún día el
    /// enlace tiene que apuntar a otra página —o a una intranet que lo replique—
    /// se cambia aquí y no se toca el .razor, que además no es un sitio donde
    /// tenga sentido cambiar URLs.
    ///
    /// Es configurable a propósito y no se valida contra una lista: no hay forma
    /// barata de saber si una URL de Mercado Público existe hoy sin pedirla, y
    /// una comprobación en cada carga sería peor que un enlace que alguien
    /// pueda cambiar mal y ver enseguida.
    /// </summary>
    public string UrlMercadoPublico { get; set; } =
        "https://www.mercadopublico.cl/Home/BusquedaLicitacion";

    /// <summary>
    /// El enlace solo se pinta si hay una URL detrás.
    ///
    /// Con la vacía no sale nada, en vez de un enlace roto: es lo mismo que se
    /// hace con el resto de datos que pueden faltar.
    /// </summary>
    public bool TieneUrlMercadoPublico =>
        !string.IsNullOrWhiteSpace(UrlMercadoPublico)
        && UrlMercadoPublico.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>"v1" (diario) o "c2" (Compra Ágil).</summary>
    public string ModoConsulta { get; set; } = "v1";

    /// <summary>
    /// Minutos de vida de la caché en memoria.
    ///
    /// Va por debajo del refresco automático (5 min) a propósito: si la caché
    /// durara más, dos de cada tres refrescos no preguntarían nada a la API y
    /// no aparecería ninguna novedad. El detalle de una licitación cambia muy
    /// poco, así que tampoco pierde nada por expires antes.
    /// </summary>
    public int MinutosDeCache { get; set; } = 4;

    /// <summary>
    /// Días de vida de la caché para los días YA PASADOS.
    ///
    /// Separate del de hoy a propósito: una licitación publicada hace meses no
    /// cambia, así que borrarla a los 4 minutos obligaría a repreguntar cada vez
    /// que se mira esa semana, que es lo contrario de lo que se quiere. Con 30
    /// días, volver a una semana ya vista es instantáneo.
    /// </summary>
    public int DiasDeCacheHistorico { get; set; } = 30;

    /// <summary>Espera máxima a Mercado Público por petición.</summary>
    public int SegundosTimeout { get; set; } = 30;

    /// <summary>
    /// Intervalo mínimo entre el principio de dos peticiones a la API.
    ///
    /// <para>
    /// MEDIDO, no elegido. Doce peticiones seguidas contra la API dan
    /// <c>203 429 203 429 203 429 429…</c>: una de cada tres. Con pausas de 400 ms
    /// casi todas fallan, de 800 ms la mitad, y de 1500 ms diez de doce pasan.
    /// La API admite del orden de una petición cada 1,5 s, y lo dice con un
    /// rechazo rápido de unos 280 ms: es un cupo de ráfaga, no un límite de
    /// duración.
    /// </para>
    ///
    /// <para>
    /// Se mide entre principios, no como espera después de cada respuesta. Contra
    /// la API de verdad las respuestas tardan 1,4 a 1,6 s, así que el intervalo se
    /// cumple solo y no se espera nada. Aquí solo se paga cuando algo vuelve más
    /// rápido de lo debido.
    /// </para>
    /// </summary>
    public int SegundosEntreLlamadas { get; set; } = 2;

    /// <summary>
    /// Cada cuánto refresca la pantalla sola mientras está abierta.
    ///
    /// El intervalo se justificó en su día diciendo que "con la API caída casi
    /// siempre" era poco. Esa premisa era FALSA: el `500` era nuestro, no suyo.
    /// El motivo ahora es más simple: la consulta es automática y nadie está
    /// esperando, así que refrescar cada 5 minutos no le cuesta nada a quien mira
    /// y hace que las publicaciones del día aparezcan sin recargar a mano.
    ///
    /// Solo se refresca la semana EN CURSO. Las pasadas no cambian, y
    /// repreguntarlas gastaría cupo a cambio de nada.
    /// </summary>
    public int MinutosEntreRefrescos { get; set; } = 5;

    /// <summary>¿Hay un ticket utilizable?</summary>
    public bool TieneTicket =>
        !string.IsNullOrWhiteSpace(Ticket)
        && !Ticket.StartsWith("CAMBIAR-ESTE-VALOR", StringComparison.Ordinal);

    /// <summary>
    /// Modo de consulta normalizado: "v1", "c2" o "demo".
    ///
    /// "demo" NO llama a Mercado Público: devuelve datos inventados y sirve para
    /// probar la pantalla sin gastar el cupo del ticket. Con él cargado en
    /// producción se estaría mostrando información falsa como si fuera real,
    /// así que el arranque avisa.
    /// </summary>
    public string Modo => ModoConsulta.ToLowerInvariant() switch
    {
        "c2" => "c2",
        "demo" => "demo",
        _ => "v1",
    };

    /// <summary>
    /// ¿Se puede atender una consulta?
    ///
    /// En modo real hacen falta las dos cosas: ticket y código de proveedor. Sin
    /// ticket la API responde 401; sin código no hay a quién preguntar.
    /// </summary>
    public bool Servible =>
        Modo == "demo" || (TieneTicket && TieneCodigoProveedor);

    /// <summary>¿Hay un código de proveedor configurado?</summary>
    public bool TieneCodigoProveedor => !string.IsNullOrWhiteSpace(CodigoProveedor);

    /// <summary>Refresco automático acotado a un valor sensato.</summary>
    public int MinutosRefresco =>
        Math.Clamp(MinutosEntreRefrescos, 1, 60);
}