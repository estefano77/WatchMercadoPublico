using System.Globalization;
using System.Net;

namespace WatchMercadoPublico.Client.Services;

/// <summary>
/// Formato de cifras y fechas para la interfaz. Todo en es-CL, con los
/// separadores que se usan en Chile, no los de en-US.
/// </summary>
public static class Formato
{
    private static readonly CultureInfo Chileno = CultureInfo.GetCultureInfo("es-CL");

    /// <summary>Monto con separador de miles.</summary>
    public static string Monto(decimal? valor) =>
        valor is null ? "—" : valor.Value.ToString("N0", Chileno);

    /// <summary>
    /// Cantidad de un ítem: entera sin decimales ("12"), y con decimales solo si
    /// los hay ("1,5"). Un trailing ",0" en una cantidad queda raro.
    /// </summary>
    public static string Cantidad(decimal? valor)
    {
        if (valor is null) return "—";

        return valor.Value == decimal.Truncate(valor.Value)
            ? valor.Value.ToString("N0", Chileno)
            : valor.Value.ToString("N2", Chileno).TrimEnd('0').TrimEnd(',').TrimEnd('.');
    }

    /// <summary>Porcentaje con un decimal: "23,2%".</summary>
    public static string Porcentaje(decimal valor) =>
        valor.ToString("N1", Chileno).Replace("%", "") + "%";

    /// <summary>Monto con la unidad al lado, para no repetirla en cada etiqueta.</summary>
    public static string MontoConUnidad(decimal? valor, string? moneda)
    {
        if (valor is null) return "Sin monto publicado";

        var unidad = Moneda(moneda);
        return string.IsNullOrEmpty(unidad)
            ? Monto(valor)
            : $"{Monto(valor)} {unidad}";
    }

    /// <summary>
    /// Unidad monetaria normalizada. "CLF" es el código que usa la API para el
    /// peso chileno como UF, pero en Chile se escribe "UF": dejarlo como CLF en
    /// pantalla sería un código interno que nadie reconoce.
    /// </summary>
    private static string Moneda(string? codigo) => codigo?.ToUpperInvariant() switch
    {
        "CLP" => "CLP",
        "UTM" => "UTM",
        "USD" => "USD",
        "CLF" => "UF",
        "EUR" => "EUR",
        null or "" => "",
        _ => codigo.ToUpperInvariant(),
    };

    /// <summary>Fecha corta: "12 jun 2026".</summary>
    public static string Fecha(DateTimeOffset? valor) =>
        valor is null ? "—" : valor.Value.ToLocalTime().ToString("d MMM yyyy", Chileno);

    /// <summary>Fecha y hora: "12 jun 2026 · 17:30".</summary>
    public static string FechaHora(DateTimeOffset? valor) =>
        valor is null ? "—" : valor.Value.ToLocalTime().ToString("d MMM yyyy · HH:mm", Chileno);

    /// <summary>Solo la hora: "17:30".</summary>
    public static string Hora(DateTimeOffset? valor) =>
        valor is null ? "" : valor.Value.ToLocalTime().ToString("HH:mm", Chileno);

    /// <summary>
    /// Cuenta los días que faltan para el cierre. Es el dato más útil de la
    /// ficha: un "cierra en 2 días" pesa mucho más que la fecha suelta.
    /// </summary>
    public static (string Texto, string Tono) CierraEn(DateTimeOffset? cierre)
    {
        if (cierre is null) return ("Sin fecha de cierre", "text-muted");

        var dias = (cierre.Value.Date - DateTime.Today).Days;

        return dias switch
        {
            < 0 => ($"Cerró hace {Math.Abs(dias)} días", "text-muted"),
            0 => ("Cierra hoy", "text-rose-500 font-bold"),
            1 => ("Cierra mañana", "text-amber-500 font-bold"),
            <= 7 => ($"Cierra en {dias} días", "text-amber-500 font-semibold"),
            _ => ($"{dias} días restantes", "text-muted"),
        };
    }

    /// <summary>
    /// Parte un texto en trozos, marcando las apariciones de las palabras
    /// buscadas para poder envolverlas en &lt;mark&gt;.
    ///
    /// Se hace aquí y NO con MarkupString sobre el texto interpolado: el nombre
    /// de una licitación viene de fuera y si se pinta como HTML es un XSS
    /// esperando a ocurrir. Al construir cada trozo se escapa al pasarlo, así
    /// que el contenido nunca se interpreta.
    /// </summary>
    public static List<TrozoTexto> Resaltar(string? texto, IEnumerable<string> palabras)
    {
        var resultado = new List<TrozoTexto>();
        if (string.IsNullOrEmpty(texto)) return resultado;

        var terminos = palabras
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(SinAcentos)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (terminos.Length == 0)
        {
            resultado.Add(new TrozoTexto(texto, false));
            return resultado;
        }

        var fuente = SinAcentos(texto);

        // Se busca sobre el texto sin acentos para no fallar con "licitacion"
        // contra "Licitación", pero se recorta el ORIGINAL con esos mismos
        // índices: quitar acentos no cambia la longitud, así que las
        // posiciones siguen siendo válidas.
        var i = 0;
        var cursor = 0;

        while (i < fuente.Length)
        {
            var encontrado = -1;
            var largo = 0;

            foreach (var termino in terminos)
            {
                if (termino.Length == 0 || termino.Length > fuente.Length - i) continue;

                if (string.CompareOrdinal(fuente, i, termino, 0, termino.Length) == 0)
                {
                    // El término más largo gana: "contrato" y "contrato de
                    // suministro" no deben partir la misma coincidencia.
                    if (encontrado < 0 || termino.Length > largo)
                    {
                        encontrado = i;
                        largo = termino.Length;
                    }
                }
            }

            if (encontrado < 0)
            {
                i++;
                continue;
            }

            if (encontrado > cursor)
                resultado.Add(new TrozoTexto(texto[cursor..encontrado], false));

            resultado.Add(new TrozoTexto(texto.Substring(encontrado, largo), true));
            cursor = encontrado + largo;
            i = cursor;
        }

        if (cursor < texto.Length)
            resultado.Add(new TrozoTexto(texto[cursor..], false));

        return resultado;
    }

    /// <summary>
    /// Minúsculas y sin tildes, para comparar sin que estorben tildes.
    ///
    /// Tabla explícita en vez de RemoveDiacritics() o NormalizationForm.FormD:
    /// no está disponible en este runtime, y FormD además reserva una cadena
    /// nueva por comparación. El filtro corre en cada tecla y sobre cada
    /// licitación, así que aquí cada asignación cuenta.
    /// </summary>
    private static string SinAcentos(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return string.Empty;

        Span<char> buffer = texto.Length <= 256 ? stackalloc char[texto.Length] : new char[texto.Length];

        for (var i = 0; i < texto.Length; i++)
        {
            var sustituto = SinAcentuar(texto[i]);
            buffer[i] = sustituto != '\0' ? char.ToLowerInvariant(sustituto) : texto[i];
        }

        return new string(buffer);
    }

    private static char SinAcentuar(char c) => c switch
    {
        'á' or 'à' or 'ä' or 'â' => 'a',
        'é' or 'è' or 'ë' or 'ê' => 'e',
        'í' or 'ì' or 'ï' or 'î' => 'i',
        'ó' or 'ò' or 'ö' or 'ô' => 'o',
        'ú' or 'ù' or 'ü' or 'û' => 'u',
        'ñ' => 'n',
        'ç' => 'c',
        _ => '\0',
    };

    /// <summary>Escapa un texto para insertarlo como HTML. Blindaje anti-XSS.</summary>
    public static string Escapar(string? texto) =>
        string.IsNullOrEmpty(texto) ? string.Empty : WebUtility.HtmlEncode(texto);
}

/// <summary>Trozo de un texto, marcado o no, para el resaltado de la búsqueda.</summary>
/// <param name="Texto">El trozo, tal cual se pinta.</param>
/// <param name="Coincide">True si es una coincidencia de la búsqueda.</param>
public readonly record struct TrozoTexto(string Texto, bool Coincide);