using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Las semanas van de LUNES A DOMINGO, recortadas al mes.
///
/// Es la regla que hace que el desplegable diga cosas como "5 al 11 de
/// Octubre": el 5 de octubre de 2026 es lunes y el 11 es el domingo de esa
/// semana. Con el reparto en trozos de 1-7, el 5 caería en la semana 1 y el
/// rangodiría "1 al 7", que no cuadra con el calendario ni con lo que la gente
/// entiende por una semana.
///
/// El recorte al mes también es deliberado: la última semana no se sale al mes
/// siguiente, porque dentro de un desplegable de octubre un "26 de octubre al 1
/// de noviembre" confunde más de lo que ayuda.
/// </summary>
public class SemanasDelMesTests
{
    // Octubre de 2026: el día 1 es jueves y el 4 es domingo. Son las fechas que
    // se han usado de verdad al desarrollar, así que están fijadas aquí.
    private const int Anio = 2026;
    private const int Mes = 10;

    [Fact]
    public void Octubre_de_2026_tiene_5_semanas()
    {
        // Del 1 al 4, del 5 al 11, del 12 al 18, del 19 al 25 y del 26 al 31.
        Assert.Equal(5, SemanasDelMes.Cuantas(Anio, Mes));
    }

    [Theory]
    // El caso que pidió el usuario, literalmente.
    [InlineData(2, 5, 11)]
    [InlineData(1, 1, 4)]   // Arranca en jueves: solo cuatro días
    [InlineData(3, 12, 18)]
    [InlineData(4, 19, 25)]
    [InlineData(5, 26, 31)] // Recortada: el lunes siguiente sería el 2 de noviembre
    public void Cada_semana_va_de_lunes_a_domingo_y_recortada_al_mes(
        int semana, int diaDesde, int diaHasta)
    {
        var (desde, hasta) = SemanasDelMes.Rango(Anio, Mes, semana);

        Assert.Equal(diaDesde, desde.Day);
        Assert.Equal(diaHasta, hasta.Day);
        Assert.Equal(Anio, desde.Year);
        Assert.Equal(Mes, desde.Month);
        Assert.Equal(Mes, hasta.Month);

        // Lunes el primero y domingo el último, salvo cuando el recorte obliga.
        if (semana > 1)
            Assert.Equal(DayOfWeek.Monday, desde.DayOfWeek);
    }

    [Fact]
    public void Ninguna_semana_se_sale_del_mes()
    {
        var total = SemanasDelMes.Cuantas(Anio, Mes);

        for (var s = 1; s <= total; s++)
        {
            var (desde, hasta) = SemanasDelMes.Rango(Anio, Mes, s);

            Assert.InRange(desde.Day, 1, DateTime.DaysInMonth(Anio, Mes));
            Assert.InRange(hasta.Day, 1, DateTime.DaysInMonth(Anio, Mes));
            Assert.True(hasta >= desde);
        }
    }

    [Fact]
    public void Las_semanas_se_cubren_sin_saltos_ni_solapamientos()
    {
        var total = SemanasDelMes.Cuantas(Anio, Mes);
        var (primera, _) = SemanasDelMes.Rango(Anio, Mes, 1);

        Assert.Equal(1, primera.Day);

        var (_, finAnterior) = SemanasDelMes.Rango(Anio, Mes, 1);

        for (var s = 2; s <= total; s++)
        {
            var (desde, hasta) = SemanasDelMes.Rango(Anio, Mes, s);

            Assert.Equal(finAnterior.AddDays(1), desde);
            Assert.True(hasta >= desde);

            finAnterior = hasta;
        }

        // Y la última cierra el mes, ni un día antes ni uno después.
        var (_, ultimo) = SemanasDelMes.Rango(Anio, Mes, total);
        Assert.Equal(DateTime.DaysInMonth(Anio, Mes), ultimo.Day);
    }

    [Fact]
    public void Solo_se_consultan_lunes_a_viernes_pero_el_rango_los_muestra()
    {
        var total = SemanasDelMes.Cuantas(Anio, Mes);

        for (var s = 1; s <= total; s++)
        {
            var (desde, hasta) = SemanasDelMes.Rango(Anio, Mes, s);
            var habiles = SemanasDelMes.DiasHabiles(Anio, Mes, s);

            // Ningún sábado ni domingo se consulta.
            Assert.DoesNotContain(habiles, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);

            // Los hábiles caen dentro del rango que se muestra.
            foreach (var d in habiles)
            {
                Assert.InRange(d, desde, hasta);
            }

            // Y el rango completo siempre tiene al menos un hábil.
            Assert.NotEmpty(habiles);
        }
    }

    [Fact]
    public void Los_sabados_y_domingos_del_rango_no_se_piden_pero_su_existencia_se_deduce_del_rango()
    {
        var (desde, hasta) = SemanasDelMes.Rango(Anio, Mes, 2);
        var habiles = SemanasDelMes.DiasHabiles(Anio, Mes, 2);

        // Del 5 al 11 son siete días naturales y cinco hábiles: los dos que
        // faltan son el fin de semana, y siguen estando en el rango.
        Assert.Equal(7, hasta.DayNumber - desde.DayNumber + 1);
        Assert.Equal(5, habiles.Count);
    }

    [Fact]
    public void Saber_la_semana_de_una_fecha_equivale_a_buscar_en_que_rango_cae()
    {
        var total = SemanasDelMes.Cuantas(Anio, Mes);

        foreach (var dia in new[] { 1, 4, 5, 11, 12, 18, 19, 25, 26, 31 })
        {
            var esperada = 0;

            for (var s = 1; s <= total; s++)
            {
                var (desde, hasta) = SemanasDelMes.Rango(Anio, Mes, s);
                if (dia >= desde.Day && dia <= hasta.Day) { esperada = s; break; }
            }

            var fecha = new DateOnly(Anio, Mes, dia);
            Assert.Equal(esperada, SemanasDelMes.SemanaDe(Anio, Mes, fecha));
        }
    }

    [Fact]
    public void Mes_que_empieza_en_lunes_tiene_la_semana_completa()
    {
        // Junio de 2026 empieza en lunes: la semana 1 va del 1 al 7, no al 1.
        var (desde, hasta) = SemanasDelMes.Rango(2026, 6, 1);

        Assert.Equal(1, desde.Day);
        Assert.Equal(7, hasta.Day);
    }

    [Theory]
    // Febrero de 2026 empieza en domingo. El tramo del día 1 al primer domingo
    // es solo ese domingo y no tiene ni un día hábil, así que se descarta: es la
    // cola de la semana de enero. La semana 1 pasa a ser la del lunes siguiente.
    // Con el tramo descartado, febrero tiene 4 semanas y la 4 es "del 23 al 28",
    // que es lo que se lee en el desplegable.
    [InlineData(1, 2, 8)]
    [InlineData(2, 9, 15)]
    [InlineData(3, 16, 22)]
    [InlineData(4, 23, 28)]
    public void Mes_que_empieza_en_domingo_descarta_el_domingo_suelto(
        int semana, int diaDesde, int diaHasta)
    {
        Assert.Equal(DayOfWeek.Sunday, new DateOnly(2026, 2, 1).DayOfWeek);

        var (desde, hasta) = SemanasDelMes.Rango(2026, 2, semana);

        Assert.Equal(diaDesde, desde.Day);
        Assert.Equal(diaHasta, hasta.Day);
        Assert.Equal(DayOfWeek.Monday, desde.DayOfWeek);
    }

    [Fact]
    public void Mes_que_empieza_en_domingo_no_gasta_un_numero_de_semana()
    {
        Assert.Equal(4, SemanasDelMes.Cuantas(2026, 2));
    }

    [Fact]
    public void Ninguna_semana_de_ningun_mes_queda_sin_dias_habiles()
    {
        // Esta es la propiedad que un mes que empieza en domingo incumplía: la
        // pantalla llegaba a decir "se consultaron 0 días hábiles", o sea, un
        // número de semana en el desplegable que no dejaba mirar nada. Se
        // comprueba mes a mes en cuatro años, no solo en los casos conocidos.
        for (var anio = 2024; anio <= 2027; anio++)
        {
            for (var mes = 1; mes <= 12; mes++)
            {
                var total = SemanasDelMes.Cuantas(anio, mes);
                Assert.InRange(total, 4, 5);

                for (var s = 1; s <= total; s++)
                {
                    var habiles = SemanasDelMes.DiasHabiles(anio, mes, s);
                    Assert.True(
                        habiles.Count > 0,
                        $"La semana {s} de {mes}/{anio} se quedó sin días hábiles.");
                }
            }
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    public void Fechas_imposibles_no_lanzan_excepcion(int anio, int mes)
    {
        // Nada de esto debería tirar la pantalla: un año o un mes que no existen
        // se tratan como una semana mínima en vez de propagar una excepción.
        Assert.Equal(1, SemanasDelMes.Cuantas(anio, mes));
        Assert.Empty(SemanasDelMes.DiasHabiles(anio, mes, 1));
    }
}