namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Semanas DENTRO de un mes: la 1 va del 1 al 7, la 2 del 8 al 14, y así.
/// La última puede ser más corta.
///
/// No es la semana ISO, que no coincide con los meses: el 1 de enero puede caer
/// en la semana 53 del año anterior. Aquí las semanas van numeradas dentro del
/// mes porque es lo que se elige en pantalla ("semana 1 de septiembre"), y
/// porque la semana ISO partiría un mes en dos listas confusas.
///
/// Los SÁBADOS Y DOMINGOS NO SE CONSULTAN. En Chile no se publica nada en fin
/// de semana —comprobado: el 3 y el 4 de octubre de 2026 dieron 0—, así que
/// consultarlos duplicaría las peticiones de cada semana sin aportar nada.
/// </summary>
public static class SemanasDelMes
{
    /// <summary>Cuántas semanas tiene el mes: los días divididos entre 7, redondeando hacia arriba.</summary>
    public static int Cuantas(int anio, int mes)
    {
        var dias = DateTime.DaysInMonth(anio, mes);
        return (dias + 6) / 7;
    }

    /// <summary>
    /// Días hábiles del rango, como fechas. La última semana puede traer menos
    /// días, y una semana entera puede quedarse en 0 si cae en fin de semana.
    /// </summary>
    public static List<DateOnly> DiasHabiles(int anio, int mes, int semana)
    {
        var resultado = new List<DateOnly>();
        if (semana < 1 || mes < 1 || mes > 12) return resultado;

        var total = Cuantas(anio, mes);
        if (semana > total) return resultado;

        var primero = new DateOnly(anio, mes, (semana - 1) * 7 + 1);

        for (var i = 0; i < 7; i++)
        {
            var dia = primero.AddDays(i);

            if (dia.Year != anio || dia.Month != mes) break;
            if (dia.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            resultado.Add(dia);
        }

        return resultado;
    }

    /// <summary>El día 1 de la semana, aunque caiga en sábado (para el rótulo del rango).</summary>
    public static DateOnly PrimerDia(int anio, int mes, int semana) =>
        new(anio, mes, (semana - 1) * 7 + 1);

    /// <summary>
    /// Semana del mes en que cae una fecha.
    ///
    /// Es aritmética, no formato: "w" en un formato personalizado no existe, y
    /// hacerlo con GetWeekOfMonth daría semanas ISO en las que el día 1 cae en
    /// la semana anterior.
    /// </summary>
    public static int SemanaDe(int anio, int mes, DateOnly fecha) =>
        ((fecha.Day - 1) / 7) + 1;

    /// <summary>Años que se ofrecen en el desplegable: el actual y cinco antes.</summary>
    public static List<int> Anios(int anioActual) =>
        Enumerable.Range(anioActual - 5, 6).OrderByDescending(a => a).ToList();
}