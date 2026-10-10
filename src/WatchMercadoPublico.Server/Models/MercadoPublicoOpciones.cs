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
    /// RUT de la empresa vigilada. Es lo ÚNICO que queda de la empresa en la
    /// configuración, y es la clave con la que se busca.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Antes aquí había nombre, RUT, código de proveedor y URL, todo junto. Los
    /// otros tres se fueron a <c>MpEmpresa</c>, y con ellos desapareció una
    /// posibilidad de fallo que era real: que el RUT y el código no describieran
    /// la misma empresa. No lo llegaron a hacer —el RUT que estaba escrito
    /// pertenecía a "SISTEMAS MODULARES DE COMPUTACION SPA" y el nombre de al
    /// lado era "SMC SPA"—, pero podía, y no había nada que lo detectara.
    /// </para>
    ///
    /// <para>
    /// <b>El formato importa y no es el que parece.</b> La API acepta
    /// <c>DD.DDD.DDD-D</c> y nada más, medido el 10 de octubre de 2026: con un
    /// dígito delante (<c>1.234.567-8</c>) o tres (<c>123.456.789-5</c>) responde
    /// 500, igual que sin puntos. <see cref="MercadoPublicoCliente.RutBienFormado"/>
    /// lo comprueba antes de gastar la llamada, y el guion de carga lo comprueba
    /// antes de insertar nada.
    /// </para>
    /// </remarks>
    public string RutEmpresa { get; set; } = "";

    /// <summary>
    /// A dónde lleva el enlace "Ir a Mercado Público" de la cabecera.
    /// </summary>
    /// <remarks>
    /// ESTA SE QUITÓ DE AQUÍ. La URL de la cabecera es un dato de la empresa, y
    /// los datos de la empresa ahora están en <c>MpEmpresa</c>. La API no la
    /// devuelve —<c>Empresas/BuscarProveedor</c> solo trae código y nombre—, así
    /// que la fila la escribe <c>cargar-base-remota.ps1</c> con el valor que
    /// hace falta.
    ///
    /// Sigue siendo configurable, pero en la base. Y eso mejora una cosa: cambiar
    /// el destino del enlace ya no obliga a tocar un fichero de configuración, y
    /// no ha pasado por un reinicio.
    ///
    /// El valor por defecto ahora vive en
    /// <see cref="EmpresaActual.UrlPorDefecto"/>, que es donde lo usa el modo API
    /// —que no tiene tabla donde leerlo—. Cuidado con eso: está duplicado en
    /// PowerShell, y el aviso para quien lo cambie está en el comentario de
    /// aquella constante.
    /// </remarks>
    public string UrlMercadoPublicoObsoleta { get; set; } = "";

    /// <summary>
    /// El enlace solo se pinta si hay una URL detrás.
    ///
    /// Con la vacía no sale nada, en vez de un enlace roto: es lo mismo que se
    /// hace con el resto de datos que pueden faltar.
    /// </summary>
    public bool TieneUrlMercadoPublico =>
        !string.IsNullOrWhiteSpace(UrlMercadoPublicoObsoleta)
        && UrlMercadoPublicoObsoleta.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

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
    /// Lo que hace falta para poder preguntar a Mercado Público, sin mirar la
    /// empresa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existió aquí una propiedad <c>Servible</c> que respondía a "se puede
    /// atender una consulta". Ya no puede estar en las opciones, porque una de
    /// las dos cosas que exigía —el código de proveedor— salió de la
    /// configuración y ahora está en <c>MpEmpresa</c>.
    /// </para>
    ///
    /// <para>
    /// Y NO se ha puesto aquí una versión nueva que solo mire la configuración.
    /// Sería una propiedad que nadie usa: los puntos donde importa preguntan a
    /// <see cref="EmpresaVigilada"/>, que sí sabe las dos cosas. Las piezas sueltas
    /// —<see cref="UsaBaseDeDatos"/>, <see cref="BaseDeDatosUtilizable"/> y
    /// <see cref="TieneTicket"/>— ya están, y son las que se prueban por
    /// separado.
    /// </para>
    /// </remarks>
    public bool PuedePreguntarALaApi => Modo == "demo" || TieneTicket;

    /// <summary>Refresco automático acotado a un valor sensato.</summary>
    public int MinutosRefresco =>
        Math.Clamp(MinutosEntreRefrescos, 1, 60);
}