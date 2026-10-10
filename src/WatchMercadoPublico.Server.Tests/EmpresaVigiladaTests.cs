using WatchMercadoPublico.Server.Models;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// La empresa vigilada: qué se resuelve, de dónde, y qué se dice cuando no se
/// puede.
/// </summary>
/// <remarks>
/// <para>
/// Esta clase pasó a ser la dueña de una pregunta que antes la contestaba
/// <c>MercadoPublicoOpciones.Servible</c>: ¿se puede atender una consulta?. Y
/// la respuesta se complicó, que es lo que hacen las cosas.
/// </para>
///
/// <para>
/// Antes había DOS motivos y ninguno se confundía con otro: "falta el ticket" y
/// "falta el código de proveedor". Ahora hay más, y cada uno se arregla con una
/// acción distinta:
///
/// <list type="bullet">
/// <item>la tabla <c>MpEmpresa</c> está vacía para ese RUT</item>
/// <item><c>mp.LeeEmpresa</c> no está instalado en la base</item>
/// <item>el RUT del appsettings no tiene el formato que acepta la API</item>
/// <item>el RUT devuelve más de una empresa y no se sabe cuál es</item>
/// </list>
///
/// <para>
/// Cuatro arreglos distintos. Decir "falta el código" cuatro veces habría sido un
/// aviso que no dice nada, que es exactamente el problema que había: el mensaje
/// era el mismo en cuatro situaciones en las que había que hacer cosas
/// diferentes.
/// </para>
/// </remarks>
public sealed class EmpresaVigiladaTests
{
    private static readonly EmpresaActual LaEmpresa = new(
        "71284", "SISTEMAS MODULARES DE COMPUTACION SPA",
        "86.130.200-8", EmpresaActual.UrlPorDefecto);

    // --- Resuelta ---

    [Fact]
    public void Una_empresa_resuelta_tiene_codigo_y_no_dice_nada_que_falte()
    {
        var empresa = new EmpresaVigilada(LaEmpresa);

        Assert.True(empresa.TieneCodigoProveedor);
        Assert.Equal("71284", empresa.CodigoProveedor);
        Assert.Null(empresa.Motivo);
        Assert.False(empresa.FaltaAlgo);
    }

    [Fact]
    public void El_codigo_sale_exacto_como_lo_resolvio_la_fuente()
    {
        // Sin recortar aquí. Ya lo viene recortado de donde sale: la API pasa por
        // MercadoPublicoCliente.Limpiar y la base por mp.LeeEmpresa. Recortarlo
        // otra vez sería poner el mismo cuidado en dos sitios, y el segundo sitio
        // es el que nadie lee cuando algo va mal.
        Assert.Equal("71284", LaEmpresa.CodigoProveedor);
    }

    // --- Sin resolver ---

    [Fact]
    public void Sin_empresa_no_hay_codigo_y_el_motivo_explica_que_pasar()
    {
        var empresa = new EmpresaVigilada(null, "la tabla MpEmpresa está vacía");

        Assert.Null(empresa.Actual);
        Assert.False(empresa.TieneCodigoProveedor);

        // Y cadena vacía, no excepción. Todos los usos son consultas que con el
        // código vacío devuelven cero filas, que es un resultado que la pantalla
        // ya sabe pintar.
        Assert.Equal("", empresa.CodigoProveedor);
        Assert.True(empresa.FaltaAlgo);
        Assert.Equal("la tabla MpEmpresa está vacía", empresa.Motivo);
    }

    [Fact]
    public void En_modo_demo_no_avisar_nada_no_es_lo_mismo_que_no_tener_empresa()
    {
        // Esta es la distinción que hace que el aviso siga valiendo. Si
        // "no hay empresa" y "no hace falta ninguna" compartieran el mismo
        // estado, en demostración aparecería un aviso pidiendo configuración que
        // en demostración no hace falta, que es lo que pasaba antes de esto.
        var empresa = new EmpresaVigilada(null);

        Assert.Null(empresa.Actual);
        Assert.Null(empresa.Motivo);
        Assert.False(empresa.FaltaAlgo);
    }

    // --- El motivo tiene que ser accionable ---

    [Theory]
    // Cada motivo debe decir QUÉ FALTA y QUÉ HACER. No basta con que no sea
    // nulo: un "error de configuración" no lleva a ningún sitio.
    [InlineData("la tabla MpEmpresa está vacía", "MpEmpresa")]
    [InlineData("falta aplicar 05-procedimientos-lectura.sql", "05-procedimientos-lectura.sql")]
    [InlineData("el RUT no tiene el formato DD.DDD.DDD-D", "DD.DDD.DDD-D")]
    public void Cada_motivo_dice_que_hacer(string motivo, string tieneQueDecir)
    {
        var empresa = new EmpresaVigilada(null, motivo);

        Assert.Contains(tieneQueDecir, empresa.Motivo!);
    }

    // --- El enlace de la cabecera ---

    [Fact]
    public void El_enlace_solo_cuenta_si_va_por_https()
    {
        Assert.True(LaEmpresa.TieneUrl);
        Assert.True((LaEmpresa with { UrlMercadoPublico = "https://otro.example/x" }).TieneUrl);

        Assert.False((LaEmpresa with { UrlMercadoPublico = "" }).TieneUrl);
        Assert.False((LaEmpresa with { UrlMercadoPublico = "http://inseguro.example" }).TieneUrl);
        Assert.False((LaEmpresa with { UrlMercadoPublico = "/relativa" }).TieneUrl);
    }
}
