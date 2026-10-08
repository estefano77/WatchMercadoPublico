using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WatchMercadoPublico.Server.Models;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// El interruptor de fuente de datos: qué se necesita para cada modo y qué pasa
/// con una configuración mal escrita.
/// </summary>
/// <remarks>
/// <para>
/// Esta clase existe por un fallo concreto que salió al poner la fuente en
/// marcha. <c>Servible</c> exigía ticket SIEMPRE, y en modo base de datos no
/// hace falta ticket para nada: no se pregunta a Mercado Público. Con la
/// condición antigua, una instalación correcta en modo base de datos —base
/// configurada, código de proveedor puesto, sin ticket— se quedaba con el
/// aviso de "la aplicación no está configurada" y sin resultados, y no había
/// ningún test que lo notara.
/// </para>
///
/// <para>
/// Por eso se prueban los DOS modos con la misma configuración incompleta, y
/// no solo el que se usa por defecto.
/// </para>
/// </remarks>
public class FuenteDeDatosTests
{
    private static MercadoPublicoOpciones Opciones(
        string fuente = "api",
        string ticket = "TICKET",
        string codigo = "71284",
        string cadena = "Server=localhost;Database=WatchMerPub;Integrated Security=True")
        => new()
        {
            FuenteDatos = fuente,
            Ticket = ticket,
            CodigoProveedor = codigo,
            CadenaConexionSql = cadena,
        };

    // --- Normalización del valor escrito ---

    [Theory]
    // Se aceptan los sinónimos porque el fichero de configuración lo escribe
    // una persona a mano y "sqlserver" es lo que le sale natural.
    [InlineData("sql", "sql")]
    [InlineData("SQL", "sql")]
    [InlineData("  sql  ", "sql")]
    [InlineData("sqlserver", "sql")]
    [InlineData("basedatos", "sql")]
    [InlineData("api", "api")]
    [InlineData("API", "api")]
    public void La_fuente_se_normaliza_para_comparar(string escrito, string esperado)
    {
        Assert.Equal(esperado, Opciones(fuente: escrito).Fuente);
    }

    [Theory]
    // Un valor que no se reconoce cae en "api", que es lo que se hacía siempre.
    // La razón de que no sea un error es que la aplicación tiene que arrancar
    // igual para poder avisar; lo que no puede es quedarse en silencio.
    [InlineData("")]
    [InlineData("postgres")]
    [InlineData("API ")]
    public void Una_fuente_no_reconocida_cae_en_api(string escrito)
    {
        var opciones = Opciones(fuente: escrito);

        Assert.Equal("api", opciones.Fuente);
        Assert.False(opciones.UsaBaseDeDatos);
    }

    [Fact]
    public void Fuente_nula_no_revienta_la_aplicacion()
    {
        var opciones = Opciones();
        opciones.FuenteDatos = null!;

        Assert.Equal("api", opciones.Fuente);
    }

    // --- Qué hace falta en cada modo ---

    [Fact]
    public void En_modo_base_de_datos_no_hace_falta_ticket()
    {
        var opciones = Opciones(fuente: "sql", ticket: "");

        Assert.True(opciones.UsaBaseDeDatos);
        Assert.True(opciones.Servible);
    }

    [Fact]
    public void En_modo_api_sin_ticket_no_es_servible()
    {
        Assert.False(Opciones(fuente: "api", ticket: "").Servible);
    }

    [Theory]
    // Sin cadena de conexión el modo base de datos está pedido pero no se puede
    // hacer, y el usuario tiene que poder distinguir esas dos cosas.
    [InlineData("")]
    [InlineData("   ")]
    public void En_modo_base_de_datos_sin_cadena_no_es_servible(string cadena)
    {
        var opciones = Opciones(fuente: "sql", cadena: cadena);

        Assert.True(opciones.UsaBaseDeDatos);
        Assert.False(opciones.BaseDeDatosUtilizable);
        Assert.False(opciones.Servible);
    }

    [Theory]
    // El código de proveedor sigue haciendo falta en los dos modos: lo que hay
    // en la base está etiquetado por empresa.
    [InlineData("api")]
    [InlineData("sql")]
    public void Sin_codigo_de_proveedor_no_hay_nada_que_mostrar(string fuente)
    {
        Assert.False(Opciones(fuente: fuente, codigo: "").Servible);
    }

    [Fact]
    public void El_modo_demo_sigue_siendo_servible_sin_nada_configurado()
    {
        // Es lo que permite probar la pantalla sin tocar la base ni gastar
        // ticket. Si se rompiera esto, se pierde la forma de probar todo lo
        // demás.
        var opciones = new MercadoPublicoOpciones { ModoConsulta = "demo" };

        Assert.True(opciones.Servible);
    }

    [Fact]
    public void La_cadena_vacia_no_molesta_en_modo_api()
    {
        // La cadena vacía es el valor por defecto de appsettings.json. Si
        // CUANTA para el modo API, aparecería un aviso de base de datos sin
        // configurar en una instalación que ni la usa.
        //
        // Y es FALSE aquí a propósito, no un descuido: la propiedad responde a
        // "¿está la base de datos usable?", y en modo API no hay base de datos
        // en juego. Los dos sitios donde importa la comprueban con
        // UsaBaseDeDatos delante, y por eso este false no se ve en pantalla.
        var opciones = Opciones(fuente: "api", cadena: "");

        Assert.False(opciones.UsaBaseDeDatos);
        Assert.False(opciones.BaseDeDatosUtilizable);
        Assert.True(opciones.Servible);
    }

    // --- Ingesta automática ---

    [Theory]
    [InlineData("sql", "TICKET", "Server=x", true)]
    [InlineData("sql", "", "Server=x", false)]
    [InlineData("sql", "TICKET", "", false)]
    // Sin ticket la ingesta no puede hacer nada, y el temporalizador solo
    // reintentaría contra la API. Pasó de verdad el 8 de octubre de 2026.
    [InlineData("api", "TICKET", "Server=x", false)]
    public void La_ingesta_solo_necesita_los_tres_piezas(
        string fuente, string ticket, string cadena, bool esperado)
    {
        var ingesta = CrearIngesta(fuente, ticket, cadena);

        Assert.Equal(esperado, ingesta.PuedeIngerir);

        // FaltaParaIngerir solo dice algo en modo base de datos. En modo API no
        // le falta nada: no hay ingesta que hacer. Por eso NO se comprueba con
        // un "esperado == (falta == null)": esa equivalencia es falsa y
        // probarla como si lo fuera dejaría escrito un error.
        var falta = ingesta.FaltaParaIngerir;

        if (fuente == "sql")
            Assert.Equal(esperado, falta is null);
        else
            Assert.Null(falta);
    }

    [Fact]
    public void La_ingesta_dice_que_le_falta_y_no_lo_dice_al_azar()
    {
        Assert.Contains("ticket", CrearIngesta("sql", "", "Server=x").FaltaParaIngerir!);
        Assert.Contains("cadena", CrearIngesta("sql", "T", "").FaltaParaIngerir!);

        // En modo API no falta nada de la ingesta: no hay ingesta que hacer.
        Assert.Null(CrearIngesta("api", "", "").FaltaParaIngerir);
    }

    private static IngestaMercadoPublico CrearIngesta(string fuente, string ticket, string cadena) =>
        new(
            Options.Create(Opciones(fuente, ticket, "71284", cadena)),
            NullLogger<IngestaMercadoPublico>.Instance);

    // --- El texto del perodo: el mes entero, sin recortes ---

    [Theory]
    // Un mes de 30 y uno de 31. Y febrero de 2026, que tiene 28 porque no es
    // bisiesto: es el mes donde un " ultimo dia" mal calculado se nota mas.
    [InlineData(2026, 2, "Del 1 al 28 de febrero de 2026")]
    [InlineData(2026, 4, "Del 1 al 30 de abril de 2026")]
    [InlineData(2026, 9, "Del 1 al 30 de septiembre de 2026")]
    [InlineData(2026, 10, "Del 1 al 31 de octubre de 2026")]
    [InlineData(2026, 12, "Del 1 al 31 de diciembre de 2026")]
    public void El_periodo_del_mes_llega_hasta_el_ultimo_dia_del_mes(int anio, int mes, string esperado)
    {
        Assert.Equal(esperado, TextosDeFecha.PeriodoDelMes(anio, mes));
    }

    [Fact]
    public void El_periodo_no_se_recorta_a_dias_habiles()
    {
        // El 31 de octubre de 2026 es SABADO, y el 1 de noviembre es domingo.
        // Con la regla anterior el texto paraba en el 30, el ultimo habil, y
        // es decir que el rango buscado acababa antes que el mes.
        //
        // Aqui la busqueda va del 1 al 31 porque eso es lo que se busca: leer una
        // fila de un SQL Server local no es una llamada que haya que ahorrar. En
        // modo API el recorte a dias habiles se sigue haciendo, y con otro
        // motivo: cada dia no consultado es una peticion mas.
        Assert.Equal("Del 1 al 31 de octubre de 2026", TextosDeFecha.PeriodoDelMes(2026, 10));

        // Y un mes que empieza en finde tambien llega entero. El 1 de marzo de
        // 2026 es domingo.
        Assert.Equal("Del 1 al 31 de marzo de 2026", TextosDeFecha.PeriodoDelMes(2026, 3));
    }

    [Fact]
    public void El_periodo_no_se_corta_en_el_dia_de_hoy()
    {
        // Esta prueba vivio al reves y es la que ahora fija la premisa.
        //
        // Antes el metodo llevaba un "hoy" y se paraba ahi: el 8 de octubre de
        // 2026 decia "Del 1 al 8 de octubre de 2026", que parece que la busqueda
        // se quedo corta por un fallo cuando lo que pasa es que el mes no habia
        // terminado. El metodo ya no recibe el reloj, asi que esto no se puede
        // volver a colar sin anadir el parametro otra vez.
        //
        // Y no hay forma de que un test asi dependa del dia en que se ejecuta:
        // no lee el reloj. Esa era la razon de partirlo en dos antes.
        Assert.Equal("Del 1 al 31 de octubre de 2026", TextosDeFecha.PeriodoDelMes(2026, 10));
        Assert.Equal("Del 1 al 30 de septiembre de 2026", TextosDeFecha.PeriodoDelMes(2026, 9));
    }

    [Fact]
    public void Febrero_de_un_ano_bisiesto_tiene_29()
    {
        // Un mes entero tiene que respetar el calendario de verdad. 2028 es
        // bisiesto, 2026 no.
        Assert.Equal("Del 1 al 29 de febrero de 2028", TextosDeFecha.PeriodoDelMes(2028, 2));
        Assert.Equal("Del 1 al 28 de febrero de 2026", TextosDeFecha.PeriodoDelMes(2026, 2));
    }

    // --- El titulo del panel de cero resultados ---

    /// <summary>
    /// Que el nombre del mes con año lo componga el servidor y llegue escrito.
    /// </summary>
    /// <remarks>
    /// El titulo del panel de cero resultados es "Nada en el mes de Junio de
    /// 2026", y el nombre del mes con mayúscula lo trae
    /// <see cref="TextosDeFecha.NombreMes"/>... en minúscula, porque va dentro de
    /// frases como "Del 1 al 30 de junio de 2026". Un título empieza con
    /// mayúscula, así que el que se usa es <c>CalendarioDelMes.NombreMes</c>.
    ///
    /// Y el mes NO se busca en el desplegable del cliente: ese array llega
    /// filtrado al mes en curso, y depender de él para escribir un texto es la
    /// clase de acoplamiento que ya rompió una vez el array de días.
    /// </remarks>
    [Theory]
    [InlineData(1, "Enero")]
    [InlineData(6, "Junio")]
    [InlineData(9, "Septiembre")]
    [InlineData(10, "Octubre")]
    [InlineData(12, "Diciembre")]
    public void El_nombre_del_mes_con_mayuscula_viene_del_calendario(int mes, string esperado)
    {
        // El que se usa para títulos. Con minúscula saldría "Nada en el mes de
        // junio de 2026", que es lo que se veía antes.
        Assert.Equal(esperado, CalendarioDelMes.NombreMes(mes));

        // Y el de dentro de frases, que sí va en minúscula.
        Assert.Equal(esperado.ToLowerInvariant(), TextosDeFecha.NombreMes(mes));
    }

    [Fact]
    public void El_rango_del_mes_sigue_en_minuscula_porque_va_en_una_frase()
    {
        // "Del 1 al 30 de junio de 2026". Con mayúscula sería
        // "Del 1 al 30 de Junio de 2026", que es como se escribe mal.
        Assert.Equal(
            "Del 1 al 30 de junio de 2026",
            TextosDeFecha.PeriodoDelMes(2026, 6));
    }

    [Fact]
    public void Los_dos_textos_del_mes_son_distintos_y_no_se_confunden()
    {
        // Son dos textos para dos sitios. Que uno se derive del otro sin querer
        // es como el panel acababa diciendo "el mes de junio" en mayuscula o
        // "Nada en Del 1 al 30 de junio de 2026".
        Assert.Equal("Del 1 al 30 de junio de 2026", TextosDeFecha.PeriodoDelMes(2026, 6));
        Assert.Equal("Junio", CalendarioDelMes.NombreMes(6));
    }
}
