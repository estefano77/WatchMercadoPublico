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
    /// Minutos que se recuerda que un día falló, antes de volver a preguntarlo.
    ///
    /// <para>
    /// Es un plazo de memoria del fallo, no de los datos: no guarda ninguna
    /// licitación, solo que ese día ya se preguntó y no respondió. El día sigue
    /// saliendo como fallido y la pantalla sigue diciendo que puede faltar algo.
    /// </para>
    ///
    /// <para>
    /// Tiene que ser MAYOR que <see cref="MinutosEntreRefrescos"/>, y por eso no
    /// son los mismos 4 minutos de <see cref="MinutosDeCache"/>. Con el mismo
    /// plazo, cada refresco automático llegaría justo cuando el fallo caduca y
    /// volvería a subir la escalera de seis intentos entera, que es justamente lo
    /// que esto evita.
    /// </para>
    ///
    /// <para>
    /// El valor viene de una medición en MonsterASP: el día que fallaba costaba
    /// 60 de los 65 segundos de cada consulta, y no porque la API tardara, sino
    /// porque las esperas entre reintentos son de 2, 4, 8, 16 y 30 s y ese día
    /// fallaba siempre y deprisa.
    /// </para>
    /// </summary>
    public int MinutosDeCacheFallo { get; set; } = 15;

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

    /// <summary>
    /// De dónde salen los datos: <c>api</c> o <c>sql</c>.
    ///
    /// <para>
    /// Es un campo APARTE de <see cref="ModoConsulta"/>, y no un valor más de
    /// ese, porque son dos cosas distintas que se confunden al leerlo. La
    /// fuente dice DE DÓNDE sale el dato; el modo dice CÓMO se consulta la API.
    /// Con un solo campo habría combinaciones que no significan nada: ¿qué es
    /// "sql" con <c>c2</c>, que es Compra Ágil? ¿Y "demo" con fuente sql?
    /// </para>
    ///
    /// <para>
    /// Un valor desconocido cae en "api", que es lo que hacía antes. Es
    /// deliberado: si alguien escribe "sqlserver" en vez de "sql", la
    /// aplicación avisa por el log en vez de quedarse en silencio. Lo que no
    /// hace es dejar de funcionar.
    /// </para>
    /// </summary>
    public string FuenteDatos { get; set; } = "api";

    /// <summary>La fuente normalizada, para comparar.</summary>
    public string Fuente =>
        (FuenteDatos ?? "").Trim().ToLowerInvariant() switch
        {
            "sql" or "sqlserver" or "basedatos" => "sql",
            _ => "api",
        };

    /// <summary>¿Se leen los datos de la base de datos?</summary>
    public bool UsaBaseDeDatos => Fuente == "sql";

    /// <summary>
    /// Cadena de conexión a SQL Server.
    ///
    /// <para>
    /// Va en la configuración y NUNCA en el código. Contiene usuario y
    /// contraseña, y el repositorio es público: una cadena de conexión escrita
    /// en un fichero versionado es una credencial filtrada.
    /// </para>
    ///
    /// <para>
    /// Y NO viaja al cliente. El cliente es WebAssembly y se descarga entero:
    /// una cadena ahí quedaría a la vista de cualquiera que abra las
    /// herramientas del navegador. Por eso la conexión se abre en el SERVIDOR,
    /// nunca en el cliente, por mucho que el interruptor se toque desde la
    /// configuración.
    /// </para>
    /// </summary>
    public string CadenaConexionSql { get; set; } = "";

    /// <summary>
    /// Minutos entre importaciones automáticas, cuando la fuente es la base de
    /// datos.
    ///
    /// <para>
    /// Con cero no se programa ninguna. Es el valor por defecto a propósito:
    /// una tarea que sale sola descarga de la API y gasta cupo del ticket sin
    /// que nadie la haya pedido, y eso no debe pasar por abrir la aplicación.
    /// </para>
    /// </summary>
    public int MinutosEntreIngestas { get; set; }

    /// <summary>
    /// ¿Se puede leer de la base de datos de verdad?
    ///
    /// <para>
    /// No basta con que la fuente sea "sql": hace falta también la cadena de
    /// conexión. Si falta, <see cref="UsaBaseDeDatos"/> sigue siendo cierto
    /// porque es lo que se pidió, y esto avisa de que además falta lo otro.
    /// Con las dos cosas a medias, la pantalla no tiene forma de distinguir
    /// "no hay nada" de "no se pudo leer", que es justo lo que no se puede
    /// decir.
    /// </para>
    /// </summary>
    public bool BaseDeDatosUtilizable =>
        UsaBaseDeDatos && !string.IsNullOrWhiteSpace(CadenaConexionSql);

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
    /// <para>
    /// Lo que hace falta depende de la fuente, y por eso la condición está
    /// partida y no es un "y" de tres cosas.
    /// </para>
    ///
    /// <para>
    /// En modo API hacen falta ticket y código de proveedor: sin ticket la API
    /// responde 401 y sin código no hay a quién preguntar.
    /// </para>
    ///
    /// <para>
    /// En modo base de datos NO hace falta ticket, porque no se pregunta nada a
    /// Mercado Público: lo que se necesita es poder abrir la conexión y saber a
    /// qué empresa pertenece lo que se lee. Pedir ticket ahí no era un detalle
    /// menor: <c>Servible</c> es lo que enciende el aviso de "la aplicación no
    /// está configurada" y lo que decide si la pantalla pinta o no los
    /// resultados, así que con la condición antigua una instalación correcta en
    /// modo base de datos y sin ticket se quedaba mostrando un aviso que no
    /// aplicaba y un hueco vacío debajo.
    /// </para>
    /// </summary>
    public bool Servible =>
        Modo == "demo"
        || (UsaBaseDeDatos
            ? BaseDeDatosUtilizable && TieneCodigoProveedor
            : TieneTicket && TieneCodigoProveedor);

    /// <summary>¿Hay un código de proveedor configurado?</summary>
    public bool TieneCodigoProveedor => !string.IsNullOrWhiteSpace(CodigoProveedor);

    /// <summary>Refresco automático acotado a un valor sensato.</summary>
    public int MinutosRefresco =>
        Math.Clamp(MinutosEntreRefrescos, 1, 60);
}