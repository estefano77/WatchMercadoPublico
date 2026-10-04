namespace WatchMercadoPublico.Server.Services;

/// <summary>
/// Nombres de los meses en español, para las cabeceras.
///
/// Antes aquí se repartía el mes en semanas y en días para pintar el selector.
/// Ya no hay selector: la pantalla solo mira el día de hoy. De la clase entera
/// solo sobrevive el nombre del mes, que se usa en las etiquetas.
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
}