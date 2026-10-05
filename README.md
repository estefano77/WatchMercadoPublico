# WatchMercadoPublico

Aplicación Blazor WebAssembly + ASP.NET Core que muestra **las licitaciones
publicadas en Mercado Público para una empresa fija**, dentro del periodo que
elija quien la usa, y nada más.

La empresa vigilada (nombre, RUT y código de proveedor) se lee de
`appsettings`, no de la pantalla. El periodo sí se elige: año, mes y semana, y
los tres van concatenados — lo que se consulta es **una semana**, no un mes ni un
año. Por defecto se abre la semana en curso y se carga sola; cualquier otro
periodo requiere pulsar "Actualizar".

- **Cliente:** Blazor WebAssembly (.NET 10)
- **Servidor:** ASP.NET Core 10, minimal API, sin base de datos
- **Estilos:** Tailwind CSS 4 (modo claro y oscuro)

> **El problema que resuelve.** Una versión anterior preguntaba un día que
> elegía el usuario y, cuando la API fallaba, llegaba a mostrar "Sin licitaciones
> en la semana" **sin haber podido preguntar nada**. Eso es lo que no puede
> pasar: si no se pudo comprobar, hay que decir que no se pudo comprobar.
>
> La solución no es "la API está caída y hay que esperar": es distinguir los tres
> casos —hay algo, no hay nada, no se pudo saber— y reintentar solo el tercero.
> El servidor insiste hasta un minuto, y si tampoco así lo dice y reintenta solo.
>
> **Lo que NO era el problema, y durante mucho tiempo se dio por hecho:** que
> Mercado Público rechazase las primeras ~10-15 peticiones de cada sesión. Es
> falso. Ese `500` constante era nuestro: la fecha iba sin el cero del día y la
> API respondía `{"Codigo":10300,"Mensaje":"El formato del parametro fechas es
> incorrecto"}`. Ver [`fecha` con los DOS campos rellenos](#%EF%B8%8F-fecha-con-los-dos-campos-rellenos-o-no-funciona).
>
> El `429` **sí** es real, y es lo único que justifica los reintentos: es un
> límite de ritmo, medido con dos peticiones seguidas.

---

## Qué hace

Una pantalla, cuatro estados:

| Estado | Qué muestra |
|---|---|
| **Hay novedades** | Las licitaciones de la semana, agrupadas por día de publicación. Al hacer clic se abre el detalle. |
| **No hay nada** | "Nada en la Semana 3 en Octubre de 2026". El nombre de la semana va porque el estado vacío no enseña ni el periodo ni la insignia, y sin él no se sabría a qué semana se refiere. |
| **No se pudo consultar** | Un aviso explícito con botón de reintento. **Nunca** dice "no hay licitaciones" cuando lo que pasó es que no se pudo preguntar. |
| **Los filtros no coinciden con la pantalla** | Al mover un desplegable sin pulsar "Actualizar": un aviso ámbar dice qué semana se está viendo y cuál se ha elegido, y el botón de la barra late. |

Ese último estado existe por una razón concreta. Los desplegables cambian al
instante y los datos no: se traen al pulsar "Actualizar". Sin un aviso, moverlos
no cambia nada de lo que hay en pantalla y el selector parece roto.

La página se refresca sola cada 5 minutos **solo cuando se está viendo la semana
en curso**, y avisa de cuándo se consultó. Una semana pasada no va a cambiar:
repreguntarla gastaría cuota para devolver exactamente lo mismo.

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

Hay un script, y es la vía normal:

```powershell
.\scripts\arrancar.ps1
```

Por defecto arranca en **modo demo**: datos inventados, sin gastar cupo del
ticket y sin depender de que la API de Mercado Público esté en pie. Para trabajar
en la interfaz es lo que hay que usar.

```powershell
.\scripts\arrancar.ps1 -Modo v1        # datos reales, consume cupo del ticket
.\scripts\arrancar.ps1 -SinCss         # no recompila Tailwind
.\scripts\arrancar.ps1 -Puerto 5080    # por defecto busca uno libre
```

Hace, en este orden: comprueba que estén `dotnet` y `npm`, compila el CSS,
detiene la instancia anterior si la hay, publica, arranca y **espera a que el
servidor responda** antes de imprimir la URL. Se cierra con el `Stop-Process -Id`
que imprime.

Tres cosas que el script hace y que conviene no perder si se toca:

- **Publica, no compila.** El target `SuperponerClienteBlazor` corre solo con
  `PublishDir` informado, y sin él la aplicación se queda en "Cargando".
- **Arranca con la ruta de publicación como directorio de trabajo.** Sin eso el
  proceso toma como raíz el directorio desde el que se lanzó, no encuentra el
  `wwwroot` y todo lo que no sea `/api` responde 404. El único síntoma es un 404
  en la raíz, que no lleva a ninguna parte.
- **No deja el ticket puesto en la sesión.** Pone las variables de entorno,
  arranca el proceso y las restaura. Si no, un `-Modo v1` dejaría la credencial
  en el entorno de la terminal.

El ticket **no está en el script**. En modo `v1` se lee de
`appsettings.Development.json` y se pasa por variable de entorno, porque ese
fichero no se publica a propósito.

### 5. Tests

```powershell
dotnet test
```

32 tests, **sin red y sin ticket**: no tocan la API, comprueban funciones puras.
Ver [Tests](#tests) para qué hay que leerlos antes de tocar nada.

---

## Probarlo sin ticket: modo demo

Es el modo por defecto del script de arranque, así que la forma normal de verlo
es:

```powershell
.\scripts\arrancar.ps1
```

Y sale lo mismo que si se fuerza a mano, con una variable de entorno:

```powershell
$env:MercadoPublico__ModoConsulta = "demo"
.\scripts\arrancar.ps1 -Modo demo
```

Devuelve **datos inventados** (semilla fija) y un aviso naranja lo dice en
pantalla, porque confundirlo con datos reales sería un fallo grave. El RUT de
ejemplo es `76.123.456-0`; la pantalla lo muestra y lo rellena al pulsarlo.

El script le pone un **nombre de empresa falso** con la misma extensión que uno
real, para que al ajustar la interfaz se vea cómo se parte el título y el
subtítulo. El que trae `appsettings.json` es "EMPRESA SIN CONFIGURAR", y con ese
texto no se puede juzgar nada del diseño.

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
adjudicados**.

**El detalle se pide siempre, no solo al abrir la ficha.** El listado diario
devuelve cuatro campos —código, nombre, estado y fecha de cierre— y ninguno es el
organismo, así que no hay manera de sacarlo de ahí. Como ahora el organismo se
pinta en la tarjeta, el servidor trae el detalle de cada licitación al responder
la semana, y lo cachea igual que antes.

Lo que cuesta, medido:

| | Peticiones | Tiempo |
|---|---|---|
| Semana con 1 licitación | 5 días + 1 detalle = **6** | 8,4 s |
| Semana vacía | 5 días + 0 detalles = **5** | 8,3 s |

El detalle añade una petición de las seis, o sea alrededor de un 17% en una
semana con una licitación, y **nada** en una semana vacía. Se piden **de uno en
uno**: en paralelo se dispararía el límite de ritmo, que es el `429` del que se
habla más abajo.

**Un detalle que falla no tira la semana.** Es la misma regla que se aplica a los
días: si un detalle falla, esa tarjeta sale sin organismo y las demás se pintan
igual. Fallar la semana entera por un dato accesorio perdería datos que sí se
pudieron traer.

Ejemplo real:

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

Los reintentos se quedan, pero **solo** por el `429`. Y conviene ser honesto
sobre qué se consigue con ellos: si el fallo es un `500` de la API por algo
nuestro, seis intentos idénticos siempre dan el mismo resultado y son tiempo
perdido. Por eso el mensaje de la API llega ahora a la pantalla, para que un
error de formato se vea en un segundo y no después de un minuto de insistencia
inútil.

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
| Se consultó y salió vacío | "Nada en la Semana 3 en Octubre de 2026", o "… es fin de semana" si es sábado o domingo |
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

### Las semanas van de lunes a domingo, recortadas al mes

El desplegable lo dice: **"Semana 2 - 5 al 11 de octubre"**. El 5 de octubre de
2026 es lunes y el 11 es el domingo de esa semana.

No son las semanas ISO, que se numeran por el año y pueden empezar en diciembre
del anterior. Aquí van numeradas **dentro del mes**, y **recortadas**: ninguna
se sale al mes siguiente, porque dentro de un desplegable de octubre un "26 de
octubre al 1 de noviembre" confunde más de lo que ayuda.

Para octubre de 2026, que empieza en jueves:

| Semana | Rango | Días hábiles que se consultan |
|---|---|---|
| 1 | del 1 al 4 | 2 (jueves y viernes) |
| 2 | del 5 al 11 | 5 |
| 3 | del 12 al 18 | 5 |
| 4 | del 19 al 25 | 5 |
| 5 | del 26 al 31 | 5 |

El rango **incluye** sábado y domingo porque la semana va de lunes a domingo.
Solo el **procesamiento** se queda con los días hábiles: en Chile no se publica
nada en fin de semana —comprobado: el 3 y el 4 de octubre de 2026 dieron 0—,
así que consultarlos duplicaría las peticiones sin aportar nada.

### Un mes que empieza en domingo

Febrero de 2026 empieza en domingo, así que el tramo del día 1 al primer domingo
es **solo ese domingo**: cero días hábiles. Dejarlo como "semana 1" gastaba un
número del desplegable sin dejar consultar nada, y la pantalla llegaba a decir
"se consultaron 0 días hábiles".

Ese domingo es en realidad la cola de la semana del mes **anterior**, así que se
descarta y la semana 1 pasa a ser la del lunes siguiente. Febrero queda con
**4 semanas**:

| Semana | Rango |
|---|---|
| 1 | del 2 al 8 de febrero |
| 2 | del 9 al 15 de febrero |
| 3 | del 16 al 22 de febrero |
| 4 | del 23 al 28 de febrero |

Es la regla general: **la semana 1 empieza el día 1 del mes, salvo que ese primer
tramo no tenga ningún día hábil.** Octubre no la activa —su tramo inicial va del
1 al 4 y tiene jueves y viernes— así que sigue teniendo 5 semanas y la semana 1
es la del 1 al 4.

### No se ofrecen periodos que aún no han ocurrido

Un periodo solo aparece si **ya ha empezado**:

- **Meses**: un año pasado ofrece los doce; el año en curso, solo hasta el mes de
  hoy; un año futuro no ofrece ninguno.
- **Semanas**: solo las que ya han empezado. La semana en curso sí, porque se ven
  los días que ya pasaron más los que faltan.

Con hoy 4 de octubre de 2026, el desplegable de semanas muestra **una sola**:
`Semana 1 - 1 al 4 de octubre`. Las semanas 2 a 5 no han empezado.

Antes sí se ofrecían, y elegir una gastaba una llamada al servidor para
devolver semanas enteras marcadas como pendientes y una pantalla vacía que
parecía un fallo. "No hay nada" y "todavía no ha pasado" tienen que verse
distinto, y la mejor forma es no llegar a ofrecer la pregunta.

El corte lo hace el servidor, en `DescribirSemanas` y `MesesVisibles`. Con hoy 4
de octubre de 2026 la lista de meses llega con diez entradas, no con doce, así que
**cualquier código del cliente que asuma doce meses se rompe**. Ya pasó: el nombre
del mes del aviso salía como "de 2 de 2026" en vez de "de febrero de 2026".

Los rangos los **calcula el servidor** y llegan en `/api/estado?anio=&mes=`. El
cliente no repite la regla de lunes a domingo: solo pinta lo que le mandan.

Antes estaba escrita en los dos lados, y **dos copias de una misma regla se
desincronizan solas** en cuanto se toca una y no la otra. Ningún aviso: el rótulo
diría una cosa y los datos consultados serían de otra. Con el cálculo en un
sitio, es imposible que discrepen.

El coste es una llamada local al cambiar de mes o de año, que no gasta cupo del
ticket. En el cliente no queda ni una línea de aritmética de fechas: ni
`DateOnly`, ni `DayOfWeek`, ni el cálculo de cuántas semanas tiene el mes. Por
eso tampoco queda copia que se pueda desincronizar, y por eso `SemanasDelMesTests`
ya solo tiene que vigilar el servidor.

Cuatro fallos que teve el cálculo, y que los tests ahora fijan:

- En .NET, `DayOfWeek` empieza por **DOMINGO = 0**, no por lunes. Calcular la
  primera semana como `7 - diaDeSemana` daba un número que parecía correcto y
  desplazaba todas las semanas un día: el 5 caía en la semana 1.
- La primera semana no se recortaba al **primer domingo** del mes, así que un mes
  que empezaba en jueves decía "del 1 al 7" y la semana 2 arrancaba el 8, que es
  lunes pero no el lunes del calendario.
- Un mes que empieza en domingo tenía una semana 1 de un solo día y sin días
  hábiles. Ver arriba.
- Al arreglar lo anterior, la semana 2 arrancaba el día siguiente al **fin** de la
  semana 1 más siete, en vez de más uno. Con octubre daba el 12 en vez del 5.

La regla tiene **una sola fuente de verdad**: `PrimeraSemana()` devuelve el inicio
y el fin de la semana 1, y de ahí salen tanto el número de semanas como todos los
rangos. Antes cada uno recalculaba por su cuenta y ya había divergido dos veces.

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
| Los desplegables salen vacíos | `/api/estado` no llegó, o no devolvió `aniosDisponibles` y `mesesDisponibles`. Un mes futuro sale vacío **a propósito**: no se ofrecen periodos que no han ocurrido. |
| Al mover un desplegable no cambia nada | Correcto: los datos llegan al pulsar "Actualizar", y sale un aviso ámbar diciéndolo. Si el aviso **no** sale, revisar que se comparen año, mes y semana: comparar solo el número de semana deja pasar los cambios de mes y de año, porque el número se repite en todos los periodos. |
| Un día sale con el nombre de otro | El array de días del cliente indexado por `DayOfWeek`, que en .NET empieza por **domingo**. Esa lógica se movió al servidor (`TextosDeFecha`) precisamente por eso. Si alguien la reintroduce, el primer síntoma es "un viernes que pone sábado". |
| Al abrir la página, la cabecera y nada debajo | Estado muerto: `Semana` a null sin error ni consulta en vuelo. Hay una rama `else` que lo evita, así que si aparece, hay que mirar la consola del navegador. |
|---|---|
| Se queda en "Cargando" tras publicar | Falta el target `SuperponerClienteBlazor`, o el `index.html` publicado sin sustituir. |
| Cabecera y título, y debajo un hueco vacío durante un minuto | Estado muerto en el render: el panel no se pintaba porque `Estado` aún era `null` y `Estado is { Servible: true }` no se cumplía. Ver [el aviso que no se veía](#%EF%B8%8F-asignar-un-campo-no-repinta-el-consultando-no-sal%C3%ADa-en-un-minuto). |
| `404` en `/_framework/*.wasm` | Recursos de una build anterior, cacheados un año. `Ctrl+F5`. |
| `Failed to find a valid digest … 47DEQpj8HBSa+` | Variante comprimida vacía: se colaron `.br`/`.gz`. Revisa las cinco propiedades de `Directory.Build.props`. |
| La pantalla dice "No se pudo consultar" | Mercado Público está rechazando. Se intentó 6 veces durante un minuto y no respondió. **No** significa que no haya nada hoy. Se reintenta solo cada 5 min. |
| Los 6 intentos salen con `500` y `Codigo 10300` | Casi siempre es **nuestro**, no de la API: se queja de lo que le mandamos. El mensaje concreto ahora llega a pantalla y al log, así que léelo antes de culpar a Mercado Público. |
| Falla solo entre el día 1 y el 9 de cada mes | El día sin relleno a la izquierda en la fecha: `dMMyyyy` en vez de `ddMMyyyy`. Ver [`fecha` con los DOS campos rellenos](#%EF%B8%8F-fecha-con-los-dos-campos-rellenos-o-no-funciona). |
| La lista sale vacía | Consulta real y de verdad no hay nada publicado en esa semana. La pantalla lo dice como "Nada en la Semana 3 en Octubre de 2026", no como error. |
| `401` o `403` al consultar | El ticket caducó, se revocó o se agotó su cupo diario. |
| Aviso naranja en pantalla | Estás en `ModoConsulta: "demo"`: son datos inventados. |
| Aviso "la aplicación no está configurada" | Falta el `Ticket` o el `CodigoProveedor` en la sección `MercadoPublico` del **servidor**. |
| La cabecera sale como "Empresa sin configurar" | Falta `NombreEmpresa`. No impide consultar: el código es lo único imprescindible. |

---

## Marcas y propiedad intelectual

Hay **tres marcas** en pantalla, y conviene no confundirlas:

| Marca | De quién es | Dónde sale |
|---|---|---|
| La *M* en zigzag + `WatchMercadoPublico` | **Propia**, dibujada para este proyecto | Símbolo y nombre de la herramienta |
| El rótulo `SMC` | **De SMC**, la empresa para la que es la herramienta | Logotipo en la cabecera, a la derecha |
| El logo de **Dirección ChileCompra** | **De ChileCompra**, el organismo que publica los datos | Logotipo en el pie |

El logotipo de SMC se muestra porque la herramienta es **interna de SMC**: es una
marca que se usa con permiso y para identificar al propietario, no una marca
registrada de la aplicación. Por eso va **en el otro extremo** de la cabecera, con la
marca propia a la izquierda y el logotipo a la derecha, sin mezclarlos en un solo
rótulo. No se pone una línea divisoria entre ellos: con la barra a 1.152 px de
ancho quedan a cientos de píxeles el uno del otro y la línea quedaría flotando
en mitad del hueco.

El logo de ChileCompra se pone por lo contrario: los datos que se muestran **son
suyos**, y decir de dónde salen es lo mínimo que corresponde. Va en el pie, y
además de pie lleva el crédito de autoría: "Desarrollado por **SMC Spa**", con
enlace a `smc.cl`.

Lo que **no** se hace, en ningún caso: reproducir, combinar ni fusionar el
logotipo de Mercado Público / ChileCompra con la marca propia. Es una marca
registrada de un tercero, y eso es infracción de marca, no solo de copyright.
El logo de ChileCompra va en su propio elemento, en el pie, sin tocar la marca
propia, que sigue solo en la cabecera.

### El logo de ChileCompra: dos ficheros, y no se pinta con máscara

`wwwroot/LogoChc-blanco.png` y `wwwroot/LogoChc-oscuro.png` son las dos
variantes del logo de Dirección ChileCompra: tinta blanca para el tema oscuro,
tinta gris 900 para el tema claro. Se intercambian por la clase `.dark` del CSS,
**no** por `prefers-color-scheme`: el tema también se cambia a mano con el botón,
y una media query seguiría al sistema en vez de al botón, con lo que el logo se
quedaría en la variante equivocada.

El archivo del que salieron, que era solo la versión **blanca**, no está en el
repositorio. Se quitó porque con el pie reducido a la autoría no había sitio para
un recuadro oscuro debajo, que es lo que hacía falta para que el texto blanco se
vera en el tema claro. Ese recuadro además quedaba feo, y el logo salía
embarrado. La causa estaba en la imagen, no en el CSS:

| Defecto del PNG | Medida |
|---|---|
| Halo difuso alrededor del logo | 1.963 px con alfa 1-80 (12,7 %) |
| El texto no era blanco, era gris claro | `rgb(224,224,224)` |
| El contenido tocaba los cuatro bordes | 0 px de margen |

Las dos variantes quedan sin halo, con el blanco normalizado y con 12 px de
margen igual, para que al cambiar de tema el logo no se mueva un píxel.

**Por qué aquí sí y en SMC no:** el logo de ChileCompra tiene la barra azul y
roja de la bandera, y una máscara de un solo color la dejaría en monocroma. El
de SMC es blanco puro con alfa variable y una sola imagen le da los dos colores
del tema. Por eso uno se pinta con `mask-image` y el otro con
`background-image`.

La bandera se respeta en el tratamiento: tiene saturación alta, así que la regla
de "esto es blanco, lo pinto" no la toca. Si la tocara, el logo perdería su color.

Va como `<span>` con `background-image` y no como `<img>`: con dos imágenes
habría que decidir en Blazor cuál mostrar, y la clase `.dark` la pone el script
en línea de `index.html` antes de pintar, sin parpadeo. Lleva
`aria-label="Dirección ChileCompra"`, igual que el de SMC: si el logo se pierde,
el nombre sigue leyéndose.

### El archivo del logo de SMC, y por qué se pinta con máscara

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
| Encabezado de la página | "Novedades en **Mercado Público**" | Uso nominativo: describe la plataforma de la que salen los datos |
| Encabezado, a la derecha | "Ir a **Mercado Público**" → la URL de `UrlMercadoPublico` | Salida a la plataforma. La URL viene de la configuración, no del marcado |
| Ficha, al abrirla | "Ir a **Mercado Público**", la misma `UrlMercadoPublico` | El buscador del portal |
| Pie | "© WatchMercadoPublico" | Autoría del producto |
| Pie | "Desarrollado por **SMC Spa**" → `smc.cl` | Crédito de autoría. Se queda aunque no haya salida a internet |
| Pie | Logotipo de **ChileCompra** | Los datos son suyos: atribución |
| Aviso de demo | "No se está llamando a **Mercado Público**" | Uso nominativo |
| Ficha de una licitación | Nombre del organismo y su RUT | Son los datos de la plataforma, no un atributo nuestro |

De las tres marcas, **ninguna se toca**: la propia solo en la cabecera, la de SMC
en la cabecera, la de ChileCompra en el pie. El logo de SMC es blanco plano sobre
el fondo mientras que el nombre propio va en degradado, así que aunque se leyeran
juntos se distinguirían.

**Los dos enlaces que salen a internet usan la misma configuración.**
`UrlMercadoPublico`, en la sección `MercadoPublico` del `appsettings`, llega al
cliente por `/api/estado` y lo usan tanto el de la cabecera como el de la ficha.
Se puede cambiar por entorno sin tocar el `.razor`, que no es un sitio donde
tenga sentido cambiar URLs.

Si `UrlMercadoPublico` se deja vacía o no empieza por `https://`, **ninguno de los
dos enlaces se pinta**: es preferible a un enlace que no lleva a ninguna parte.

### Por qué ninguno de los dos va a la licitación concreta

Se probó la ruta `mercadopublico.cl/licitacion/{código}`, que es la que se usaba
antes, y **está rota**. Lo difícil de verlo es que responde **HTTP 200**: devuelve
una página de cuatro mil bytes que es la shell de una SPA, y con un código
inventado devuelve exactamente lo mismo. El 200 no dice nada.

Abriéndola en un navegador sí se ve: redirige a una pantalla de **"En estos
momentos no podemos atender su solicitud"**.

Por eso los dos enlaces van al buscador. Se pierde poder abrir una licitación
concreta desde aquí, que es lo que haría el `/licitacion/{código}`; a cambio el
enlace funciona, y el texto del botón de la ficha pasó de "Ver en
mercadopublico.cl" a "Ir a Mercado Público", porque ya no lleva a ver esa
licitación y decir "Ver" sería engañoso.

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

**104 tests** (80 del servidor, 24 del cliente), todos en verde. **No hay que
tener ticket, ni red, ni la API en pie.** Eso no es una comodidad: es lo que hace
posible testear. Un método que hace una petición no se puede comprobar sin
pedirla, y para la aplicación casi todo lo que se rompió no necesitaba la API para
estar mal.

Por eso `ConstruirUrlDia` y `ConstruirError` están extraídos del cliente como
funciones estáticas, aunque solo los use un sitio: es el requisito para poder
mirarlos desde un test. Para exponerlos hay un `InternalsVisibleTo` en el
`.csproj` del servidor, y nada más.

### Qué cubren, y por qué existen

Todos nacieron de bugs que estuvieron en producción:

| Archivo | Qué ata |
|---|---|
| `FormatoDeFechaTests.cs` | La fecha va en `DDMMAAAA` con los dos campos rellenos, los **730 días** de dos años, y no depende del calendario de la cultura del servidor |
| `ErroresDeApiTests.cs` | Que el mensaje de la API llegue a quien lee, y que un `500` no se traduzca en "la API está caída" cuando la API está diciendo otra cosa |
| `SemanasDelMesTests.cs` | La regla de lunes a domingo, recortada al mes, y que ninguna semana se quede sin días hábiles |
| `TextosDeFechaTests.cs` | Los nombres de día y de mes, y que `DayOfWeek.Sunday` siga siendo 0 |
| `PeriodosDisponiblesTests.cs` | Que no se ofrezcan meses ni semanas que aún no han ocurrido |

### Lo que los tests no cubren

**El cliente no tiene proyecto de pruebas.** Todo lo anterior vive en el
servidor. Es una decisión con consecuencias, y hay que conocerlas:

Los tres bugs de fechas que hicieron falta arreglar estaban **en el cliente**,
no en el servidor: el array de días ordenado por lunes pero indexado por
`DayOfWeek`, que en .NET empieza por domingo, y por eso un viernes salía como
"sábado". **Ningún test lo detectó, porque no había ninguno que lo mirara.**

La reacción fue mover el texto de las fechas al servidor, donde sí se prueba
(`TextosDeFecha`). Quedó sin lógica de fechas en el cliente, pero el hueco
sigue: `Formato.cs` no tiene tests, y `Home.razor` es una pantalla de 500 líneas
que no se puede probar sin un proyecto de pruebas de Blazor.

El último bug fue de la misma familia y **tampoco lo cazó ningún test**:
comparar solo el número de semana para saber si los filtros habían cambiado.
Como el número de semana se repite en todos los periodos, cambiar de mes o de
año no disparaba el aviso. Era una línea, y estaba a dos líneas de otra que sí
comparaba las tres cosas.

### Lo que los tests no cubren

**`Home.razor` sigue sin poder probarse.** Hay proyecto de tests para el cliente,
pero solo alcanza a las clases estáticas: `Formato`, `Texto`, los DTOs. La
lógica de la pantalla vive en el code-behind de un componente Blazor, y eso
necesita otra cosa: bUnit, o sacar la lógica fuera del componente.

Conviene tener presente el historial, porque explica por qué el hueco importa:

Los **tres** bugs de fechas estaban **en el cliente**, y ninguno pudo ser visto
por un test, porque no había ninguno que mirara. El más caro de todos, el array
de días ordenado por lunes pero indexado por `DayOfWeek`, que en .NET empieza por
domingo, y por eso un viernes salía como "sábado" en pantalla. La reacción
inmediata fue mover el texto de las fechas al servidor, donde sí se prueba.

Y el proyecto de tests del cliente, cuando se creó, **encontró un bug real a la
primera**: `Formato.SinAcentos` bajaba a minúscula solo los caracteres a los que
les quitaba la tilde, y el resto los copiaba con su mayúscula. Así que "Licitación"
quedaba como "Licitacion" y no se encontraba buscando "licitacion": **el
resaltado de la búsqueda no funcionaba en cuanto la palabra empezaba por
mayúscula**, que es justo el caso normal en un nombre de licitación. Ese bug
llevaba tiempo ahí y solo apareció al escribir el test.

Un test de regresión que nunca ha fallado no demuestra nada, así que el del
resaltado se validó **volviendo a romper el código a propósito**: sin el
`ToLowerInvariant` en la segunda rama, falla.

### La prueba de que un test sirve

Un test de regresión que nunca ha fallado no demuestra nada: puede estar
comprobando lo que siempre se comprobó. Los primeros dos se validaron
**reintroduciendo el bug**:

| Con el bug reintroducido | Resultado |
|---|---|
| `ddMMyyyy` → `dMMyyyy` | **Falla** en los 9 días de la primera decena |
| Sin leer `Mensaje` | **Falla** en los 2 tests que dependen de ese mensaje |
| Restaurado (en aquel momento, eran 32) | **32 de 32** |

El mismo truco se usó después con `PeriodosDisponiblesTests`, desactivando el
corte de semanas futuras: se pusieron rojos **3 tests**, entre ellos el barrido de
dos años que exige que ninguna semana ofrecida empiece después de hoy.

Si alguna vez se toca `ConstruirUrlDia`, `ConstruirError` o el corte de periodos
futuros, ese truco es la forma de saber si el test sigue vigilando algo.

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
├── scripts/arrancar.ps1           # compila, publica y levanta en local
├── secrets/                       # plantilla del ticket (fuera del repo)
└── src
    ├── WatchMercadoPublico.Client # la SPA
    │   ├── Components/            # Icon, Marca, Aviso, LogoSmc, LogoChileCompra,
    │   │                          #   FichaLicitacion, ModalDetalle, ThemeToggle
    │   ├── Layout/MainLayout.razor # cabecera y pie
    │   ├── Models/                # DTOs (mismos nombres que el JSON del servidor)
    │   ├── Pages/Home.razor       # la pantalla entera: los tres filtros, y su refresco
    │   ├── Services/              # MercadoPublicoApi, Formato, ThemeService
    │   └── wwwroot/               # index.html, css/app.css, icono.svg, marca.svg,
    │                              #   logo-smc.png, LogoChc-blanco.png, LogoChc-oscuro.png
    ├── WatchMercadoPublico.Client.Tests # tests del cliente, sin Blazor
    ├── WatchMercadoPublico.Server # la API y el hosting de la SPA
    │   ├── Endpoints/             # LicitacionesEndpoints (/estado, /semana, detalle)
    │   ├── Models/                # opciones y DTOs
    │   ├── Services/              # MercadoPublicoCliente, CacheMercadoPublico,
    │   │                          #   SemanasDelMes (la regla de semanas),
    │   │                          #   CalendarioDelMes (nombres de mes),
    │   │                          #   TextosDeFecha (días y meses en palabras),
    │   │                          #   DatosDemo
    │   └── web.config
    └── WatchMercadoPublico.Server.Tests # tests del servidor, sin red y sin ticket
```

`DatosDemo.cs` y el modo `demo` son **una ayuda de desarrollo**, no parte de la
aplicación.

### Quién decide qué

La regla que vertebra el proyecto: **el servidor calcula, el cliente pinta.**

| Cosa | Dónde vive | Por qué |
|---|---|---|
| Qué semanas tiene el mes | `SemanasDelMes` | Una sola copia de la regla de lunes a domingo |
| Qué periodos se pueden elegir | `DescribirSemanas`, `MesesVisibles` | Que los tres desplegables no puedan discrepar |
| Los nombres de día y de mes | `TextosDeFecha`, `CalendarioDelMes` | **El cliente ya tuvo su propia copia y salió un viernes como sábado** |
| El rango de cada semana, escrito | `SemanaDescrita.Texto` | El cliente pintaba "23 al 28 de febrero" por su cuenta |
| El día de una licitación, escrito | `Licitacion.PublicadoTexto` | Ídem |
| El periodo del contador | `RespuestaSemana.Periodo` | Ídem |

Hay un test que califica esto, y es `TextosDeFechaTests`. Si algún día alguien
añade en el cliente un array de meses o de días, ese test no lo va a detectar:
lo detectaría el próximo bug de un día corrido.
