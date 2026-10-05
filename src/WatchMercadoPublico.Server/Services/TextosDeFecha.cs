namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Los textos de fecha que ve la persona que usa la aplicación, en un solo sitio.
///
/// ESTO VIVE AQUÍ Y NO EN EL CLIENTE, y hay una razón concreta: el cliente
/// tenía su propio array de días y su propio listado de meses, y el array
/// estaba ordenado por lunes mientras se indexaba con <c>DayOfWeek</c>, que en
/// .NET empieza por DOMINGO. Todas las fechas salían corridas un día y un
/// viernes se pintaba como "sábado". No lo detectaba ningún test porque el
/// cliente no tiene proyecto de pruebas.
///
/// La regla que ya se aplicó a los rangos de las semanas —que los calcula solo
/// el servidor— se extiende ahora a los textos. El cliente pinta; no decide
/// cómo se llama un día.
///
/// Al estar aquí, estas funciones sí se prueban.
/// </summary>
public static class TextosDeFecha
{
    /// <summary>
    /// Días en el ORDEN DEL ENUM, no en el orden del calendario.
    ///
    /// OJO: en .NET <c>DayOfWeek.Sunday = 0</c>. El enum empieza por domingo.
    /// Ordenar este array por lunes y luego indexarlo por <c>DayOfWeek</c>
    /// desplaza todos los nombres un día. Es un error que ya se ha cometido dos
    /// veces en este proyecto, y por eso el aviso está en el propio array y no
    /// solo en el comentario de arriba.
    /// </summary>
    private static readonly string[] Dias =
        ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    /// <summary>Nombre del día, en minúsculas: "viernes".</summary>
    public static string NombreDia(DayOfWeek dia) => Dias[(int)dia];

    /// <summary>
    /// Nombre del mes, en minúsculas: "febrero".
    ///
    /// Delega en <see cref="CalendarioDelMes"/> en vez de llevar su propia tabla:
    /// dos listas de meses en el mismo proyecto es exactamente el tipo de
    /// duplicado que después se desincroniza. Aquí solo se baja el primer
    /// carácter, porque los textos en línea van en minúscula y las cabeceras no.
    ///
    /// Un mes fuera de 1 a 12 devuelve vacío en vez de tirar: es un dato que
    /// viene de una ruta y no debe tumbar la pantalla.
    /// </summary>
    public static string NombreMes(int mes) =>
        mes is >= 1 and <= 12 ? CalendarioDelMes.NombreMes(mes).ToLowerInvariant() : "";

    /// <summary>Fecha corta: "27 de febrero".</summary>
    public static string DiaCorto(DateOnly fecha) => $"{fecha.Day} de {NombreMes(fecha.Month)}";

    /// <summary>
    /// El periodo completo de un rango: "Del 23 de febrero al 28 de febrero de
    /// 2026".
    ///
    /// El año va SOLO al final, y se usa el de <paramref name="hasta"/>. Son lo
    /// mismo siempre, porque las semanas van recortadas al mes: ningún rango
    /// cruza de año. Si algún día cruzara, el año del final es el que se lee
    /// bien: "del 29 de diciembre al 4 de enero de 2027".
    /// </summary>
    public static string Periodo(DateOnly desde, DateOnly hasta) =>
        $"Del {DiaCorto(desde)} al {DiaCorto(hasta)} de {hasta.Year}";

    /// <summary>
    /// Fecha con el día de la semana y el año: "viernes 27 de febrero de 2026".
    ///
    /// El año va aquí y no en <see cref="DiaCorto"/> porque el rango de la semana
    /// en el desplegable se lee sin él, y se vería raro: "Semana 4 - 23 al 28
    /// de febrero de 2026". Cada sitio decide si lo necesita.
    /// </summary>
    public static string DiaEnPalabras(DateOnly fecha) =>
        $"{NombreDia(fecha.DayOfWeek)} {DiaCorto(fecha)} de {fecha.Year}";

    /// <summary>
    /// Rango tal como se muestra en el desplegable: "23 al 28 de febrero".
    ///
    /// Si el rango cruza de mes —solo pasa con la última semana de mes corto,
    /// que llega hasta el día 1 del siguiente— se escribe el mes en las dos
    /// partes: "26 de octubre al 1 de noviembre". Poner el mes solo al final
    /// daría a entender que el día 1 de noviembre es de octubre.
    /// </summary>
    public static string Rango(DateOnly desde, DateOnly hasta)
    {
        if (desde.Month == hasta.Month && desde.Year == hasta.Year)
            return $"{desde.Day} al {hasta.Day} de {NombreMes(hasta.Month)}";

        return $"{DiaCorto(desde)} al {DiaCorto(hasta)}";
    }
}