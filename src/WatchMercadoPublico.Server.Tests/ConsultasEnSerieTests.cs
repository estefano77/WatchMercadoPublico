using Microsoft.Extensions.Logging.Abstractions;
using WatchMercadoPublico.Server.Endpoints;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Dos consultas a Mercado Público van SIEMPRE en serie, y el refresco
/// automático se conforma con menos reintentos.
///
/// <para>
/// Esto viene de una medición que contradijo la hipótesis con la que se empezó.
/// La sospecha era que el candado único de la aplicación hacía esperar a
/// quien mirara una semana distinta, y se llegó a cambiarlo por un candado por
/// semana. Al probarlo, la medición salió al revés:
/// </para>
///
/// <list type="bullet">
/// <item>Una semana en frío: <b>6,5 s</b></item>
/// <item>Dos semanas, una detrás de otra: <b>13,2 s</b>, la suma exacta</item>
/// <item>Dos semanas, en paralelo: <b>23,5 s</b></item>
/// </list>
///
/// <para>
/// Mandar dos consultas a la vez desde la misma conexión es más de tres veces
/// peor que hacerlas seguidas, y no por un 429: la API se limita callando y cada
/// llamada simplemente tarda más. Así que el candado sigue siendo único, y el
/// cambio bueno no era el candado sino lo que pasaba ALREDEDOR de él.
/// </para>
///
/// <para>
/// Estos tests fijan las dos cosas que sí quedaron.
/// </para>
/// </summary>
public class ConsultasEnSerieTests
{
    private static CacheMercadoPublico NuevaCache() =>
        new(NullLogger<CacheMercadoPublico>.Instance, TimeSpan.FromMinutes(5));

    // ------------------------------------------------------------------
    // El candado
    // ------------------------------------------------------------------

    /// <summary>
    /// Semanas distintas comparten candado, y es lo correcto.
    ///
    /// Este test es la sombra del cambio que se revirtió. Si alguien vuelve a
    /// partir el candado por semana creyendo que así se paraleliza, esto falla y
    /// dice por qué.
    /// </summary>
    [Fact]
    public void ElCandadoEsUnicoParaTodasLasSemanas()
    {
        var cache = NuevaCache();

        Assert.Same(cache.Candado, cache.Candado);
    }

    [Fact]
    public async Task UnaSemanaOcupadaRetieneAOtra()
    {
        var cache = NuevaCache();

        // El comportamiento counterintuitive, a propósito: si esto se rompe,
        // significa que se han puesto en paralelo, y la API lo castiga.
        await cache.Candado.WaitAsync();

        var segunda = await cache.Candado.WaitAsync(TimeSpan.FromMilliseconds(200));

        Assert.False(segunda, "Las consultas a la API van en serie, no en paralelo.");

        cache.Candado.Release();
    }

    // ------------------------------------------------------------------
    // Los reintentos en segundo plano
    // ------------------------------------------------------------------

    /// <summary>
    /// El refresco automático se conforma con menos reintentos que el botón.
    ///
    /// La cuenta importa por el tiempo que alguien espera: con seis intentos y
    /// esperas de 2 a 30 s, un día que falla retiene el candado -que es único-
    /// hasta cuatro minutos. Si eso lo hace el refresco invisible, quien esté
    /// mirando la pantalla se queda esperando detrás sin poder cancelar.
    /// </summary>
    [Fact]
    public void ElSegundoPlanoSeConformaConMenosIntentos()
    {
        Assert.True(
            LicitacionesEndpoints.IntentosPorDiaEnSegundoPlano < LicitacionesEndpoints.IntentosPorDia);
    }

    [Fact]
    public void ElSegundoPlanoNoBajaDeTresIntentos()
    {
        // Que sean menos no significa que sea cero: un 500 puntual se recupera
        // solo esperando, y en este proceso no hay nadie mirando el resultado.
        Assert.True(LicitacionesEndpoints.IntentosPorDiaEnSegundoPlano >= 3);
    }
}