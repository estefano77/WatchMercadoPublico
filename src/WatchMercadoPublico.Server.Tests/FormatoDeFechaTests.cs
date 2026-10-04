using System.Globalization;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Regresión del bug que tuvo la aplicación: la consulta fallaba SIEMPRE.
///
/// Se mandaba <c>fecha=4102026</c>, de siete dígitos, porque en .NET <c>"d"</c>
/// es el día SIN cero a la izquierda y <c>"dd"</c> el día CON cero. La API
/// responde <c>500</c> con <c>{"Codigo":10300,"Mensaje":"El formato del
/// parametro fechas es incorrecto"}</c>.
///
/// Lo que lo escondía es que <c>"d"</c> y <c>"dd"</c> solo se diferencian en los
/// días 1 al 9. Del 10 en adelante el día ya tiene dos cifras y el fallo
/// desaparece solo, así que el bug solo vivía en la primera quincena de cada
/// mes y no se veía en las fechas de prueba (28/09/2026 era día 28).
///
/// Estos tests no tocan la API: comprueban funciones puras, así que corren sin
/// red y sin ticket.
/// </summary>
public class FormatoDeFechaTests
{
    private const string Proveedor = "71284";

    /// <summary>
    /// Un ticket **inventado**. Aquí se comprueba cómo se construye la URL, no
    /// si el ticket sirve, así que no hay que poner el real.
    ///
    /// OJO: no pegar aquí el ticket de verdad. Este proyecto está en un
    /// repositorio, y un ticket en un archivo de test queda en el historial
    /// para siempre aunque después se borre la línea. Ya pasó una vez.
    /// </summary>
    private const string TicketFalso = "00000000-0000-0000-0000-000000000000";

    private static string FechaDe(string url) => url
        .Split("fecha=", StringSplitOptions.None)[1]
        .Split('&')[0];

    [Theory]
    // El día que lo destapó.
    [InlineData(2026, 10, 4, "04102026")]
    // La primera quincena, donde "d" y "dd" se diferencian.
    [InlineData(2026, 9, 1, "01092026")]
    [InlineData(2026, 1, 9, "09012026")]
    // El día 10, donde el bug desaparece solo: el que daba falsa tranquilidad.
    [InlineData(2026, 10, 10, "10102026")]
    // Fin de mes y de año.
    [InlineData(2026, 12, 31, "31122026")]
    [InlineData(2026, 9, 30, "30092026")]
    // Año bisiesto: 29 de febrero.
    [InlineData(2028, 2, 29, "29022028")]
    public void La_fecha_va_en_DDMMAAAA_con_los_dos_campos_rellenos(
        int anio, int mes, int dia, string esperado)
    {
        var url = MercadoPublicoCliente.ConstruirUrlDia(
            Proveedor, new DateOnly(anio, mes, dia), TicketFalso);

        Assert.Equal(esperado, FechaDe(url));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void La_primera_decena_conserva_el_cero_a_la_izquierda(int dia)
    {
        var url = MercadoPublicoCliente.ConstruirUrlDia(
            Proveedor, new DateOnly(2026, 10, dia), TicketFalso);

        // Ocho dígitos, y los dos primeros NO pueden ser un número suelto.
        Assert.Equal(8, FechaDe(url).Length);
        Assert.StartsWith("0", FechaDe(url));
    }

    [Fact]
    public void Ningun_dia_de_dos_anos_produce_una_fecha_de_menos_de_ocho_digitos()
    {
        // Se recorren los 730 días porque el fallo no depende del mes: depende
        // de si el día tiene una o dos cifras. Un solo día de prueba no
        // demuestra nada, y una fecha de prueba elegida al azar no lo habría
        // detectado: por eso el bug convivió con las pruebas sin que se vieran.
        var fallos = new List<string>();

        var dia = new DateOnly(2026, 1, 1);
        var fin = new DateOnly(2027, 12, 31);

        while (dia <= fin)
        {
            var url = MercadoPublicoCliente.ConstruirUrlDia(Proveedor, dia, TicketFalso);
            var fecha = FechaDe(url);

            if (fecha.Length != 8)
                fallos.Add($"{dia:yyyy-MM-dd} -> '{fecha}' ({fecha.Length} dígitos)");

            dia = dia.AddDays(1);
        }

        Assert.Empty(fallos);
    }

    [Fact]
    public void El_formato_que_provocaba_el_bug_da_siete_digitos_en_la_primera_decena()
    {
        // Esto no prueba nada de nuestro código: documenta POR QUÉ el bug era
        // invisible. Si algún día .NET cambiara esta diferencia entre "d" y
        // "dd", este test avisa y hay que revisar la conclusión.
        var conCero = new DateOnly(2026, 10, 4).ToString("ddMMyyyy", CultureInfo.InvariantCulture);
        var sinCero = new DateOnly(2026, 10, 4).ToString("dMMyyyy", CultureInfo.InvariantCulture);

        Assert.Equal("04102026", conCero);
        Assert.Equal("4102026", sinCero);

        // Y del 10 en adelante los dos coinciden: por eso el fallo se escondía.
        Assert.Equal(
            new DateOnly(2026, 10, 28).ToString("dMMyyyy", CultureInfo.InvariantCulture),
            new DateOnly(2026, 10, 28).ToString("ddMMyyyy", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("ar-SA")]  // Calendario islámico: el año no es el gregoriano.
    [InlineData("th-TH")]  // Calendario budista.
    [InlineData("fa-IR")]  // Calendario solar hijri.
    public void La_fecha_no_depende_del_calendario_de_la_cultura_del_servidor(string cultura)
    {
        var anterior = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultura);

            var url = MercadoPublicoCliente.ConstruirUrlDia(
                Proveedor, new DateOnly(2026, 10, 4), TicketFalso);

            Assert.Equal("04102026", FechaDe(url));
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }

    [Fact]
    public void La_url_lleva_los_tres_parametros()
    {
        var url = MercadoPublicoCliente.ConstruirUrlDia(
            Proveedor, new DateOnly(2026, 9, 28), TicketFalso);

        Assert.StartsWith("publico/licitaciones.json?", url);
        Assert.Contains("CodigoProveedor=71284", url);
        Assert.Contains($"ticket={TicketFalso}", url);
    }

    [Fact]
    public void El_codigo_y_el_ticket_se_escapan()
    {
        // Sin escapar, un "Y" o un espacio rompería la URL entera.
        var url = MercadoPublicoCliente.ConstruirUrlDia(
            "codigo&con=riesgo", new DateOnly(2026, 9, 28), "ticket con espacios");

        Assert.Contains("CodigoProveedor=codigo%26con%3Driesgo", url);
        Assert.Contains("ticket=ticket%20con%20espacios", url);
    }
}