namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Semanas de lunes a domingo, recortadas al mes que se está mirando.
///
/// El recorte importa: la semana 1 de octubre de 2026 va del 1 al 4, porque el
/// día 1 es jueves y el domingo es el 4. La semana 2 va del 5 al 11, la 3 del 12
/// al 18, la 4 del 19 al 25 y la 5 del 26 al 31. Ninguna se sale del mes: si la
/// última se dejara pasar al mes siguiente, sería "del 26 de octubre al 1 de
/// noviembre", que dentro de un desplegable de octubre es una confusión.
///
/// No son las semanas ISO: esas se numeran por el año y el 1 de enero puede
/// caer en la semana 53 del anterior. Aquí se numeran dentro del mes porque es lo
/// que se elige en pantalla.
///
/// CASO ESPECÍFICO: un mes que empieza en DOMINGO.
///
/// Febrero de 2026 empieza domingo. El tramo del día 1 al primer domingo es
/// solo ese domingo, cero días hábiles, y dejarlo como "semana 1" gastaba un
/// número del desplegable sin dejar consultar nada: la pantalla llegaba a decir
/// "se consultaron 0 días hábiles". Ese domingo es, en realidad, la cola de la
/// semana del mes ANTERIOR, así que se descarta y la semana 1 pasa a ser la del
/// lunes siguiente. Con eso febrero queda con 4 semanas: del 2 al 8, del 9 al 15,
/// del 16 al 22 y del 23 al 28, que es lo que se espera al mirar el desplegable.
///
/// Los SÁBADOS Y DOMINGOS NO SE CONSULTAN. En Chile no se publica nada en fin
/// de semana —comprobado: el 3 y el 4 de octubre de 2026 dieron 0—, así que
/// consultarlos duplicaría las peticiones sin aportar nada. Pero sí se
/// <c>muestran</c> en el rango, porque la semana es de lunes a domingo y ocultar
/// el fin de semana daría rangos que no cuadran con el calendario.
/// </summary>
public static class SemanasDelMes
{
    /// <summary>
    /// Cuántas semanas tiene el mes, ya-sea con el descarte de los meses que
    /// empiezan en domingo.
    /// </summary>
    public static int Cuantas(int anio, int mes)
    {
        if (anio is < 1 or > 9999 || mes is < 1 or > 12) return 1;

        var (desde, hasta) = PrimeraSemana(anio, mes);
        var ultimo = new DateOnly(anio, mes, DateTime.DaysInMonth(anio, mes));

        var largo = hasta.Day - desde.Day + 1;
        var restantes = ultimo.Day - desde.Day + 1 - largo;

        if (restantes <= 0) return 1;

        return 1 + ((restantes + 6) / 7);
    }

    /// <summary>
    /// Días que abarca la semana, de lunes a domingo, recortada al mes.
    /// El primero y el último son los que se muestran en el desplegable.
    /// </summary>
    public static (DateOnly Desde, DateOnly Hasta) Rango(int anio, int mes, int semana)
    {
        // Sin esta guarda, new DateOnly(0, 1, 1) lanza y el fallo sube hasta la
        // pantalla. Un año imposible se trata como hoy: es un dato que nunca
        // debería llegar, pero tumbar la aplicación por ello no vale.
        if (anio is < 1 or > 9999 || mes is < 1 or > 12)
        {
            var hoy = DateOnly.FromDateTime(DateTime.Today);
            return (hoy, hoy);
        }

        var ultimo = new DateOnly(anio, mes, DateTime.DaysInMonth(anio, mes));
        if (semana < 1) semana = 1;

        var (primeraDesde, primeraHasta) = PrimeraSemana(anio, mes);

        // La semana 2 arranca el día DESPUÉS del final de la semana 1, y de ahí
        // cada siete. Ojo: no es "siete días después de donde acabó la 1", que
        // saltaría un día entero. Con octubre, cuya semana 1 va del 1 al 4, la
        // semana 2 empieza el 5: el +1 va antes del salto de semana.
        var inicio = primeraHasta.AddDays(1 + (7 * (semana - 2)));
        var hasta = inicio.AddDays(6);

        if (semana == 1)
        {
            // La semana 1 no dura siete días: dura lo que queda hasta el primer
            // domingo, recortada al mes.
            inicio = primeraDesde;
            hasta = primeraHasta;
        }

        if (inicio > ultimo) inicio = ultimo;
        if (hasta > ultimo) hasta = ultimo;
        if (hasta < inicio) hasta = inicio;

        return (inicio, hasta);
    }

    /// <summary>
    /// Días hábiles de la semana: los que caen de lunes a viernes dentro del
    /// rango. La última puede traer menos.
    /// </summary>
    public static List<DateOnly> DiasHabiles(int anio, int mes, int semana)
    {
        var resultado = new List<DateOnly>();
        if (anio is < 1 or > 9999 || mes is < 1 or > 12) return resultado;
        if (semana < 1 || semana > Cuantas(anio, mes)) return resultado;

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

        var total = Cuantas(anio, mes);

        for (var s = 1; s <= total; s++)
        {
            var (desde, hasta) = Rango(anio, mes, s);
            if (fecha >= desde && fecha <= hasta) return s;
        }

        // Solo llega aquí el día descartado: el 1 de un mes que empieza en
        // domingo. Pertenece a la semana del mes anterior, así que aquí no hay
        // respuesta correcta; se devuelve la 1 para no inventar una quinta.
        return 1;
    }

    /// <summary>Años que se ofrecen en el desplegable: el actual y cinco antes.</summary>
    public static List<int> Anios(int anioActual) =>
        Enumerable.Range(anioActual - 5, 6).OrderByDescending(a => a).ToList();

    /// <summary>
    /// La semana 1 del mes, con su largo. Es la única fuente de verdad: el
    /// número de semanas y el rango salen de aquí, así que no pueden discrepar.
    /// </summary>
    private static (DateOnly Desde, DateOnly Hasta) PrimeraSemana(int anio, int mes)
    {
        var primero = new DateOnly(anio, mes, 1);

        // OJO con el enum: en .NET <c>DayOfWeek</c> empieza por DOMINGO = 0, no
        // por lunes. Calcularlo con un "7 menos el día de la semana" daba un
        // resultado que parecía correcto y desplazaba todas las semanas un día.
        var hastaElDomingo = (7 - (int)primero.DayOfWeek) % 7;
        var primerDomingo = primero.AddDays(hastaElDomingo);

        var tieneUtil = false;
        for (var d = primero; d <= primerDomingo; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            tieneUtil = true;
            break;
        }

        // Sin días útiles el tramo es solo la cola de la semana anterior: se
        // descarta y la semana 1 arranca el lunes siguiente.
        return tieneUtil
            ? (primero, primerDomingo)
            : (primerDomingo.AddDays(1), primerDomingo.AddDays(7));
    }
}
