using Microsoft.Extensions.Logging.Abstractions;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// Un día que falla se recuerda, para no volver a pagar su escalera de reintentos.
/// </summary>
/// <para>
/// Esto viene de una medición en MonsterASP, no de una suposición. Dos consultas
/// seguidas de la misma semana tardaron <b>65,51 s</b> y <b>64,98 s</b>, las dos
/// con <c>desdeCache: false</c> y un día sin respuesta. Ese día era el 7 de
/// octubre, es decir, hoy.
/// </para>
/// <para>
/// Los 60 de esos 65 segundos no eran la API tardando: eran las esperas entre
/// reintentos, que son de 2, 4, 8, 16 y 30 s. El día fallaba siempre y deprisa, se
/// subía la escalera entera, y como el fallo no se guardaba en ninguna parte, la
/// siguiente consulta volvía a subirla. Para siempre.
/// </para>
/// <para>
/// Estos tests fijan las cuatro cosas que tienen que seguir ciertas: que el fallo
/// se recuerda, que no se recuerda para siempre, que un éxito lo borra, y que el
/// botón "Actualizar" lo salta.
/// </para>
public class FallosDeDiaTests
{
    private const string Proveedor = "71284";

    private static CacheMercadoPublico NuevaCache(TimeSpan? fallo = null) =>
        new(NullLogger<CacheMercadoPublico>.Instance,
            TimeSpan.FromMinutes(4),
            TimeSpan.FromDays(30),
            fallo);

    private static DateOnly UnDia() => new(2026, 10, 7);

    [Fact]
    public void UnDiaQueNoHaFalladoNoEstaRecordado()
    {
        var cache = NuevaCache();

        Assert.False(cache.DiaFallidoReciente(Proveedor, UnDia()));
    }

    [Fact]
    public void UnDiaQueFalloSeRecuerda()
    {
        var cache = NuevaCache();

        cache.GuardarDiaFallido(Proveedor, UnDia());

        Assert.True(cache.DiaFallidoReciente(Proveedor, UnDia()));
    }

    /// <summary>
    /// Un fallo que dura para siempre sería peor que el problema que arregla:
    /// ese día jamás volvería a aparecer y no se podría ni saber por qué.
    /// </summary>
    [Fact]
    public void ElPlazoDelFalloCaduca()
    {
        // Un plazo de un milisegundo ya está vencido cuando se pregunta.
        var cache = NuevaCache(TimeSpan.FromMilliseconds(1));

        cache.GuardarDiaFallido(Proveedor, UnDia());

        Assert.False(cache.DiaFallidoReciente(Proveedor, UnDia()));
    }

    /// <summary>
    /// El fallo es por DÍA, no por empresa entera: el mismo día de otro proveedor
    /// no puede quedar marcado.
    /// </summary>
    [Fact]
    public void ElFalloNoSeContaminaAOtroProveedor()
    {
        var cache = NuevaCache();

        cache.GuardarDiaFallido(Proveedor, UnDia());

        Assert.False(cache.DiaFallidoReciente("99999", UnDia()));
    }

    /// <summary>
    /// Si el día acaba de responder, el fallo ya no cuenta. Si se olvidara
    /// borrarlo, la siguiente consulta saltaría ese día sin preguntar y el
    /// listado bueno se perdería.
    /// </summary>
    [Fact]
    public void UnExitoBorraElFallo()
    {
        var cache = NuevaCache();

        cache.GuardarDiaFallido(Proveedor, UnDia());
        cache.GuardarDia(Proveedor, UnDia(), []);

        Assert.False(cache.DiaFallidoReciente(Proveedor, UnDia()));
    }

    /// <summary>
    /// El botón "Actualizar" vacía la caché, y con ella los fallos. Si no los
    /// vaciara, el botón dejaría de reintentar el día que más urge reintentar.
    /// </summary>
    [Fact]
    public void ActualizarVaciaTambienLosFallos()
    {
        var cache = NuevaCache();

        cache.GuardarDiaFallido(Proveedor, UnDia());
        cache.Invalidar(Proveedor);

        Assert.False(cache.DiaFallidoReciente(Proveedor, UnDia()));
    }

    /// <summary>
    /// La razón de que el plazo del fallo sea de 15 minutos y no de 4.
    ///
    /// Con el mismo plazo que la caché de datos, cada refresco automático
    /// llegaría justo cuando el fallo caduca, y la escalera de seis intentos se
    /// volvería a pagar entera una vez cada cinco minutos. Sería lo mismo que no
    /// haber arreglado nada, pero con más código.
    /// </summary>
    [Fact]
    public void ElPlazoDelFalloSuperaAlRefrescoAutomatico()
    {
        var cache = NuevaCache();

        Assert.True(
            cache.CaducidadFallo > TimeSpan.FromMinutes(5),
            "El plazo del fallo tiene que durar más que el refresco automático.");
    }
}
