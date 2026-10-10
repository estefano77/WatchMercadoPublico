using System.Text.Json;
using WatchMercadoPublico.Server.Models;
using WatchMercadoPublico.Server.Services;
using Xunit;

namespace WatchMercadoPublico.Server.Tests;

/// <summary>
/// La especificación del comprador: el campo que faltaba en los ítems.
/// </summary>
/// <remarks>
/// El JSON de un ítem de Mercado Público trae <c>Descripcion</c>. La página lo
/// rotula "Especificaciones del comprador", y <b>no existe ninguna clave
/// "Especificacion"</b>. Eso se comprobó contra la API con la licitacion
/// 2342-28-LR24, que tiene ocho líneas con el MISMO nombre de producto.
///
/// <para>
/// Estas pruebas existen por el modo en que falla ese error. Leer
/// "Descripcion" como si fuera "Especificacion" no da ningún error: el campo
/// sale a null, la fila se pinta igual, y la única pista es que en la pantalla
/// no hay texto donde debería haberlo. Y en una adjudicación donde ocho líneas
/// comparten producto, sin ese texto las ocho salen idénticas.
/// </para>
/// <para>
/// El fragmento es una forma exacta de lo que devolvió la API, con las dos
/// empresas que se repartieron los ocho ítems.
/// </para>
/// </remarks>
public class LecturaDeItemsTests
{
    /// <summary>
    /// Las dos primeras líneas de 2342-28-LR24. Mismo producto, misma unidad,
    /// descripciones distintas y empresas distintas: el caso que hace falta el
    /// campo.
    /// </summary>
    private const string DosLineas =
        """
        {
          "Listado": [
            {
              "Correlativo": 1,
              "NombreProducto": "Software del sistema de administración de bases de datos",
              "Descripcion": "De acuerdo a lo indicado en bases técnicas",
              "UnidadMedida": "Unidad",
              "Cantidad": 1,
              "Adjudicacion": {
                "RutProveedor": "61.981.420-7",
                "NombreProveedor": "INGENIERIA Y SISTEMAS COMPUTACIONALES S A",
                "Cantidad": 1,
                "MontoUnitario": 12000000
              }
            },
            {
              "Correlativo": 2,
              "NombreProducto": "Software del sistema de administración de bases de datos",
              "Descripcion": "Línea a) Sistemas Computacionales Juzgados - TÉCNICO RESIDENTE",
              "UnidadMedida": "Unidad",
              "Cantidad": 1,
              "Adjudicacion": {
                "RutProveedor": "86.130.200-8",
                "NombreProveedor": "SISTEMAS MODULARES DE COMPUTACION SPA",
                "Cantidad": 1,
                "MontoUnitario": 9500000
              }
            }
          ]
        }
        """;

    private static List<ItemAdjudicado> Leer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return MercadoPublicoCliente.LeerItems(doc.RootElement);
    }

    [Fact]
    public void La_descripcion_del_item_se_lee_de_la_clave_Descripcion()
    {
        var items = Leer(DosLineas);

        Assert.Equal(2, items.Count);
        Assert.Equal(
            "Línea a) Sistemas Computacionales Juzgados - TÉCNICO RESIDENTE",
            items[1].Descripcion);
    }

    [Fact]
    public void Dos_lineas_del_mismo_producto_caden_distintas_descripciones()
    {
        var items = Leer(DosLineas);

        // Lo que hace falta el campo. Si las dos lineas salieran con la misma
        // descripcion, la tabla de items no distinguiria una de otra.
        Assert.Equal(items[0].NombreProducto, items[1].NombreProducto);
        Assert.NotEqual(items[0].Descripcion, items[1].Descripcion);
    }

    [Fact]
    public void Un_item_sin_Descripcion_no_falla_solo_deja_el_campo_a_null()
    {
        // Y no "vacio" ni "sin descripcion" inventado: null, que es lo que
        // distingue "la API no lo manda" de "la API lo manda en blanco".
        var items = Leer(
            """
            {
              "Listado": [
                {
                  "Correlativo": 1,
                  "NombreProducto": "Servicio",
                  "Adjudicacion": { "Cantidad": 1, "MontoUnitario": 100 }
                }
              ]
            }
            """);

        Assert.Single(items);
        Assert.Null(items[0].Descripcion);
    }

    [Fact]
    public void La_descripcion_no_mezcla_la_de_la_licitacion_con_la_del_item()
    {
        // La clave esta a DOS niveles: la licitacion tiene su propia Descripcion,
        // que es el objeto entero, y cada item tiene la suya. Que una se pise con
        // la otra seria el error de leer del nivel equivocado.
        var items = Leer(
            """
            {
              "Descripcion": "Objeto de la licitación completo",
              "Listado": [
                {
                  "Correlativo": 1,
                  "NombreProducto": "Servicio",
                  "Descripcion": "Especificación de esta línea",
                  "Adjudicacion": { "Cantidad": 1, "MontoUnitario": 100 }
                }
              ]
            }
            """);

        Assert.Equal("Especificación de esta línea", items[0].Descripcion);
    }
}
