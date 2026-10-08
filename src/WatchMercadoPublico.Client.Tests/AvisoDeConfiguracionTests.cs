using WatchMercadoPublico.Client.Models;
using Xunit;

namespace WatchMercadoPublico.Client.Tests;

/// <summary>
/// El aviso de "la aplicación no está configurada", en los dos modos de fuente.
/// </summary>
/// <remarks>
/// <para>
/// Esta clase existe por un fallo que estuvo en pantalla al poner la fuente en
/// marcha. En modo base de datos aparecía un aviso pidiendo un ticket, en una
/// instalación que ya tenía la base configurada y el código de proveedor puesto,
/// y no hacía falta para nada: en ese modo no se pregunta a Mercado Público.
/// </para>
///
/// <para>
/// El aviso es lo que más se lee de toda la pantalla cuando algo va mal, así que
/// si dice la cosa equivocada manda sobre lo que haya.
/// </para>
/// </remarks>
public class AvisoDeConfiguracionTests
{
    private static EstadoApi Estado(
        string fuente = "api",
        bool baseUtilizable = true,
        bool conTicket = true,
        bool conEmpresa = true) => new()
        {
            Fuente = fuente,
            BaseDeDatosUtilizable = baseUtilizable,
            TicketConfigurado = conTicket,
            EmpresaConfigurada = conEmpresa,
            Servible = conTicket && conEmpresa,
        };

    // --- Modo base de datos ---

    [Fact]
    public void En_modo_base_de_datos_no_se_pide_ticket()
    {
        // Lo que se rompió de verdad: con fuente "sql" y sin ticket, el aviso
        // decía que faltaba el ticket. No faltaba nada y el mensaje era falso.
        var estado = Estado(fuente: "sql", conTicket: false);

        Assert.Null(estado.FaltaConfiguracion);
    }

    [Fact]
    public void En_modo_base_de_datos_sin_cadena_si_se_avisa()
    {
        var aviso = Estado(fuente: "sql", baseUtilizable: false).FaltaConfiguracion;

        Assert.NotNull(aviso);
        Assert.Contains("cadena de conexi", aviso!.Replace("ó", "o"));
    }

    [Fact]
    public void En_modo_base_de_datos_tambien_hace_falta_el_codigo_de_proveedor()
    {
        // Lo que se lee de la base está etiquetado por empresa. Sin esto no se
        // sabe qué parte mirar, así que el aviso tiene que seguir apareciendo.
        var estado = Estado(fuente: "sql", conEmpresa: false);

        Assert.Contains("proveedor", estado.FaltaConfiguracion!);
    }

    [Fact]
    public void En_modo_base_de_datos_la_cadena_falta_antes_que_el_codigo()
    {
        // El orden importa: si los dos faltan y el aviso dice lo de la empresa,
        // el usuario configura el código, lo reinicia y sigue viendo un aviso.
        var aviso = Estado(fuente: "sql", baseUtilizable: false, conEmpresa: false)
            .FaltaConfiguracion!;

        Assert.Contains("cadena", aviso.Replace("ó", "o"));
    }

    // --- Modo API: lo de antes, sin cambios ---

    [Fact]
    public void En_modo_api_sin_ticket_se_pide_el_ticket()
    {
        Assert.Contains("ticket", Estado(fuente: "api", conTicket: false).FaltaConfiguracion!);
    }

    [Fact]
    public void En_modo_api_sin_codigo_de_proveedor_se_pide_el_codigo()
    {
        Assert.Contains("proveedor", Estado(fuente: "api", conEmpresa: false).FaltaConfiguracion!);
    }

    [Fact]
    public void En_modo_api_todo_configurado_no_dice_nada()
    {
        Assert.Null(Estado(fuente: "api").FaltaConfiguracion);
    }

    [Fact]
    public void En_modo_api_la_cadena_de_conexion_no_da_lo_mismo()
    {
        // La cadena de la base no tiene nada que ver con este modo. Que no se
        // note es lo que evita un aviso fantasma en una instalación
        // que nunca usa la base.
        Assert.Null(Estado(fuente: "api", baseUtilizable: false).FaltaConfiguracion);
    }
}
