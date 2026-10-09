using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace WatchMercadoPublico.Client.Components;

public partial class Aviso : ComponentBase
{
    [Parameter] public string? Mensaje { get; set; }
    [Parameter] public string? Titulo { get; set; }
    [Parameter] public string Tipo { get; set; } = "info";
    [Parameter] public bool Cerrable { get; set; } = true;
    [Parameter] public EventCallback OnCerrado { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private string Rol => Tipo == "error" ? "alert" : "status";

    private string NombreIcono => Tipo switch
    {
        "error" => "alerta",
        "exito" => "check",
        _ => "info",
    };

    // La clase entera se calcula en @code y se mete en el atributo: partida en
    // varias líneas dentro de un atributo Razor no compila (RZ1006) y con
    // contenido complejo tampoco (RZ9986).
    private string ClaseContenedor => string.Join(' ',
        "flex items-start gap-3 rounded-2xl border px-4 py-3.5",
        Tipo switch
        {
            "error" => "border-rose-500/30 bg-rose-500/10",
            "exito" => "border-emerald-500/30 bg-emerald-500/10",
            "aviso" => "border-amber-500/30 bg-amber-500/10",
            _ => "border-violet-500/30 bg-violet-500/10",
        });

    private string ClaseIcono => string.Join(' ',
        "mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg",
        Tipo switch
        {
            "error" => "bg-rose-500/20 text-rose-400",
            "exito" => "bg-emerald-500/20 text-emerald-400",
            "aviso" => "bg-amber-500/20 text-amber-400",
            _ => "bg-violet-500/20 text-violet-400",
        });

    private string ClaseTitulo => "text-sm font-bold text-ink";

    private string ClaseMensaje => "mt-0.5 text-sm leading-relaxed text-ink-soft";

    private async Task Cerrar()
    {
        Mensaje = null;
        await OnCerrado.InvokeAsync();
        Cerrado = true;
    }

    /// <summary>
    /// Evita que el aviso reaparezca solo tras cerrarlo: sin esto, el siguiente
    /// cambio de estado lo vuelve a pintar y el usuario no puede descartarlo.
    /// </summary>
    private bool Cerrado { get; set; }
}
