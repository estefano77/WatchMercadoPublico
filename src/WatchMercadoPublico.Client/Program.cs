using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WatchMercadoPublico.Client;
using WatchMercadoPublico.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// La aplicación habla SOLO con su propio servidor. El ticket de Mercado Público
// nunca llega aquí: vive en la configuración del servidor, porque el
// WebAssembly se descarga entero y el ticket sería legible dentro del .dll.
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),

    // Sin esto se usaba el valor por defecto de .NET, 100 s, por accidente y sin
    // que nadie lo supiera. El número y su relación con los reintentos del
    // servidor están explicados en MercadoPublicoApi.SegundosEspera.
    Timeout = TimeSpan.FromSeconds(MercadoPublicoApi.SegundosEspera),
});

builder.Services.AddScoped<MercadoPublicoApi>();
builder.Services.AddScoped<ThemeService>();

await builder.Build().RunAsync();