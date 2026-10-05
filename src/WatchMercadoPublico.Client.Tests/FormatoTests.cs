using WatchMercadoPublico.Client.Services;
using Xunit;

namespace WatchMercadoPublico.Client.Tests;

/// <summary>
/// Formato: montos, fechas y el resaltado de la búsqueda.
///
/// Esta clase no existía hasta que se cayó en la cuenta de que el cliente no
/// tenía NINGÚN test, y que eso es lo que dejó pasar tres bugs de fechas sin que
/// nadie los viera. Lo que aquí se comprueba es lógica pura, sin Blazor y sin
/// red: formatear y partir texto.
///
/// NO cubre la pantalla. Home.razor sigue sin poder probarse desde aquí, porque
/// su lógica vive en el code-behind de un componente. Eso es un hueco que sigue
/// abierto, y conviene no olvidarlo.
/// </summary>
public class FormatoTests
{
    // --- Montos ------------------------------------------------------------

    [Theory]
    [InlineData(1234567, "1.234.567")]
    [InlineData(1000, "1.000")]
    [InlineData(999, "999")]
    [InlineData(59333400, "59.333.400")]
    public void Los_montos_usan_el_separador_de_miles_es_CL(int valor, string esperado)
    {
        // El separador de miles chileno es el punto. Con el de en-US, que es la
        // coma, una cantidad de siete cifras sale como "1,234,567", que en Chile
        // se lee como mil doscientos treinta y cuatro con sescentos.
        Assert.Equal(esperado, Formato.Monto(valor));
    }

    [Fact]
    public void Un_monto_que_no_esta_va_con_raya()
    {
        // Distinto del cero: un monto de 0 es un dato, un monto que falta no.
        Assert.Equal("—", Formato.Monto(null));
        Assert.Equal("0", Formato.Monto(0));
    }

    [Fact]
    public void La_unidad_monetaria_se_normaliza()
    {
        // CLF es el código que usa la API para el peso chileno, pero en Chile se
        // escribe UF. Dejarlo como CLF en pantalla es un código interno que
        // nadie reconoce.
        Assert.Equal("1.000 UF", Formato.MontoConUnidad(1000, "CLF"));
        Assert.Equal("1.000 CLP", Formato.MontoConUnidad(1000, "CLP"));
        Assert.Equal("1.000 USD", Formato.MontoConUnidad(1000, "usd"));
    }

    [Fact]
    public void Un_monto_sin_unidad_no_inventa_espacios()
    {
        Assert.Equal("Sin monto publicado", Formato.MontoConUnidad(null, "CLP"));
        Assert.Equal("1.000", Formato.MontoConUnidad(1000, ""));
    }

    [Fact]
    public void Una_cantidad_entera_no_lleva_comas_de_demas()
    {
        // 12 se ve como "12", no como "12,0". Y 1,5 sí conserva el decimal.
        Assert.Equal("12", Formato.Cantidad(12));
        Assert.Equal("1,5", Formato.Cantidad(1.5m));
        Assert.Equal("—", Formato.Cantidad(null));
    }

    // --- Fechas ------------------------------------------------------------

    [Fact]
    public void Las_fechas_se_pintan_cortas_y_en_es_CL()
    {
        var fecha = new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero);

        // Con espacios y sin punto tras el mes. Un formato pegado, tipo
        // "25feb2026", se lee como un identificador y no como una fecha.
        var texto = Formato.Fecha(fecha);

        Assert.Contains("25", texto);
        Assert.Contains("2026", texto);
        Assert.DoesNotContain("25feb2026", texto);
        Assert.Equal(texto, texto.Trim());
    }

    [Fact]
    public void Una_fecha_que_no_esta_va_con_raya()
    {
        Assert.Equal("—", Formato.Fecha(null));
        Assert.Equal("—", Formato.FechaHora(null));
        Assert.Equal("", Formato.Hora(null));
    }

    [Fact]
    public void La_hora_y_la_fecha_usan_el_mismo_dia_que_la_fecha_sola()
    {
        // Una inconsistencia aquí se ve como un error de la aplicación: el
        // mismo dato en dos sitios, con dos días distintos.
        var fecha = new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero);

        var dia = Formato.Fecha(fecha).Split(' ')[0];
        var diaEnHora = Formato.FechaHora(fecha).Split(' ')[0];

        Assert.Equal(dia, diaEnHora);
    }

    [Theory]
    [InlineData(-3, "Cerró hace 3 días")]
    [InlineData(0, "Cierra hoy")]
    [InlineData(1, "Cierra mañana")]
    [InlineData(5, "Cierra en 5 días")]
    [InlineData(20, "20 días restantes")]
    public void Cuenta_atras_hasta_el_cierre(int dias, string esperado)
    {
        // "Cierra en 2 días" pesa mucho más que la fecha suelta, y es el dato
        // más útil de la ficha. Todos los tramos tienen que decir algo: un
        // número solo, sin texto, no se lee de un vistazo.
        var cierre = new DateTimeOffset(DateTime.Today.AddDays(dias));

        Assert.Equal(esperado, Formato.CierraEn(cierre).Texto);
    }

    [Fact]
    public void Sin_fecha_de_cierre_se_dice_que_no_la_hay()
    {
        var (texto, tono) = Formato.CierraEn(null);

        Assert.Equal("Sin fecha de cierre", texto);
        Assert.Equal("text-muted", tono);
    }

    // --- Resaltado ---------------------------------------------------------

    [Fact]
    public void Resaltar_devuelve_el_texto_entero_rearmado()
    {
        // El resaltado parte el texto en trozos. Si al rearmarlos se pierde o se
        // duplica un carácter, el nombre de la licitación sale mal.
        const string original = "Adquisición de herramientas neumáticas";

        var trozos = Formato.Resaltar(original, ["neumáticas"]);

        Assert.Equal(original, string.Concat(trozos.Select(t => t.Texto)));
    }

    [Fact]
    public void Resaltar_encuentra_aunque_el_texto_lleve_acentos()
    {
        // Se busca sin acentos para no fallar con "licitacion" contra
        // "Licitación", y se recorta el ORIGINAL con esos mismos índices.
        var trozos = Formato.Resaltar("Licitación de Adquisición", ["licitacion"]);

        Assert.Equal("Licitación de Adquisición", string.Concat(trozos.Select(t => t.Texto)));
        Assert.Contains(trozos, t => t.Coincide && t.Texto == "Licitación");
    }

    [Fact]
    public void Resaltar_encuentra_palabras_que_empiezan_en_mayuscula()
    {
        // REGRESIÓN. SinAcentos bajaba a minúscula SOLO los caracteres a los que
        // les quitaba la tilde, y el resto los copiaba con su mayúscula. El
        // resultado era que "Licitación" quedaba como "Licitacion" y no
        // encontraba "licitacion": el resaltado no funcionaba en cuanto la
        // palabra empezaba por mayúscula, que es el caso normal.
        //
        // Los nombres de licitación empiezan por mayúscula casi siempre, así que
        // esto no era un caso raro: era el caso normal, y estaba roto.
        var conMayuscula = Formato.Resaltar("Contrato de suministro", ["contrato"]);

        Assert.Contains(conMayuscula, t => t.Coincide && t.Texto == "Contrato");
        Assert.Equal("Contrato de suministro", string.Concat(conMayuscula.Select(t => t.Texto)));

        // Y también al revés: buscar en mayúsculas un texto en minúsculas.
        var conTerminoEnMayus = Formato.Resaltar("suministro de matériel", ["SUMINISTRO"]);

        Assert.Contains(conTerminoEnMayus, t => t.Coincide);
        Assert.Equal("suministro de matériel", string.Concat(conTerminoEnMayus.Select(t => t.Texto)));
    }

    [Fact]
    public void Resaltar_marca_todas_las_apariciones()
    {
        var trozos = Formato.Resaltar("contrato de suministro y contrato de obra", ["contrato"]);

        Assert.Equal(2, trozos.Count(t => t.Coincide));
        Assert.Equal("contrato de suministro y contrato de obra", string.Concat(trozos.Select(t => t.Texto)));
    }

    [Fact]
    public void Resaltar_prefiere_el_termino_mas_largo()
    {
        // SiNJ399 buscase "contrato" y "contrato de suministro" a la vez, el
        // corto se llevaría la mitad de la coincidencia y el texto quedaría
        // partido en trozos raros.
        var trozos = Formato.Resaltar("contrato de suministro", ["contrato", "contrato de suministro"]);

        Assert.Equal("contrato de suministro", string.Concat(trozos.Select(t => t.Texto)));
        Assert.Contains(trozos, t => t.Coincide && t.Texto == "contrato de suministro");
    }

    [Fact]
    public void Resaltar_aguanta_un_texto_vacio_o_sin_coincidencias()
    {
        Assert.Empty(Formato.Resaltar(null, ["x"]));
        Assert.Empty(Formato.Resaltar("", ["x"]));

        var sinNada = Formato.Resaltar("nada que ver aquí", ["ausente"]);
        Assert.Equal("nada que ver aquí", string.Concat(sinNada.Select(t => t.Texto)));
        Assert.DoesNotContain(sinNada, t => t.Coincide);
    }

    [Fact]
    public void Resaltar_devuelve_el_texto_tal_cual_y_el_escape_va_a_separado()
    {
        // El nombre de una licitación viene de fuera. Resaltar lo devuelve
        // INTACTO, con sus etiquetas y todo, y por eso no se pinta como HTML:
        // quien lo escapa es Escapar, en el punto donde se inserta. Si Resaltar
        // escapara por su cuenta, el texto se veria con "&lt;" en pantalla.
        const string peligroso = "<img src=x onerror=alert(1)>Licitación</img>";

        var trozos = Formato.Resaltar(peligroso, ["licitacion"]);

        Assert.Equal(peligroso, string.Concat(trozos.Select(t => t.Texto)));
        Assert.Contains(trozos, t => t.Coincide);

        // Y el escape es lo que convierte las etiquetas en texto visible.
        //
        // OJO: HtmlEncode codifica también los acentos como entidades numéricas,
        // así que "Licitación" sale como "Licitaci&#243;n". Es HTML válido y el
        // navegador lo pinta igual, pero el código fuente queda con menos
        // acentos de los que uno espera, y conviene que esté anotado para que
        // nadie lo lea como un fallo de codificación.
        Assert.Equal(
            "&lt;img src=x onerror=alert(1)&gt;Licitaci&#243;n&lt;/img&gt;",
            Formato.Escapar(peligroso));

        // Lo que no puede pasar nunca es que las etiquetas se queden sin
        // escapar, que es el punto de todo esto.
        Assert.DoesNotContain("<", Formato.Escapar(peligroso));
        Assert.DoesNotContain(">", Formato.Escapar(peligroso));

        Assert.Equal("", Formato.Escapar(null));
    }
}
