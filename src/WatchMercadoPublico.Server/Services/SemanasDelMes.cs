namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Semanas de lunes a domingo, recortadas al mes que se está mirando.
///
/// El recorte importa: la semana 1 de octubre de 2026 va del 1 al 4, porque el
/// día 1 es jueves y el domingo es el 4. La semana 2 va del 5 al 11, la 3 del 12
/// al 18, la 4 del 19 al 25 y la 5 del 26 al 31. Ninguna se sale del mes: si se
/// dejara spilling, la última sería "del 26 de octubre al 1 de noviembre", que
/// dentro de un desplegable de octubre es una confusión.
///
/// No son las semanas ISO: esas se numeran por el año y el 1 de enero puede
/// caer en la semana 53 del anterior. Aquí se numeran dentro del mes porque es lo
/// que se elige en pantalla.
///
/// Los SÁBADOS Y DOMINGOS NO SE CONSULTAN. En Chile no se publica nada en fin
/// de semana —comprobado: el 3 y el 4 de octubre de 2026 dieron 0—, así que
/// consultarlos duplicaría las peticiones sin aportar nada. Pero sí se
/// <b>muestran</b> en el rango, porque la semana es de lunes a domingo y hiding
/// el fin de semana daría rangos que no cuadran con el calendario.
/// </summary>
public static class SemanasDelMes
{
    /// <summary>
    /// Cuántas semanas tiene el mes. La primera va del día 1 al primer domingo,
    /// y las siguientes de siete en siete, con la última recortada.
    /// </summary>
    public static int Cuantas(int anio, int mes)
    {
        if (anio is < 1 or > 9999 || mes is < 1 or > 12) return 1;

        var dias = DateTime.DaysInMonth(anio, mes);
        var primera = LongitudDeLaPrimera(anio, mes);

        if (primera >= dias) return 1;

        return 1 + ((dias - primera) + 6) / 7;
    }

    /// <summary>
    /// Días que abarca la semana, de lunes a domingo, recortada al mes.
    /// El primero y el último son los que se muestran en el desplegable.
    /// </summary>
    public static (DateOnly Desde, DateOnly Hasta) Rango(int anio, int mes, int semana)
    {
        // Sin esta guarda, new DateOnly(0, 1, 1) lanza y el fallo sube hasta la
        // pantalla. Un año imposible se trata como el 1 de enero: es un dato
        // que nunca debería llegar, pero tumbar la aplicación por ello no vale.
        if (anio is < 1 or > 9999 || mes is < 1 or > 12)
        {
            var ahora = DateOnly.FromDateTime(DateTime.Today);
            return (ahora, ahora);
        }

        var primeroDelMes = new DateOnly(anio, mes, 1);
        var ultimoDelMes = new DateOnly(anio, mes, DateTime.DaysInMonth(anio, mes));

        if (semana < 1) semana = 1;

        var desplazamiento = 0;
        for (var k = 1; k < semana; k++)
            desplazamiento += k == 1 ? LongitudDeLaPrimera(anio, mes) : 7;

        var desde = primeroDelMes.AddDays(desplazamiento);
        if (desde > ultimoDelMes) desde = ultimoDelMes;

        // La semana 1 acaba en el PRIMER DOMINGO del mes, no siete días después
        // del día 1: si no, un mes que empieza en jueves diría "del 1 al 7" y la
        // semana 2 empezaría el 8, que es lunes pero no el lunes del calendario.
        var hasta = semana == 1
            ? desde.AddDays(LongitudDeLaPrimera(anio, mes) - 1)
            : desde.AddDays(6);

        if (hasta > ultimoDelMes) hasta = ultimoDelMes;

        return (desde, hasta);
    }

    /// <summary>
    /// Días hábiles de la semana: los que caen de lunes a viernes dentro del
    /// rango. Una semana puede quedarse en 0, y la última puede traer menos.
    /// </summary>
    public static List<DateOnly> DiasHabiles(int anio, int mes, int semana)
    {
        var resultado = new List<DateOnly>();
        if (semana < 1 || mes < 1 || mes > 12) return resultado;

        if (semana > Cuantas(anio, mes)) return resultado;

        var (desde, hasta) = Rango(anio, mes, semana);

        for (var dia = desde; dia <= hasta; dia = dia.AddDays(1))
        {
            if (dia.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            resultado.Add(dia);
        }

        return resultado;
    }

    /// <summary>Semana del mes en que cae una fecha.</summary>
    public static int SemanaDe(int anio, int mes, DateOnly fecha)
    {
        if (anio is < 1 or > 9999 || mes is < 1 or > 12) return 1;
        if (fecha.Year != anio || fecha.Month != mes) return 1;

        var (desde, hasta) = Rango(anio, mes, 1);
        var numero = 1;

        while (fecha > hasta && numero < Cuantas(anio, mes))
        {
            numero++;
            (desde, hasta) = Rango(anio, mes, numero);
        }

        return numero;
    }

    /// <summary>Años que se ofrecen en el desplegable: el actual y cinco antes.</summary>
    public static List<int> Anios(int anioActual) =>
        Enumerable.Range(anioActual - 5, 6).OrderByDescending(a => a).ToList();

    /// <summary>
    /// Cuántos días tiene la semana 1: del día 1 al primer domingo.
    ///
    /// OJO con el enum: en .NET <c>DayOfWeek</c> empieza por <b>DOMINGO = 0</b>,
    /// no por lunes. Por eso el cálculo va con módulo y no con una resta: restar
    /// directamente daba un resultado que parecía correcto y desplazaba todas
    /// las semanas un día.
    /// </summary>
    private static int LongitudDeLaPrimera(int anio, int mes)
    {
        var diaDeSemana = (int)new DateOnly(anio, mes, 1).DayOfWeek;

        // Cuántos días hay hasta el domingo, en domingo = 0.
        var hastaElDomingo = (7 - diaDeSemana) % 7;

        return hastaElDomingo + 1;
    }
}