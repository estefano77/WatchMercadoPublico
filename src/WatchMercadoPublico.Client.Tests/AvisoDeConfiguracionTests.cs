using System.Text.Json;
using WatchMercadoPublico.Client.Models;
using Xunit;

namespace WatchMercadoPublico.Client.Tests;

/// <summary>
/// El aviso de "la aplicación no está configurada": que el cliente MIRE lo que
/// dice el servidor y no lo invente.
/// </summary>
/// <remarks>
/// <para>
/// Antes esta clase comprobaba una propiedad <c>get</c> del cliente que armaba el
/// mensaje con tres banderas binarias. Eso ya no existe: la empresa sale de
/// <c>MpEmpresa</c> y hay motivos que el cliente no puede ni saber ni deducir.
/// El mensaje llega escrito desde <c>/api/estado</c> y aquí se limita a mostrarse.
/// </para>
///
/// <para>
/// La consecuencia es que estas pruebas ya no pueden fijar el TEXTO de cada
/// caso, porque el texto lo pone el servidor y no el cliente. Lo que fijan es lo
/// contrario: que el cliente no lo sustituya por su cuenta.
/// </para>
///
/// <para>
/// Y el riesgo real que queda es otro, más aburrido y más probable: que la
/// propiedad no se empareje con la clave del JSON y llegue siempre vacía. Eso
/// dejaría el aviso sin pintar en cualquier instalación mal configurada, sin
/// error ni aviso de nada. La primera prueba de esta clase es exactamente eso.
/// </para>
/// </remarks>
public class AvisoDeConfiguracionTests
{
    /// <summary>Las opciones con las que se deserializa el estado.</summary>
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void El_mensaje_del_servidor_llega_tal_cual_incluso_con_acentos()
    {
        // El caso que más miedo da al cambiar esto. "afición" no es un ejemplo de
        // laboratorio: el mensaje se arma en el servidor y se pinta en el
        // cliente, y si el emparejamiento de la propiedad falla se queda en
        // blanco sin decir nada. Lo que no se puede es "arreglarlo" sustituyendo
        // un texto con otro que se parezca.
        const string motivo =
            "la tabla MpEmpresa está vacía para el RUT 86.130.200-8. Se llena sola: "
            + "corre scripts\\cargar-base-remota.ps1 -Ingerir -Si en la máquina que carga la base.";

        var estado = Deserializar("""
            {
              "servible": false,
              "empresaConfigurada": false,
              "usaBaseDeDatos": true,
              "faltaConfiguracion": "%MOTIVO%"
            }
            """.Replace("%MOTIVO%", motivo.Replace("\\", "\\\\").Replace("\"", "\\\"")));

        Assert.Equal(motivo, estado.FaltaConfiguracion);
    }

    [Fact]
    public void Sin_mensaje_del_servidor_no_se_inventa_uno()
    {
        // Y la mitad que importa. Con empresa no configurada y SIN mensaje, el
        // cliente tiene que callarse. Si apareciera un aviso, sería el cliente
        // decidiendo que algo falta por su cuenta, que es exactamente lo que se
        // quitó al hacer que el mensaje viaje desde el servidor: el cliente sabe
        // que no hay empresa, pero no sabe POR QUÉ, y un motivo inventado puede
        // mandar a alguien a donde no es.
        var estado = Deserializar("""
            {
              "servible": false,
              "empresaConfigurada": false,
              "ticketConfigurado": true
            }
            """);

        Assert.False(estado.EmpresaConfigurada);
        Assert.Null(estado.FaltaConfiguracion);
    }

    [Theory]
    // Los cuatro motivos que el servidor sabe dar y el cliente no. Se comprueba
    // que ninguno se pierde por el camino, con su texto entero.
    [InlineData("falta el ticket de Mercado Público.")]
    [InlineData("en la base no está el procedimiento mp.LeeEmpresa.")]
    [InlineData("el RUT de la sección MercadoPublico no tiene el formato DD.DDD.DDD-D.")]
    [InlineData("el RUT 86.130.200-8 devuelve 2 empresas en Mercado Público.")]
    public void Cualquier_motivo_sobrevive_al_viaje(string motivo)
    {
        var estado = Deserializar(
            "{\"servible\":false,\"faltaConfiguracion\":\""
            + motivo.Replace("\"", "\\\"") + "\"}");

        Assert.Equal(motivo, estado.FaltaConfiguracion);
    }

    [Fact]
    public void Un_estado_correcto_no_trae_motivo()
    {
        var estado = Deserializar("""
            {"servible":true,"empresaConfigurada":true,"ticketConfigurado":true,
             "faltaConfiguracion":null}
            """);

        Assert.Null(estado.FaltaConfiguracion);
    }

    [Fact]
    public void Un_estado_sin_la_clave_no_revienta()
    {
        // Y que falte la clave entero tampoco es un error. El servidor puede
        // publicar una versión anterior que no la mande, y reventar la pantalla al
        // arrancar por eso sería lo peor de todo: la web entera caída por una
        // propiedad que falta.
        var estado = Deserializar("""{"servible":true,"empresaConfigurada":true}""");

        Assert.Null(estado.FaltaConfiguracion);
    }

    private static EstadoApi Deserializar(string json) =>
        JsonSerializer.Deserialize<EstadoApi>(json, Opciones)!;
}
