using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using WatchMercadoPublico.Client.Models;
using WatchMercadoPublico.Client.Services;

namespace WatchMercadoPublico.Client.Components;

public partial class ModalDetalle : ComponentBase
{
    /// <summary>Licitación abierta. Puede ser null si aún no hay nada.</summary>
    [Parameter] public Licitacion? Licitacion { get; set; }

    /// <summary>Detalle ya cargado (null mientras se pide).</summary>
    [Parameter] public DetalleLicitacion? Detalle { get; set; }

    [Parameter] public bool Cargando { get; set; }
    [Parameter] public bool DesdeCache { get; set; }

    /// <summary>
    /// ¿Viene la ficha de SQL Server en vez de de la API?
    /// </summary>
    /// <remarks>
    /// Lo pasa quien abre el modal, que es quien sabe la fuente. No se deduce
    /// aquí: un componente que adivina de dónde vienen los datos es un
    /// componente que un día dice "caché" sobre una lectura a la base.
    /// </remarks>
    [Parameter] public bool UsaBaseDeDatos { get; set; }

    /// <summary>
    /// Mensaje de error a mostrar. OJO con el nombre: un parámetro de tipo
    /// string recibe LITERAL lo que se escriba, así que hay que pasarlo como
    /// <c>Error="@errorDetalle"</c> y no <c>Error="errorDetalle"</c>. Con lo
    /// segundo el modal pintaba la palabra "errorDetalle" como si fuera el
    /// mensaje. Los parámetros de tipo objeto (Licitacion, Detalle) sí exigen
    /// expresión, y por eso funcionan sin '@'.
    /// </summary>
    [Parameter] public string? MensajeError { get; set; }

    [Parameter] public EventCallback OnCerrar { get; set; }

    private string EstadoTexto => Detalle?.Estado ?? Licitacion?.EstadoLegible ?? "";

    private string ClaseEstado => (Detalle?.CodigoEstado ?? Licitacion?.CodigoEstado) is null or 5
        ? "rounded-md bg-emerald-500/15 px-2 py-0.5 text-[0.65rem] font-bold tracking-wide text-emerald-600 uppercase dark:text-emerald-400"
        : "rounded-md bg-line px-2 py-0.5 text-[0.65rem] font-bold tracking-wide text-muted uppercase";

    private string Ubicacion => string.Join(", ",
        new[] { Detalle?.RegionOrganismo, Detalle?.ComunaOrganismo }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    private (string Texto, string Tono) Cierre =>
        Formato.CierraEn(Detalle?.FechaCierre ?? Licitacion?.FechaCierre);

    /// <summary>
    /// Cuánto se adjudicó frente a lo presupuestado, en una frase corta.
    ///
    /// Solo si las dos cifras están: sin el estimado no hay contra qué
    /// comparar, y con la misma cifra no hay nada que decir.
    /// </summary>
    private string? DiferenciaAdjudicado
    {
        get
        {
            if (Detalle?.TotalAdjudicado is not { } adjudicado) return null;
            if (Detalle.MontoEstimado is not { } estimado || estimado <= 0) return null;

            var porcentaje = (adjudicado - estimado) / estimado * 100m;

            // Menos de 1% es ruido de redondeo, no una diferencia real.
            if (Math.Abs(porcentaje) < 1m) return null;

            return porcentaje < 0
                ? $"un {Formato.Porcentaje(Math.Abs(porcentaje))} por debajo"
                : $"un {Formato.Porcentaje(porcentaje)} por encima";
        }
    }

    private string TextoCierre => Cierre.Texto;
    private string ClaseCierre => Licitacion?.Activa == false
        ? "text-xs font-semibold text-muted"
        : "rounded-md bg-amber-500/15 px-2 py-1 text-xs font-bold " + Cierre.Tono;

    /// <summary>
    /// Fechas del proceso en orden, quitando las vacías. El servidor ya las
    /// devuelve anidadas en "Fechas"; aquí solo se ordenan y se limpian.
    /// </summary>
    private List<(string Etiqueta, DateTimeOffset Fecha)> Hitos
    {
        get
        {
            if (Detalle is null) return [];

            var lista = new List<(string, DateTimeOffset)>();
            void Añadir(string etiqueta, DateTimeOffset? fecha)
            {
                if (fecha is { } f) lista.Add((etiqueta, f));
            }

            Añadir("Creación", Detalle.FechaCreacion);
            Añadir("Publicación", Detalle.FechaPublicacion);
            Añadir("Inicio de vigencia", Detalle.FechaFinal);
            Añadir("Apertura técnica", Detalle.FechaAperturaTecnica);
            Añadir("Apertura económica", Detalle.FechaAperturaEconomica);
            Añadir("Adjudicación", Detalle.FechaAdjudicacion);

            return lista.OrderBy(x => x.Item2).ToList();
        }
    }

    /// <summary>
    /// A dónde lleva el enlace a Mercado Público.
    ///
    /// ANTES se armaba aquí con el código: mercadopublico.cl/licitacion/{código}.
    /// ESA RUTA ESTÁ ROTA — se comprobó: devuelve HTTP 200 con una página de
    /// cuatro mil bytes que es una SPA, y al abrirla en el navegador redirige a
    /// una pantalla de "En estos momentos no podemos atender su solicitud". El
    /// 200 engañaba; comprobada en el navegador, no lo está.
    ///
    /// Así que el enlace usa la MISMA URL configurable que el de la cabecera. Se
    /// pierde una cosa: antes llevaba a la licitación concreta y ahora lleva al
    /// buscador. A cambio funciona, y el sitio puede cambiar de rutas sin que
    /// haya que tocar código.
    /// </summary>
    [Parameter] public string? UrlMercadoPublico { get; set; }

    /// <summary>
    /// El texto del enlace cambió de "Ver en mercadopublico.cl" a "Ir a Mercado
    /// Público" porque ya no lleva a VER esta licitación, sino al buscador.
    /// Decir "Ver" engañaría, y esa es justo la diferencia que hace que alguien
    /// confíe en un enlace y pulse donde no toca.
    /// </summary>
    private bool HayUrl => !string.IsNullOrWhiteSpace(UrlMercadoPublico);

    private async Task CerrarAsync() => await OnCerrar.InvokeAsync();
}
