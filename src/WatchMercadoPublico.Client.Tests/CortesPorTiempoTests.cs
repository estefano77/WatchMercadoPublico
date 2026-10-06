using System.Net;
using WatchMercadoPublico.Client.Services;
using Xunit;

namespace WatchMercadoPublico.Client.Tests;

/// <summary>
/// Lo que pasa cuando se corta la espera por tiempo.
///
/// <para>
/// El fallo que motivó esto fue silencioso y visible a la vez. Cuando el cliente
/// agotaba su espera, <c>HttpClient</c> lanza <see cref="TaskCanceledException"/>,
/// que hereda de <see cref="OperationCanceledException"/> y NO de
/// <see cref="HttpRequestException"/>. El filtro de las llamadas era
/// <c>ex is HttpRequestException or JsonException or NotSupportedException</c>, así
/// que la excepción pasaba por debajo, sin aviso, y la pantalla se quedaba
/// mostrando "Consultando" para siempre con el contador congelado en el último
/// número pintado.
/// </para>
///
/// <para>
/// Estos tests comprueban la regla que lo arregla: una espera cortada SIEMPRE se
/// convierte en un error que la pantalla puede mostrar, y nunca en una excepción
/// que nadie ve. Con el código anterior fallaban todos a la vez.
/// </para>
/// </summary>
public class CortesPorTiempoTests
{
    /// <summary>
    /// Un <see cref="HttpClient"/> que falla siempre con la excepción que se le
    /// pase, para poder reproducir cada caso sin esperar de verdad a que expire
    /// nada.
    /// </summary>
    private sealed class ManejadorQueFalla(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage peticion, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(error);
    }

    private static MercadoPublicoApi Cliente(Exception error) => new(
        new HttpClient(new ManejadorQueFalla(error))
        {
            BaseAddress = new Uri("https://ejemplo.invalid/"),
        });

    // ------------------------------------------------------------------
    // El corte por tiempo
    // ------------------------------------------------------------------

    [Fact]
    public async Task LaSemanaDevuelveErrorYNoRevienta()
    {
        var (datos, error) =
            await Cliente(new TaskCanceledException("Se agotó el tiempo de espera."))
                .GetSemanaAsync(2026, 10, 2);

        Assert.Null(datos);
        Assert.NotNull(error);
        Assert.Contains("tardó demasiado", error);
    }

    [Fact]
    public async Task ElDetalleDevuelveErrorYNoRevienta()
    {
        var (datos, desdeCache, error) =
            await Cliente(new TaskCanceledException("Se agotó el tiempo de espera."))
                .GetDetalleAsync("1456839-6-LP26");

        Assert.Null(datos);
        Assert.False(desdeCache);
        Assert.NotNull(error);
        Assert.Contains("tardó demasiado", error);
    }

    [Fact]
    public async Task ElEstadoDevuelveErrorYNoRevienta()
    {
        var (datos, error) =
            await Cliente(new TaskCanceledException("Se agotó el tiempo de espera."))
                .GetEstadoAsync();

        Assert.Null(datos);
        Assert.NotNull(error);
        Assert.Contains("tardó", error);
    }

    [Fact]
    public async Task VaciarLaCacheDevuelveErrorYNoReventa()
    {
        var error = await Cliente(new TaskCanceledException("Se agotó el tiempo de espera."))
            .RefrescarAsync();

        Assert.NotNull(error);
        Assert.Contains("tardó", error);
    }

    /// <summary>
    /// El corte es un límite, no una espera eterna. Si alguien quita el
    /// <c>Timeout</c> para "dejar que el servidor termine", esto falla, y esa es
    /// justo la mitad del problema original: una espera sin techo.
    /// </summary>
    [Fact]
    public void LaEsperaTieneTecho()
    {
        Assert.True(MercadoPublicoApi.SegundosEspera > 0);
    }

    // ------------------------------------------------------------------
    // Lo que NO es un corte por tiempo
    // ------------------------------------------------------------------

    /// <summary>
    /// Una cancelación de verdad se propaga, y no se disfraza de corte por tiempo.
    ///
    /// El filtro usa <c>when (!ct.IsCancellationRequested)</c> justamente para esto:
    /// <see cref="GetEstadoAsync"/> es la única de las cuatro que recibe un token,
    /// y si quien llama cancela, necesita saber que canceló, no que el servidor
    /// tardó. Confundir los dos casos haría que un cierre de pantalla se anunciara
    /// como un fallo del servidor.
    /// </summary>
    [Fact]
    public async Task UnaCancelacionDeVerdadNoSeDisfrazaDeCorte()
    {
        using var ct = new CancellationTokenSource();
        await ct.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Cliente(new TaskCanceledException("cancelado")).GetEstadoAsync(ct: ct.Token));
    }

    /// <summary>
    /// Los fallos de verdad de la red siguen funcionando como antes: estos no se
    /// tocan. Si el servidor no está, la pantalla lo dice con su mensaje propio.
    /// </summary>
    [Fact]
    public async Task UnFalloDeRedSigueSiendoUnFalloDeRed()
    {
        var (datos, error) =
            await Cliente(new HttpRequestException("no hay conexión"))
                .GetSemanaAsync(2026, 10, 2);

        Assert.Null(datos);
        Assert.Equal("No se pudo conectar con el servidor.", error);
    }

    /// <summary>
    /// Y un cuerpo que no es JSON sigue siendo un fallo de formato, no un corte.
    /// </summary>
    [Fact]
    public async Task UnaRespuestaRotaNoSeConfundeConElCorte()
    {
        using var http = new HttpClient(new RespuestaRota("esto no es json"))
        {
            BaseAddress = new Uri("https://ejemplo.invalid/"),
        };

        var (datos, error) = await new MercadoPublicoApi(http).GetSemanaAsync(2026, 10, 2);

        Assert.Null(datos);
        Assert.NotNull(error);
    }

    private sealed class RespuestaRota(string cuerpo) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage peticion, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(cuerpo),
        });
    }
}