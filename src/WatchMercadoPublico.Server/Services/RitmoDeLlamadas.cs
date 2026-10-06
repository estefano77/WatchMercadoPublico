using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;

namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// El ritmo al que se le habla a Mercado Público, para todo el proceso a la vez.
///
/// <para>
/// Existe por un <c>429</c> medido. Doce peticiones seguidas contra la API, sin
/// espera propia, dan <c>203 429 203 429 203 429 429…</c>: una de cada tres. Con
/// 400 ms casi todas fallan, con 800 ms la mitad, y con 1500 ms diez de doce
/// pasan. La API admite del orden de <b>una petición cada 1,5 s</b> y avisa con
/// un rechazo rápido, en unos 280 ms, que es un cupo de ráfaga y no un límite de
/// duración.
/// </para>
///
/// <para>
/// Se mide el intervalo entre el <b>principio</b> de una petición y el principio
/// de la siguiente, y no una espera después de cada respuesta. La diferencia es
/// todo: contra la API de verdad las respuestas tardan 1,4 a 1,6 s, así que el
/// intervalo se cumple por sí solo y esta espera sale a coste cero. Solo se paga
/// cuando algo vuelve más rápido de lo debido.
/// </para>
///
/// <para>
/// Es un singleton y va en su propia clase por dos razones. Una: si el ritmo
/// viviera en <c>MercadoPublicoCliente</c>, que es de ámbito por petición, cada
/// petición HTTP tendría el suyo y dos a la vez no se limitarían entre sí, que es
/// exactamente el <c>429</c> que se quiere evitar. Dos: al ser un sitio único,
/// ningún bucle nuevo puede olvidarse de la pausa.
/// </para>
/// </summary>
public sealed class RitmoDeLlamadas
{
    private readonly TimeSpan intervaloMinimo;
    private readonly SemaphoreSlim turno = new(1, 1);
    private DateTimeOffset ultimaSalida = DateTimeOffset.MinValue;

    /// <summary>
    /// Toma el intervalo de la configuración, con tope y suelo. El suelo está para
    /// que un 0 en el fichero no lo deje salir sin pacear, que es justo lo que
    /// vendría mal.
    /// </summary>
    public RitmoDeLlamadas(IOptions<MercadoPublicoOpciones> opciones)
    {
        intervaloMinimo = TimeSpan.FromSeconds(Math.Clamp(opciones.Value.SegundosEntreLlamadas, 1, 30));
    }

    /// El intervalo que realmente se aplica, ya acotado. Se expone para el log y
    /// para que los tests puedan comprobar el acotado sin esperar segundos.
    /// </summary>
    public TimeSpan IntervaloEfectivo => intervaloMinimo;

    /// <summary>
    /// Espera lo que falte para no salirse del ritmo y anota cuándo sale.
    ///
    /// Se queda con el turno hasta que se llame a <see cref="Liberar"/>, que
    /// ocurre cuando la respuesta termina de leerse. Soltarlo antes dejaría pasar
    /// la siguiente mientras esta sigue en curso.
    /// </summary>
    public async Task<bool> PedirTurnoAsync(CancellationToken ct)
    {
        await turno.WaitAsync(ct);

        try
        {
            var faltan = intervaloMinimo - (DateTimeOffset.Now - ultimaSalida);

            if (faltan > TimeSpan.Zero)
                await Task.Delay(faltan, ct);

            ultimaSalida = DateTimeOffset.Now;
            return true;
        }
        catch
        {
            // Si se cancela mientras esperaba, el turno hay que soltarlo aquí o
            // se queda bloqueado para siempre.
            turno.Release();
            throw;
        }
    }

    /// <summary>Suelta el turno. Siempre en un <c>finally</c>.</summary>
    public void Liberar() => turno.Release();
}