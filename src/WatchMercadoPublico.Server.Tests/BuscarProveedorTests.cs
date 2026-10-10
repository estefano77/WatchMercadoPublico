using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// El RUT → código de proveedor, que es el paso que decide <b>qué empresa</b>
/// está vigilando la aplicación.
///
/// <para>
/// Estas pruebas existen por dos motivos, y los dos son fallos que ya casi
/// ocurrieron.
/// </para>
///
/// <para>
/// El primero es el NOMBRE DEL PARÁMETRO. Con <c>rut</c> la API responde
/// <c>HTTP 500</c> sin decir por qué, y el mensaje no menciona el RUT ni la
/// empresa: parece que se cayó la plataforma. El nombre bueno es
/// <c>rutempresaproveedor</c>, y no hay forma de deducirlo del error.
/// </para>
///
/// <para>
/// El segundo, y más grave, es que <c>BuscarProveedor</c> <b>no filtra</b>. Un
/// RUT bien escrito que no es de nadie devuelve empresas de verdad, no una
/// lista vacía. Medido con <c>99.999.999-9</c>, que devolvió <c>Cantidad: 2</c>:
/// "Canale" y "SANDRA CECILIA CISTERNA ALVIAL". Así que la respuesta no se
/// puede interpretar sola, y estas pruebas fijan que el cliente NO elija: que
/// devuelva todas las que llegan y que el que decida exija una sola.
/// </para>
/// </summary>
public sealed class BuscarProveedorTests
{
    private const string TicketFalso = "TICKET-DE-PRUEBA";

    /// <summary>
    /// El cuerpo exacto que devolvió la API el 10 de octubre de 2026 con el RUT
    /// real. Se copia entero, con <c>listaEmpresas</c> en minúscula, porque
    /// cambiar de mayúscula no da error de compilación: da una lista vacía y
    /// un "Cantidad: 1" que parece un RUT sin empresa.
    /// </summary>
    private const string RespuestaReal = """
        {"Cantidad":1,"FechaCreacion":"2026-10-10T14:27:53.6261063Z",
         "listaEmpresas":[{"CodigoEmpresa":"71284","NombreEmpresa":"SISTEMAS MODULARES DE COMPUTACION SPA"}]}
        """;

    // ----------------------------------------------------------------------
    // La URL
    // ----------------------------------------------------------------------

    [Fact]
    public void La_url_usa_el_nombre_del_parametro_que_entiende_la_api()
    {
        var url = MercadoPublicoCliente.ConstruirUrlBuscarProveedor("86.130.200-8", TicketFalso);

        Assert.StartsWith("Publico/Empresas/BuscarProveedor?", url);

        // El fallo que cuesta un rato: con "rut" esto no aparece, la API
        // responde 500 y el error no dice nada de parámetros.
        Assert.Contains("rutempresaproveedor=86.130.200-8", url);
        Assert.Contains($"ticket={TicketFalso}", url);
    }

    [Fact]
    public void El_rut_y_el_ticket_se_escapan()
    {
        var url = MercadoPublicoCliente.ConstruirUrlBuscarProveedor(
            "86.130.200-8", "ticket con espacios&y=mas");

        Assert.Contains("ticket=ticket%20con%20espacios%26y%3Dmas", url);
    }

    // ----------------------------------------------------------------------
    // El formato del RUT, medido contra la API
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData("86.130.200-8")]   // dos dígitos delante: HTTP 200
    [InlineData("12.345.678-9")]   // otros dos: HTTP 200
    [InlineData("  86.130.200-8  ")] // con espacios pegados: los mismos
    public void Un_rut_que_la_api_acepta_pasa_el_control(string rut) =>
        Assert.True(MercadoPublicoCliente.RutBienFormado(rut));

    [Theory]
    [InlineData("861302008")]      // sin puntos: HTTP 500
    [InlineData("86130200-8")]     // sin puntos: HTTP 500
    [InlineData("123.456.789-5")]  // TRES dígitos delante: HTTP 500
    [InlineData("1.234.567-8")]    // UN dígito delante: HTTP 500
    [InlineData("86.130.200")]     // sin verificador
    [InlineData("86.130.200-")]    // verificador vacío
    [InlineData("86.130.200-88")]  // verificador de dos cifras
    [InlineData("")]               // vacío: HTTP 500
    [InlineData(null)]
    public void Un_rut_que_la_api_rechaza_no_pasa_el_control(string? rut) =>
        Assert.False(MercadoPublicoCliente.RutBienFormado(rut));

    [Fact]
    public void Delante_han_de_venir_dos_digitos_ni_uno_ni_tres()
    {
        // Esta prueba NACIÓ ROJA. El patrón que parece evidente es \d{1,2},
        // "uno o dos dígitos delante", y acepta 1.234.567-8, que la API rechaza
        // con 500. Está aquí para que volver a aflojar el patrón a \d{1,2} salga
        // en rojo en vez de pasar y gastar una llamada por RUT mal escrito.
        Assert.True(MercadoPublicoCliente.RutBienFormado("86.130.200-8"));
        Assert.False(MercadoPublicoCliente.RutBienFormado("1.234.567-8"));
        Assert.False(MercadoPublicoCliente.RutBienFormado("123.456.789-5"));
    }

    // ----------------------------------------------------------------------
    // La respuesta
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Devuelve_el_codigo_y_el_nombre_de_la_unica_empresa()
    {
        var empresas = await Montar(RespuestaReal).Cliente.BuscarProveedorAsync("86.130.200-8", default);

        var empresa = Assert.Single(empresas);
        Assert.Equal("71284", empresa.CodigoEmpresa);
        Assert.Equal("SISTEMAS MODULARES DE COMPUTACION SPA", empresa.NombreEmpresa);
    }

    [Fact]
    public async Task Devuelve_TODAS_las_empresas_y_no_elige_una()
    {
        // Este es el caso que hace que el método no devuelva "la empresa".
        //
        // Un RUT que no es de nadie devuelve empresas de verdad. Si el método
        // devolviera la primera, o la que "parezca más parecida", con un RUT mal
        // escrito la aplicación se pondría a vigilar a un tercero y todo lo
        // demás —licitaciones, ingesta, copia— escribiría sus datos sin un solo
        // error por el camino. Que devuelva la lista entera, y que el que llama
        // exija una sola, es lo que convierte eso en un fallo ruidoso.
        const string respuesta = """
            {"Cantidad":2,"listaEmpresas":[
              {"CodigoEmpresa":"150821","NombreEmpresa":"Canale"},
              {"CodigoEmpresa":"1294859","NombreEmpresa":"SANDRA CECILIA CISTERNA ALVIAL"}]}
            """;

        var empresas = await Montar(respuesta).Cliente.BuscarProveedorAsync("99.999.999-9", default);

        Assert.Equal(2, empresas.Count);
        Assert.Equal("150821", empresas[0].CodigoEmpresa);
        Assert.Equal("1294859", empresas[1].CodigoEmpresa);
    }

    [Fact]
    public async Task Una_empresa_sin_codigo_se_salta_en_vez_de_devolverla_con_el_codigo_vacio()
    {
        // Un código vacío daría una URL que parece correcta y no lo es: la
        // consulta saldría sin filtro y traería las licitaciones del país.
        const string respuesta = """
            {"Cantidad":2,"listaEmpresas":[
              {"NombreEmpresa":"SIN CODIGO"},
              {"CodigoEmpresa":"71284","NombreEmpresa":"CON CODIGO"}]}
            """;

        var empresas = await Montar(respuesta).Cliente.BuscarProveedorAsync("86.130.200-8", default);

        var empresa = Assert.Single(empresas);
        Assert.Equal("71284", empresa.CodigoEmpresa);
    }

    [Fact]
    public async Task Una_respuesta_sin_la_lista_devuelve_vacia_y_no_lanza()
    {
        // El nombre de la lista es "listaEmpresas" y no "Listado" como en los
        // demás endpoints. Con la clave equivocada la respuesta llega con
        // Cantidad 1 y lista vacía, que es indistinguible de "no hay empresa
        // con ese RUT".
        const string respuesta = """{"Cantidad":1}""";

        var empresas = await Montar(respuesta).Cliente.BuscarProveedorAsync("86.130.200-8", default);

        Assert.Empty(empresas);
    }

    [Fact]
    public async Task Un_rut_mal_formado_no_gasta_una_llamada()
    {
        var (cliente, peticiones) = Montar(RespuestaReal);

        var empresas = await cliente.BuscarProveedorAsync("861302008", default);

        Assert.Empty(empresas);
        Assert.Equal(0, peticiones.Llamadas);
    }

    [Fact]
    public async Task Un_rut_vacio_no_gasta_una_llamada()
    {
        var (cliente, peticiones) = Montar(RespuestaReal);

        var empresas = await cliente.BuscarProveedorAsync("   ", default);

        Assert.Empty(empresas);
        Assert.Equal(0, peticiones.Llamadas);
    }

    /// <summary>
    /// Un cliente con la respuesta que se le quiera, y sin red de verdad.
    /// </summary>
    /// <remarks>
    /// Devuelve también el manejador porque dos de estas pruebas miran que NO se
    /// salga a la red. Lo que se prueba es el parseo de la respuesta, no que
    /// haya red: el manejador falso solo hace que la llamada HTTP devuelva algo.
    /// </remarks>
    private static (MercadoPublicoCliente Cliente, ContadorPeticiones Peticiones) Montar(
        string respuesta)
    {
        var manejador = new ContadorPeticiones(respuesta);
        var opciones = Microsoft.Extensions.Options.Options.Create(
            new WatchMercadoPublico.Server.Models.MercadoPublicoOpciones { Ticket = TicketFalso });

        var cliente = new MercadoPublicoCliente(
            new FabricaDeClientes(manejador),
            opciones,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MercadoPublicoCliente>.Instance,
            new RitmoDeLlamadas(opciones));

        return (cliente, manejador);
    }

    private sealed class ContadorPeticiones(string cuerpo) : HttpMessageHandler
    {
        /// <summary>Cuántas peticiones HTTP han salido de verdad.</summary>
        public int Llamadas { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage peticion, CancellationToken ct)
        {
            Llamadas++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FabricaDeClientes(HttpMessageHandler manejador) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(manejador, disposeHandler: false);
    }
}
