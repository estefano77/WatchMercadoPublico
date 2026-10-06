using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Que no se le hable a Mercado Público más rápido de lo que admite.
///
/// <para>
/// Sale de una medición con peticiones sin ticket, para no gastar cupo. Doce
/// seguidas contra la API dan <c>203 429 203 429 203 429 429…</c>: una de cada
/// tres. Con pausas de 400 ms casi todas fallan, de 800 ms la mitad, y de 1500 ms
/// diez de doce pasan. La API admite del orden de <b>una petición cada 1,5 s</b>, y
/// lo dice con un rechazo rápido: un <c>429</c> que vuelve en unos 280 ms es un
/// cupo de ráfaga, no un límite de duración.
/// </para>
///
/// <para>
/// Estos tests usan un intervalo de 1 s, el suelo que impone la propia clase, en
/// lugar del de producción. Con el valor real cada test tardaría segundos, y lo
/// que se comprueba aquí es la MECÁNICA, no el número.
/// </para>
/// </summary>
public class RitmoDeLlamadasTests
{
    private static RitmoDeLlamadas Nuevo(int segundos) =>
        new(Options.Create(new MercadoPublicoOpciones { SegundosEntreLlamadas = segundos }));

    [Fact]
    public async Task DosTurnosSeparadosRespetanElIntervalo()
    {
        var ritmo = Nuevo(1);

        await ritmo.PedirTurnoAsync(CancellationToken.None);
        ritmo.Liberar();

        var salida = DateTimeOffset.Now;

        await ritmo.PedirTurnoAsync(CancellationToken.None);
        ritmo.Liberar();

        Assert.True(
            DateTimeOffset.Now - salida >= TimeSpan.FromMilliseconds(900),
            "La segunda salida tiene que esperar al intervalo.");
    }

    /// <summary>
    /// El turno se queda tomado hasta que se suelte, y no se suelta solo.
    ///
    /// Si se soltara al terminar la espera, la siguiente podría salir mientras la
    /// anterior sigue en vuelo, que es justo el 429 que se evita.
    /// </summary>
    [Fact]
    public async Task ElTurnoNoSeSueltaSolo()
    {
        var ritmo = Nuevo(1);

        await ritmo.PedirTurnoAsync(CancellationToken.None);

        var segundo = ritmo.PedirTurnoAsync(CancellationToken.None);

        var entro = await Task.WhenAny(segundo, Task.Delay(200)) == segundo;

        Assert.False(entro, "Sin liberar, el turno tiene que seguir tomado.");

        // Se libera para no dejar el test colgado. La segunda espera termina
        // sola después, cuando el primer turno se queda libre.
        ritmo.Liberar();
        await Task.WhenAny(segundo, Task.Delay(2000));
        ritmo.Liberar();
    }

    /// <summary>
    /// Un intervalo en blanco se corrige hacia arriba.
    ///
    /// Poner <c>SegundosEntreLlamadas: 0</c> en el fichero desactivaría el ritmo y
    /// volverían los 429, así que el suelo está puesto a propósito: el error se
    /// paga en tiempos de espera, no en perder semanas enteras.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(999, 30)]
    [InlineData(2, 2)]
    public void ElIntervaloSeAcota(int configurado, int esperado)
    {
        Assert.Equal(TimeSpan.FromSeconds(esperado), Nuevo(configurado).IntervaloEfectivo);
    }
}