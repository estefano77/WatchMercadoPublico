using System.Net;
using Microsoft.Extensions.Logging;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Regresión del segundo bug: el mensaje de la API se perdía.
///
/// <c>ConstruirError</c> buscaba el motivo en <c>error.message</c>,
/// <c>Descripcion</c> y <c>Message</c>. La API responde en español, con
/// <c>{"Codigo":…,"Mensaje":"…"}</c>, así que ninguno de los tres acertaba.
///
/// Consecuencia medida: el log ponía <c>(null)</c> y la pantalla informaba de
/// que "Mercado Público está con problemas en este momento", quando el fallo
/// era de la petición que le mandábamos. Un error que sí dice qué le molesta
/// tiene que llegar a quien lee, o se depura la mitad equivocada durante horas.
/// </summary>
public class ErroresDeApiTests
{
    // Lo que devuelve la API de verdad cuando la fecha va mal formada.
    private const string FechaInvalida =
        """{"Codigo":10300,"Mensaje":"El formato del parametro fechas es incorrecto"}""";

    [Fact]
    public void Un_500_con_Mensaje_lo_cuenta_en_vez_de_decir_que_la_api_esta_caida()
    {
        var ex = MercadoPublicoCliente.ConstruirError(
            HttpStatusCode.InternalServerError,
            FechaInvalida,
            new LoggerCapturado());

        Assert.Contains("El formato del parametro fechas es incorrecto", ex.Message);
        Assert.Contains("rechazó la consulta", ex.Message);

        // Lo que NO debe decir: culpa a la API de estar caída cuando el
        // problema es la URL que le mandamos.
        Assert.DoesNotContain("está con problemas", ex.Message);
    }

    [Fact]
    public void El_codigo_propio_de_la_api_tambien_se_lee()
    {
        var log = new LoggerCapturado();

        MercadoPublicoCliente.ConstruirError(
            HttpStatusCode.InternalServerError, FechaInvalida, log);

        // El 10300 es lo que dice qué está mal. Si se pierde, se vuelve a
        // depurar a ciegas.
        // OJO con el orden: Assert.Contains(textoBuscado, dondeBuscar), no al
        // revés. Pasados al revés, el fallo dice "10300" en lugar de la línea
        // del log, que es justo lo que había que leer.
        Assert.Contains("10300", log.Texto);
        Assert.Contains("código propio", log.Texto);
    }

    [Fact]
    public void Un_500_sin_cuerpo_si_dice_que_la_api_esta_caida()
    {
        // Sin mensaje no hay nada que contar: aquí sí es un problema de la API.
        var ex = MercadoPublicoCliente.ConstruirError(
            HttpStatusCode.InternalServerError, "", new LoggerCapturado());

        Assert.Contains("está con problemas", ex.Message);
    }

    [Fact]
    public void Un_429_habla_del_limite_de_peticiones()
    {
        var ex = MercadoPublicoCliente.ConstruirError(
            HttpStatusCode.TooManyRequests, "", new LoggerCapturado());

        Assert.Contains("limitando las peticiones", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Un_ticket_invalido_se_responde_con_502(HttpStatusCode codigo)
    {
        var ex = MercadoPublicoCliente.ConstruirError(codigo, "", new LoggerCapturado());

        Assert.Contains("ticket", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 502 y no 401/403: quien está mirando la pantalla no puede arreglar
        // nada con su ticket. Si el navegador ve un 401, el código del cliente
        // puede pensar que es su sesión la que caducó.
        Assert.Equal(502, ex.CodigoHttp);
    }

    [Fact]
    public void Un_codigo_de_la_api_que_no_son_peticion_tampoco_se_toma_como_mensaje()
    {
        // La API devuelve {"Codigo":203,"Mensaje":"Ticket no válido."} con HTTP
        // 203, que no es error de servidor. El mensaje debe llegar igual.
        var ex = MercadoPublicoCliente.ConstruirError(
            (HttpStatusCode)203,
            """{"Codigo":203,"Mensaje":"Ticket no válido."}""",
            new LoggerCapturado());

        Assert.Contains("Ticket no válido", ex.Message);
    }

    [Fact]
    public void Un_cuerpo_que_no_es_json_no_revienta()
    {
        var ex = MercadoPublicoCliente.ConstruirError(
            HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>", new LoggerCapturado());

        Assert.Contains("502 Bad Gateway", ex.Message);
    }

    [Fact]
    public void Un_200_nunca_llega_aqui_pero_no_debe_mirar_el_cuerpo()
    {
        // Guarda contra un uso por error: si algún día pasara un 200, el
        // resultado tiene que ser inocuo, no una excepción al construirla.
        var ex = MercadoPublicoCliente.ConstruirError(
            HttpStatusCode.OK, FechaInvalida, new LoggerCapturado());

        Assert.Equal(200, ex.CodigoHttp);
    }
}

/// <summary>
/// Logger que guarda lo escrito, para comprobar qué llega al log.
///
/// Hace falta porque parte del arreglo —registrar el <c>Codigo</c> propio de la
/// API— solo se ve en el log, y un <c>NullLogger</c> la dejaría sin comprobar.
/// </summary>
internal sealed class LoggerCapturado : ILogger
{
    public List<string> Lineas { get; } = [];

    /// <summary>Todo lo escrito, unido. Para assert sobre contenido.</summary>
    public string Texto => string.Join(Environment.NewLine, Lineas);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Lineas.Add(formatter(state, exception));
}