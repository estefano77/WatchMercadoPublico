using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;

namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// La otra mitad del interruptor de fuente: leer lo mismo que devuelve la API,
/// pero desde SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// Va por <b>procedimientos almacenados</b> y solo por ellos. No hay Entity
/// Framework, ni LINQ, ni un modelo de objetos que mapear. El acceso a la base
/// son los dos procedimientos de <c>sql/05-procedimientos-lectura.sql</c> y nada
/// más.
/// </para>
///
/// <para>
/// La razón está escrita en ese fichero: con mapeo, el contrato entre el
/// servidor y la base son las clases; sin mapeo, el contrato son dos
/// procedimientos que se leen enteros en una pantalla. A cambio, un cambio de
/// esquema hay que hacerlo a mano. Se prefiere que el cambio sea visible.
/// </para>
///
/// <para>
/// Y NADA de esto va al cliente. La cadena de conexión se abre aquí, en el
/// servidor. El cliente es WebAssembly y se descarga entero: una cadena ahí
/// quedaría a la vista de cualquiera que abra las herramientas del navegador.
/// </para>
/// </remarks>
public sealed class LectorMercadoPublico
{
    private readonly string _cadena;
    private readonly ILogger<LectorMercadoPublico> _log;

    public LectorMercadoPublico(
        IOptions<MercadoPublicoOpciones> opciones,
        ILogger<LectorMercadoPublico> log)
    {
        _cadena = opciones.Value.CadenaConexionSql;
        _log = log;
    }

    /// <summary>Un día del mes que no se pudo comprobar, con su motivo.</summary>
    public sealed record DiaSinComprobar(DateOnly Fecha, string Motivo);

    /// <summary>
    /// Todo lo que hay de un mes: las licitaciones, sus fichas y sus items.
    /// </summary>
    public sealed record MesLeido(
        int Anio,
        int Mes,
        int DiasHabiles,
        int DiasConsultados,
        int DiasFallidos,
        int DiasPendientes,
        List<Licitacion> Items,
        List<DiaSinComprobar> DiasSinRespuesta,
        DateTimeOffset Consultado);

    /// <summary>
    /// Abre una conexión con la cadena de la configuración.
    /// </summary>
    /// <remarks>
    /// <see cref="SqlConnectionStringBuilder"/> en vez de la cadena cruda porque
    ///asar por ahí deja siempre la aplicación con la misma configuración de red
    /// y tiempos de espera, sin importar lo que traiga la cadena. Un
    /// <c>Connect Timeout</c> ausente convierte un servidor parado en una espera
    /// de quince segundos por petición.
    /// </remarks>
    private SqlConnection Abrir()
    {
        if (string.IsNullOrWhiteSpace(_cadena))
            throw new InvalidOperationException(
                "La fuente de datos es 'sql' pero no hay cadena de conexión " +
                "en MercadoPublico__CadenaConexionSql.");

        var constructor = new SqlConnectionStringBuilder(_cadena)
        {
            // Sin esto, un SQL Server que no responde deja la petición colgada
            // quince segundos por defecto. La lectura a la base es local y
            // rápida: si tarda, es que algo va mal y hay que saberlo ya.
            ConnectTimeout = 15,

            // MARS no hace falta: no hay transacciones ni lotes con varios
            // comandos abiertos a la vez, y activarlo cambia el comportamiento
            // del pool sin aportar nada aquí.
            MultipleActiveResultSets = false,
        };

        return new SqlConnection(constructor.ConnectionString);
    }

    /// <summary>
    /// Las licitaciones de un mes, con el detalle y los items ya armados.
    /// </summary>
    /// <remarks>
    /// Delega en <c>mp.LeeMes</c> y no trae una consulta propia, a propósito.
    /// Toda la lógica de qué es un día no comprobado está dentro del
    /// procedimiento, que es donde se puede ver y probar con T-SQL sin montar
    /// la aplicación.
    /// </remarks>
    public async Task<MesLeido> LeerMesAsync(
        string codigoProveedor, int anio, int mes, CancellationToken ct)
    {
        int diasHabiles = 0, diasConsultados = 0, diasFallidos = 0, diasPendientes = 0;
        var total = 0;

        /* long y no int: LicitacionId es bigint en la base. Declararlo int
           aquí no compila, y "arreglarlo" poniendo int en la base sería
           cambiar el esquema por un error de lectura. */
        var instantes = new List<(long LicitacionId, Licitacion Item)>();
        var detalles = new Dictionary<long, DetalleLicitacion>();

        /* Índice de código por LicitacionId. La versión anterior buscaba con
           instantes.FirstOrDefault(...) por CADA detalle, que es un recorrido
           lineal por cada uno: con veinte licitaciones, veinte-squared
           Comparisons. Con un mes importado completo eso ya son miles, y con
           varios años de historia, cientos de miles por petición. El coste
           crece con el cuadrado de los datos guardados, que es la forma más
           mala posible de que un acceso a base de datos se ponga lento justo
           cuando hay más datos. */
        var codigos = new Dictionary<long, string?>();
        var fallidos = new List<DiaSinComprobar>();

        await using var conexion = Abrir();
        await conexion.OpenAsync(ct);

        await using var comando = conexion.CreateCommand();

        // CommandType.Text con el nombre del procedimiento, y no
        // CommandType.StoredProcedure, por una razón concreta: con
        // StoredProcedure, un nombre mal escrito no da error de compilación sino
        // al ejecutar, y el mensaje habla de un objeto inexistente en vez de
        // decir que el procedimiento no está instalado. Con Text, el error sale
        // al abrir la conexión y se lee en voz alta.
        comando.CommandType = CommandType.StoredProcedure;
        comando.CommandText = "mp.LeeMes";

        comando.Parameters.Add("@codigoProveedor", SqlDbType.NVarChar, 50).Value = codigoProveedor;
        comando.Parameters.Add("@anio", SqlDbType.Int).Value = anio;
        comando.Parameters.Add("@mes", SqlDbType.Int).Value = mes;

        await using var lector = await comando.ExecuteReaderAsync(ct);

        // El procedimiento devuelve cinco conjuntos seguidos y el orden es
        // parte del contrato:
        //   0. Cabecera con los números
        //   1. Días sin comprobar
        //   2. Listado
        //   3. Detalles
        //   4. Items
        //
        // Y el bucle está escrito al revés de como parece natural, con un
        // NextResultAsync() AL FINAL en vez de al principio. No es un gusto:
        // ExecuteReaderAsync() devuelve el lector YA SOBRE EL PRIMER CONJUNTO,
        // así que un while (await NextResultAsync()) se come la cabecera entera
        // y empieza por el segundo. Lo que se leía como cabecera era la lista de
        // días sin comprobar, y el fallo sale como
        //
        //     IndexOutOfRangeException: DiasHabiles
        //
        // que no dice ni qué conjunto es ni que hubo un salto.
        //
        // Los demás casos van por posición y no por nombre porque un
        // SqlDataReader solo conoce las columnas del conjunto que tiene delante:
        // no hay forma de preguntar "¿este conjunto es el del listado?".
        var numero = 0;

        while (true)
        {
            switch (numero)
            {
                case 0:
                    if (await lector.ReadAsync(ct))
                    {
                        diasHabiles = lector.GetInt32(lector.GetOrdinal("DiasHabiles"));
                        diasConsultados = lector.GetInt32(lector.GetOrdinal("DiasConsultados"));
                        diasFallidos = lector.GetInt32(lector.GetOrdinal("DiasFallidos"));
                        diasPendientes = lector.GetInt32(lector.GetOrdinal("DiasPendientes"));
                        total = lector.GetInt32(lector.GetOrdinal("Total"));
                    }
                    break;

                case 1:
                    while (await lector.ReadAsync(ct))
                        fallidos.Add(new DiaSinComprobar(
                            DateOnly.FromDateTime(lector.GetDateTime(lector.GetOrdinal("FechaDia"))),
                            lector.IsDBNull(lector.GetOrdinal("Motivo"))
                                ? "Sin respuesta"
                                : lector.GetString(lector.GetOrdinal("Motivo"))));
                    break;

                case 2:
                    while (await lector.ReadAsync(ct))
                    {
                        var id = lector.GetInt64(lector.GetOrdinal("LicitacionId"));

                        var item = new Licitacion
                        {
                            CodigoExterno = Leer(lector, "CodigoExterno"),
                            Nombre = Leer(lector, "Nombre"),
                            CodigoEstado = LeerEntero(lector, "CodigoEstado"),
                            FechaCierre = LeerInstante(lector, "FechaCierre"),
                            FechaPublicacion = LeerDia(lector, "FechaPublicacion"),
                        };

                        instantes.Add((id, item));
                        codigos[id] = item.CodigoExterno;
                    }
                    break;

                case 3:
                    while (await lector.ReadAsync(ct))
                    {
                        var id = lector.GetInt64(lector.GetOrdinal("LicitacionId"));

                        detalles[id] = new DetalleLicitacion
                        {
                            CodigoExterno = codigos.GetValueOrDefault(id),
                            Nombre = Leer(lector, "Nombre"),
                            Estado = Leer(lector, "Estado"),
                            CodigoEstado = LeerEntero(lector, "CodigoEstado"),
                            Descripcion = Leer(lector, "Descripcion"),
                            Tipo = Leer(lector, "Tipo"),
                            NombreOrganismo = Leer(lector, "NombreOrganismo"),
                            RutOrganismo = Leer(lector, "RutOrganismo"),
                            CodigoOrganismo = LeerEntero(lector, "CodigoOrganismo"),
                            RegionOrganismo = Leer(lector, "RegionOrganismo"),
                            ComunaOrganismo = Leer(lector, "ComunaOrganismo"),
                            MontoEstimado = LeerDecimal(lector, "MontoEstimado"),
                            Moneda = Leer(lector, "Moneda"),
                            Estimacion = LeerEntero(lector, "Estimacion"),
                            FechaCreacion = LeerInstante(lector, "FechaCreacion"),
                            FechaCierre = LeerInstante(lector, "FechaCierre"),
                            FechaPublicacion = LeerInstante(lector, "FechaPublicacion"),
                            FechaAperturaTecnica = LeerInstante(lector, "FechaAperturaTecnica"),
                            FechaAperturaEconomica = LeerInstante(lector, "FechaAperturaEconomica"),
                            FechaAdjudicacion = LeerInstante(lector, "FechaAdjudicacion"),
                            FechaFinal = LeerInstante(lector, "FechaFinal"),
                            NumeroOferentes = LeerEntero(lector, "NumeroOferentes"),
                            NumeroAdjudicacion = Leer(lector, "NumeroAdjudicacion"),
                            UrlActa = Leer(lector, "UrlActa"),
                            NumeroItems = LeerEntero(lector, "NumeroItems"),
                            DiasCierreLicitacion = LeerEntero(lector, "DiasCierreLicitacion"),
                        };
                    }
                    break;

                case 4:
                    while (await lector.ReadAsync(ct))
                    {
                        var id = lector.GetInt64(lector.GetOrdinal("LicitacionId"));
                        if (!detalles.TryGetValue(id, out var detalle)) continue;

                        detalle.Items.Add(new ItemAdjudicado
                        {
                            Correlativo = lector.GetInt32(lector.GetOrdinal("Correlativo")),
                            NombreProducto = Leer(lector, "NombreProducto"),
                            Descripcion = Leer(lector, "Descripcion"),
                            UnidadMedida = Leer(lector, "UnidadMedida"),
                            Cantidad = LeerDecimal(lector, "Cantidad"),
                            CantidadAdjudicada = LeerDecimal(lector, "CantidadAdjudicada"),
                            MontoUnitario = LeerDecimal(lector, "MontoUnitario"),
                            RutProveedor = Leer(lector, "RutProveedor"),
                            NombreProveedor = Leer(lector, "NombreProveedor"),
                        });
                    }
                    break;
            }

            // El salto va AQUÍ, al final. Ver el comentario del bucle: si se
            // pone al principio se pierde el primer conjunto.
            if (!await lector.NextResultAsync(ct))
                break;

            numero++;
        }

        // El detalle se pega a su licitación. Se hace aquí y no en el
        // procedimiento porque en SQL Server no hay referencias entre filas de
        // conjuntos distintos, y porque el orden de los tres conjuntos es el que
        // es: primero todos los listados, después todos los detalles.
        var resultado = new List<Licitacion>(instantes.Count);

        foreach (var (id, item) in instantes)
        {
            if (detalles.TryGetValue(id, out var detalle))
            {
                detalle.CodigoExterno = item.CodigoExterno;
                item.Detalle = detalle;
            }

            if (item.FechaPublicacion is { } dia)
                item.PublicadoTexto = TextosDeFecha.DiaEnPalabras(dia);

            resultado.Add(item);
        }

        if (resultado.Count != total)
            _log.LogWarning(
                "mp.LeeMes devolvió {Reales} licitaciones y la cabecera decía {Declaradas} " +
                "para {Anio}-{Mes:00} del proveedor {Proveedor}. Se usan las reales.",
                resultado.Count, total, anio, mes, codigoProveedor);

        return new MesLeido(
            anio, mes, diasHabiles, diasConsultados, diasFallidos, diasPendientes,
            resultado, fallidos, DateTimeOffset.UtcNow);
    }

    /// <summary>La ficha de una licitación, o null si no se ha importado.</summary>
    public async Task<DetalleLicitacion?> LeerDetalleAsync(
        string codigoProveedor, string codigo, CancellationToken ct)
    {
        await using var conexion = Abrir();
        await conexion.OpenAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.CommandType = CommandType.StoredProcedure;
        comando.CommandText = "mp.LeeDetalle";
        comando.Parameters.Add("@codigoProveedor", SqlDbType.NVarChar, 50).Value = codigoProveedor;
        comando.Parameters.Add("@codigo", SqlDbType.NVarChar, 64).Value = codigo;

        await using var lector = await comando.ExecuteReaderAsync(ct);

        DetalleLicitacion? resultado = null;

        // Mismo bucle al revés que en LeerMesAsync, y por el mismo motivo:
        // ExecuteReaderAsync() ya deja el lector sobre el primer conjunto.
        //   0. ¿Existe?
        //   1. La ficha
        //   2. Los items
        var numero = 0;

        while (true)
        {
            switch (numero)
            {
                case 0:
                    // El primer conjunto solo dice si existe. Un 0 aquí NO es un
                    // error de la base: es que de esa licitación no se ha
                    // importado nada, que la pantalla cuenta como "sin detalle"
                    // y no como "la base está caída".
                    if (!await lector.ReadAsync(ct)) return null;
                    if (lector.GetInt32(lector.GetOrdinal("Existe")) == 0) return null;
                    break;

                case 1:
                    if (!await lector.ReadAsync(ct)) return null;

                    resultado = new DetalleLicitacion
                    {
                        CodigoExterno = codigo,
                        Nombre = Leer(lector, "Nombre"),
                        Estado = Leer(lector, "Estado"),
                        CodigoEstado = LeerEntero(lector, "CodigoEstado"),
                        Descripcion = Leer(lector, "Descripcion"),
                        Tipo = Leer(lector, "Tipo"),
                        NombreOrganismo = Leer(lector, "NombreOrganismo"),
                        RutOrganismo = Leer(lector, "RutOrganismo"),
                        CodigoOrganismo = LeerEntero(lector, "CodigoOrganismo"),
                        RegionOrganismo = Leer(lector, "RegionOrganismo"),
                        ComunaOrganismo = Leer(lector, "ComunaOrganismo"),
                        MontoEstimado = LeerDecimal(lector, "MontoEstimado"),
                        Moneda = Leer(lector, "Moneda"),
                        Estimacion = LeerEntero(lector, "Estimacion"),
                        FechaCreacion = LeerInstante(lector, "FechaCreacion"),
                        FechaCierre = LeerInstante(lector, "FechaCierre"),
                        FechaPublicacion = LeerInstante(lector, "FechaPublicacion"),
                        FechaAperturaTecnica = LeerInstante(lector, "FechaAperturaTecnica"),
                        FechaAperturaEconomica = LeerInstante(lector, "FechaAperturaEconomica"),
                        FechaAdjudicacion = LeerInstante(lector, "FechaAdjudicacion"),
                        FechaFinal = LeerInstante(lector, "FechaFinal"),
                        NumeroOferentes = LeerEntero(lector, "NumeroOferentes"),
                        NumeroAdjudicacion = Leer(lector, "NumeroAdjudicacion"),
                        UrlActa = Leer(lector, "UrlActa"),
                        NumeroItems = LeerEntero(lector, "NumeroItems"),
                        DiasCierreLicitacion = LeerEntero(lector, "DiasCierreLicitacion"),
                    };
                    break;

                case 2:
                    if (resultado is null) continue;

                    while (await lector.ReadAsync(ct))
                    {
                        resultado.Items.Add(new ItemAdjudicado
                        {
                            Correlativo = lector.GetInt32(lector.GetOrdinal("Correlativo")),
                            NombreProducto = Leer(lector, "NombreProducto"),
                            Descripcion = Leer(lector, "Descripcion"),
                            UnidadMedida = Leer(lector, "UnidadMedida"),
                            Cantidad = LeerDecimal(lector, "Cantidad"),
                            CantidadAdjudicada = LeerDecimal(lector, "CantidadAdjudicada"),
                            MontoUnitario = LeerDecimal(lector, "MontoUnitario"),
                            RutProveedor = Leer(lector, "RutProveedor"),
                            NombreProveedor = Leer(lector, "NombreProveedor"),
                        });
                    }
                    break;
            }

            if (!await lector.NextResultAsync(ct))
                break;

            numero++;
        }

        return resultado;
    }

    /// <summary>
    /// Prueba de que la base está viva y responde.
    ///
    /// <para>
    /// Se llama al arrancar y no a la primera petición, para que un servidor de
    /// base de datos que no esté se note al arrancar y no con la primera
    /// pantalla en blanco.
    /// </para>
    /// </summary>
    public async Task<(bool Ok, string? Error)> ComprobarAsync(CancellationToken ct)
    {
        try
        {
            await using var conexion = Abrir();
            await conexion.OpenAsync(ct);

            await using var comando = conexion.CreateCommand();
            comando.CommandType = CommandType.StoredProcedure;
            comando.CommandText = "mp.LeeDetalle";
            comando.Parameters.Add("@codigoProveedor", SqlDbType.NVarChar, 50).Value = "COMPROBACION";
            comando.Parameters.Add("@codigo", SqlDbType.NVarChar, 64).Value = "COMPROBACION";

            await using var lector = await comando.ExecuteReaderAsync(ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Lectura de valores. Todo pasa por aquí y todo comprueba IsDBNull.
    // ------------------------------------------------------------------

    /// <summary>
    /// Lee un texto, o null si la columna está vacía.
    /// </summary>
    /// <remarks>
    /// Nunca devuelve cadena vacía. En el cliente, un <c>Nombre = ""</c> se
    /// pinta como un hueco y un <c>null</c> no se pinta: la diferencia entre
    /// "viene vacío" y "no viene" se pierde si se homogeneiza aquí.
    /// </remarks>
    private static string? Leer(SqlDataReader lector, string columna)
    {
        var i = lector.GetOrdinal(columna);
        return lector.IsDBNull(i) ? null : lector.GetString(i);
    }

    private static int? LeerEntero(SqlDataReader lector, string columna)
    {
        var i = lector.GetOrdinal(columna);
        if (lector.IsDBNull(i)) return null;
        return Convert.ToInt32(lector.GetValue(i));
    }

    /// <summary>
    /// Lee un decimal, o null.
    /// </summary>
    /// <remarks>
    /// Sale de la base como <see cref="decimal"/> y entra como
    /// <see cref="decimal"/>. Si alguna vez se leyera como double, el
    /// 192.000.000 acabaría en 191.999.999,99999997 y la diferencia de un
    /// céntimo entre la base y la API no se vería en ninguna parte.
    /// </remarks>
    private static decimal? LeerDecimal(SqlDataReader lector, string columna)
    {
        var i = lector.GetOrdinal(columna);
        if (lector.IsDBNull(i)) return null;
        return Convert.ToDecimal(lector.GetValue(i));
    }

    /// <summary>
    /// Lee un instante con zona horaria, o null.
    /// </summary>
    /// <remarks>
    /// La columna es <c>datetimeoffset</c> y se lee tal cual. No se pasa a
    /// <see cref="DateTime"/> porque se perdería el desfase de Chile, que es lo
    /// que dice si una licitación cerró a tiempo o un día más tarde.
    /// </remarks>
    private static DateTimeOffset? LeerInstante(SqlDataReader lector, string columna)
    {
        var i = lector.GetOrdinal(columna);
        if (lector.IsDBNull(i)) return null;

        var valor = lector.GetValue(i);
        return valor switch
        {
            DateTimeOffset instante => instante,
            DateTime local => new DateTimeOffset(local),
            _ => DateTimeOffset.Parse(Convert.ToString(valor)!),
        };
    }

    /// <summary>Lee un día sin hora, o null.</summary>
    private static DateOnly? LeerDia(SqlDataReader lector, string columna)
    {
        var i = lector.GetOrdinal(columna);
        if (lector.IsDBNull(i)) return null;
        return DateOnly.FromDateTime(lector.GetDateTime(i));
    }
}
