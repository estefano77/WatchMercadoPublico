---
name: Estilo de este repositorio
description: Usar al escribir o modificar cualquier código del proyecto. Cómo se escriben aquí los comentarios, el XML doc, los nombres de los tests y la estructura de un mensaje de commit o de un README.
---

# Estilo de este repositorio

El código se escribe **en español, con tildes**. Es lo primero: un fichero de
otro idioma se nota de inmediato y no encaja.

Si no tienes claro algo del estilo, **lee el fichero más cercano a lo que vas a
tocar**. Está muy uniforme y es buena guía.

## Comentarios y documentación

Se documenta el **por qué**, no el qué. El qué ya está en el código; lo que
necesita explicación es por qué está así, y sobre todo **qué se midió**.

XML doc en todo lo público y en muchos privados. Corto si cabe en una línea:

```csharp
/// <summary>Si el día es sábado o domingo.</summary>
/// <summary>El plazo que se recuerda un fallo. Solo para pruebas y para el log.</summary>
```

Cuando explica un porqué de verdad, varios `<para>`:

```csharp
/// <summary>
/// El plazo del fallo tiene que ser MÁS LARGO que el refresco automático.
/// Si fuera igual, cada refresco llegaría justo cuando el fallo caduca.
/// </summary>
```

Se usan `<c>`, `<b>`, `<see cref="..."/>`, `<paramref name="..."/>`, `<list>`
y `<item>`. Y a veces **markdown dentro del `<summary>`**, para destacar:
`**vuelve a salir gris sin que nada avise**`.

En el cuerpo, comentarios largos con guiones, alineados, y el patrón es
**afirmar la regla y dar la medición o el fallo que la motivó**:

```csharp
// El plazo del fallo tiene que ser MÁS LARGO que el refresco automático.
// Si fuera igual, cada refresco llegaría justo cuando el fallo caduca y
// volvería a subir la escalera entera, y no se arreglaría nada. Quince
// minutos es más que los cinco del refresco.
```

Y para marcar algo que no es negociable, en mayúsculas: `OJO`, `SIEMPRE`,
`NUNCA`, `REGRESIÓN`.

Separadores de sección dentro de una clase:

```csharp
// ------------------------------------------------------------------
// El candado
// ------------------------------------------------------------------
```

En `.csproj` y en `.razor`, los bloques largos van entre `====` o `@* ... *@`.

## Nombres

| Elemento | Convención | Ejemplos |
|---|---|---|
| Ficheros | PascalCase | `CacheMercadoPublico.cs`, `SemanasDelMesTests.cs` |
| Tipos | PascalCase | `public sealed class`, `public static class` |
| Métodos, **privados incluidos** | PascalCase | `private static string ClaveDia(...)` |
| Campos privados | camelCase, sin `_` | `porDia`, `candado`, `caducidad` |
| Constantes | PascalCase | `private const int IntentosPorDia = 6;` |
| Locales y parámetros | camelCase | `fecha, dia, proveedor` |
| **Métodos de test** | **frase en `snake_case`** | `Cada_semana_va_de_lunes_a_domingo_y_recortada_al_mes` |

Namespace *file-scoped*, siempre. `using Xunit;` siempre el último y explícito.
Orden: `System.*` → `Microsoft.*` → proyecto → `Xunit`. Llaves en línea nueva.
Indentación de 4 espacios.

## Tests

xUnit v2. Nada de Moq, bUnit, FluentAssertions ni AutoFixture.

- `[Fact]` para un caso, `[Theory]` + `[InlineData]` para una tabla de valores.
- El nombre del test **es lo que documenta**, y va en frase.
- **Sin red y sin ticket.** Para probar el cliente HTTP se escribe un
  `HttpMessageHandler` falso en el propio fichero, con una `BaseAddress` de
  `https://ejemplo.invalid/`.
- Los dobles van como `private sealed class` **al final del fichero**.
- Helpers locales en `PascalCase` y estáticos: `NuevaCache()`, `Local(...)`,
  `FechaDe(url)`.
- Fechas fijas con constantes con nombre (`Anio = 2026`, `Mes = 10`), nunca
  `DateTime.Today` salvo que la prueba sea **relativa** a hoy.
- Si el test cambia `CultureInfo.CurrentCulture`, lo **restaura en `finally`**.
- Nada de `Skip`, `Trait` ni colecciones: el comportamiento por defecto de xUnit
  (clases en paralelo) es el que se quiere.

Al añadir un test sobre una función pura: **va junto a la función, no en el
proyecto de tests.** Ese es el motivo de que `SemanasDelMes`,
`TextosDeFecha` y `CalendarioDelMes` sean `internal` y de que el servidor tenga
`InternalsVisibleTo` para su proyecto de pruebas.

## Ficheros que no se versionan

- `appsettings.Development.json` — lleva el **ticket**.
- `secrets/*` **menos** `*.ejemplo` — la plantilla se versiona, el ticket no.
- `publicacion*/` y `bin/` y `obj/`

El ticket es una credencial personal de Mercado Público. No va en el código, ni
en un script, ni en un mensaje de commit, ni en la salida de una prueba.

**OJO con `secrets/` a secas.** Con ese patrón el `.gitignore` se come también la
plantilla, y entonces el comando que la propia aplicación imprime en el aviso de
arranque —`Copy-Item secrets\appsettings.Development.json.ejemplo …`— falla con
"No se encuentra la ruta de acceso", porque el fichero nunca se sube. Pasó de
verdad. Va `secrets/*` y luego `!secrets/*.ejemplo`.

## `.gitignore`

Cada patrón lleva un comentario explicando **por qué** está. Si añades uno,
explícalo también. Y fíjate en el ancla `^`: sin ella, `bin/` solo ignoraba la
carpeta de la raíz y `sql\clr\bin` se colaba con el `.dll` dentro.

## Mensajes de commit

Van en español y en **minúsculas**, sin punto final. Y explican **la decisión y
el porqué**, no la lista de ficheros. No es un changelog: es el razonamiento que
hace falta dentro de seis meses.

El estilo del repositorio es el "por qué falló y qué se cambió":

> Un bug que solo aparecio al ejecutar, no al leer: la API contesta HTTP 203 a
> un ticket que no vale, no un 401. Es un 2xx, asi que se tomaba por una consulta
> buena. La prueba tardaba 185 segundos; ahora tarda 4.

Y los commits mezclan tamaño a propósito: un commit con el esquema, otro con el
importador y sus pruebas. No un commit "todo lo de SQL".

## README

Es largo y **no tiene tabla de contenidos**: los enlaces internos van sueltos en
el texto. Las secciones que valen la pena tienen un `⚠️` delante cuando es una
trampa.

Si cambias algo que el README describe (número de tests, una propiedad, un
nombre de fichero), **actualiza el README en el mismo commit**. Y mira esto:
ahora mismo hay tres cifras distintas de tests ("32", "134 tests", "160
pruebas") y el número real es **167**. Conviene arreglarlo.
