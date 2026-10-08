using WatchMercadoPublico.Client.Services;
using Xunit;

namespace WatchMercadoPublico.Client.Tests;

/// <summary>
/// La lógica de la semana, fuera del componente para poder probarla.
///
/// Las dos primeras pruebas de este archivo son REGRESIONES de bugs que
/// estuvieron en pantalla, y las dos versiones anteriores de esta lógica
/// estaban en el code-behind de <c>Home.razor</c>, donde ningún test podía
/// mirar. No se movió nada aquí por arquitectura: se movió porque aquí no se
/// puede volver a meter un bug sin que se entere alguien.
/// </summary>
public class FiltrosDeSemanaTests
{
    /// <summary>
    /// Los nombres de mes que llegan del servidor. En el año en curso viene
    /// FILTRADA: solo hasta el mes de hoy.
    /// </summary>
    private static readonly string[] MesesDelAnyoEnCurso =
        ["Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
         "Julio", "Agosto", "Septiembre", "Octubre"];

    private static readonly string[] LosDoceMeses =
        ["Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
         "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"];

    // --- El aviso de "los filtros ya no coinciden" -------------------------

    [Fact]
    public void Avisa_si_cambia_la_semana()
    {
        // Lo de siempre: la 4 en pantalla y la 2 elegida.
        Assert.True(FiltrosDeSemana.HayCambioPendiente(2026, 2, 2, 2026, 2, 4));
    }

    [Fact]
    public void Avisa_si_cambia_el_mes_aunque_el_numero_sea_el_mismo()
    {
        // REGRESIÓN. Comparando solo el número de semana esto daba "no hay
        // cambio": la semana 1 de octubre de 2026 y la semana 1 de febrero de
        // 2026 se llaman igual. Los datos en pantalla eran los de octubre y el
        // desplegable decía febrero, sin ninguna señal de que fueran dos
        // cosas distintas. Pasó porque el número de semana se repite en todos
        // los periodos.
        Assert.True(FiltrosDeSemana.HayCambioPendiente(2026, 2, 1, 2026, 10, 1));
    }

    [Fact]
    public void Avisa_si_cambia_el_ano_aunque_el_mes_y_el_numero_sean_iguales()
    {
        // La otra mitad del mismo bug.
        Assert.True(FiltrosDeSemana.HayCambioPendiente(2025, 10, 1, 2026, 10, 1));
    }

    [Theory]
    [InlineData(2026, 2, 4, 2026, 2, 4)]
    [InlineData(2026, 1, 1, 2026, 1, 1)]
    [InlineData(2021, 12, 5, 2021, 12, 5)]
    public void No_avisa_si_no_ha_cambiado_nada(
        int ae, int me, int se, int am, int mm, int sm)
    {
        // Y el caso normal, que es el que más se repite: no hay que ensuciar la
        // cabecera con un aviso que no viene a cuento.
        Assert.False(FiltrosDeSemana.HayCambioPendiente(ae, me, se, am, mm, sm));
    }

    [Fact]
    public void Las_tres_cosas_se_comparan_y_ninguna_se_olvida()
    {
        // Comprobación de que la función mira las tres. Si alguien dejara fuera
        // una, esta prueba no lo vería por sí sola; lo que la fija es la de
        // arriba, que es la que falló en producción.
        var lasTresDistintas = FiltrosDeSemana.HayCambioPendiente(2030, 6, 9, 2029, 5, 8);
        Assert.True(lasTresDistintas);
    }

    // --- El nombre de la semana ---------------------------------------------

    [Fact]
    public void La_semana_se_nombra_con_mes_y_ano()
    {
        Assert.Equal(
            "la semana 4 en febrero de 2026",
            FiltrosDeSemana.EnPalabras(2026, 2, 4, LosDoceMeses));

        Assert.Equal(
            "la Semana 4 en Febrero de 2026",
            FiltrosDeSemana.EnPalabras(2026, 2, 4, LosDoceMeses, capitalizado: true));
    }

    [Fact]
    public void Usa_la_lista_de_meses_que_le_pasan_no_una_propia()
    {
        // La lista llega filtrada desde el servidor. Con una lista de los doce
        // meses, el nombre sale bien; con la de diez que llega en octubre,
        // también. Lo que no puede ser es que la función tenga su propia
        // tabla, que es por donde entró el bug de los nombres de día.
        Assert.Equal(
            "la semana 1 en octubre de 2026",
            FiltrosDeSemana.EnPalabras(2026, 10, 1, MesesDelAnyoEnCurso));
    }

    [Fact]
    public void Un_mes_que_no_cabe_en_la_lista_no_deja_un_hueco()
    {
        // Con la lista de diez, el mes 11 no cabe. La salida tiene que avisar
        // de que algo va mal, no imprimir "la semana 4 de  de 2026".
        Assert.Equal(
            "la semana 4 en mes 11 de 2026",
            FiltrosDeSemana.EnPalabras(2026, 11, 4, MesesDelAnyoEnCurso));

        Assert.Equal(
            "la semana 4 en mes 0 de 2026",
            FiltrosDeSemana.EnPalabras(2026, 0, 4, MesesDelAnyoEnCurso));

        // Y sin lista ninguna tampoco.
        Assert.Equal(
            "la semana 4 en mes 7 de 2026",
            FiltrosDeSemana.EnPalabras(2026, 7, 4, null));
    }

    [Fact]
    public void Todos_los_meses_de_la_lista_se_nombran_bien()
    {
        for (var m = 1; m <= LosDoceMeses.Length; m++)
        {
            var texto = FiltrosDeSemana.EnPalabras(2026, m, 1, LosDoceMeses);

            Assert.Equal($"la semana 1 en {LosDoceMeses[m - 1].ToLowerInvariant()} de 2026", texto);
        }
    }

    // --- Los rótulos de días -------------------------------------------------

    [Fact]
    public void Los_dias_consultados_singular_y_plural()
    {
        Assert.Equal("1 día hábil", FiltrosDeSemana.DiasConsultadosEnPalabras(1, haySemana: true));
        Assert.Equal("2 días hábiles", FiltrosDeSemana.DiasConsultadosEnPalabras(2, haySemana: true));
        Assert.Equal("0 días hábiles", FiltrosDeSemana.DiasConsultadosEnPalabras(0, haySemana: true));
    }

    [Fact]
    public void Sin_semana_cargada_no_se_inventa_un_numero()
    {
        // Antes de que llegue la respuesta no se dice "0 días hábiles", que
        // sería afirmar que se_ha consultado y no se ha consultado nada.
        Assert.Equal(
            "los días hábiles de la semana",
            FiltrosDeSemana.DiasConsultadosEnPalabras(null, haySemana: false));
    }

    [Fact]
    public void Los_dias_fallidos_son_un_conteo_y_no_una_lista_de_fechas()
    {
        // El aviso dice "(3 días). Lo que ves está completo, pero puede que
        // falte algo de esos días". Un "Sin respuesta: 2026-02-24" ahí se
        // leería como un error.
        Assert.Equal("1 día", FiltrosDeSemana.DiasFallidosEnPalabras(1));
        Assert.Equal("3 días", FiltrosDeSemana.DiasFallidosEnPalabras(3));
        Assert.Equal("0 días", FiltrosDeSemana.DiasFallidosEnPalabras(0));
        Assert.Equal("0 días", FiltrosDeSemana.DiasFallidosEnPalabras(null));
    }

    [Fact]
    public void El_contador_dice_publicaciones_y_no_novedades()
    {
        Assert.Equal("1 publicación", FiltrosDeSemana.Publicaciones(1));
        Assert.Equal("2 publicaciones", FiltrosDeSemana.Publicaciones(2));
        Assert.Equal("7 publicaciones", FiltrosDeSemana.Publicaciones(7));

        // "novedad" era lo que decía, y suena a hecho reciente. En pantalla hay
        // semanas de hace meses, así que la palabra no era cierta: lo que se
        // cuenta es lo que se PUBLICÓ en esa semana, viejo o no.
        Assert.DoesNotContain("novedad", FiltrosDeSemana.Publicaciones(1) + FiltrosDeSemana.Publicaciones(3));
    }

    [Fact]
    public void El_contador_no_decide_publicacion_en_singular_por_el_cero()
    {
        // El cero es plural. Es el caso que un "== 1" mal puesto convierte en
        // "1 publicación" y queda mintiendo: no se publicó ninguna.
        Assert.Equal("0 publicaciones", FiltrosDeSemana.Publicaciones(0));
    }
    // --- El mes en palabras, para el modo base de datos ---

    [Fact]
    public void El_mes_en_palabras_no_lleva_numero_de_semana()
    {
        // El fallo concreto: en modo base de datos el aviso de "has cambiado el
        // filtro" decia "Estás viendo la semana 1 en octubre de 2026" y "has
        // elegido la semana 2 en septiembre de 2026". No hay ninguna semana
        // detrás: el selector de semana ni siquiera está en pantalla.
        Assert.Equal(
            "octubre de 2026",
            FiltrosDeSemana.MesEnPalabras(2026, 10, LosDoceMeses));

        Assert.Equal(
            "septiembre de 2026",
            FiltrosDeSemana.MesEnPalabras(2026, 9, LosDoceMeses));
    }

    [Fact]
    public void El_mes_en_palabras_no_empieza_con_mayuscula_dentro_de_una_frase()
    {
        // Va DENTRO de una frase: "Estás viendo octubre de 2026". Con mayúscula
        // se lee como si empezara una oracion.
        Assert.Equal(
            "septiembre de 2026",
            FiltrosDeSemana.MesEnPalabras(2026, 9, LosDoceMeses));

        // Y con capitalizado:true, para cuando SÍ sea el principio de una linea.
        Assert.Equal(
            "Septiembre de 2026",
            FiltrosDeSemana.MesEnPalabras(2026, 9, LosDoceMeses, capitalizado: true));
    }

    [Fact]
    public void El_mes_en_palabras_no_inventa_el_articulo()
    {
        // "el mes de octubre de 2026" dentro de "Estás viendo..." produce
        // "Estás viendo el mes de octubre de 2026". No es un error, pero es otra
        // frase, y el aviso ya dice que lo que se ha elegido es un mes.
        var texto = FiltrosDeSemana.MesEnPalabras(2026, 10, LosDoceMeses);

        Assert.DoesNotContain("el mes", texto);
        Assert.DoesNotContain("semana", texto);
        Assert.DoesNotContain(" la ", $" {texto} ");
    }

    [Fact]
    public void El_mes_en_palabras_cae_a_un_numero_si_no_cabe_en_la_lista()
    {
        // La lista llega FILTRADA al mes en curso, así que un mes futuro no
        // está en ella. Imprimir un hueco sería peor que decir "mes 12".
        Assert.Equal("mes 12 de 2026", FiltrosDeSemana.MesEnPalabras(2026, 12, MesesDelAnyoEnCurso));
        Assert.Equal("mes 0 de 2026", FiltrosDeSemana.MesEnPalabras(2026, 0, LosDoceMeses));
        Assert.Equal("mes 5 de 2026", FiltrosDeSemana.MesEnPalabras(2026, 5, null));
    }

    [Fact]
    public void El_mes_y_la_semana_no_se_confunden()
    {
        // Los dos textos conviven en el mismo aviso segun el modo, y uno no
        // puede contaminar el formato del otro.
        var delMes = FiltrosDeSemana.MesEnPalabras(2026, 9, LosDoceMeses);
        var deLaSemana = FiltrosDeSemana.EnPalabras(2026, 9, 2, LosDoceMeses);

        Assert.DoesNotContain("semana", delMes);
        Assert.DoesNotContain("2026 de", deLaSemana);
        Assert.Contains("septiembre de 2026", deLaSemana);
    }
}
