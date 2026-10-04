namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Nombres de los meses en español, para las cabeceras y el desplegable de mes.
///
/// Las semanas del mes ya NO viven aquí: están en <see cref="SemanasDelMes"/>.
/// Se separaron porque una cosa es "cómo se llama septiembre" y otra "qué días
/// abarca su semana 3".
/// </summary>
public static class CalendarioDelMes
{
    private static readonly string[] Nombres =
    [
        "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre",
    ];

    /// <summary>Nombre del mes, para las cabeceras.</summary>
    public static string NombreMes(int mes) => Nombres[Math.Clamp(mes, 1, 12) - 1];

    /// <summary>
    /// Los doce meses, en orden, para el desplegable. Va en el servidor para
    /// que el cliente no tenga su propia copia que se pueda desincronizar.
    /// </summary>
    public static string[] TodosLosMeses() => [.. Nombres];
}