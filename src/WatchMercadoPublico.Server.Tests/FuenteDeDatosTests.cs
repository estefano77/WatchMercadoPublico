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
/// marcha. La condición de "esto se puede servir" exigía ticket SIEMPRE, y en
/// modo base de datos no hace falta ticket para nada: no se pregunta a Mercado
/// Público. Con la condición antigua, una instalación correcta en modo base de
/// datos —base configurada, sin ticket— se quedaba con el aviso de "la
/// aplicación no está configurada" y sin resultados, y no había ningún test que
/// lo notara.
/// </para>
///
/// <para>
/// Por eso se prueban los DOS modos con la misma configuración incompleta, y
/// no solo el que se usa por defecto.
/// </para>
///
/// <para>
/// <b>OJO CON LO QUE YA NO ESTÁ AQUÍ.</b> Estas pruebas usaban
/// <c>MercadoPublicoOpciones.Servible</c> y <c>CodigoProveedor</c>, y las dos
/// cosas ya no existen: el código de proveedor se lee de <c>MpEmpresa</c> y la
/// pregunta de si se puede atender la responde <c>EmpresaVigilada</c>. Lo que
/// queda en las opciones es solo si se puede <i>preguntar a la API</i>, que es
/// otra pregunta. Los casos de "no se puede atender" que estaban aquí se han ido
/// a <see cref="EmpresaVigiladaTests"/>.
/// </para>
/// </remarks>
public class FuenteDeDatosTests
{
    private static MercadoPublicoOpciones Opciones(
        string fuente = "api",
        string ticket = "TICKET",
        string rut = "86.130.200-8",
        string cadena = "Server=localhost;Database=WatchMerPub;Integrated Security=True")
        => new()
        {
            FuenteDatos = fuente,
            Ticket = ticket,
            RutEmpresa = rut,
            CadenaConexionSql = cadena,
        };

    /// <summary>Una empresa ya resuelta, para las pruebas que no miran cómo se resolvió.</summary>
    private static EmpresaVigilada ConEmpresa(string codigo = "71284") =>
        new(new EmpresaActual(codigo, "EMPRESA DE PRUEBA", "86.130.200-8", ""));


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
    //
    // Esto mira las PIEZAS sueltas, no una propiedad que las reúna. Ya hubo aquí
    // un "Servible" que las juntaba, y se quitó cuando el código de proveedor
    // dejó de estar en la configuración: si se volviera a poner una propiedad
    // así, volvería a ser la respuesta a una pregunta que no se puede responder
    // solo con este fichero.

    [Fact]
    public void En_modo_base_de_datos_no_hace_falta_ticket()
    {
        // El fallo que motivó esta clase: Servible exigía ticket siempre, y en
        // modo base de datos no se pregunta nada a la API. Una instalación
        // correcta sin ticket se quedaba con un aviso que no aplicaba.
        var opciones = Opciones(fuente: "sql", ticket: "");

        Assert.True(opciones.UsaBaseDeDatos);
        Assert.False(opciones.TieneTicket);
        Assert.True(opciones.BaseDeDatosUtilizable);
    }

    [Fact]
    public void En_modo_api_no_hay_base_de_datos_que_usable()
    {
        // Y al revés: en modo API la cadena vacía no es un problema. Sin esta
        // pieza, el aviso de "falta la cadena de conexión" saldría en una
        // instalación que ni la usa.
        var opciones = Opciones(fuente: "api", cadena: "");

        Assert.False(opciones.UsaBaseDeDatos);
        Assert.False(opciones.BaseDeDatosUtilizable);
        Assert.True(opciones.TieneTicket);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void En_modo_base_de_datos_la_cadena_vacia_no_sirve_de_nada(string cadena)
    {
        var opciones = Opciones(fuente: "sql", cadena: cadena);

        Assert.True(opciones.UsaBaseDeDatos);
        Assert.False(opciones.BaseDeDatosUtilizable);
    }

    [Fact]
    public void El_modo_demo_no_necesita_nada()
    {
        // Es lo que permite probar la pantalla sin tocar la base ni gastar
        // ticket. Si se rompiera esto, se pierde la forma de probar todo lo
        // demás.
        var opciones = new MercadoPublicoOpciones { ModoConsulta = "demo" };

        Assert.True(opciones.PuedePreguntarALaApi);
        Assert.False(opciones.TieneTicket);
        Assert.False(opciones.UsaBaseDeDatos);
    }

    [Fact]
    public void El_rut_via_sin_normalizar_todavia_cuesta_lo_mismo()
    {
        // La configuración no normaliza el RUT. Lo hace mp.LeeEmpresa al
        // comparar, para que cambiarlo por otra forma del mismo número no rompa
        // nada, y lo comprueba MercadoPublicoCliente.RutBienFormado antes de
        // llamar a la API. Aquí solo se deja escrito que el valor llega tal cual,
        // porque quien lo normaliza no son estas opciones.
        Assert.Equal("86.130.200-8", Opciones().RutEmpresa);
        Assert.Equal("  86130200-8  ", Opciones(rut: "  86130200-8  ").RutEmpresa);
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
    public void La_ingesta_no_corre_sin_el_codigo_de_proveedor_de_la_empresa()
    {
        // La cuarta pieza. Se añadió cuando el código de proveedor dejó de estar
        // en la configuración y pasó a MpEmpresa: una ingesta sin empresa no
        // sabe a quién preguntar, y sin esto se lanzaría a la API con el código
        // vacío, que es una consulta que devuelve todo el país.
        var sinEmpresa = new IngestaMercadoPublico(
            Options.Create(Opciones(fuente: "sql", ticket: "TICKET", cadena: "Server=x")),
            new EmpresaVigilada(null, "la tabla MpEmpresa está vacía"),
            NullLogger<IngestaMercadoPublico>.Instance);

        Assert.False(sinEmpresa.PuedeIngerir);
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
            Options.Create(Opciones(fuente, ticket, "86.130.200-8", cadena)),
            ConEmpresa(),
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
