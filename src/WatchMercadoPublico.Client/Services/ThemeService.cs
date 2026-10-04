using Microsoft.JSInterop;

namespace WatchMercadoPublico.Client.Services;

/// <summary>
/// Modo claro/oscuro.
///
/// La clase .dark se aplica en el &lt;html&gt; por un script en línea de
/// index.html, ejecutado ANTES de pintar, para que la página no parpadee. Aquí
/// solo se lee ese estado ya aplicado y se cambia; Blazor vuelve a renderizar
/// el botón solo, porque el manejador del clic dispara el ciclo de render.
/// </summary>
public sealed class ThemeService(IJSRuntime js)
{
    /// <summary>Se llama una vez, al montar la aplicación, para leer el tema ya aplicado.</summary>
    public async Task InicializarAsync()
    {
        try
        {
            EsOscuro = await js.InvokeAsync<bool>(
                "watchMercadoPublico.isDarkTheme", CancellationToken.None);
        }
        catch (JSException)
        {
            // Sin JS no hay conmutación: se mantiene el tema que puso el script.
            EsOscuro = false;
        }
    }

    public bool EsOscuro { get; private set; }

    public async Task AlternarAsync()
    {
        EsOscuro = !EsOscuro;

        try
        {
            await js.InvokeVoidAsync("watchMercadoPublico.applyTheme", EsOscuro);
        }
        catch (JSException)
        {
            // La interfaz ya muestra el modo nuevo aunque el script no esté
            // disponible: solo se pierde la persistencia en localStorage.
        }
    }
}