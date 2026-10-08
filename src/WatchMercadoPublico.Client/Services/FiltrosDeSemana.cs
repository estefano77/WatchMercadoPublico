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
    /// "septiembre de 2026", o "Septiembre de 2026" con
    /// <paramref name="capitalizado"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// El equivalente de <see cref="EnPalabras"/> para cuando lo que se está
    /// mirando es un MES, que es lo que pasa en modo base de datos: ahí no hay
    /// semana, y decir "la semana 1 en octubre de 2026" señalaría a un período
    /// que no existe.
    /// </para>
    /// <para>
    /// Sin el número de semana a propósito. El mes ya no se repite dentro del
    /// año, así que mes y año juntos identifican el período, que es lo que
    /// <see cref="EnPalabras"/> necesitaba el número para conseguir.
    /// </para>
    /// <para>
    /// Y sin palabras alrededor —"el mes de", "la semana 1 en"— porque esto va
    /// DENTRO de una frase: "Estás viendo octubre de 2026". Añadir el artículo
    /// aquí produce "Estás viendo el mes de octubre de 2026", que no es un
    /// error pero es otra frase.
    /// </para>
    /// <para>
    /// Los nombres de mes vienen de fuera, como argumento, por el mismo motivo
    /// que en <see cref="EnPalabras"/>: la lista llega filtrada y una tabla
    /// propia aquí ya salió mal una vez.
    /// </para>
    /// </remarks>
    public static string MesEnPalabras(
        int anio,
        int mes,
        IReadOnlyList<string>? meses,
        bool capitalizado = false)
    {
        var nombre = meses is not null && mes >= 1 && mes <= meses.Count
            ? meses[mes - 1]
            : $"mes {mes}";

        if (!capitalizado) nombre = nombre.ToLowerInvariant();

        return $"{nombre} de {anio}";
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

    /// <summary>
    /// El contador de la cabecera: "1 publicación", "7 publicaciones".
    ///
    /// "publicación" y no "licitación" porque lo que se cuenta es lo que se
    /// PUBLICÓ en la semana, que es justo lo que el usuario pidió mirar. Y
    /// "novedad", que era lo que decía antes, suena a hecho reciente: en
    /// pantalla hay semanas de hace meses y todas sus publicaciones son
    /// viejas, así que la palabra no era cierta.
    ///
    /// Vive aquí y no en el componente porque salía en DOS sitios —el contador
    /// grande y el encabezado de cada grupo por día— y son dos copias del
    /// mismo plural. Es el patrón que ya ha dado dos bugs en este proyecto.
    /// </summary>
    public static string Publicaciones(int total) =>
        total == 1 ? "1 publicación" : $"{total} publicaciones";

    /// <summary>
    /// Las dos piezas del panel de "no se pudo", por separado.
    /// </summary>
    /// <remarks>
    /// El título va entero y la aclaración lleva la palabra "no" marcada en
    /// negrita, así que el texto no se puede devolver como una frase sola sin
    /// meter HTML aquí dentro. Se devuelven las dos piezas y el componente las
    /// junta, que es lo que permite probarlas.
    /// </remarks>
    public sealed record PanelDeFallo(string Titulo, string Periodo);

    /// <summary>
    /// El texto del panel de error, que DEBE hablar en el modo en el que se
    /// está mirando.
    ///
    /// ESTE ERA EL TERCER texto que se quedó hablando en semanas cuando ya no
    /// había semanas. Decía "No se pudo consultar" con un cuerpo que el servidor
    /// escribe como "No se pudo leer la base de datos" —dos verbos distintos en
    /// dos líneas seguidas de la misma caja— y debajo "no haya nada publicado
    /// ESA SEMANA", cuando en modo base de datos el selector de semana no está
    /// en pantalla y el usuario no tiene ninguna semana delante.
    ///
    /// El verbo y el período salen de <paramref name="usaBaseDeDatos"/>, y el
    /// nombre del mes de los desplegables, NO de la respuesta del servidor: en
    /// un fallo no hay respuesta, que es justo cuando el panel aparece. Es el
    /// mismo camino que el aviso de período pendiente, y por el mismo motivo.
    ///
    /// El título en modo base de datos nombra el mes. Un "No se pudo leer" a
    /// secas diría que algo falló pero no sobre qué, y en una pantalla donde el
    /// mes se cambia con un desplegable eso deja al usuario sin saber cuál de
    /// sus meses hay que reintentar.
    /// </summary>
    public static PanelDeFallo TextoDelPanelDeFallo(
        bool usaBaseDeDatos,
        int anio,
        int mes,
        IReadOnlyList<string>? meses)
    {
        if (!usaBaseDeDatos)
            return new PanelDeFallo("No se pudo consultar", "esa semana");

        return new PanelDeFallo(
            $"No se pudo leer el mes de {MesEnPalabras(anio, mes, meses, capitalizado: true)}",
            "este mes");
    }
}
