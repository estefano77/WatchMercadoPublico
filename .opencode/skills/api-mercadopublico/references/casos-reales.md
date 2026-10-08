# Casos reales de esta API

Historial de lo que ha fallado de verdad, con el síntoma exacto que se ve y por
qué pasó. Están aquí porque **ninguno dio un error que señalara su causa**:
todos devolvieron HTTP 200 o un 2xx con datos equivocados.

---

## El `500` constante que no era de la API

**Síntoma.** La API devolvía `500` en casi todas las peticiones. La primera
reacción razonable era que ChileCompra tuviese un problema, y se subió el número
de reintentos de 2 a 6 "por si acaso".

**Causa.** Nuestra URL. Con `"dMMyyyy"`, el 4 de octubre se mandaba `4102026`.
La respuesta era:

```json
{"Codigo":10300,"Mensaje":"El formato del parametro fechas es incorrecto"}
```

**Por qué costó tanto.** Solo falla del 1 al 9. Las pruebas usaban el 28/09
(día 28) y el 27/02 (día 27), así que nunca lo ACTIVARON. Parecía intermitente,
que es la señal clásica de un bug de formato de fecha.

**Lo que se aprendió.** Leer `Mensaje`, no solo `error.message`. Ese campo es lo
que permitió distinguir "nosotros mandamos mal la petición" de "ellos están
caídos".

---

## El precio salía diez veces más alto

**Síntoma.** Un presupuesto de 192.000.000 se veía como 1.920.000.000.

**Causa.** `"MontoEstimado": 192000000.0` es un **número** JSON. El código lo
pasaba por texto y quitaba los puntos "porque en Chile son separador de miles".
`192000000.0` → `1920000000`.

**Lo que se aprendió.** Separador de miles ≠ separador decimal. Solo tiene
sentido normalizar cuando el valor viene como *string*, y ahí se prueban las dos
convenciones antes de quitar nada. Para números, `GetDecimal()`.

Lo mismo pasaba con `"Cantidad": 1.0` → `10`.

---

## `CodigoExterno` vacío y las 4.631 licitaciones descartadas

**Síntoma.** HTTP 200, lista vacía, sin error.

**Causa.** Se buscó un campo con una **ruta** donde hacía falta una **alternativa**:

```csharp
// mal: busca CodigoLicitacion DENTRO de CodigoExterno
LeerRuta(item, "CodigoExterno", "CodigoLicitacion")

// bien: dos claves alternativas del mismo objeto
Leer(item, "CodigoExterno", "CodigoLicitacion")
```

Como la lista se deduplicaba por ese campo, y el campo salía `null` en todas,
las 4.631 se fueron.

**Lo que se aprendió.** Son dos funciones distintas con dos propósitos. La
documentación de la API menciona `CodigoLicitacion`, pero el JSON manda
`CodigoExterno`; de ahí la doble clave.

---

## El día que fallaba costaba 60 de los 65 segundos

**Síntoma.** Una consulta de una semana tardaba más de un minuto, y el día que
fallaba se llevaba casi todo.

**Causa.** El fallo no se guardaba en ninguna parte. Cada petición volvía a
subir la escalera entera de 6 intentos con esperas de 2, 4, 8, 16, 30 y 30
segundos, para siempre.

**Lo que se aprendió.** Recordar un fallo **no** autoriza a decir que no había
nada. El día sigue saliendo como "sin respuesta" y la pantalla sigue
avisando; lo único que cambia es que no se vuelve a preguntar hasta que pase el
plazo. De ahí `CacheMercadoPublico.DiaFallidoReciente`.

Y el plazo del fallo tiene que ser **mayor** que el refresco automático
(15 min contra 5 min), o cada refresco llegaría justo cuando el fallo caduca.

---

## Dos consultas en paralelo: 23,5 s en vez de 13,2 s

**Medición.**

| Escenario | Tiempo |
|---|---|
| Una semana en frío | 6,5 s |
| Dos semanas en serie | 13,2 s (la suma exacta) |
| Dos semanas en paralelo | 23,5 s |

Y lo peor: **la API no avisa con un `429` al paralelizar**. Se limita a callarse;
cada llamada tarda mucho más y el total sube por encima de la suma.

**Lo que se aprendió.** El candado es único **a propósito**. Se probó lo
contrario (uno por semana, para que dos personas no se estorbaran) y la
medición salió al revés. El problema nunca fue el candado: eran las esperas sin
límite al entrar y el refresco automático reteniéndolo.

---

## `POSTGRES`/`LICENSE`/`None`, y el `ServiceCollection`

**Síntoma.** Todas las rutas tumbadas al arrancar, incluida la SPA. Error:
`Body was inferred but the method does not allow inferred body parameters`.

**Causa.** Un servicio no registrado. En un minimal API, un parámetro de un tipo
concreto que no está en el contenedor se infiere como **cuerpo de la petición**.

**Lo que se aprendió.** El mensaje menciona el parámetro, no el `AddScoped` que
falta. `IOptions<T>` sí funciona como parámetro; `T` concreta no, aunque
`Configure<T>()` esté registrado.

---

## La fecha del día siguiente ya en el listado

**Síntoma.** Un viernes aparecía pintado como **sábado**.

**Causa.** El cliente tenía su propio array de días y lo aplicaba al revés.

**Lo que se aprendió.** El texto de la fecha lo compone **el servidor** y viaja
en el JSON (`PublicadoTexto`). El cliente lo pinta tal cual. Cuando dos sitios
calculan la misma regla, uno de los dos acaba disagreesin que nada avise.

---

## El "Consultando…" que no salía

**Síntoma.** Tras pulsar "Actualizar", la pantalla se quedaba igual un minuto.

**Causa.** Asignar un campo en Blazor **no repinta**. El handler mutaba el estado
pero no había un cambio de parámetro que invalidate el render.

---

## El logo de ChileCompra y el de SMC

Dos ficheros de logo distintos y **ninguno** se pinta en la tarjeta de la
licitación. Son marcas de terceros y de la empresa, no de este dato. Está escrito
en el README por si alguien lo mueve por "que se vería mejor".