using WatchMercadoPublico.Server.Endpoints;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Regresión del fallo que hacía que el nombre del organismo comprador no
/// saliera en algunas tarjetas, de forma aleatoria.
///
/// El organismo no viene en el listado: solo viene en el detalle de cada
/// licitación. Y el detalle se pedía UNA vez, sin reintentos, mientras que los
/// días insisten hasta seis veces.
///
/// El 429 de Mercado Público es un límite de ritmo, y el README del proyecto ya
/// tenía medido que de cada diez peticiones más o menos una lo recibe. El detalle
/// es la petición que va justo detrás de los días, así que es la que más caía:
/// cada 429 suyo dejaba una tarjeta sin la línea del organismo.
///
/// Y no se notaba, que es lo que lo hacía parecer un fallo de la plantilla en vez
/// de un fallo de la red. Abrir la tarjeta de detalles sí mostraba el
/// organismo, porque el modal pide el detalle por su cuenta al abrirse: una
/// segunda petición que a veces sí funcionaba. Eso despistaba más todavía.
///
/// Estos tests no tocan la API: comprueban la política de reintentos, que es
/// pura, así que corren sin red y sin ticket.
/// </summary>
public class ReintentosDeDetalleTests
{
    [Fact]
    public void ElDetalleSePideMasDeUnaVez()
    {
        // El bug entero cabe aquí: un solo intento, y un 429 se come la tarjeta.
        Assert.True(
            LicitacionesEndpoints.IntentosPorDetalle > 1,
            "El detalle se pide una sola vez, y con el 429 que hay se pierde la tarjeta");
    }

    [Fact]
    public void LosIntentosNoSonLosMismosQueLosDias()
    {
        // No es que sean pocos: es que son MENOS a propósito, porque el detalle es
        // un dato acessorio y los días pueden esperar 90 s. Lo que no puede ser es
        // que sean más, o una semana con varias tarjetas sin detalle se pasaría
        // de los 120 s de IIS.
        Assert.InRange(LicitacionesEndpoints.IntentosPorDetalle, 2, 4);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void LaEsperaSeDoblaEnCadaReintento(int intento, int segundosEsperados)
    {
        Assert.Equal(
            segundosEsperados,
            LicitacionesEndpoints.EsperaDetalle(intento).TotalSeconds);
    }

    [Fact]
    public void LaEsperaTieneTope()
    {
        // Sin tope, el doble de 8 sería 16, luego 32, luego 64... y una semana
        // lenta se caería por acumular esperas.
        Assert.Equal(30, LicitacionesEndpoints.EsperaDetalle(20).TotalSeconds);
    }

    [Fact]
    public void LaEsperaNoEsNegativaParaIntentosRaros()
    {
        // El primer reintento es el intento 1. Si alguien pasara un 0 o un
        // negativo, una potencia de dos con exponent negativo devuelve una espera
        // menor que cero y Task.Delay lanza.
        Assert.True(LicitacionesEndpoints.EsperaDetalle(0).TotalSeconds > 0);
    }

    [Fact]
    public void ElPeorCasoCabeEnElRelojDeIis()
    {
        // Suma de las esperas de los reintentos que se hacen con
        // IntentosPorDetalle intentos. Con tres intentos se esperan 2 y 4 s, o sea
        // 6 s por tarjeta que falla. El README avisa de que los días solos ya
        // pueden tardar 150 s y de que el límite de IIS son 120 s, así que esto
        // tiene que ser una cifra pequeña y no una sorpresa.
        var esperaTotal = Enumerable.Range(1, LicitacionesEndpoints.IntentosPorDetalle - 1)
            .Sum(i => LicitacionesEndpoints.EsperaDetalle(i).TotalSeconds);

        Assert.Equal(6, esperaTotal);
    }
}
