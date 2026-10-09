using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using WatchMercadoPublico.Client.Models;
using WatchMercadoPublico.Client.Services;

namespace WatchMercadoPublico.Client.Pages;

/// <summary>
/// La pantalla de la semana, con el cÃ³digo aparte del marcado.
/// </summary>
/// <para>
/// Home.razor tenÃ­a 2.036 lÃ­neas y 1.425 de ellas eran de @code. El marcado
/// quedaba enterrado bajo el cÃ³digo: para leer cÃ³mo se pinta la habÃ­a que
/// saltar mil y pico lÃ­neas.
/// </para>
/// <para>
/// <b>OJO: esto NO la hace mÃ¡s fÃ¡cil de probar.</b> Todo lo de aquÃ­ sigue sin
/// cobertura, porque harÃ­a falta bUnit, que el repositorio no usa. La razÃ³n
/// por la que <c>FiltrosDeSemana</c> saliÃ³ del componente era otra, y buena:
/// allÃ­ el cÃ³digo se puede tocar sin que ninguna prueba se entere.
/// </para>
/// <para>
/// El <c>@implements</c>, los <c>@inject</c> y el <c>@using</c> se quedan en
/// el .razor a propÃ³sito. Declararlos ademÃ¡s aquÃ­ darÃ­a el error de que no
/// se puede implementar <c>IAsyncDisposable</c> dos veces.
/// </para>
public partial class Home : ComponentBase
{
    private EstadoApi? Estado { get; set; }
    private SemanaLicitaciones? Semana { get; set; }

    private bool Cargando { get; set; }
    private string? ErrorConsulta { get; set; }

    /// <summary>Segundos que lleva esperando la respuesta, para el contador.</summary>
    private int SegundosEsperando { get; set; }

    /// <summary>
    /// Instante en que empezó la espera, para medirla con el reloj.
    ///
    /// Es un reloj y no un contador porque el fallo que motiva esto es que el
    /// navegador puede parar la página: con un contador, los segundos que no
    /// llega ningún tick no se cuentan, y el contador queda diciendo un número
    /// pequeño cuando en realidad lleva mucho rato esperando.
    /// </summary>
    private DateTimeOffset? inicioEspera;

    // Selección
    // Selección. NO se dejan en 0 a propósito: el primer render ocurre antes de
    // que GetEstadoAsync responda, y con Mes = 0 la cabecera pedía el nombre del
    // mes 0 y Blazor tiraba ArgumentOutOfRangeException. Es el mismo fallo de
    // antes en otra forma: el estado de arranque necesita una representación,
    // aunque no sea la buena.
    private int Anio { get; set; } = DateTime.Today.Year;
    private int Mes { get; set; } = DateTime.Today.Month;
    private int NumeroSemana { get; set; } = 1;

    private System.Timers.Timer? temporizador;
    private System.Timers.Timer? reloj;
    private System.Timers.Timer? vigilante;

    /// <summary>
    /// Margen que se suma al plazo del cliente para que salte la red de seguridad.
    ///
    /// El cliente ya corta por su cuenta a los <see cref="MercadoPublicoApi.SegundosEspera"/>
    /// segundos, así que si a los 170 segundos la pantalla sigue esperando, lo que se
    /// atascó no fue la consulta al servidor sino el camino de vuelta de la respuesta.
    /// Con el margen justo la red saltaría en la misma consulta que el cliente ya
    /// está cortando por su cuenta, y entonces daría la por buena y se pelearían los
    /// dos mensajes.
    /// </summary>
    private const int MargenDelVigilante = 20;

    /// <summary>
    /// Token de la espera en curso, para que el usuario pueda dejarla. Vive aquí
    /// y no se pasa por parámetro a propósito: <c>CargarAsync</c> se llama desde
    /// cuatro sitios y el botón tiene que poder llegar a la que está corriendo.
    /// </summary>
    private CancellationTokenSource? cancelarEspera;

    /// <summary>
    /// Si la última espera se cortó porque el usuario lo pidió, y no por un fallo.
    ///
    /// Es un estado aparte y no un <c>ErrorConsulta</c> porque lo que pasó no fue
    /// un error: se intentó, tardó demasiado y el usuario prefirió no seguir
    /// esperando. Pintarlo con la caja roja sería gritarle por una decisión suya.
    /// </summary>
    private bool esperaCancelada;

    /// <summary>
    /// Número de la carga en curso.
    ///
    /// La red de seguridad lo sube cuando fuerza la pantalla a bajar. Así, una
    /// consulta que llega tarde ve que su número ya no es el vigente y no escribe
    /// su respuesta encima de lo que haya ahora, que pertenece a otra carga.
    /// </summary>
    private int generacionCarga;

    /// <summary>
    /// Que hay un refresco pendiente que hacer cuando termine la carga en vuelo.
    ///
    /// Existe por un motivo concreto: al volver de la pestaña, el rescate cancela
    /// la consulta que estaba en marcha y enseguida hay que pedir los datos otra
    /// vez. Pero pedirlos desde ahí se perdía en silencio, porque
    /// <see cref="CargarAsync"/> se sale de inmediato al ver <c>Cargando</c> en
    /// true, y en ese instante todavía lo está: lo baja el <c>finally</c> de la
    /// consulta que se acaba de cancelar, y ese <c>finally</c> aún no ha corrido.
    ///
    /// Así que el refresco se pide desde el <c>finally</c>, que es el único sitio
    /// donde <c>Cargando</c> ya es falso. El usuario volvía a ver los mismos datos
    /// viejos, sin error y sin aviso de nada.
    /// </summary>
    private bool refrescoPendiente;

    /// <summary>
    /// Minutos que se anunciarán cuando se atienda el refresco pendiente.
    ///
    /// Va aparte del refresco porque el aviso solo se puede poner si la consulta
    /// salió bien y consultó de verdad, y eso se sabe al terminar, no al pedirlo.
    /// </summary>
    private int? minutosParaAviso;

    /// <summary>
    /// Si la espera se cortó por plazo y no porque el usuario lo pidiera.
    ///
    /// Los dos caminos llegan por la misma puerta, <c>OperationCanceledException</c>,
    /// y necesitan mensajes distintos. Si el usuario no pulsó nada, decir "dejaste
    /// de esperar" es mentira, y además taparía que lo que falló fue el navegador
    /// parando la página.
    /// </summary>
    private bool cortePorPlazo;

    // --- Aviso de vuelta a la pestaña ---
    //
    // Minutos que los datos pueden envejecer antes de considerar que hay que
    // volver a traerlos. Un minuto, no cero: mirar un correo y volver NO es una
    // consulta, y llenarle la pantalla al usuario con un aviso por eso sería
    // hacer que el aviso se vuelva ignorable.
    private static readonly TimeSpan AntiguedadParaAvisar = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Cuándo se consultó por última vez, en reloj de pared. Ojo: es un
    /// <see cref="DateTimeOffset"/> y NO un temporizador, porque el fallo que
    /// motiva esto viene justamente de los temporizadores: si la pestaña se
    /// congela, el temporizador se detiene con ella, y al volver hay que poder
    /// preguntarse cuánto pasó de verdad. Un reloj sigue corriendo aunque el
    /// navegador congele la pestaña.
    /// </summary>
    private DateTimeOffset? ultimaConsultaOk;

    /// <summary>
    /// Cuándo se salió de la pestaña, para poder decir "estuvo 12 minutos".
    /// Es distinto de <see cref="ultimaConsultaOk"/> a propósito: una cosa es
    /// cuándo se consultó y otra cuánto estuvo fuera. Con solo una de las dos el
    /// mensaje mentiría en la mitad de los casos.
    /// </summary>
    private DateTimeOffset? seFueDeLaPestana;

    /// <summary>
    /// Minutos que estuvo la pestaña en segundo plano, ya redondeados para el
    /// texto. Null cuando aún no se ha ido nunca, o cuando la caducidad se
    /// cumple en menos de un minuto, en cuyo caso el texto sería "0 minutos" y
    /// suena a error.
    /// </summary>
    private int? MinutosFuera => seFueDeLaPestana is null
        ? null
        : Math.Max(1, (int)Math.Round((DateTimeOffset.Now - seFueDeLaPestana.Value).TotalMinutes));

    // Detalle abierto
    private Licitacion? abierta { get; set; }
    private DetalleLicitacion? detalleAbierto { get; set; }
    private bool cargandoDetalle { get; set; }
    private bool detalleDesdeCache { get; set; }
    private string? errorDetalle { get; set; }

    // --- Textos de fecha que llegan hechos desde el servidor ---
    //
    // Aquí ya no se compone ninguna fecha. Antes este archivo tenía su propio
    // array de días de la semana, ordenado por lunes pero indexado por
    // DayOfWeek, que en .NET empieza por domingo: todas las fechas salían
    // corridas un día y un viernes se pintaba como "sábado". No lo detectaba
    // ningún test porque el cliente no tiene proyecto de pruebas.
    //
    // Los textos se escriben en TextosDeFecha, en el servidor, donde sí se
    // prueban. Aquí solo se pintan.

    /// Lo que se pinta junto al contador: "Del 23 de febrero al 28 de febrero".
    private string Periodo => Semana?.Periodo ?? "";

    /// <summary>
    /// Cuántos días se CONSULTARON de verdad. Distinto del total de la semana:
    /// si la semana aún no termina, no se han preguntado los días futuros, y
    /// decir "se consultaron 5" cuando se preguntaron 2 es mentir.
    ///
    /// El texto vive en FiltrosDeSemana, con su plural y su caso sin semana.
    /// </summary>
    private string TextoDiasConsultados =>
        FiltrosDeSemana.DiasConsultadosEnPalabras(Semana?.DiasConsultados, haySemana: Semana is not null);

    /// <summary>
    /// Días que no respondieron. Es un CONTEO, no la lista de fechas: el aviso
    /// que lo usa dice "(3 días). Lo que ves está completo, pero puede que
    /// falte algo de esos días", y ahí un "Sin respuesta: 2026-02-24,
    /// 2026-02-25" se leería como un error.
    /// </summary>
    private string DiasFallidosEnPalabras =>
        FiltrosDeSemana.DiasFallidosEnPalabras(Semana?.DiasFallidos);

    /// El encabezado de cada grupo: "viernes 27 de febrero".
    private static string EncabezadoDelGrupo(IGrouping<DateOnly, Licitacion> grupo) =>
        grupo.First().PublicadoTexto;

    private string NombreEmpresa => Estado?.Empresa?.Nombre ?? "…";
    /// <summary>
    /// ¿Estamos leyendo de la base de datos en vez de la API?
    ///
    /// <para>
    /// Viene del servidor y se lee de <see cref="Estado"/>, que a su vez viene de
    /// <c>/api/estado</c>. No se deduce de nada del cliente a propósito: si el
    /// servidor dice que la fuente es la base, el cliente lo cree, y si el
    /// cliente lo dedujera por su cuenta habría dos verdades y algún día
    /// discreparían.
    /// </para>
    ///
    /// <para>
    /// Antes de que llegue el estado es false. El servidor lo pide al arrancar,
    /// así que el primer pintado usa la forma de la API y luego se corrige sola.
    /// Invertirlo daría un parpadeo en el primer arranque, que es peor que
    /// un instante de la forma "equivocada".
    /// </para>
    /// </summary>
    private bool UsaBaseDeDatos => Estado?.UsaBaseDeDatos == true;

    /// <summary>El nombre del mes que se está mirando, con mayúscula inicial.</summary>
    /// <remarks>
    /// Sale del desplegable del servidor, que en el año en curso llega FILTRADO
    /// hasta el mes de hoy. Solo se usa para la insignia verde, y solo en modo
    /// base de datos: el título del panel de cero resultados lo trae el servidor
    /// entero en PeriodoDelMes.
    /// </remarks>
    private string NombreMesActual =>
        Estado?.MesesDisponibles is { Count: > 0 } meses && Mes - 1 < meses.Count
            ? meses[Mes - 1]
            : $"{Mes:00}";

    private int MinutosRefresco => Estado?.MinutosEntreRefrescos is > 0 ? Estado.MinutosEntreRefrescos : 5;

    /// <summary>
    /// occupied la pantalla sin poder mostrar un resultado todavía: o hay una
    /// consulta en vuelo, o aún no se sabe si la API está servible.
    /// </summary>
    private bool Ocupado => Cargando || (Estado is null && ErrorConsulta is null);

    /// <summary>

    /// <summary>
    /// ¿Lo que se está mirando es la semana en que estamos?
    ///
    /// Se compara contra lo que el servidor ya mandó al decir en qué semana
    /// está hoy, así que tampoco hace falta aquí aritmética de fechas.
    /// </summary>
    private bool EsSemanaActual =>
        Estado is not null
        && Anio == Estado.Anio
        && Mes == Estado.Mes
        && NumeroSemana == Estado.Semana;

    /// <summary>
    /// ¿Lo que se mira es el MES en que estamos?
    ///
    /// <para>
    /// Es el equivalente de <see cref="EsSemanaActual"/> para el modo base de
    /// datos, y lo decide el temporizador de refresco: solo programa si se está
    /// mirando el mes en curso.
    /// </para>
    ///
    /// <para>
    /// No se compara el número de semana porque en este modo no hay semana. Y no
    /// se reusa <see cref="EsSemanaActual"/> porque devolvería false siempre —el
    /// selector no está— y el refresco automático no se programaría nunca. Un
    /// fallo que no se ve: la pantalla simplemente nunca más se actualiza sola.
    /// </para>
    /// </summary>
    private bool EsMesActual =>
        Estado is not null
        && Anio == Estado.Anio
        && Mes == Estado.Mes;

    /// <summary>
    /// ¿El filtro apunta a algo distinto de lo que está en pantalla?
    ///
    /// En modo base de datos solo hay año y mes, así que se comparan esos dos.
    /// Sin este matiz, mover el desplegable de mes en modo base de datos daría
    /// un aviso de "cambio pendiente" con un desplegable de semana que no
    /// existe, y el usuario no tendría forma de pulsarlo.
    /// </summary>
    private bool HayPeriodoPendiente =>
        UsaBaseDeDatos
            ? Semana is not null && (Anio != Semana.Anio || Mes != Semana.Mes)
            : HaySemanaPendiente;

    // --- Rótulos del rango ---

    private DateOnly? Desde => Semana?.Desde is { } d ? DateOnly.Parse(d) : null;
    private DateOnly? Hasta => Semana?.Hasta is { } h ? DateOnly.Parse(h) : null;

    /// <summary>
    /// Número de la semana que se está MOSTRANDO, que no es el mismo dato que la
    /// semana ELEGIDA en el desplegable.
    ///
    /// Son dos estados distintos y confundirlos era un bug. El evento
    /// <c>CambiarSemana</c> mueve el selector al instante, sin consultar nada,
    /// así que la insignia —que leía el selector— se adelantaba sola: ponía
    /// "Semana 3" mientras el periodo y los resultados seguían siendo los de la
    /// semana 2. Tres etiquetas que deberían describir la misma semana
    /// discrepando, y la que más llama la atención era la equivocada.
    ///
    /// El rango y los resultados ya venían de la respuesta del servidor, que
    /// trae el número. Ahora la insignia sale de ahí también, y las tres cuentan
    /// lo mismo mientras no se pulse "Actualizar".
    /// </summary>
    private int SemanaMostrada => Semana?.Semana ?? NumeroSemana;

    /// <summary>
    /// La insignia verde: "Semana 3" en modo API, el nombre del mes en modo base
    /// de datos.
    /// </summary>
    /// <remarks>
    /// No dice "Semana 1" en modo base de datos aunque el campo valga 1. Sería
    /// verdad en el papel —ese es el número que se guarda— y mentira en la
    /// pantalla, porque no hay ninguna semana detrás de ese rótulo.
    /// </remarks>
    private string RangoCorto =>
        UsaBaseDeDatos ? NombreMesActual : $"Semana {SemanaMostrada}";

    /// <summary>
    /// ¿El desplegable apunta a algo distinto de lo que está en pantalla?
    ///
    /// La comparación vive en FiltrosDeSemana, no aquí. Estaba aquí y era un
    /// bug vivo: comparaba solo el número de semana, y como el número se
    /// repite en todos los periodos, cambiar de mes o de año no disparaba el
    /// aviso. Ningún test lo podía ver porque el code-behind de un componente
    /// Blazor no se prueba.
    /// </summary>
    private bool HaySemanaPendiente =>
        Semana is not null
        && FiltrosDeSemana.HayCambioPendiente(
            Anio, Mes, NumeroSemana,
            Semana.Anio, Semana.Mes, Semana.Semana);

    /// <summary>
    /// El botón de la barra late cuando hay algo pendiente de traer.
    ///
    /// El aviso de abajo ya explica la situación y trae su propio botón, pero el
    /// de la barra es el que está siempre a la vista. Que se mueva es lo que
    /// convierte "he cambiado el desplegable y no pasa nada" en un clic.
    /// </summary>
    private string ClaseBotonActualizar =>
        HayPeriodoPendiente ? "boton-actualizar-pendiente" : "";

    /// <summary>
    /// Título del aviso. Va en una propiedad y no en el atributo porque Razor no
    /// admite contenido mixto dentro de un atributo de componente: se calcula
    /// aquí y se pinta como <c>Titulo="@Texto"</c>.
    /// </summary>
    /// <remarks>
    /// En modo base de datos NO lleva el número de semana. Ahí no hay semana, y
    /// "Estás viendo la semana 1 en octubre de 2026" señalaría a un período que
    /// no existe: el selector de semana ni siquiera está en pantalla. Pone solo
    /// el mes, que es lo que hay.
    /// </remarks>
    private string TituloPeriodoPendiente =>
        UsaBaseDeDatos
            ? $"Estás viendo {MesEnPalabras(Semana!.Anio, Semana.Mes)}"
            : $"Estás viendo {SemanaEnPalabras(Semana!.Anio, Semana.Mes, SemanaMostrada)}";

    /// <summary>
    /// El período ELEGIDO en el desplegable, dicho en palabras.
    /// </summary>
    /// <remarks>
    /// Es el mes en modo base de datos y la semana en modo API, por el mismo
    /// motivo que el título: hay que nombrar lo que el usuario tiene delante del
    /// ratón, y en modo base de datos eso es un mes.
    ///
    /// Y va aparte del título porque son dos momentos distintos: este es lo que
    /// se ha elegido y todavía NO está en pantalla, así que no se puede leer de
    /// la respuesta del servidor. Sale de los desplegables.
    /// </remarks>
    private string PeriodoElegidoEnPalabras =>
        UsaBaseDeDatos
            ? MesEnPalabras(Anio, Mes)
            : SemanaEnPalabras(Anio, Mes, NumeroSemana);

    /// <summary>
    /// El texto del panel de "no se pudo", en el idioma del modo en el que se
    /// está mirando.
    /// </summary>
    /// <remarks>
    /// Va a <see cref="FiltrosDeSemana.TextoDelPanelDeFallo"/> y no se compone
    /// aquí porque este texto estuvo hablando de SEMANAS en modo base de datos,
    /// donde no hay semanas, y eso no lo podía ver ningún test desde aquí: el
    /// componente es Blazor y sin bUnit no se prueba.
    ///
    /// Los meses salen de <c>Anio</c> y <c>Mes</c>, que son los desplegables, y
    /// no de <c>Semana</c>: cuando este panel sale no hay respuesta del servidor,
    /// y <c>Semana</c> es justamente lo que la respuesta trae.
    /// </remarks>
    private FiltrosDeSemana.PanelDeFallo TextoFallo =>
        FiltrosDeSemana.TextoDelPanelDeFallo(UsaBaseDeDatos, Anio, Mes, Estado?.MesesDisponibles);

    /// <summary>
    /// "la semana 4 en febrero de 2026", o "la Semana 4 en Febrero de 2026" con
    /// <paramref name="capitalizado"/>.
    ///
    /// Con el número solo no basta. Al cambiar el desplegable se puede pasar
    /// de una semana a otra de otro mes, y "la semana 1" frente a "la semana 5"
    /// no dice nada: el mismo número de semana existe en todos los meses. Con
    /// mes y año, sí.
    ///
    /// El nombre del mes sale de la lista que manda el servidor, no de una
    /// tabla propia del cliente: esa tabla ya estuvo aquí una vez, y mal.
    ///
    /// El guardia mira que el mes quepa en la lista, y no que la lista tenga
    /// doce. Antes daba igual porque la lista eran siempre los doce meses, pero
    /// ahora viene FILTERADA: en el año en curso solo llega hasta el mes de hoy.
    /// Con un "Count >= 12" el nombre se caía al número y salía "la semana 4 de
    /// 2 de 2026".
    ///
    /// El texto entero vive en FiltrosDeSemana y la lista de meses va como
    /// argumento. Aquí solo se lee del estado y se le pasa, para que el método no
    /// pueda quedarse con una copia propia.
    /// </summary>
    private string SemanaEnPalabras(int anio, int mes, int semana, bool capitalizado = false) =>
        FiltrosDeSemana.EnPalabras(anio, mes, semana, Estado?.MesesDisponibles, capitalizado);

    /// <summary>"septiembre de 2026". Va por el mismo camino que la semana.</summary>
    private string MesEnPalabras(int anio, int mes, bool capitalizado = false) =>
        FiltrosDeSemana.MesEnPalabras(anio, mes, Estado?.MesesDisponibles, capitalizado);

    /// <summary>

    /// <summary>
    /// Se engancha al evento de visibilidad del navegador.
    ///
    /// Es la pieza que cierra el agujero del refresco congelado. Antes, con la
    /// pestaña en segundo plano, el temporizador de cinco minutos se detenía
    /// junto con la pestaña: al volver, la página seguía mostrando datos viejos
    /// y sin ninguna señal de que fueran viejos, y el usuario no tenía forma
    /// de saberlo.
    ///
    /// Se registra con AddOnAfterRenderAsync y no en el constructor porque en el
    /// primero Blazor todavía no hay un DOM al que engancharse.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        // Va por el interop porque leer document.hidden es cosa de JavaScript.
        // El módulo tiene nombre fijo en wwwroot/js a propósito: si estuviera
        // dentro del proyecto, Blazor lo empaquetaría con un nombre que cambia
        // en cada compilación y este import se quedaría apuntando a un archivo
        // que ya no existe tras el siguiente despliegue.
        interopVisibilidad = await JS.InvokeAsync<IJSObjectReference>("import", "./js/visibilidad.js");

        // La referencia al objeto de .NET se guarda para poder soltarla después.
        // Sin guardarla, Dispose no podría deshacer la suscripción.
        refVisibilidad = DotNetObjectReference.Create(this);

        await interopVisibilidad.InvokeVoidAsync("observar", refVisibilidad, IdPantalla);

        StateHasChanged();
    }

    /// <summary>
    /// Identificador de esta pantalla para el módulo de JavaScript. Es una
    /// cadena fija porque solo hay una: no se necesita un GUID por pantalla.
    /// </summary>
    private const string IdPantalla = "home";

    private IJSObjectReference? interopVisibilidad;
    private DotNetObjectReference<Home>? refVisibilidad;

    /// <summary>
    /// Lo llama el navegador cuando la pestaña se ve o se esconde.
    ///
    /// Se invoca desde JavaScript, así que puede venir de un hilo que no es el
    /// de la interfaz: todo lo que toca el estado pasa por InvokeAsync, que es
    /// lo único que garantiza que Blazor pueda repintar.
    /// </summary>
    [JSInvokable]
    public async Task AlCambiarVisibilidad()
    {
        var oculto = await interopVisibilidad!.InvokeAsync<bool>("oculto");

        // Se pregunta el estado ANTES de InvokeAsync, no dentro: leerlo es una
        // llamada asíncrona a JavaScript, y si se hiciera dentro, otra cosa
        // podría haber cambiado la visibilidad mientras se esperaba.
        if (oculto)
        {
            await InvokeAsync(() =>
            {
                // Guardar el instante de la salida es lo que permite después
                // decir "estuvo 12 minutos" en vez de un texto sin número.
                seFueDeLaPestana = DateTimeOffset.Now;
                return Task.CompletedTask;
            });

            return;
        }

        await InvokeAsync(async () =>
        {
            // PRIMERO, una consulta que se quedó colgada.
            //
            // Cuando el navegador para la página se paran a la vez el reloj de
            // segundos y el corte de espera del cliente, porque los dos son
            // temporizadores. Al volver, lo primero que se hace es mirar si había
            // una consulta en vuelo y si ya rebasó el plazo: si lo rebasó, se
            // cierra y se dice, en vez de dejar un número clavado para siempre.
            //
            // Va antes que lo demás porque es lo único que hay que hacer aunque
            // los datos estén fresquísimos: si la consulta está colgada no hay
            // datos frescos que enseñar.
            //
            // No se espera: el rescate dispara la cancelación y sigue. La
            // consulta se desenrolla sola y avisa por su finally.
            RescatarConsultaColgada();

            var minutos = MinutosFuera;

            // Sin este caso, volver a la pestaña después de dos segundos
            // dispararía una consulta por mirar un correo.
            if (minutos is null) return;

            var caducada = ultimaConsultaOk is null ||
                DateTimeOffset.Now - ultimaConsultaOk.Value >= AntiguedadParaAvisar;

            if (!caducada)
            {
                // Volvió pronto y los datos son del momento: no hay nada que
                // decir ni que traer.
                seFueDeLaPestana = null;
                return;
            }

            minutosParaAviso = minutos.Value;
            refrescoPendiente = true;

            // Si hay una consulta en vuelo, el rescate ya la canceló y va a
            // desenrollarse: de ella se ocupa su finally, que es el único sitio
            // donde Cargando ya es falso. Pedirlo aquí se perdía en silencio,
            // porque CargarAsync se sale al ver Cargando en true y en ese
            // instante todavía lo está.
            if (Cargando) return;

            await AtenderRefrescoPospuestoAsync();
        });
    }

    /// <summary>
    /// Cierra una consulta que se quedó colgada mientras la página estaba parada.
    ///
    /// <para>
    /// El caso que resuelve: el contador se queda clavado en un número y no avanza.
    /// Pasa porque el navegador paró la página, y entonces se paran a la vez el
    /// reloj de segundos y el corte de espera del cliente, que son los dos
    /// temporizadores. Al volver, nada de eso reanuda solo.
    /// </para>
    ///
    /// <para>
    /// Aquí no se puede hacer nada mientras la página está parada: sin
    /// temporizadores no hay forma de detectarlo. Lo que sí llega cuando la
    /// pestaña vuelve a verse es el evento de visibilidad, y es lo que se usa.
    /// </para>
    ///
    /// <para>
    /// El plazo que se mira es el del cliente, que es el más agarrable: si la
    /// consulta ya lo rebasó, el navegador tiene la culpa y no la API, y decirlo
    /// así es lo único honesto.
    /// </para>
    /// </summary>
    private void RescatarConsultaColgada()
    {
        if (!Cargando || inicioEspera is not DateTimeOffset inicio) return;

        var transcurrido = DateTimeOffset.Now - inicio;

        if (transcurrido < TimeSpan.FromSeconds(MercadoPublicoApi.SegundosEspera)) return;

        // El contador se queda en lo que iba, y se anota que el corte fue por
        // plazo y no porque el usuario lo pidiera: los dos mensajes son distintos
        // y decir "dejaste de esperar" sería mentira.
        SegundosEsperando = (int)transcurrido.TotalSeconds;

        cortePorPlazo = true;

        var espera = cancelarEspera;

        if (espera is not null && !espera.IsCancellationRequested)
        {
            // SIN await, y esto es lo importante. Este era el punto exacto donde
            // el manejador de visibilidad se quedaba atascado: al esperar aquí, si
            // el navegador tiene la página congelada, la continuación no llega
            // nunca y la llamada entera se queda colgada sin llegar nunca al
            // refresco que iba justo detrás.
            //
            // El disparo no necesita esperar a nadie. La consulta se desenrolla
            // sola, avisa por su finally, y ese finally es el que recoge el
            // refresco pendiente.
            VigilarAsync(espera.CancelAsync());
        }
    }

    /// <summary>
    /// Hace el refresco que se pidió al volver de la pestaña y avisa de ello.
    ///
    /// <para>
    /// El aviso va DESPUÉS de la consulta y solo si salió bien. Ponerlo antes
    /// obligaría a una de dos cosas malas: o decir "actualizado" cuando la
    /// consulta falla, o tener que desvanecer un aviso que ya se vio.
    /// </para>
    ///
    /// <para>
    /// Y si la consulta no aporta nada nuevo porque venía de la caché, no se
    /// dice nada: el aviso habla de que se consultó, y si no se consultó,
    /// mentimos.
    /// </para>
    ///
    /// <para>
    /// Igual con el fallo: si la consulta falla, los datos que quedan son los de
    /// antes y la caja de error ya lo dice con todas las letras. Añadir un aviso
    /// de "actualizado" ahí sería mentir dos veces, porque el usuario leería que
    /// los datos son nuevos cuando son justamente los viejos.
    /// </para>
    /// </summary>
    private async Task AtenderRefrescoPospuestoAsync()
    {
        if (!refrescoPendiente) return;

        // Se apaga antes de consultar, no después. Si al consultar salta una
        // excepción, quedaría encendido para siempre y cada carga posterior
        // volvería a dispararlo.
        refrescoPendiente = false;

        var minutos = minutosParaAviso;
        minutosParaAviso = null;

        // Sin enSegundoPlano: el usuario volvió a mirar la pantalla, así que
        // esto no es un refresco de fondo y el servidor no debe conformarse con
        // menos reintentos.
        await CargarAsync(refrescar: false);

        if (ErrorConsulta is not null)
        {
            // Falló. Los datos en pantalla son los de antes y el error ya lo
            // dice con su caja: un aviso de "actualizado" aquí sería falso.
            seFueDeLaPestana = null;
            return;
        }

        var consultadaAhora = consultoDeVerdad;
        seFueDeLaPestana = null;

        if (consultadaAhora && minutos is not null)
        {
            minutosAvisoVuelta = minutos;
            StateHasChanged();
        }
    }

    /// <summary>Minutos que se anuncian en el aviso, o null si no toca.</summary>
    private int? minutosAvisoVuelta;

    /// <summary>
    /// Textos del aviso de vuelta. Están en propiedades y no en el marcado
    /// porque el plural tiene dos formas y escribirlas con una condición dentro
    /// del HTML se lee peor que leerlas aquí.
    /// </summary>
    private string TextoAvisoVuelta => "Datos actualizados al volver";

    private string TextoMinutosEstuvo => minutosAvisoVuelta == 1
        ? "1 minuto"
        : $"{minutosAvisoVuelta} minutos";

    /// <summary>
    /// Cierra el aviso a petición del usuario.
    ///
    /// No basta con poner <c>minutosAvisoVuelta</c> a null: el componente Aviso
    /// ya se cerró por su cuenta, pero si el usuario vuelve a mirar la pestaña
    /// más tarde y los datos están viejos otra vez, el aviso reaparece. Eso es
    /// lo correcto, porque para entonces habrá datos nuevos de verdad.
    /// </summary>
    private void CerrarAvisoVuelta() => minutosAvisoVuelta = null;

    /// <summary>Cierra el aviso de espera abandonada.</summary>
    private void CerrarAvisoEspera() => esperaCancelada = false;

    /// <summary>
    /// Si la última consulta fue de verdad a Mercado Público y no una lectura de
    /// caché. El aviso de vuelta dice "se consultó ahora", y eso solo es cierto
    /// en el primer caso: si vino de la caché, el usuario no recibió nada
    /// nuevo y anunciarlo sería hacer ruido para no decir nada.
    /// </summary>
    private bool consultoDeVerdad;

    protected override async Task OnInitializedAsync()
    {
        var (estado, error) = await Api.GetEstadoAsync();
        Estado = estado;

        if (estado is null)
        {
            ErrorConsulta = error ?? "No se pudo conectar con el servidor.";
            Cargando = false;
            StateHasChanged();
            return;
        }

        // Arranca en la semana en curso, que es lo que se mira casi siempre.
        Anio = estado.Anio;
        Mes = estado.Mes;
        NumeroSemana = estado.Semana;

        StateHasChanged();

        // Entra YA con el mes en curso cargado, y en los dos modos. La
        // decisión de qué pedir la toma CargarAsync de la fuente, así que aquí no
        // hay nada condicional: en modo API trae la semana en curso y en modo
        // base de datos el mes en curso entero, que es lo que se mira casi
        // siempre. La pantalla no espera a que se pulse "Actualizar" en ninguno
        // de los dos casos.
        await CargarAsync();
        ProgramarRefresco();
    }

    // ------------------------------------------------------------------
    // Filtros
    // ------------------------------------------------------------------

    private async Task CambiarAnio(ChangeEventArgs e)
    {
        // Se valida antes de guardar: un año imposible dejaría la pantalla
        // en un estado que no se puede ni pintar.
        if (int.TryParse(e.Value?.ToString(), out var n) && n is >= 1 and <= 9999 && n != Anio)
        {
            Anio = n;
            await RecargarSemanasAsync();

            // Rearma o cancela el refresco. Sin esta línea el temporizador seguía
            // con la cuenta atrás de antes y se disparaba igual, consultando la
            // semana del desplegable por la espalda.
            ProgramarRefresco();
        }
    }

    private async Task CambiarMes(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var n) && n is >= 1 and <= 12 && n != Mes)
        {
            Mes = n;
            await RecargarSemanasAsync();

            // Como en CambiarAnio: el temporizador se decide con los valores del
            // desplegable, así que hay que volver a decidirlo aquí.
            ProgramarRefresco();
        }
    }

    /// <summary>
    /// El desplegable de semanas cambia AL INSTANTE y no consulta nada. Los
    /// datos de la semana elegida llegan al pulsar "Actualizar".
    ///
    /// Es deliberado: traer una semana son cinco peticiones a la API y el usuario
    /// puede estar simplemente mirando el desplegable. Por eso aquí no se toca
    /// <c>Semana</c>, que es lo que hay en pantalla.
    ///
    /// Lo que sí rompía era leer <c>NumeroSemana</c> para pintar la insignia
    /// verde: como este método cambia ese campo al momento, la insignia se
    /// adelantaba a los resultados y las tres etiquetas de la cabecera —
    /// insignia, periodo y lista— contaban semanas distintas. Ahora la insignia
    /// lee la respuesta del servidor, y mientras no se pulse "Actualizar" las
    /// tres dicen lo mismo.
    /// </summary>
    private void CambiarSemana(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var n) && n >= 1 && n != NumeroSemana)
        {
            NumeroSemana = n;

            // ESTA LÍNEA FALTABA, y es la causa de lo que pasó. El comentario de
            // arriba decía que el desplegable cambia al instante sin consultar
            // nada, y es cierto: no se toca <c>Semana</c>. Pero el temporizador
            // del refresco automático consultaba el desplegable, así que sin
            // rearmarlo seguía pendiente con la cuenta atrás del arranque.
            //
            // Al llegar a cero, <c>CargarAsync</c> consultaba la semana ELEGIDA,
            // que no era la que estaba en pantalla ni la que el usuario había
            // pedido todavía. Con una semana pasada y sin caché, eso son hasta
            // cinco días en serie: el contador de segundos subiendo y sin salida,
            // porque el botón queda deshabilitado mientras consulta.
            ProgramarRefresco();
        }
    }

    /// <summary>
    /// Vuelve a pedir al servidor los rangos del mes que se acaba de elegir.
    ///
    /// Hace falta porque los rangos vienen del servidor: al cambiar de mes, los
    /// que tenemos son los del anterior, y un mes de 30 días no tiene las
    /// mismas semanas que uno de 31. Es una llamada local que no gasta cupo del
    /// ticket, y es el precio de no tener la regla de lunes a domingo escrita en
    /// los dos lados.
    /// </summary>
    private async Task RecargarSemanasAsync()
    {
        var (estado, error) = await Api.GetEstadoAsync(Anio, Mes);

        if (estado is null)
        {
            ErrorConsulta = error ?? "No se pudo actualizar las semanas del mes.";
            return;
        }

        Estado = estado;
        AjustarSemanaAlMes();
    }
    /// <summary>
    /// Al cambiar de mes, la semana elegida puede no existir: octubre tiene 5 y
    /// septiembre 5, pero no todos los meses. Se deja la última, que siempre
    /// existe, en vez de dejar el número colgando sin opción a la que
    /// pertenecer.
    /// </summary>
    private void AjustarSemanaAlMes()
    {
        var maximo = Estado?.Semanas.Count ?? 1;
        if (NumeroSemana > maximo) NumeroSemana = maximo;
        if (NumeroSemana < 1) NumeroSemana = 1;
    }

    /// <summary>Botón "Actualizar".</summary>
    private Task ActualizarAsync() => CargarAsync(refrescar: true);

    /// <summary>
    /// Deja de esperar la consulta en curso, a petición del usuario.
    ///
    /// NO cancela nada en el servidor. La petición ya está hecha y allí sigue su
    /// curso hasta el final, guardando el día en la caché; esto solo corta la
    /// espera del navegador. Es el mismo criterio de siempre: el trabajo del
    /// servidor no se tira por no aguantar la espera, y por eso el siguiente
    /// intento sale rápido.
    ///
    /// Sin esto, la pantalla de consulta era un callejón sin salida: el botón de
    /// actualizar queda deshabilitado mientras dura, así que si la consulta no
    /// terminaba no había nada que pulsar. Solo quedaba recargar la página.
    /// </summary>
    private async Task DejarDeEsperarAsync()
    {
        var espera = cancelarEspera;

        if (espera is null || espera.IsCancellationRequested) return;

        await espera.CancelAsync();
    }

    // ------------------------------------------------------------------
    // Consulta
    // ------------------------------------------------------------------

    /// <summary>
    /// Consulta la semana seleccionada.
    ///
    /// No lleva CancellationToken a propósito: si se cancela a mitad, el
    /// servidor sigue insistiendo con sus reintentos y el trabajo se tira
    /// (junto con el cupo). El botón queda bloqueado mientras tanto, así que
    /// no se solapan peticiones.
    /// </summary>
    /// <param name="refrescar">Vaciar la caché del día de hoy, para el botón
    /// "Actualizar" de verdad.</param>
    /// <param name="enSegundoPlano">
    /// Que nadie está mirando: solo el refresco automático. El servidor se
    /// conforma con menos reintentos cuando se le dice que sí, porque retener la
    /// semana cuatro minutos invisible le cuesta dinero a quien sí la está
    /// mirando.
    /// </param>
    private async Task CargarAsync(bool refrescar = false, bool enSegundoPlano = false)
    {
        // El guard salta en la recarga del temporizador mientras aún no ha
        // terminado la anterior.
        if (Cargando) return;

        var generacion = ++generacionCarga;

        Cargando = true;
        ErrorConsulta = null;
        esperaCancelada = false;
        cortePorPlazo = false;

        // Se limpia ANTES de consultar, no después. Antes conservaba el valor
        // del intento anterior, y si este fallaba, el aviso de vuelta diciendo
        // "se consultó ahora" con un valor viejo: el servidor caído, la pantalla
        // diciendo que se acababa de consultar. Es el mismo error del que huyen
        // este proyecto y la casa, y salió justo al probar el camino de fallo.
        consultoDeVerdad = false;

        IniciarReloj();

        // El token va aquí y solo sirve para que el usuario pueda dejar de
        // esperar. No hay tope de tiempo en este lado: ese lo pone el
        // HttpClient, y son dos cosas distintas con dos mensajes distintos.
        using var espera = new CancellationTokenSource();
        cancelarEspera = espera;

        try
        {
            // Y con el reloj, la red de seguridad, que es lo único que
            // garantiza que la capa de espera se quite.
            //
            // Va DENTRO del try a propósito. Si fallara aquí fuera, el finally
            // no se ejecutaría, Cargando se quedaría en true y, sin red de
            // seguridad armada, la pantalla se quedaría clavada justo del mismo
            // modo que se quería arreglar. Dentro, el catch la convierte en un
            // error visible y el finally baja la capa.
            ArmarVigilante();

            // LA FUENTE DECIDE LA UNIDAD. En modo API se pide una semana y se le
            // pasa el token de cancelación; en modo base de datos se pide un mes
            // entero y no se le pasa token.
            //
            // Y el resultado se guarda en la MISMA variable, Semana, en los dos
            // casos. No hay dos tipos en pantalla ni dos caminos de pintado: el
            // modelo es el mismo con la unidad cambiada. Si se guardara aparte,
            // habría que duplicar el dibujado y cualquier cambio en las tarjetas
            // tendría que hacerse dos veces, y un día se olvidaría una.
            // Nullable a propósito, y es lo que quita el null! de más abajo y el
            // aviso del compilador. Las dos ramas pueden no devolver nada, y el
            // código que sigue ya lo comprueba con "if (resultado is null)". Declarar
            // esto como no nulable obligaba a mentir por los dos lados: aquí con un
            // null!, y en la otra rama el compilador avisando de que se le está
            // asignando algo que puede no estar.
            SemanaLicitaciones? resultado;
            string? error;

            if (UsaBaseDeDatos)
            {
                var (mes, errorMes) = await Api.GetMesAsync(Anio, Mes);

                resultado = mes is null
                    ? null
                    : new SemanaLicitaciones
                    {
                        // La semana se pone a 1 porque el selector no está, pero
                        // los textos que lo usan solo se pintan en modo API. Se
                        // deja un 1 y no un 0 para que ninguna cuenta dé "semana
                        // 0" si algún día se usa sin querer.
                        Semana = 1,
                        Items = mes.Items,
                        Total = mes.Total,
                        Anio = mes.Anio,
                        Mes = mes.Mes,
                        Desde = $"{mes.Anio}-{mes.Mes:00}-01",
                        DiasHabiles = mes.DiasHabiles,
                        DiasConsultados = mes.DiasConsultados,
                        DiasPendientes = mes.DiasPendientes,
                        DiasFallidos = mes.DiasFallidos,
                        DiasSinRespuesta = mes.DiasSinRespuesta,
                        PeriodoDelMes = mes.PeriodoDelMes,
                        DesdeCache = mes.DesdeCache,
                        Periodo = mes.Periodo,
                        Consultado = mes.Consultado,
                    };

                error = errorMes;
            }
            else
            {
                (resultado, error) = await Api.GetSemanaAsync(
                    Anio, Mes, NumeroSemana, refrescar, espera.Token, enSegundoPlano);
            }

            // Si la red de seguridad ya bajó esta carga, su respuesta llega
            // tarde. No se escribe nada: la pantalla está en otro estado ahora
            // y esta carga ya no es la vigente.
            if (generacion != generacionCarga) return;

            if (error is not null)
            {
                ErrorConsulta = error;
                return;
            }

            // Sin datos y sin error es una combinación que no debería existir, y
            // antes de comprobarla, una semana nula se pintaba como una
            // pantalla vacía. Para el usuario eso es indistinguible de "esta
            // semana no hay nada", que es justo lo que este proyecto no dice
            // nunca sin estar seguro.
            if (resultado is null)
            {
                ErrorConsulta = "El servidor no devolvió la semana.";
                return;
            }

            Semana = resultado;

            // Solo cuenta como fresco si vino de verdad. La marca se pone
            // después de comprobar que no hay error, no antes: si la consulta
            // falla, los datos que quedan en pantalla son los de antes, y
            // decir "se consultó ahora" sobre una pantalla con datos viejos
            // sería justo la mentira que este proyecto no quiere contar.
            // Solo si la consulta salió bien: si falló, los datos en pantalla son los de
            // antes y marcarlos como frescos sería mentir. Va aquí y no en el
            // catch a propósito.
            ultimaConsultaOk = DateTimeOffset.Now;

            // Y otra cosa distinta: si el servidor tuvo que ir a Mercado Público
            // o si solo leyó su caché. El aviso de vuelta dice "se consultó
            // ahora", así que solo puede mostrarse en el primer caso. Lo decide
            // el servidor, que es quien sabe si consultó de verdad.
            consultoDeVerdad = resultado.DesdeCache is not true;
        }
        catch (OperationCanceledException)
        {
            // Lo mismo que arriba: una carga que ya no es la vigente no escribe
            // su mensaje sobre el estado que tiene ahora la pantalla.
            if (generacion != generacionCarga) return;

            if (cortePorPlazo)
            {
                // Cortada porque la página estuvo parada más rato del plazo. Se dice
                // lo que pasó, que es lo único que sirve: no es que Mercado Público
                // no respondió, es que el navegador dejó de responder mientras
                // le esperábamos.
                ErrorConsulta =
                    "La página estuvo en segundo plano más rato del previsto y el navegador " +
                    "dejó de responder. La consulta se cerró al volver. Puede que Mercado Público " +
                    "la siga terminar: inténtalo otra vez en un momento.";
            }
            else
            {
                // Solo entra por aquí si el usuario pulsó "Dejar de esperar". Un corte
                // por tiempo NO llega por esta puerta: el cliente lo traduce a error
                // antes, porque para la pantalla son dos cosas distintas.
                esperaCancelada = true;
            }

            // NO se pone ErrorConsulta en el caso del usuario. Cancelar no es un
            // fallo, y la caja roja es para fallos: si el usuario decide dejar de
            // esperar, gritarle en rojo sería gritarle por algo que hizo a propósito.
            //
            // Se deja en pantalla lo que hubiera. Si no había nada, la zona de
            // resultados se queda en blanco, que es peor que un aviso, y por eso
            // el marcado tiene un caso propio para eso, que no dice que no haya
            // nada porque no se comprobó.
        }
        catch (Exception ex)
        {
            if (generacion != generacionCarga) return;

            // Cinturón y tirantes. GetSemanaAsync devuelve el error en la tupla y
            // no debería lanzar, pero si algo se escapa esta línea es la que
            // impide el congelamiento: sin ella, el finally de abajo apagaba el
            // reloj y ponía Cargando a false, pero NADIE repintaba, así que la
            // pantalla se quedaba con el contador clavado en el último número
            // pintado y sin un solo error a la vista.
            //
            // Que además se escriba en la consola del navegador es lo que hace
            // falta para que exista un rastro cuando esto pasa en producción.
            Console.Error.WriteLine($"La carga de la semana falló: {ex}");
            ErrorConsulta = "No se pudo cargar la semana.";
        }
        finally
        {
            // Una carga que ya no es la vigente no toca NADA de aquí. Todo lo de
            // este bloque, el token, el reloj, la vigilancia y el refresco,
            // pertenecen a la carga que la sustituyó, y pisarlo dejaría sin
            // reloj, sin red de seguridad o sin refresco a una carga viva.
            //
            // Va con if y no con return porque en un finally no se puede salir
            // del bloque.
            if (generacion == generacionCarga)
            {
                cancelarEspera = null;
                Cargando = false;
                PararReloj();
                PararVigilante();

                // Rearma el refresco automático al terminar CUALQUIER carga, no solo
                // al arrancar. Faltaba este rearme y era el mismo fallo que se corrigió
                // esta mañana en los filtros, por el otro lado: al cambiar el desplegable
                // se cancelaba el temporizador, pero al traer esa semana a pantalla
                // con "Actualizar" NO se volvía a programar. Resultado: pulsar
                // "Actualizar" dejaba la página sin refresco automático hasta
                // recargarla.
                //
                // Va en el finally y no en el try porque también tiene que rearme
                // cuando la carga falla: tras un error la pantalla sigue mostrando la
                // semana en curso, y esa es justo la que debe seguir refrescando sola.
                ProgramarRefresco();

                // Y aquí, y no antes, se atiende el refresco que se pidió al volver
                // de la pestaña. Este es el único punto donde Cargando ya es falso,
                // que es la condición que hace falta para que CargarAsync no se
                // salga por su guardia sin hacer nada.
                await AtenderRefrescoPospuestoAsync();
            }
        }
    }

    /// <summary>Arranca el contador de segundos de espera.</summary>
    private void IniciarReloj()
    {
        SegundosEsperando = 0;
        inicioEspera = DateTimeOffset.Now;

        reloj?.Dispose();
        reloj = new System.Timers.Timer(1000) { AutoReset = true };

        reloj.Elapsed += (_, _) =>
        {
            try
            {
                // Antes era: reloj.Elapsed += (_, _) => InvokeAsync(...)
                // devolviendo el valor y SOLTÁNDOLO. Así, si algo fallaba dentro,
                // la excepción se quedaba en una tarea que nadie leía nunca, y el
                // contador se paraba en seco sin error en ninguna parte: la
                // pantalla congelada sin explicación.
                VigilarAsync(InvokeAsync(() =>
                {
                    // SE MIDE EL TIEMPO, NO SE CUENTA LOS TICKS. Antes era
                    // SegundosEsperando++, o sea un tick por segundo. Si el
                    // navegador para la página, los ticks dejan de llegar y el
                    // número se queda donde estaba: se vio marcando 10 s con la
                    // consulta parada desde hacía minutos, o sea mintiendo sobre
                    // lo único que el contador tenía que decir.
                    //
                    // Ahora se lee el reloj. El temporizador sigue estando para
                    // forzar el repintado, que es lo que Blazor no hace solo, pero
                    // el número sale de la hora real, así que ni se queda corto
                    // ni se salta.
                    SegundosEsperando = (int)(DateTimeOffset.Now - inicioEspera.Value).TotalSeconds;

                    // Repintar a mano: al no haber ningún await en curso, Blazor solo
                    // repintaría cuando terminara la consulta, y el contador se
                    // quedaría en 0 durante los 30 s.
                    StateHasChanged();
                }));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"El contador de segundos no pudo arrancar: {ex}");
            }
        };

        reloj.Start();
    }

    /// <summary>
    /// Vigila una tarea en vez de soltarla.
    ///
    /// Una tarea sin mirar que falla no rompe nada y no avisa de nada: la excepción
    /// se pierde. En un temporizador eso es peor que un fallo visible, porque la
    /// pantalla sigue ahí, con el número que alcanzó a pintarse, dando la impresión
    /// de que la aplicación está pensando.
    /// </summary>
    private static async void VigilarAsync(Task tarea)
    {
        try
        {
            await tarea;
        }
        catch (Exception ex)
        {
            // La consola del navegador es el único sitio donde puede verse esto en
            // un despliegue real, así que se escribe.
            Console.Error.WriteLine($"El contador de segundos falló: {ex}");
        }
    }

    /// <summary>Para el contador de segundos.</summary>
    private void PararReloj()
    {
        reloj?.Stop();
        reloj?.Dispose();
        reloj = null;
    }

    /// <summary>
    /// Red de seguridad de la espera: la única cosa que garantiza que la capa
    /// de espera se quite.
    ///
    /// <para>
    /// Hasta ahora nada lo garantizaba. La capa se bajaba en el <c>finally</c> de
    /// <see cref="CargarAsync"/>, y ese <c>finally</c> solo corre cuando la espera
    /// se desenrolla. Si el navegador congela la pestaña, esa desenrollada puede
    /// no llegar nunca, y entonces la pantalla se queda con el contador clavado,
    /// sin error y sin salida: se vio justo eso al volver de la pestaña después
    /// de un rato largo.
    /// </para>
    ///
    /// <para>
    /// Este temporizador no espera a la consulta, la vigila, y por eso no depende
    /// de ella: si cuando salta la consulta sigue en curso, da la carga por
    /// perdida, baja la capa y deja un aviso honesto. También se apoya en que
    /// congelar la pestaña congela los temporizadores, pero al volver disparan
    /// de inmediato, que es justo cuando hace falta que bajen.
    /// </para>
    /// </summary>
    private void ArmarVigilante()
    {
        PararVigilante();

        vigilante = new System.Timers.Timer
        {
            Interval = TimeSpan.FromSeconds(
                MercadoPublicoApi.SegundosEspera + MargenDelVigilante).TotalMilliseconds,
            AutoReset = false,
        };

        vigilante.Elapsed += (_, _) =>
        {
            try
            {
                VigilarAsync(InvokeAsync(async () =>
                {
                    // Si la carga terminó por la vía normal, no hay nada que
                    // salvar: el finally ya bajó la capa y apagó este temporizador.
                    // Solo salta de verdad cuando la espera se quedó atascada.
                    if (!Cargando) return;

                    // Se sube la generación para que la consulta atascada, si
                    // alguna vez responde, vea que ya no es su turno y no escriba
                    // su respuesta encima de lo de ahora.
                    generacionCarga++;

                    Cargando = false;
                    PararReloj();
                    PararVigilante();

                    ErrorConsulta =
                        "La consulta se quedó atorada en el camino de vuelta y se cerró " +
                        "para no dejar la pantalla clavada. Puede que Mercado Público la " +
                        "siga buscando: inténtalo otra vez en un momento.";

                    ProgramarRefresco();

                    // Y si al volver de la pestaña había un refresco esperando,
                    // esta es su oportunidad: ya no hay nada encima que lo bloquee.
                    await AtenderRefrescoPospuestoAsync();
                }));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"La red de seguridad de la espera falló: {ex}");
            }
        };

        vigilante.Start();
    }

    /// <summary>Apaga la red de seguridad de la espera.</summary>
    private void PararVigilante()
    {
        vigilante?.Stop();
        vigilante?.Dispose();
        vigilante = null;
    }

    /// <summary>
    /// Refresco automático, SOLO si se está mirando la semana en curso.
    ///
    /// Una semana pasada no va a cambiar: repreguntarla gastaría cuota para
    /// devolver exactamente lo mismo. Por eso no se programa para las antiguas.
    ///
    /// Y hay un segundo motivo para no programarlo, que salió de un fallo real:
    /// el temporizador dispara <see cref="CargarAsync"/>, que consulta los
    /// valores del DESPLEGABLE, no los de lo que hay en pantalla. Así que con un
    /// cambio sin traer, el temporizador arrancaba solo una consulta de una
    /// semana distinta de la que el usuario estaba mirando, y el aviso de
    /// "pulsa Actualizar para traerla" se volvía mentira a los cinco minutos: el
    /// usuario iba a septiembre, sin pulsar nada, y se le lanzaba una consulta de
    /// septiembre, de una semana pasada y sin caché, que son hasta cinco días
    /// consultados uno detrás de otro.
    /// </summary>
    private void ProgramarRefresco()
    {
        temporizador?.Stop();
        temporizador?.Dispose();
        temporizador = null;

        if (MinutosRefresco <= 0) return;

        // Solo el PERIODO EN CURSO: en modo API es la semana en curso y en modo
        // base de datos es el mes. La decisión va dentro de una línea en vez de
        // duplicar el temporizador, porque con dos copias el día que se tocara
        // una se habría olvidado la otra y ese fallo no da ningún error: la
        // pantalla simplemente dejaría de actualizarse sola.
        if (UsaBaseDeDatos ? !EsMesActual : !EsSemanaActual) return;

        // Con un cambio pendiente de traer, no se programa. Es el mismo motivo
        // de arriba escrito como condición: si hay algo sin traer, el refresco
        // no puede traerlo, solo contradecirlo.
        if (HayPeriodoPendiente) return;

        temporizador = new System.Timers.Timer
        {
            Interval = TimeSpan.FromMinutes(MinutosRefresco).TotalMilliseconds,
            AutoReset = false,
        };

        // AutoReset = false y se rearma al final: si el servidor tardó un
        // minuto con sus reintentos, el siguiente disparo se cuenta desde que
        // terminó y no se solapan.
        temporizador.Elapsed += async (_, _) =>
        {
            try
            {
                await InvokeAsync(async () =>
                {
                    try
                    {
                        // En segundo plano: es el temporizador, no hay nadie
                        // mirando. El servidor se conforma con menos reintentos
                        // porque así no retiene la semana mientras nadie la mira.
                        await CargarAsync(enSegundoPlano: true);
                    }
                    finally
                    {
                        // SE REARMA SIEMPRE. Antes esta llamada iba después del
                        // await y sin try: si CargarAsync lanzaba, no se llegaba
                        // a ejecutar, el temporizador quedaba sin rearme y el
                        // refresco automático moría en silencio para el resto de
                        // la sesión, sin aviso en ninguna parte. Con un fallo, eso
                        // es lo que más se nota, porque la página sigue abierta y
                        // simplemente nunca más se actualiza sola.
                        ProgramarRefresco();
                    }
                });
            }
            catch (Exception ex)
            {
                // Solo se llega aquí si falla el InvokeAsync de entrada, o sea,
                // si ya no hay a quién repintar. EN ESE CASO NO SE REARMA A PROPÓSITO:
                // si el componente ya no existe, rearmar dejaría un temporizador
                // llamando a una pantalla desaparecida, para siempre.
                //
                // Antes esta excepción no se registraba en ninguna parte. Ahora
                // queda en la consola del navegador.
                Console.Error.WriteLine($"El refresco automático no pudo arrancar: {ex}");
            }
        };

        temporizador.Start();
    }

    // ------------------------------------------------------------------
    // Detalle
    // ------------------------------------------------------------------

    /// <summary>
    /// Abre la ficha y pide su detalle. Es una petición por licitación, así que
    /// solo se hace al abrir: pedirlas todas al cargar gastaría cupo sin
    /// necesidad.
    /// </summary>
    private async Task AbrirDetalleAsync(Licitacion licitacion)
    {
        abierta = licitacion;
        detalleAbierto = null;
        errorDetalle = null;
        cargandoDetalle = true;
        detalleDesdeCache = false;

        if (string.IsNullOrWhiteSpace(licitacion.CodigoExterno))
        {
            errorDetalle = "Esta licitación no trae código, así que no se puede consultar su detalle.";
            cargandoDetalle = false;
            return;
        }

        DetalleLicitacion? detalle = null;
        bool desdeCache = false;
        string? error = null;

        try
        {
            (detalle, desdeCache, error) = await Api.GetDetalleAsync(licitacion.CodigoExterno);
        }
        catch (Exception ex)
        {
            // Lo mismo que en la carga de la semana: la API devuelve el error en la
            // tupla, pero si algo se escapa el modal se quedaba girando para
            // siempre, que es la forma más confusa de fallar que hay.
            Console.Error.WriteLine($"El detalle de {licitacion.CodigoExterno} falló: {ex}");
            error = "No se pudo cargar el detalle de esta licitación.";
        }

        // Si el modal se cerró mientras esperaba, el resultado se descarta.
        if (abierta?.CodigoExterno != licitacion.CodigoExterno) return;

        detalleAbierto = detalle;
        detalleDesdeCache = desdeCache;
        errorDetalle = error;
        cargandoDetalle = false;
    }

    private Task CerrarDetalleAsync()
    {
        CerrarDetalle();
        return Task.CompletedTask;
    }

    private void CerrarDetalle()
    {
        abierta = null;
        detalleAbierto = null;
        cargandoDetalle = false;
        detalleDesdeCache = false;
        errorDetalle = null;
    }

    public async ValueTask DisposeAsync()
    {
        // Sin esto el temporizador seguiría consultando en segundo plano con la
        // pantalla cerrada.
        temporizador?.Stop();
        temporizador?.Dispose();

        // El reloj también: si no, seguiría pidiendo repintados sobre un
        // componente ya descartado, que en Blazor lanza.
        PararReloj();

        // Y la red de seguridad, por lo mismo y con más razón: si se quedara
        // viva, saltaría sobre una pantalla que ya no existe y bajaría la capa
        // de un componente destruido.
        PararVigilante();

        // Y la escucha de visibilidad hay que soltarla a mano. El módulo de
        // JavaScript la tiene en una tabla y, sin esta llamada, seguiría
        // escuchando y llamaría a un componente destruido: en Blazor eso lanza y
        // deja un error en la consola del navegador cada vez que se cambia de
        // pestaña.
        if (interopVisibilidad is not null)
        {
            try
            {
                await interopVisibilidad.InvokeVoidAsync("soltar", IdPantalla);
                await interopVisibilidad.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // El navegador ya no está. No hay nada que soltar.
            }
            catch (ObjectDisposedException)
            {
                // Igual: la conexión se cerró antes de que llegara esta llamada.
            }
        }

        refVisibilidad?.Dispose();
    }
}
