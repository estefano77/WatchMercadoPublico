---
name: Code-behind en Blazor
description: Usar al decidir si el bloque @code de un .razor se mueve a un archivo .razor.cs aparte, o al hacerlo. Cubre el umbral que decide, el namespace que debe coincidir, la base que hay que declarar, y por qué separar NO hace el componente más fácil de probar.
---

# Code-behind en Blazor

## Cuándo se mueve, y cuándo no

**El motivo por el que `FiltrosDeSemana` salió de `Home.razor` no es el motivo por
el que un `.razor` se separa.** Son dos cosas distintas y conviene no mezclarlas.

| Motivo | ¿Sirve separar en `.razor.cs`? |
|---|---|
| Leer el marcado sin saltar mil líneas | **Sí.** Es lo que arregla separar |
| Poder probar el código | **No.** Sigue sin cobertura |

## Por qué separar no lo hace más fácil de probar

El componente sigue siendo un componente: necesita el render de Blazor, y para eso
haría falta **bUnit**, que este repositorio no usa. Mover el código a un
`.razor.cs` no añade ninguna cobertura.

Donde sí se puede probar es en una **clase normal**, que es lo que se hizo con
`FiltrosDeSemana`, `SemanasDelMes`, `TextosDeFecha` y `CalendarioDelMes`: clases
propias, en su proyecto, con sus pruebas al lado. Eso sí escoverage.

**La regla práctica:** si el motivo es que algo no se puede probar, el destino es
una clase aparte, no un `.razor.cs`. Si el motivo es que el marcado queda
enterrado, el destino es un `.razor.cs`.

## El umbral

No todos los `.razor` se ganan un archivo. En este repositorio:

| Archivo | Líneas | De `@code` | Separado |
|---|---|---|---|
| `Pages/Home.razor` | 2.036 | 1.425 | Sí |
| `Components/ModalDetalle.razor` | 583 | 126 | Sí |
| `Components/Aviso.razor` | 97 | 56 | Sí |
| `Components/FichaLicitacion.razor` | 172 | 46 | Sí |
| `Components/Icon.razor` | 49 | 40 | Sí |
| `Components/LogoChileCompra.razor` | 68 | 15 | No |
| `Components/Marca.razor` | 77 | 12 | No |
| `Components/LogoSmc.razor` | 29 | 10 | No |
| `App.razor` | 36 | 7 | No |
| `Layout/MainLayout.razor` | 84 | 7 | No |
| `Components/ThemeToggle.razor` | 22 | 4 | No |

La regla: **a partir de unos 40 líneas de código y con el marcado ahogado debajo,
se separa.** Por debajo, dos archivos se abren peor que uno, y no se gana nada.

## Cómo se hace

1. El bloque `@code {` entero va al `.razor.cs`, dentro de
   `public partial class <Nombre>`.
2. El cuerpo **se mueve tal cual**. Sin reindentar, sin reordenar, sin tocar una
   línea. Comprobado después comparando el cuerpo movido con el que había: si no
   dan idénticos, algo se ha colado.
3. En el `.razor` se deja una nota de a dónde fue el código.

## Lo que no se mueve

`@page`, `@implements`, `@inject`, `@using`, `@inherits` y `@attribute` **se quedan
en el `.razor`**.

**`@implements` se queda por un motivo concreto:** declararlo también en el
`.razor.cs` da `CS0528`, "no se puede implementar `IAsyncDisposable` dos veces".
La clase que genera Razor ya lo implementa, y añadirlo en la otra mitad lo
declara otra vez.

## El namespace

`_Imports.razor` **no declara `@namespace`**, así que el namespace sale de la
carpeta. Si el `.razor.cs` no lo lleva **exacto**, las dos mitades no se unen y el
error es de clase duplicada, que no señala el namespace.

| Carpeta del `.razor` | Namespace del `.razor.cs` |
|---|---|
| `Client/` | `WatchMercadoPublico.Client` |
| `Client/Components/` | `WatchMercadoPublico.Client.Components` |
| `Client/Layout/` | `WatchMercadoPublico.Client.Layout` |
| `Client/Pages/` | `WatchMercadoPublico.Client.Pages` |

## La clase base

Cada `.razor` tiene su base, y hay que repetirla en el `.razor.cs`. **Si no, no
compila.**

| En el `.razor` | En el `.razor.cs` |
|---|---|
| (nada, lo normal) | `public partial class X : ComponentBase` |
| `@inherits LayoutComponentBase` | `public partial class X : LayoutComponentBase` |

`MainLayout.razor` es el caso: lleva `@inherits LayoutComponentBase`. Ponerle
`ComponentBase` lo deja de heredar de `LayoutComponentBase`, y el fallo aparece
como que faltan cosas del layout, muy lejos de la causa.

## Los `using`

El `.razor.cs` **no** hereda los `@using` de `_Imports.razor`. Son directivas del
compilador de Razor, no del proyecto, así que el archivo nuevo necesita los suyos.
Los que se piden de verdad, según el componente:

```csharp
using Microsoft.AspNetCore.Components;          // ComponentBase, ChangeEventArgs
using Microsoft.AspNetCore.Components.Forms;     // solo si hay selects o inputs
using Microsoft.AspNetCore.Components.Web;       // si hay eventos del DOM
using Microsoft.JSInterop;                       // si hay IJSRuntime o JSInvokable
using WatchMercadoPublico.Client.Models;         // DTOs
using WatchMercadoPublico.Client.Services;       // servicios
```

`System.*`, `System.Linq` y `System.Threading.Tasks` no hacen falta:
`ImplicitUsings` ya los pone.

## Después de mover

```powershell
dotnet build src\WatchMercadoPublico.Client\WatchMercadoPublico.Client.csproj -v q --nologo --no-incremental
dotnet test  src\WatchMercadoPublico.Client.Tests\WatchMercadoPublico.Client.Tests.csproj
```

El proyecto tiene que quedar con **0 advertencias**. Si aparece un `CS8600` o un
`CS8602` al mover, no es del movimiento: es una declaración que mentía sobre la
nulabilidad y que el compilador ya avisaba. Se arregla en el sitio, y merece la
pena mirar qué estaba ocultando.