using WatchMercadoPublico.Server.Endpoints;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Lo que se puede elegir en los desplegables.
///
/// La regla es una sola: <b>no se ofrece ningún periodo que todavía no ha
/// empezado</b>. Ni meses por delante, ni semanas por delante.
///
/// Antes sí se ofrecían, y elegirlas gastaba una llamada al servidor para
/// devolver semanas enteras marcadas como pendientes y una pantalla vacía que
/// parecía un fallo. "No hay nada" y "todavía no ha pasado" tienen que verse
/// distinto, y la mejor forma es no llegar a ofrecer la pregunta.
///
/// Estas pruebas llaman a las funciones DE VERDAD del servidor, no a una copia:
/// si el servidor cambiara la regla y estas pruebas no, pasarían igual mientras
/// la aplicación siguiera offering el futuro.
/// </summary>
public class PeriodosDisponiblesTests
{
    private static List<int> SemanasVisibles(int anio, int mes, DateOnly hoy) =>
        LicitacionesEndpoints.DescribirSemanas(anio, mes, hoy).Select(s => s.Numero).ToList();

    [Fact]
    public void El_anio_en_curso_solo_llega_hasta_el_mes_de_hoy()
    {
        // 4 de octubre de 2026: no tiene sentido ofrecer noviembre ni diciembre.
        var meses = LicitacionesEndpoints.MesesVisibles(new DateOnly(2026, 10, 4), 2026);

        Assert.Equal(10, meses.Count);
        Assert.Equal("Enero", meses[0]);
        Assert.Equal("Octubre", meses[^1]);
        Assert.DoesNotContain("Noviembre", meses);
        Assert.DoesNotContain("Diciembre", meses);
    }

    [Fact]
    public void Un_anio_pasado_ofrece_los_doce_meses()
    {
        var hoy = new DateOnly(2026, 10, 4);

        Assert.Equal(12, LicitacionesEndpoints.MesesVisibles(hoy, 2025).Count);
        Assert.Equal(12, LicitacionesEndpoints.MesesVisibles(hoy, 2024).Count);
    }

    [Fact]
    public void Un_anio_futuro_no_ofrece_ningun_mes()
    {
        // Es lo que hace que la petición sea absurda y no una molestia: sin un
        // solo mes no hay nada que elegir, y el cliente debe aguantarlo.
        var hoy = new DateOnly(2026, 10, 4);

        Assert.Empty(LicitacionesEndpoints.MesesVisibles(hoy, 2027));
        Assert.Empty(LicitacionesEndpoints.MesesVisibles(hoy, 2030));
    }

    [Fact]
    public void Del_mes_en_curso_solo_se_ofrece_la_semana_que_ha_empezado()
    {
        // Octubre de 2026: hoy es la semana 1, del 1 al 4. Las otras cuatro no
        // han empezado.
        Assert.Equal([1], SemanasVisibles(2026, 10, new DateOnly(2026, 10, 4)));
    }

    [Fact]
    public void Al_avanzar_la_semana_aparece_la_siguiente()
    {
        // El 5 de octubre empieza la semana 2, así que ya se ofrece. Y la 1 se
        // sigue ofreciendo: mirar el pasado es lo de siempre.
        Assert.Equal([1, 2], SemanasVisibles(2026, 10, new DateOnly(2026, 10, 5)));
    }

    [Fact]
    public void Un_mes_pasado_ofrece_todas_sus_semanas()
    {
        var hoy = new DateOnly(2026, 10, 4);

        // Febrero tiene 4 semanas porque empieza en domingo.
        Assert.Equal(4, SemanasVisibles(2026, 2, hoy).Count);
        Assert.Equal(5, SemanasVisibles(2026, 9, hoy).Count);
        Assert.Equal(5, SemanasVisibles(2025, 10, hoy).Count);
    }

    [Fact]
    public void Un_mes_futuro_no_ofrece_ninguna_semana()
    {
        var hoy = new DateOnly(2026, 10, 4);

        Assert.Empty(SemanasVisibles(2026, 12, hoy));
        Assert.Empty(SemanasVisibles(2027, 1, hoy));
    }

    [Fact]
    public void En_enero_del_anio_en_curso_si_se_ofrece_la_semana_que_estan()
    {
        // El caso límite: en enero, la semana 1 es la del 1 al 4 y hoy puede ser
        // el día 2. Se ofrece igual, porque ha empezado y se ven los días que ya
        // han pasado más los que faltan.
        var visibles = SemanasVisibles(2026, 1, new DateOnly(2026, 1, 2));

        Assert.Equal([1], visibles);
        Assert.Equal(new DateOnly(2026, 1, 1), SemanasDelMes.Rango(2026, 1, 1).Desde);
    }

    [Fact]
    public void El_texto_de_la_semana_viene_todo_desde_el_servidor()
    {
        // El desplegable se pinta con este texto, así que tiene que llegar
        // escrito. Se comprueba porque el cliente tuvo una versión propia que
        // salía corrida un día.
        var semanas = LicitacionesEndpoints.DescribirSemanas(2026, 2, new DateOnly(2026, 10, 4));

        Assert.Equal("2 al 8 de febrero", semanas[0].Texto);
        Assert.Equal("23 al 28 de febrero", semanas[3].Texto);
    }

    [Fact]
    public void Ninguna_semana_ofertada_empieza_despues_de_hoy()
    {
        // La propiedad que lo resume todo, comprobada para todos los meses de
        // dos años y para los doce días de cada mes. Si esto se cumple, no se
        // puede ofrecer el futuro por la puerta de atrás.
        for (var anio = 2026; anio <= 2027; anio++)
        {
            for (var mes = 1; mes <= 12; mes++)
            {
                for (var dia = 1; dia <= DateTime.DaysInMonth(anio, mes); dia++)
                {
                    var hoy = new DateOnly(anio, mes, dia);
                    var visibles = SemanasVisibles(anio, mes, hoy);

                    foreach (var s in visibles)
                    {
                        var desde = SemanasDelMes.Rango(anio, mes, s).Desde;

                        Assert.True(
                            desde <= hoy,
                            $"La semana {s} de {mes}/{anio} empieza el {desde:yyyy-MM-dd}, posterior al {hoy:yyyy-MM-dd}.");

                        // Y no se salta ninguna: si se ofrece la s, se ofrece
                        // también la s-1.
                        Assert.True(s == 1 || visibles.Contains(s - 1));
                    }
                }
            }
        }
    }
}
