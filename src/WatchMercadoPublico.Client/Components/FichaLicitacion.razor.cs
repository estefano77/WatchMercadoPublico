using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using WatchMercadoPublico.Client.Models;
using WatchMercadoPublico.Client.Services;

namespace WatchMercadoPublico.Client.Components;

public partial class FichaLicitacion : ComponentBase
{
    [Parameter, EditorRequired] public Licitacion Licitacion { get; set; } = default!;

    /// <summary>Palabras de la búsqueda actual, para resaltarlas.</summary>
    [Parameter] public string[] Palabras { get; set; } = [];

    /// <summary>Se dispara al pedir el detalle (una petición a la API).</summary>
    [Parameter] public EventCallback<Licitacion> AbrirAsync { get; set; }

    private string FranjaEstado => Licitacion.Activa
        ? "bg-emerald-500"
        : "bg-line-strong";

    /// <summary>
    /// La insignia del estado.
    ///
    /// ADJUDICADA TIENE INSIGNIA PROPIA, y antes no: como no es "activa", caía en
    /// la gris de los estados cerrados y quedaba igual que una cerrada o una
    /// desierta. Pero es el único estado con una decisión y un documento que la
    /// respalda, que es el acta, y eso merecía distinguirse.
    ///
    /// Los colores están medidos, no elegidos a ojo. Contraste del texto sobre el
    /// fondo de la tarjeta, WCAG:
    ///
    ///     claro   5,32      sobre tinte violeta al 22% con texto de marca
    ///     oscuro  5,24      el mismo tinte con violet-400
    ///
    /// El texto cambia de tono entre temas porque el violeta de la marca es
    /// #6d28d9 en claro y #8b5cf6 en oscuro, y con uno solo no sale bien en ninguno
    /// de los dos: con violet-500 en claro se queda en 3,30. Es el mismo truco que
    /// hace la de publicada con emerald-600 y emerald-400.
    ///
    /// El violeta es el único tono libre del tema: emerald es publicada, amber es
    /// cerrando y rose es error.
    /// </summary>
    private string ClaseEstado => Licitacion.EsAdjudicada
        ? "rounded-md bg-violet-500/[0.22] px-2 py-0.5 text-[0.65rem] font-bold tracking-wide uppercase ring-1 ring-inset ring-violet-500/[0.38] text-marca-b dark:text-violet-400"
        : Licitacion.Activa
            ? "rounded-md bg-emerald-500/15 px-2 py-0.5 text-[0.65rem] font-bold tracking-wide text-emerald-600 uppercase dark:text-emerald-400"
            : "rounded-md bg-line px-2 py-0.5 text-[0.65rem] font-bold tracking-wide text-muted uppercase";

    private (string Texto, string Tono) Cierre => Formato.CierraEn(Licitacion.FechaCierre);
    private string TextoCierre => Cierre.Texto;
    private string TonoCierre => Cierre.Tono;

    private List<TrozoTexto> Resaltado => Formato.Resaltar(Licitacion.Nombre, Palabras);
}
