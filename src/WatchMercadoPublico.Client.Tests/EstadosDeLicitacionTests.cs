using WatchMercadoPublico.Client.Models;
using Xunit;

namespace WatchMercadoPublico.Client.Tests;

/// <summary>
/// El mapeo de estados de Mercado Público, que decide el color de la insignia.
///
/// <para>
/// La insignia de ADJUDICADA se distingue de la gris de los estados cerrados, y
/// eso lo decide una línea: <c>CodigoEstado is 8</c>. Si alguien cambia ese 8, o
/// el número que Mercado Público usa, la tarjeta sigue compilando, sigue
/// enseñando el estado correcto en texto y **vuelve a salir gris sin que nada
/// avise**. Un cambio de color no rompe la compilación, que es justo por lo que
/// hace falta atarlo a un test.
/// </para>
///
/// <para>
/// Estos tests no miran el color, que es CSS. Miran la decisión: qué estados son
/// adjudicados y cuáles no, y que la insignia se elija por el código y no por el
/// texto, que es una cadena que el servidor podría cambiar sin que nadie se entere.
/// </para>
/// </summary>
public class EstadosDeLicitacionTests
{
    [Fact]
    public void ElCodigoOchoEsElUnicoAdjudicado()
    {
        Assert.True(new Licitacion { CodigoEstado = 8 }.EsAdjudicada);
    }

    [Theory]
    [InlineData(5)]   // Publicada
    [InlineData(6)]   // Cerrada
    [InlineData(7)]   // Desierta
    [InlineData(18)]  // Revocada
    [InlineData(19)]  // Suspendida
    public void LosOtrosEstadosNoSonAdjudicados(int codigo)
    {
        Assert.False(new Licitacion { CodigoEstado = codigo }.EsAdjudicada);
    }

    [Fact]
    public void AdjudicadaNoEstaActiva()
    {
        // Adjudicada cae en la rama de su propia insignia, pero no puede ser
        // "activa": tiene ofertas en curso es lo único que significa activa.
        Assert.False(new Licitacion { CodigoEstado = 8 }.Activa);
    }

    [Fact]
    public void LaAdjudicadaNoSeDecidePorElTexto()
    {
        // La insignia se elige por CodigoEstado y no por EstadoLegible, que es una
        // cadena traducible. Si se apoyara en el texto, bastaría con que el
        // servidor escribiera "Adjudicada" en otro idioma o con otra mayúscula para
        // que la tarjeta volviera a salir gris sin que nadie se entere.
        //
        // Aquí se comprueba el caso límite: sin código de estado, EstadoLegible
        // devuelve "Sin estado", y sin código no se pinta ninguna insignia de
        // adjudicada.
        var sinCodigo = new Licitacion();
        Assert.Equal("Sin estado", sinCodigo.EstadoLegible);
        Assert.False(sinCodigo.EsAdjudicada);

        // Y el texto no va al revés: con el código 8 sale adjudicada, y el texto
        // legible es el que se calcula a partir de ese código, no al contrario.
        var licitacion = new Licitacion { CodigoEstado = 8 };
        Assert.Equal("Adjudicada", licitacion.EstadoLegible);
        Assert.True(licitacion.EsAdjudicada);
    }
}
