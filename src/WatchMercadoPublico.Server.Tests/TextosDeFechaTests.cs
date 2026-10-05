using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Los textos de fecha se calculan en el servidor, y aquí se comprueban.
///
/// Esta clase existe por un bug concreto: el cliente tenía su propio array de
/// días, ordenado por lunes pero indexado por <c>DayOfWeek</c>, que en .NET
/// empieza por DOMINGO. Todas las fechas salían corridas un día: un viernes se
/// pintaba como "sábado". No lo detectó ningún test porque el cliente no tiene
/// proyecto de pruebas, así que el arreglo definitivo no es solo corregir el
/// array: es que el texto de las fechas viva donde sí se puede probar.
/// </summary>
public class TextosDeFechaTests
{
    [Fact]
    public void El_viernes_27_de_febrero_de_2026_es_viernes()
    {
        // La fecha concreta que se vio mal en pantalla. Anclarla es lo que
        // impide que el desplazamiento vuelva a colarse: si alguien vuelve a
        // ordenar el array por lunes, esta prueba se pone roja el mismo día.
        Assert.Equal(DayOfWeek.Friday, new DateOnly(2026, 2, 27).DayOfWeek);

        Assert.Equal("viernes", TextosDeFecha.NombreDia(DayOfWeek.Friday));
        Assert.Equal(
            "viernes 27 de febrero de 2026",
            TextosDeFecha.DiaEnPalabras(new DateOnly(2026, 2, 27)));
    }

    [Theory]
    // Una semana entera de febrero de 2026, día a día. El 1 es domingo y el
    // 28 es sábado, así que la semana cubre los dos extremos del enum.
    [InlineData("2026-02-23", "lunes 23 de febrero de 2026")]
    [InlineData("2026-02-24", "martes 24 de febrero de 2026")]
    [InlineData("2026-02-25", "miércoles 25 de febrero de 2026")]
    [InlineData("2026-02-26", "jueves 26 de febrero de 2026")]
    [InlineData("2026-02-27", "viernes 27 de febrero de 2026")]
    [InlineData("2026-02-28", "sábado 28 de febrero de 2026")]
    public void Cada_dia_de_la_semana_lleva_su_nombre(string fecha, string esperado)
    {
        Assert.Equal(esperado, TextosDeFecha.DiaEnPalabras(DateOnly.Parse(fecha)));
    }

    [Fact]
    public void El_dia_en_palabras_lleva_el_ano()
    {
        // Sin el año, un "lunes 28 de septiembre" no dice de qué se está
        // hablando, y el caso habitual es que la semana sea de hace meses.
        var texto = TextosDeFecha.DiaEnPalabras(new DateOnly(2026, 9, 28));

        Assert.Equal("lunes 28 de septiembre de 2026", texto);
        Assert.EndsWith("de 2026", texto);
    }

    [Fact]
    public void El_periodo_lleva_el_ano_al_final()
    {
        // El formato pedido: el año una sola vez, al final, para no repetirlo en
        // las dos mitades del rango.
        Assert.Equal(
            "Del 28 de septiembre al 30 de septiembre de 2026",
            TextosDeFecha.Periodo(new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 30)));

        Assert.Equal(
            "Del 23 de febrero al 28 de febrero de 2026",
            TextosDeFecha.Periodo(new DateOnly(2026, 2, 23), new DateOnly(2026, 2, 28)));
    }

    [Fact]
    public void El_periodo_usa_el_ano_del_final()
    {
        // Las semanas van recortadas al mes, así que esto no se da. Pero si
        // algún día un rango cruzara el año, el del final es el que se lee bien:
        // "al 4 de enero de 2027", no "de 2026".
        Assert.Equal(
            "Del 29 de diciembre al 4 de enero de 2027",
            TextosDeFecha.Periodo(new DateOnly(2026, 12, 29), new DateOnly(2027, 1, 4)));
    }

    [Fact]
    public void El_el_domingo_es_el_primer_elemento_del_enum()
    {
        // Esta es la trampa concreta. Si el array se vuelve a ordenar por lunes,
        // el domingo pasará a ser el índice 6 y esto falla.
        Assert.Equal(0, (int)DayOfWeek.Sunday);
        Assert.Equal(1, (int)DayOfWeek.Monday);
        Assert.Equal(6, (int)DayOfWeek.Saturday);

        Assert.Equal("domingo", TextosDeFecha.NombreDia(DayOfWeek.Sunday));
        Assert.Equal("lunes", TextosDeFecha.NombreDia(DayOfWeek.Monday));
        Assert.Equal("sábado", TextosDeFecha.NombreDia(DayOfWeek.Saturday));
    }

    [Fact]
    public void Los_dos_extremos_del_enum_salen_bien_en_la_pantalla()
    {
        // El domingo y el sábado son los que más se confunden con los del
        // otro extremo, así que se comprueban por separado y en una sola fecha
        // que los contiene a los dos.
        Assert.Equal(
            "domingo 1 de febrero de 2026",
            TextosDeFecha.DiaEnPalabras(new DateOnly(2026, 2, 1)));

        Assert.Equal(
            "sábado 28 de febrero de 2026",
            TextosDeFecha.DiaEnPalabras(new DateOnly(2026, 2, 28)));
    }

    [Theory]
    [InlineData(1, "enero")]
    [InlineData(2, "febrero")]
    [InlineData(9, "septiembre")]
    [InlineData(12, "diciembre")]
    public void Los_meses_se_llaman_bien(int mes, string esperado)
    {
        Assert.Equal(esperado, TextosDeFecha.NombreMes(mes));
    }

    [Fact]
    public void Un_mes_que_no_existe_no_tira_excepcion()
    {
        // Viene de una ruta: no debe tumbar la pantalla ni el endpoint.
        Assert.Equal("", TextosDeFecha.NombreMes(0));
        Assert.Equal("", TextosDeFecha.NombreMes(13));
    }

    [Fact]
    public void El_rango_dentro_del_mes_no_repite_el_mes()
    {
        // El desplegable de semana NO lleva el año: ahí el mes ya está en el
        // nombre del desplegable de arriba y "Semana 4 - 23 al 28 de febrero de
        // 2026" queda raro. El año se añade solo donde hace falta, que es en el
        // contador y en el encabezado de los grupos.
        Assert.Equal(
            "23 al 28 de febrero",
            TextosDeFecha.Rango(new DateOnly(2026, 2, 23), new DateOnly(2026, 2, 28)));
    }

    [Fact]
    public void Un_rango_que_cruza_de_mes_dice_el_mes_en_las_dos_partes()
    {
        // La última semana de mes corto llega hasta el día 1 del siguiente.
        // Poner el mes solo al final daría a entender que el 1 de noviembre es
        // de octubre.
        Assert.Equal(
            "26 de octubre al 1 de noviembre",
            TextosDeFecha.Rango(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void El_texto_de_la_semana_no_lleva_el_ano()
    {
        // El desplegable se pinta con este texto, así que tiene que llegar
        // escrito. Se comprueba porque el cliente tuvo una versión propia que
        // salía corrida un día.
        Assert.Equal(
            "2 al 8 de febrero",
            TextosDeFecha.Rango(new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 8)));
    }
}
