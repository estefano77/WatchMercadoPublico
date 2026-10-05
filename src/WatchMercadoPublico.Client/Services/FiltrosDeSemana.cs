namespace WatchMercadoPublico.Client.Services;

/// <summary>
/// Lo que la pantalla sabe hacer con la semana que está mirando y la que tiene
/// elegida, sin ser una pantalla.
///
/// Existe porque aquí hubo DOS bugs vivos y ninguno podía ser visto por un test:
/// la lógica estaba en el code-behind de <c>Home.razor</c>, que es un componente
/// de Blazor y no se puede probar sin bUnit. Los dos están aquí abajo, con el
/// motivo escrito, que es la parte que de verdad evita que volv a pasar.
///
/// NO mueve nada: ni pide nada, ni guarda estado. Son funciones puras sobre
/// números y textos, y por eso se pueden comprobar todas. Lo que de verdad
/// depende de Blazor —los renders, el temporizador, el estado de "cargando"— se
/// queda en el componente.
/// </summary>
public static class FiltrosDeSemana
{
    /// <summary>
    /// ¿Lo que dice el desplegable es distinto de lo que está en pantalla?
    ///
    /// COMPARA AÑO, MES Y SEMANA. Comparar solo el número de semana fue un bug
    /// en vivo: el número se repite en todos los periodos, así que la semana 1
    /// de octubre de 2026 y la semana 1 de febrero de 2026 se llaman igual. Al
    /// cambiar de mes o de año, la comparación daba "igual" y no salía el aviso,
    /// con los datos de octubre en pantalla y el desplegable diciendo febrero, y
    /// ninguna señal de que fueran dos cosas distintas.
    ///
    /// La comparación completa va como argumento y no leyéndola de un sitio, para
    /// que sea imposible volver a comparar una parte solo.
    /// </summary>
    public static bool HayCambioPendiente(
        int anioElegido,
        int mesElegido,
        int semanaElegida,
        int anioMostrado,
        int mesMostrado,
        int semanaMostrada) =>
        anioElegido != anioMostrado
        || mesElegido != mesMostrado
        || semanaElegida != semanaMostrada;

    /// <summary>
    /// "la semana 4 en febrero de 2026", o "la Semana 4 en Febrero de 2026" con
    /// <paramref name="capitalizado"/>.
    ///
    /// El número solo no basta: el mismo número de semana existe en todos los
    /// meses, y cambiar de mes en el desplegable produce "la semana 1" frente a
    /// "la semana 5", que no dice nada de qué mes.
    ///
    /// LOS NOMBRES DE MES VIENEN DE FUERA, como argumento, y no de una tabla
    /// propia. La lista llega FILTRADA desde el servidor: en el año en curso solo
    /// tiene los meses hasta el de hoy. Una tabla propia aquí fue, precisamente,
    /// el principio del bug de los nombres de día.
    ///
    /// Si el mes no cabe en la lista se dice "mes 2" y no se imprime un hueco:
    /// que se vea raro es preferible a que salga "la semana 4 de  de 2026".
    /// </summary>
    public static string EnPalabras(
        int anio,
        int mes,
        int semana,
        IReadOnlyList<string>? meses,
        bool capitalizado = false)
    {
        var nombre = meses is not null && mes >= 1 && mes <= meses.Count
            ? meses[mes - 1]
            : $"mes {mes}";

        if (!capitalizado) nombre = nombre.ToLowerInvariant();

        var palabra = capitalizado ? "Semana" : "semana";

        return $"la {palabra} {semana} en {nombre} de {anio}";
    }

    /// <summary>
    /// Cuántos días se CONSULTARON de verdad. Distinto del total de la semana:
    /// si la semana aún no termina, no se han preguntado los días futuros, y
    /// decir "se consultaron 5" cuando se preguntaron 2 es mentir.
    /// </summary>
    public static string DiasConsultadosEnPalabras(int? diasConsultados, bool haySemana) =>
        !haySemana || diasConsultados is null
            ? "los días hábiles de la semana"
            : diasConsultados.Value == 1
                ? "1 día hábil"
                : $"{diasConsultados.Value} días hábiles";

    /// <summary>
    /// Días que no respondieron. Es un CONTEO, no la lista de fechas: el aviso
    /// que lo usa dice "(3 días). Lo que ves está completo, pero puede que
    /// falte algo de esos días", y ahí un "Sin respuesta: 2026-02-24,
    /// 2026-02-25" se leería como un error.
    /// </summary>
    public static string DiasFallidosEnPalabras(int? diasFallidos)
    {
        var n = diasFallidos ?? 0;
        return n == 1 ? "1 día" : $"{n} días";
    }
}
