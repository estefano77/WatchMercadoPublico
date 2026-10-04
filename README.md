# WatchMercadoPublico

Aplicación Blazor WebAssembly + ASP.NET Core que muestra **las licitaciones
publicadas HOY en Mercado Público para una empresa fija**, y nada más.

La empresa vigilada (nombre, RUT y código de proveedor) se lee de
`appsettings`, no de la pantalla. El día tampoco se elige: es siempre el de hoy.

- **Cliente:** Blazor WebAssembly (.NET 10)
- **Servidor:** ASP.NET Core 10, minimal API, sin base de datos
- **Estilos:** Tailwind CSS 4 (modo claro y oscuro)

> **El problema que resuelve.** Mercado Público rechaza las primeras ~10-15
> peticiones de cada sesión con `500` y `429`. Una versión anterior de esta
> aplicación preguntaba un día que elegía el usuario y, cuando la API fallaba,
> llegaba a mostrar "Sin licitaciones en la semana" sin haber podido preguntar
> nada. Ahora la consulta es automática y al cargar la página, así que el
> servidor **insiste hasta un minuto** antes de renderse; y si tampoco así, lo
> dice y reintenta solo.

---

## Qué hace

Una pantalla, tres estados:

| Estado | Qué muestra |
|---|---|
| **Hay novedades** | Las licitaciones de hoy, ordenadas: primero lo que sigue vigente. Al hacer clic se abre el detalle. |
| **No hay nada** | "Nada nuevo hoy". Si hoy es sábado o domingo, lo dice, porque es lo más probable y evita que parezca un fallo. |
| **No se pudo consultar** | Un aviso explícito con botón de reintento. **Nunca** dice "no hay licitaciones" cuando lo que pasó es que no se pudo preguntar. |

La página se refresca sola cada 5 minutos, y avisa de cuándo se consultó.

### Lo que la pantalla NO dice, y aquí está

Estas cosas son de funcionamiento, no de uso. No aparecen en la interfaz porque
restan, y el usuario no puede cambiarlas: están aquí, que es de donde se sacará
el manual.

| Comportamiento | Dónde se ajusta |
|---|---|
| La pantalla consulta sola al abrir y luego cada 5 minutos | `MercadoPublico:MinutosEntreRefrescos` |
| Ante un fallo, insiste 6 veces con esperas de 2, 4, 8, 16, 30 y 30 s (~90 s) | `IntentosPorDia` y `EsperaInicial` en `LicitacionesEndpoints` |
| Los datos de hoy se guardan 4 minutos en memoria | `MercadoPublico:MinutosDeCache` |
| No se consulta nada más que el día actual | No es configurable: es el diseño |
| La empresa vigilada es fija | `NombreEmpresa`, `RutEmpresa`, `CodigoProveedor` |

Lo único que la pantalla sí menciona es el intervalo de reintento **cuando hay
un error**, y el motivo es distinto: ahí le dice al usuario que no tiene que
hacer nada, no es información de funcionamiento.

---

## Por qué hay un servidor si la aplicación es WebAssembly

Porque **el ticket de Mercado Público es una credencial**.

Blazor WebAssembly se descarga entero en el navegador: el `.dll` va al cliente y
cualquiera lo puede abrir. Un ticket metido en la configuración del cliente es un
ticket público —y el tope son **10.000 consultas al día por persona**.

Por eso el ticket vive **solo en la configuración del servidor**, y el navegador
habla con `/api/*` de nuestra aplicación, nunca con Mercado Público directamente.

---

## Puesta en marcha

### 1. Requisitos

- .NET SDK 10
- Node.js 20 o superior (solo para compilar el CSS)

### 2. Tailwind

```powershell
npm install
npm run css
```

`app.css` se versiona en el repositorio, así que esto solo hace falta si tocas
`Styles/tailwind.css` o algún componente.

### 3. El ticket

Se pide gratis en <https://api.mercadopublico.cl/modules/IniciarSesion.aspx>
(Clave Única; llega al correo). Es uno por persona y no se puede transferir.

Copia la plantilla y pega tu ticket:

```powershell
Copy-Item secrets\appsettings.Development.json.ejemplo `
        src\WatchMercadoPublico.Server\appsettings.Development.json
```

```jsonc
"MercadoPublico": {
  "NombreEmpresa": "TU EMPRESA",
  "RutEmpresa": "00.000.000-0",
  "CodigoProveedor": "00000",
  "Ticket": "00000000-0000-0000-0000-000000000000"   // <- pon el tuyo, es un UUID
}
```

El `CodigoProveedor` es lo único imprescindible: sin él la aplicación arranca
pero no hay a quién consultar. Los otros dos son solo para la cabecera.

El fichero queda fuera del repositorio (está en `.gitignore`) y **no se publica**
(`CopyToPublishDirectory="Never"`).

### 4. Arrancar

```powershell
dotnet run --project src\WatchMercadoPublico.Server
```

Abre <http://localhost:5xxx> y escribe el RUT de la empresa.

### 5. Tests

```powershell
dotnet test
```

32 tests, **sin red y sin ticket**: no tocan la API, comprueban funciones puras.
Ver [Tests](#tests) para qué hay que leerlos antes de tocar nada.

---

## Probarlo sin ticket: modo demo

Para ver la interfaz antes de tener el ticket (o para probar sin gastar el cupo):

```jsonc
"MercadoPublico": { "ModoConsulta": "demo" }
```

O sin tocar ficheros, con una variable de entorno:

```powershell
$env:MercadoPublico__ModoConsulta = "demo"
dotnet run --project src\WatchMercadoPublico.Server
```

Devuelve **datos inventados** (47 licitaciones falsas, semilla fija) y un aviso
naranja lo dice en pantalla, porque confundirlo con datos reales sería un fallo
grave. El RUT de ejemplo es `76.123.456-0`; la pantalla lo muestra y lo rellena
al pulsarlo.

**En producción tiene que estar en `v1`.**

---

## Configuración

Sección `MercadoPublico`:

| Clave | Por defecto | Qué hace |
|---|---|---|
| `Ticket` | *(vacío)* | Credencial personal. Vacío = la API no responde. |
| `ModoConsulta` | `v1` | `v1` (diario), `c2` (Compra Ágil) o `demo` (ficticio). |
| `DiasPorDefecto` | `7` | Días hacia atrás que se barren al abrir el listado. |
| `DiasMaximos` | `30` | Tope duro. El servidor no deja pedir más, aunque se manipule la petición. |
| `MinutosDeCache` | `15` | Vida de la caché en memoria de un barrido. |
| `SegundosTimeout` | `30` | Espera máxima a Mercado Público. |

En producción, el ticket va por **variable de entorno**, nunca en un fichero:

```
MercadoPublico__Ticket = "tu-ticket"
```

---

## Cómo se consulta la API

Tres endpoints, en el orden en que se usan.

> **Los datos de los ejemplos de esta sección son inventados.** El RUT
> `99.999.999-9` y el nombre `EMPRESA DE EJEMPLO SPA` no corresponden a nadie:
> el RUT es inválido a propósito, porque un ejemplo con el RUT real de la
> empresa convertiría el repositorio en un registro de datos identificables, y
> eso no tiene ninguna ventaja para quien lee. Los que sí son reales, porque
> describen el comportamiento de la API y sin ellos las tablas no se
> entienden, son los **códigos** y los **recuentos**.

### 1) RUT → código de proveedor

```
GET https://api.mercadopublico.cl/servicios/v1/Publico/Empresas/BuscarProveedor
    ?rutempresaproveedor=99.999.999-9&ticket=...
```

Respuesta real:

```json
{
  "Cantidad": 1,
  "listaEmpresas": [
    { "CodigoEmpresa": "71284",
      "NombreEmpresa": "EMPRESA DE EJEMPLO SPA" }
  ]
}
```

Dos cosas que no son las que dice la documentación, descubiertas contra la API:

- La lista se llama **`listaEmpresas`**, no `Listado` como en los demás endpoints.
- **El RUT tiene que ir con puntos, guion y dígito verificador.** Sin ellos la
  API devuelve cero resultados y no avisa: parece un fallo de la plataforma
  cuando en realidad es el RUT.

Por eso la aplicación normaliza (`70017820k` → `70.017.820-k`) y **valida el
dígito verificador (módulo 11) antes de gastar una consulta**.

### 2) Licitaciones de UN D

```
GET https://api.mercadopublico.cl/servicios/v1/publico/licitaciones.json
    ?fecha=DDMMAAAA&CodigoProveedor=71284&ticket=...
```

Cada registro trae **solo cuatro campos**:

```json
{ "CodigoExterno": "1456839-6-LP26",
  "Nombre": "SERVICIO DE ARRIENDO Y MANTENCIÓN DE SOFTWARE...",
  "CodigoEstado": 8,
  "FechaCierre": "2026-09-21T15:30:00" }
```

### ⚠️ `fecha` con los DOS campos rellenos, o no funciona

La API acepta **un solo formato**: `DDMMAAAA`, con el día y el mes siempre a dos
dígitos. Se comprobaron todos:

| `fecha=` | Resultado |
|---|---|
| `04102026` | **HTTP 200** |
| `4102026` | 500 `{"Codigo":10300,"Mensaje":"El formato del parametro fechas es incorrecto"}` |
| `2026-10-04` | 500, mismo mensaje |
| `04-10-2026` | 500, mismo mensaje |
| `20261004` | 500 `{"Codigo":...,"Mensaje":"Mes inválido, el Mes debe ser entre 1 y 12."}` |

En .NET el día con relleno es **`dd`**, no `d`. Con `dMMyyyy` el 4 de octubre se
mandaba `4102026`, de siete dígitos, y la API respondía 500 siempre. Como `d` y
`dd` solo se diferencian **en los días 1 a 9**, a partir del 10 el día ya tiene
dos cifras y el fallo desaparece solo: por eso el bug se escondía detrás del
calendario y solo se rompía en la primera quincena de cada mes. La fecha de
prueba (28/09/2026) era día 28, así que nunca lo dejó ver.

**Verificado con un barrido real de los 30 días de septiembre de 2026**, en
memoria, secuencial y con 4 s entre peticiones:

- **30 de 30** días responden `200`.
- **9 de 9** días del 1 al 9 fallaban con el formato viejo, y funcionan con el
  nuevo. El contraste está medido, no deducido.
- El día 28 devuelve la licitación `1456839-6-LP26`; los otros 29, ninguna.
- **Ningún 429** con ese ritmo. En paralelo sí los hay.

Que 29 de 30 días salgan a cero no significa que la consulta esté rota: con el
mismo día y **sin** filtro de proveedor la API devuelve 733 (día 15) y 1295
(día 28) licitaciones de todo el país. Un código inexistente devuelve 0 igual
que SMC, así que el filtro sigue distinguiendo. Para distinguir "no hay nada"
de "no se pudo preguntar" está el `502` explícito del endpoint.

- El código se llama **`CodigoExterno`**, no `CodigoLicitacion`.
- **`CodigoProveedor` solo filtra si mandas `fecha`.** Es la diferencia entre
  que esto sirva o no, y se comprobó de las dos formas:

| Consulta | Resultado |
|---|---|
| `?fecha=28092026&CodigoProveedor=71284` (SMC) | **1** |
| `?fecha=28092026&CodigoProveedor=999999` (inexistente) | 0 |
| `?estado=activas&CodigoProveedor=71284` | 4.631 |
| `?estado=activas&CodigoProveedor=999999` | **4.631** ← ignora el filtro |

Con `estado=activas` el parámetro se **ignora** y sale el total del país (~4.600
al día). Por eso la pantalla pide siempre un `fecha`: es el único filtro que la API respeta.

### 3) Detalle de una licitación

```
GET https://api.mercadopublico.cl/servicios/v1/publico/licitaciones.json
    ?codigo=1456839-6-LP26&ticket=...
```

Trae mucho más que el listado: organismo comprador con su RUT, todas las fechas
del proceso, adjudicación con número de oferentes, enlace al acta y **los ítems
adjudicados**. Ejemplo real:

```
estado     Adjudicada   tipo LP
organismo  SERVICIO LOCAL DE EDUCACIÓN PÚBLICA LOS COPIHUES
rut        61.981.420-7   región Región del Biobío
estimado   192.000.000 CLP      <- lo que se presupuestó
cerrado    147.432.000 CLP      <- lo que se adjudicó (1 × 147.432.000)
cierre     21 sept 2026 12:30      adjudicación 27 sept 2026
oferentes  4     acta N° 143
ítem       Software del sistema de administración de bases de datos
           a EMPRESA DE EJEMPLO SPA (99.999.999-9)
```

### El monto que se muestra es el adjudicado, no el estimado

Son **dos campos distintos y en sitios distintos**:

| Campo | Dónde | Qué es |
|---|---|---|
| `MontoEstimado` | raíz del registro | Lo que se presupuestó y se licitó |
| `MontoUnitario` | `Items.Listado[].Adjudicacion.MontoUnitario` | Lo que se contrató por unidad |

El `Adjudicacion` de **primer nivel** es el ACTA (fecha, número, oferentes,
enlace) y **no lleva ningún monto**. El unitario está dentro del `Adjudicacion`
de cada ítem, y solo existe si la licitación está adjudicada: si está publicada,
desierta o revocada, `Items` viene vacío y solo se muestra el estimado.

En la pantalla el adjudicado va como cifra grande y el estimado debajo como
referencia, con la diferencia en porcentaje. Con un solo ítem se muestra como
tarjeta; con varios, como tabla con una fila por producto y su total.

### ⚠️ Los montos son NÚMEROS JSON, no strings

Vienen como `"MontoEstimado": 192000000.0`, con **punto** decimal. No son
"192.000.000,00" en formato chileno.

Por eso `LeerDecimal` los lee con `GetDecimal()` cuando son
`JsonValueKind.Number`. La versión anterior pasaba todo por texto y quitaba los
puntos "porque en Chile son separador de miles", con lo que `192000000.0` se
convertía en `1920000000`: **el precio salía diez veces más alto** y
`"Cantidad": 1.0` se leía como 10. Solo tiene sentido normalizar cuando el
valor viene como string, y ahí se prueban las dos convenciones antes de quitar
nada.

Es **una petición por licitación**, así que se pide al abrir la ficha y se
cachea en el servidor. Pedirlos todos al cargar la lista gastaría el cupo del
ticket sin necesidad.

### ⚠️ La API falla, y hay que decirlo

**Corrección importante.** Este apartado decía antes, como si estuviera medido,
que "las primeras peticiones de cada sesión fallan con `500` y `429` sea cual sea
la espera". **No era verdad.** El `500` constante era **nuestro**: la fecha
mandada sin el cero del día. Corregido eso, la consulta responde `200` a la
primera, en ~450 ms.

Lo que **sí** es real es el `429`: se reproduce haciendo dos peticiones seguidas,
incluso con `curl`, y con `User-Agent` de navegador. El ritmo importa de verdad.

Los reintentos se quedan —son baratos y el `429` existe— pero el motivo real es
el que acaba con el `Codigo 10300`. Insistir un minuto ante un error de formato
es tiempo perdido: seis intentos idénticos siempre dan el mismo resultado.

El servidor hace **6 intentos con esperas de 2, 4, 8, 16, 30 y 30 segundos**
—unos 90 s en el peor caso— antes de rendirse. La consulta es automática al
cargar la página, así que **nadie está esperando**: insistir un minuto no le
cuesta nada.

Y si aun así se agotan los intentos, se responde con un `502` y un mensaje que
dice explícitamente que no se pudo comprobar. **Nunca** se devuelve una lista
vacía por no haber preguntado: afirmar "no hay licitaciones" cuando no se sabe
es peor que mostrar un error.

**El `500` de esta API casi nunca significa que la API esté caída.** Suele ser
quejarse de lo que le hemos mandado, y lo dice: `{"Codigo":10300,"Mensaje":"El
formato del parametro fechas es incorrecto"}`. Como `ConstruirError` solo miraba
`error.message`, `Descripcion` y `Message` — y la API escribe **`Mensaje`** en
español — el mensaje se perdía, el log decía `(null)` y la pantalla culpaba a
Mercado Público de estar "con problemas". Un error que sabe decir qué le molesta
tiene que leerse; si no, se depura la mitad equivocada durante horas. Ahora se
lee `Mensaje` y `Codigo`, y un `500` con explicación dice *"Mercado Público
rechazó la consulta: …"* en vez de *"está con problemas"*.

Tres mensajes distintos, según lo que pasó:

| Situación | Lo que dice |
|---|---|
| Se consultó el día y hay licitaciones | Las lista, con el detalle al hacer clic |
| Se consultó y salió vacío | "Nada nuevo hoy", o "… es fin de semana" si es sábado o domingo |
| **No se pudo consultar** | "No se pudo consultar", con botón de reintento, y se aclara que **no** significa que no haya nada |

La pantalla se refresca sola cada `MinutosEntreRefrescos` (5 por defecto), así
que un fallo puntual se recupera sin que nadie pulse nada.

### El refresco automático no puede quedar huérfano

El temporizador se para en `Dispose`. Un bucle con `await Task.Delay` seguiría
consultando en el navegador con la pantalla cerrada, gastando el cupo del
ticket sin que nadie mire nada. Con `AutoReset = false` y rearmado al final, si
el servidor tardó un minuto con sus reintentos el siguiente disparo se cuenta
desde que terminó, y no se solapan peticiones.

La caché dura **menos** que el intervalo de refresco (4 min frente a 5). Si
durara más, dos de cada tres refrescos no preguntarían nada a la API y no
aparecería ninguna novedad: el refresco automático sería decorativo.

## API propia

| Método | Ruta | Qué hace |
|---|---|---|
| `GET` | `/api/estado` | Empresa configurada, si hay ticket, día actual, y **qué se puede elegir** en los tres desplegables: años, nombres de mes y cuántas semanas tiene el mes en curso. |
| `GET` | `/api/semana?anio=&mes=&semana=&refrescar=` | Licitaciones de **una semana**: sus días hábiles y los días que no se pudieron consultar. |
| `GET` | `/api/licitaciones/{codigo}` | Detalle de una licitación. `desdeCache` indica si gastó consulta. |
| `POST` | `/api/refrescar` | Vacía la caché del proveedor para forzar consulta nueva. |

`anio`, `mes` y `semana` van **explícitamente** como `[FromQuery]`. Son tipos
simples, y sin el atributo el enlazador de minimal API los busca en la **ruta**,
que no los tiene: responde `400` con el **cuerpo vacío**, un error que no dice
nada de qué se Quejó.

`refrescar=true` solo hace que se vuelva a preguntar el **día de hoy**. Los días
pasados no se repreguntan porque no cambian, y repreguntarlos gastaría cupo para
devolver justo lo mismo.

Ya no existe `/api/hoy` (lo sustituyó `/api/semana`), ni `/api/proveedor`, ni
`/api/calendario`, ni los parámetros `q`, `pagina` y `porPagina`.

### La semana es la unidad, no el año

Los tres filtros van **concatenados**: lo que se consulta es **una semana**, no
un mes ni un año. No es una decisión de interfaz, es una consecuencia medida de
la API:

| Selección | Peticiones | Tiempo medido |
|---|---|---|
| Semana (5 días hábiles) | 5 | **~7 s** la primera vez, ~50 ms después |
| Mes (22 días hábiles) | 22 | 4-5 min |
| Año (261 días hábiles) | **261** | **50-55 min** |

La API devuelve **un día por consulta**: no hay forma de preguntar "el mes de
marzo" en una llamada. Encadenando los tres filtros, el rango máximo son cinco
peticiones.

### Días futuros: no se consultan, y no son un fallo

Una semana que aún no ha terminado tiene días que no han llegado. La API no
tiene nada que devolver de ellos, así que responde `500` y el cliente insistiría
seis veces con esperas de 2 a 30 s. Una semana que empieza el día 4 del mes
tenía **cuatro minutos y medio** de espera por tres días que no existen.

Ahora los días posteriores a hoy **no se consultan** y se cuentan aparte, en
`diasPendientes`. No van a `diasSinRespuesta` porque eso sería mentir: no falló
nada, es que el día todavía no ha llegado. La respuesta trae las dos cuentas:

```json
{ "diasHabiles": 5, "diasConsultados": 2, "diasPendientes": 3, "diasFallidos": 0 }
```

### Un día que falla no tira la semana

`ConsultarDiaAsync` devuelve `null` si agota los intentos, en vez de lanzar. El
endpoint sigue con los demás días y anota el que falló en `diasSinRespuesta`.
Antes un solo fallo dejaba la semana entera en error y se perdían los otros
cuatro días, que sí se habían podido consultar.

Solo se responde `502` cuando **ningún** día pudo consultarse. Es el único caso
en que la pantalla no tiene nada que enseñar y no sería honesto devolver una
lista vacía.

`/api/...` devuelve **404 en JSON** para rutas desconocidas, antes del
`MapFallbackToFile`, o la API devolvería el `index.html` de la SPA.

---

## Publicación

```powershell
dotnet publish src\WatchMercadoPublico.Server -c Release -o publicacion
```

Verificado en esta máquina: **10,3 MB**, `_framework` con 54 ficheros y **ningún
`.br` ni `.gz`**.

### No toques el target `SuperponerClienteBlazor`

Está en el `.csproj` del servidor y **no se puede quitar**. Al publicar, el SDK
copia el `index.html` **original** del cliente, con el token `#[.{fingerprint}]`
sin sustituir, junto a los ficheros de `_framework` con huella. El navegador
pide `_framework/blazor.webassembly`, recibe un 404 y la aplicación **se queda en
"Cargando"**. El target publica el cliente aparte y superpone esa `wwwroot`.

Para regenerar el `index.html` a mano hay que publicar primero el cliente.

### Antes de subir nada, comprueba

```powershell
# 1. El importmap del index.html PUBLICADO tiene contenido
#    (en desarrollo el de disco está vacío a propósito)
Select-String -Path publicacion\wwwroot\index.html -Pattern '"imports"'

# 2. Sin variantes comprimidas
(Get-ChildItem publicacion\wwwroot\_framework -Include *.br,*.gz -Recurse).Count

# 3. El ticket NO viaja en la publicación
Test-Path publicacion\appsettings.Development.json   # debe ser False
```

### En IIS

`web.config` incluido. Hace falta el **Hosting Bundle** de .NET, no solo el
runtime. El ticket va en las variables de entorno de `web.config`
(`MercadoPublico__Ticket`), no en `appsettings.json`.

Al actualizar el sitio: **detener la aplicación → subir → extraer → arrancar**.
Extraer con el proceso en marcha falla con *"The process cannot access the
file"*.

---

## Detalles que no son evidentes

### Solo `_framework/` puede ser `immutable`

El middleware de `Program.cs` reparte tres `Cache-Control`:

| Ruta | Valor | Por qué |
|---|---|---|
| Navegación y `.html` | `no-store` | Un `index.html` viejo hace pedir a Blazor módulos con huellas que ya no existen |
| `/_framework/…` | `max-age=31536000, immutable` | **Llevan la huella en el nombre** (`blazor.webassembly.w3qd1tpl0e.js`); un despliegue nuevo cambia el nombre |
| Todo lo demás con extensión | `max-age=0, must-revalidate` | `css/app.css` **no lleva huella**: revalida por ETag y responde 304 si no cambió |
| Error (>= 400) | `no-store` | Un 404 cacheado durante un despliegue a medias contaminaba al visitante |

La regla anterior era "cualquier ruta con extensión → un año e immutable". Con
`css/app.css` servido así, **un cambio de estilos llegaba a los usuarios un año
después**: durante el arreglo de los `<select>` en modo oscuro, el navegador
siguió sirviendo el CSS viejo aunque el archivo en disco ya estaba corregido.

**Si añades un asset propio, no lo marques `immutable`.** O le pones huella en el
nombre, o que revalide.

### Los `<option>` de un `<select>` no los dibuja la página

El desplegable de un `<select>` lo pinta el sistema, no el CSS de la página. Con
solo `color-scheme`, el navegador les da un **fondo gris por defecto** y el texto
del tema encima: en modo oscuro el desplegable salía claro con la letra blanca
encima, ilegible.

Hay que ponerles fondo y color explícitos en `tailwind.css`:

```css
select option        { background-color: var(--app-surface); color: var(--app-ink); }
select option:checked { background-color: var(--app-marca-b); color: var(--app-marca-ink); }
```

Se usa `--app-surface` y no `--app-field` porque **`--app-field` es translúcido**
(`rgb(255 255 255 / 0.05)` en oscuro) y, sobre el fondo del popup —que es del
sistema y no se controla—, el resultado sería impredecible. `--app-surface` es
opaco en los dos modos.

Para verlo sin desplegar el popup del sistema, clona el `<select>` y ponle
`size="5"`: las opciones se pintan en la página y ya se ven con sus colores.

### Un `@*` Razor dentro de una etiqueta se come el elemento

Un comentario Razor **entre los atributos** de un elemento no se cierra en el
`>` del tag: Razor lo lee hasta el siguiente `/>`. El resultado compila sin
errores, pero el elemento desaparece de la página:

```razor
<input id="buscar" type="search"
       aria-describedby="ayuda"
       @* min-h-[46px] para igualar alturas *@     <-- se traga el "/>" del input
       class="..." />
```

El `<input>` y el `@if` de al lado desaparecieron, y la página se quedó sin
campo de búsqueda sin ningún error que lo anunciara. **Los comentarios van fuera
de la etiqueta.**

### `Configure<T>()` no registra la clase concreta

En un minimal API, un parámetro de tipo `MercadoPublicoOpciones` **no se reconoce
como servicio**: se infiere como cuerpo de la petición y la ruta peta al arrancar
con `Body was inferred but the method does not allow inferred body parameters`.
Hay que inyectar `IOptions<MercadoPublicoOpciones>`.

### Un servicio sin registrar tumba TODAS las rutas

`MercadoPublicoCliente` estaba bien diseñado pero **faltaba
`AddScoped<MercadoPublicoCliente>()`**. El error que salía no mencionaba la
llamada que faltaba, sino el parámetro, y además tumbaba también la SPA. Si *todo*
devuelve 500 a la vez, mira `Program.cs` antes que los endpoints.

### El aviso que no se veía

`Aviso.razor` comprobaba solo `Mensaje is not null`. Usado como contenedor, con
`ChildContent` y sin `Mensaje`, **no pintaba nada** y parecía que la condición
que lo rodea estuviera mal. La guarda mira las dos cosas.

### ⚠️ Asignar un campo NO repinta: el "Consultando" no salía en un minuto

El más traicionero de todos, y eran **tres fallos encadenados** que se tapaban:

1. El bloque de resultados estaba condicionado a `Estado is { Servible: true }`.
   `Estado` es `null` hasta que responde `/api/estado`, y `null` **no** cumple esa
   condición: durante el arranque no se cumplía ninguna y la pantalla pintaba la
   cabecera, el título y a continuación un hueco vacío.
2. En Blazor, `Estado = estado;` **no repinta**. El siguiente repintado llega
   cuando termina el `await` siguiente, es decir, **90 s después**, ya con el
   error. Por eso nunca se veía el panel de cargando, ni el de error.
3. `Cargando` arrancaba en `false`, así que el primer render —con `Estado` a
   `null`— no tenía nada que enseñar.

Ahora hay tres piezas:

- La condición del bloque es `Estado is not { Servible: false }`, para que `null`
  caiga en el panel de cargando en vez de en ningún sitio.
- `StateHasChanged()` explícito tras asignar `Estado`, en los dos caminos.
- **`Ocupado`**, que **no** es lo mismo que `Cargando`:

  ```csharp
  private bool Ocupado => Cargando || (Estado is null && ErrorConsulta is null);
  ```

  Arranca en `Cargando`, o hay una consulta en vuelo, o aún no se sabe si la API
  está servible. Los dos casos son indistinguibles para quien mira, y ninguno
  puede enseñar un resultado.

⚠️ **No "arreglar" esto poniendo `Cargando = true` al inicializar.** Parece el
fix obvious y es un bug peor: la primera llamada a `CargarAsync` sale por su
guarda `if (Cargando) return;` y **la consulta inicial no llega a lanzarse**. El
panel se queda en "Consultando" para siempre. Son dos conceptos distintos y van
en campos distintos.

Además, como 90 s sin que se mueva nada parece una página colgada, hay un
`temporizador` de 1 s (`reloj`) que lleva la cuenta y solo se muestra a partir
del segundo 9 — en el caso rápido, que es el normal, sería ruido. Se para en el
`finally` de `CargarAsync` y en `Dispose`.

### Los grupos de miles se cuentan desde la derecha

`76123456` es `76.123.456`, no `761.234.56`. Agrupar de tres en tres desde la
izquierda produce lo segundo, y en los montos de una licitación un error de
agrupación cambia la cifra que se lee.

### `RemoveDiacritics()` no existe

No está disponible en este runtime, ni con `using System.Globalization` ni con
`using System.Text`. Queda una tabla explícita en `Formato.SinAcentos`, que
sigue usándose para resaltar las palabras dentro del nombre de una licitación.

### Los espacios entre elementos Razor se colapsan

`<span>· USD</span>` pegado a un texto anterior sale como `REFERENCIAL· USD`. El
espacio va **dentro** del elemento: `&nbsp;· USD`.

### Sin `removeDiacritics`, los `<h1>` no llevan indicador de foco

`<FocusOnNavigate Selector="h1">` de Blazor enfoca el título en cada
navegación; con `outline-offset`, el marco se dibuja como una caja suelta y
parece un elemento seleccionado. Se anula **solo** en los `h1`; botones y campos
lo conservan.

### Los campos de texto van a `text-base`

Con menos de 16 px, iOS hace zoom automático al escribir y la página se queda
desplazada. Las zonas táctiles son de 44 px (`min-h-11`).

---

### `CodigoProveedor` solo filtra si mandas `fecha`

Si la lista sale con miles de resultados que no son de la empresa, **no es un
fallo del filtro**: es que se está usando `estado=activas`, que ignora
`CodigoProveedor`. Comprobado:

```powershell
$t = "TU-TICKET"
$b = "https://api.mercadopublico.cl/servicios/v1/publico/licitaciones.json"

# estado=activas: el codigo NO filtra (ambos devuelven el total del pais)
(Invoke-WebRequest "$b`?estado=activas&CodigoProveedor=71284&ticket=$t").Content  # 4631
(Invoke-WebRequest "$b`?estado=activas&CodigoProveedor=999999&ticket=$t").Content # 4631

# con fecha: el codigo SI filtra (0 para uno inexistente)
(Invoke-WebRequest "$b`?fecha=28092026&CodigoProveedor=71284&ticket=$t").Content  # 1
(Invoke-WebRequest "$b`?fecha=28092026&CodigoProveedor=999999&ticket=$t").Content # 0
```

Regla: **para filtrar por empresa hay que mandar `fecha`**. Por eso el selector
es mes y ano, y no "activas".
### Un campo con otro nombre, y la lista entera a cero

`LeerTexto(item, "CodigoExterno", "CodigoLicitacion")` busca **alternativas**, no
una ruta. Si se implementa como recorrido anidado, busca `CodigoLicitacion`
*dentro de* `CodigoExterno`, no encuentra nada, devuelve `null`, y como la lista
se deduplicaba por ese campo, **todo se descartaba**: HTTP 200 y cero
licitaciones.

Lo mismo pasó con `listaEmpresas` (no `Listado`): el RUT de una empresa real
devolvía "no encontrada" sin error, un mensaje que encaja demasiado bien con un
fallo de la plataforma.

### Un día que falla tira el barrido entero

Con 7 días, casi seguro hay algún día con `500`. Si el error aborta el bucle, se
pierden los 6 días buenos y el usuario no puede ni reintentar. Cada día se aísla:
si falla, se avisa en el log y se sigue. Con `429` sí se corta, porque insistir
solo consigue más `429`.

### Peticiones en paralelo → `429`

`Task.WhenAll` con los 7 días dispara el límite de peticiones en milisegundos, y
un `429` no se cachea. Van **uno a uno, con ~800 ms de pausa**.

---

## Catálogo de fallos

Por **síntoma**, no por causa interna:

| Síntoma | Qué mirar |
|---|---|
| Tarda más de un minuto en una sola semana | Son 5 peticiones seguidas y cada una puede insistir un minuto. Si además tardó **cuatro minutos y medio**, se estaban consultando días futuros: comprueba `diasPendientes` en la respuesta. |
| La semana sale "incompleta" | `diasFallidos` > 0. Son días que se intentaron y no respondieron; `diasSinRespuesta` dice cuáles. Los días que **no han llegado** van aparte, en `diasPendientes`, y no son un fallo. |
| Los desplegables salen vacíos | `/api/estado` no llegó, o no devolvió `aniosDisponibles` y `mesesDisponibles`. |
| Al abrir la página, la cabecera y nada debajo | Estado muerto: `Semana` a null sin error ni consulta en vuelo. Hay una rama `else` que lo evita, así que si aparece, hay que mirar la consola del navegador. |
|---|---|
| Se queda en "Cargando" tras publicar | Falta el target `SuperponerClienteBlazor`, o el `index.html` publicado sin sustituir. |
| Cabecera y título, y debajo un hueco vacío durante un minuto | Estado muerto en el render: el panel no se pintaba porque `Estado` aún era `null` y `Estado is { Servible: true }` no se cumplía. Ver [el aviso que no se veía](#%EF%B8%8F-asignar-un-campo-no-repinta-el-consultando-no-sal%C3%ADa-en-un-minuto). |
| `404` en `/_framework/*.wasm` | Recursos de una build anterior, cacheados un año. `Ctrl+F5`. |
| `Failed to find a valid digest … 47DEQpj8HBSa+` | Variante comprimida vacía: se colaron `.br`/`.gz`. Revisa las cinco propiedades de `Directory.Build.props`. |
| La pantalla dice "No se pudo consultar" | Mercado Público está rechazando. Se intentó 6 veces durante un minuto y no respondió. **No** significa que no haya nada hoy. Se reintenta solo cada 5 min. |
| Los 6 intentos salen con `500` y `Codigo 10300` | Casi siempre es **nuestro**, no de la API: se queja de lo que le mandamos. El mensaje concreto ahora llega a pantalla y al log, así que léelo antes de culpar a Mercado Público. |
| Falla solo entre el día 1 y el 9 de cada mes | El día sin relleno a la izquierda en la fecha: `dMMyyyy` en vez de `ddMMyyyy`. Ver [`fecha` con los DOS campos rellenos](#%EF%B8%8F-fecha-con-los-dos-campos-rellenos-o-no-funciona). |
| La lista sale vacía | Consulta real y de verdad no hay nada publicado hoy. La pantalla lo dice como "Nada nuevo hoy", no como error. |
| `401` o `403` al consultar | El ticket caducó, se revocó o se agotó su cupo diario. |
| Aviso naranja en pantalla | Estás en `ModoConsulta: "demo"`: son datos inventados. |
| Aviso "la aplicación no está configurada" | Falta el `Ticket` o el `CodigoProveedor` en la sección `MercadoPublico` del **servidor**. |
| La cabecera sale como "Empresa sin configurar" | Falta `NombreEmpresa`. No impide consultar: el código es lo único imprescindible. |

---

## Marcas y propiedad intelectual

Hay **dos marcas** en pantalla, y conviene no confundirlas:

| Marca | De quién es | Dónde sale |
|---|---|---|
| La *M* en zigzag + `WatchMercadoPublico` | **Propia**, dibujada para este proyecto | Símbolo y nombre de la herramienta |
| El rótulo `SMC` | **De SMC**, la empresa para la que es la herramienta | Logotipo en la cabecera, a la derecha |

El logotipo de SMC se muestra porque la herramienta es **interna de SMC**: es una
marca que se usa con permiso y para identificar al propietario, no una marca
registrada de la aplicación. Por eso va **en el otro extremo** de la cabecera, con la
marca propia a la izquierda y el logotipo a la derecha, sin mezclarlos en un solo
rótulo. No se pone una línea divisoria entre ellos: con la barra a 1.152 px de
ancho quedan a cientos de píxeles el uno del otro y la línea quedaría flotando
en mitad del hueco.

Lo que **no** se hace, en ningún caso: reproducir, combinar ni fusionar el
logotipo de Mercado Público / ChileCompra con la marca propia. Es una marca
registrada de un tercero, y eso es infracción de marca, no solo de copyright.

### El archivo del logo, y por qué se pinta con máscara

`wwwroot/logo-smc.png` es la derivada de 300×72 del logo oficial que usa
`smc.cl` en su propia cabecera (`smc_b-300x72.png`, 2,8 KB). Se copia al
proyecto en vez de enlazarla para que la aplicación no dependa de que `smc.cl`
esté accesible: puede desplegarse en una red interna sin salida a internet.

Se prefirió la derivada y no el original de 2.297 px por dos razones medibles:
pesa 2,8 KB en vez de 16,8 KB, y tiene **128 niveles de alfa** en vez de 17, así
que el suavizado se ve mejor al tamaño al que se muestra.

Se pinta con `mask-image` y no como `<img>` porque el PNG es **blanco puro con
alfa variable**: el RGB es `#FFFFFF` en todos los píxeles visibles y solo cambia
la opacidad. Con el alfa como máscara, un solo fichero da los dos colores que
hacen falta —el azul de marca en claro y el blanco en oscuro— sin degradar la
imagen:

```css
.logo-smc          { background-color: var(--app-marca-a); }  /* #1d4ed8 */
.dark .logo-smc    { background-color: #fff; }
```

**El fallo silencioso a vigilar:** si la máscara no se aplicara, el elemento
saldría como un rectángulo de color sólido. Por eso el `<span>` lleva
`aria-label="SMC"`: si el logo se pierde, el nombre sigue leyiéndose.

Y la ruta del PNG tiene que ser **absoluta** (``/logo-smc.png``). El CSS se sirve
en ``/css/app.css``, y una ruta relativa se resuelve respecto a él, no al
documento: con ``url("logo-smc.png")`` pedía ``/css/logo-smc.png``, daba 404 y el
elemento se quedaba sin máscara. Pasó de verdad al montarlo, y por eso la
comprobación de cabecera mira el ``maskImage`` ya resuelto.

### Dónde aparece cada nombre

| Dónde | Qué sale | Por qué |
|---|---|---|
| Cabecera, izquierda | Símbolo propio + `WatchMercadoPublico` | Identidad de la herramienta |
| Cabecera, derecha | Logotipo de **SMC** | Identifica al propietario: herramienta interna suya |
| Encabezado de la página | "Novedades de hoy en **Mercado Público**" | Uso nominativo: describe la plataforma de la que salen los datos |
| Pie | "© WatchMercadoPublico" | Solo la autoría del producto |
| Pie | "Documentación de la API" → `api.mercadopublico.cl` | Enlace externo, con `rel="noopener noreferrer"` |
| Aviso de demo | "No se está llamando a **Mercado Público**" | Uso nominativo |
| Ficha de una licitación | Nombre del organismo y su RUT | Son los datos de la plataforma, no un atributo nuestro |

**ChileCompra no aparece por ninguna parte**, y de SMC solo el logotipo.
"WatchMercadoPublico" y el logo de SMC conviven en la cabecera sin mezclarse: están
en extremos opuestos de la barra, y además el logo es blanco plano sobre el fondo
mientras que el nombre propio va en degradado.

Lo que **conviene que se quede** es "Mercado Público" en el encabezado, en el
aviso de demo y en los enlaces a `mercadopublico.cl`: sin eso, un aviso de error
de la plataforma se confundiría con un fallo propio de la herramienta.

Iconos de [Lucide](https://lucide.dev) (licencia ISC). Tipografía
*Plus Jakarta Sans* (SIL Open Font License).

---

## Tests

```powershell
dotnet test
```

**No hay que tener ticket, ni red, ni la API en pie.** Eso no es una comodidad:
es lo que hace posible testear. Un método que hace una petición no se puede
comprobar sin pedirla, y para la aplicación casi todo lo que se rompió no
necesitaba la API para estar mal.

Por eso `ConstruirUrlDia` y `ConstruirError` están extraídos del cliente como
funciones estáticas, aunque solo los use un sitio: es el requisito para poder
mirarlos desde un test. Para exponerlos hay un `InternalsVisibleTo` en el
`.csproj` del servidor, y nada más.

### Qué cubren, y por qué existen

Los dos nacieron de bugs que estuvieron en producción:

| Archivo | Qué ata |
|---|---|
| `FormatoDeFechaTests.cs` | La fecha va en `DDMMAAAA` con los dos campos rellenos, los **730 días** de dos años, y no depende del calendario de la cultura del servidor |
| `ErroresDeApiTests.cs` | Que el mensaje de la API llegue a quien lee, y que un `500` no se traduzca en "la API está caída" cuando la API está diciendo otra cosa |

### La prueba de que un test sirve

Un test de regresión que nunca ha fallado no demuestra nada: puede estar
comprobando lo que siempre se comprobó. Los dos se validaron **reintroduciendo
el bug**:

| Con el bug reintroducido | Resultado |
|---|---|
| `ddMMyyyy` → `dMMyyyy` | **Falla** en los 9 días de la primera decena |
| Sin leer `Mensaje` | **Falla** en los 2 tests que dependen de ese mensaje |
| Restaurado | **32 de 32** |

Si alguna vez se toca `ConstruirUrlDia` o `ConstruirError`, ese truco es la
forma de saber si el test sigue vigilando algo.

### Detalle que sale de escribir el test

Al recorrer los 730 días apareció un riesgo que no buscaba: `{"ddMMyyyy"}` usa
el **calendario de la cultura actual**, y hay culturas que no es el gregoriano
(`ar-SA` usa el islámico, `th-TH` el budista). Con la cultura equivocada se
mandaría un año equivocado y no habría aviso: la fecha sería plausible. Por eso el
formato va con `CultureInfo.InvariantCulture` explícito, y hay un test por
cultura no gregoriana.

---

## Estructura

```
WatchMercadoPublico.slnx
├── Directory.Build.props          # compresión desactivada (los dos que se publican)
├── nuget.config
├── package.json                   # scripts de Tailwind
├── secrets/                       # plantilla del ticket (fuera del repo)
└── src
    ├── WatchMercadoPublico.Client # la SPA
    │   ├── Components/            # Icon, Marca, Aviso, TemaOscuro,
    │   │                          #   FichaLicitacion, ModalDetalle
    │   ├── Layout/                # MainLayout
    │   ├── Models/                # DTOs (mismos nombres que el JSON del servidor)
    │   ├── Pages/Home.razor       # la pantalla entera: hoy, y su refresco
    │   ├── Services/              # MercadoPublicoApi, Formato, ThemeService
    │   └── wwwroot/               # index.html, css/app.css, icono.svg, marca.svg
    ├── WatchMercadoPublico.Server # la API y el hosting de la SPA
    │   ├── Endpoints/             # LicitacionesEndpoints (/estado, /hoy, detalle)
    │   ├── Models/                # opciones y DTOs
    │   ├── Services/              # MercadoPublicoCliente, CacheMercadoPublico,
    │   │                          #   CalendarioDelMes (nombres de mes), DatosDemo
    │   └── web.config
    └── WatchMercadoPublico.Server.Tests # tests, sin red y sin ticket
```

`DatosDemo.cs` y el modo `demo` son **una ayuda de desarrollo**, no parte de la
aplicación.
