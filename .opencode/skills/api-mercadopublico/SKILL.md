---
name: API de Mercado Público
description: Usar al tocar MercadoPublicoCliente, LicitacionesEndpoints, el importador SQL, o cualquier llamada a api.mercadopublico.cl. Cubre el formato DDMMAAAA, los montos como número JSON, el 203 que parece un 200, el 429 y los reintentos.
---

# API de Mercado Público

Las trampas de `api.mercadopublico.cl`. Todas están medidas y todas fallan
**sin error visible**: devuelven HTTP 200 o un 2xx y un resultado equivocado.

Hay dos clientes, y las dos tienen que respetar lo mismo:

- `src/WatchMercadoPublico.Server/Services/MercadoPublicoCliente.cs` — la app.
- `sql/03-procedimiento-importar.sql` — el importador de T-SQL.

Si tocas uno, el otro tiene que seguir diciendo lo mismo. Divergen, la
importación guarda cosas que la pantalla no enseñaría.

## 1. La fecha va en `DDMMAAAA`, con los dos campos rellenos

```
DDMMAAAA   correcto    04 → "04102026"  ✓
dMMyyyy    prohibido   04 → "4102026"   ✗ siete dígitos
```

`d` es el día **sin** cero y `dd` **con** cero. Con `dMMyyyy` la API responde:

```json
{"Codigo":10300,"Mensaje":"El formato del parametro fechas es incorrecto"}
```

Lo que lo esconde: solo falla **entre el 1 y el 9**. Del 10 en adelante el día ya
tiene dos cifras y el bug desaparece solo. Por eso parece intermitente, y por eso
las pruebas con fechas de fin de mes nunca lo detectaron.

En T-SQL, `FORMAT(@d,'ddMMyyyy')` es correcto pero lento y depende de la cultura
de la sesión. El importador lo arma a mano con `RIGHT('0'+DAY(@d),2)`.

## 2. Los montos son NÚMEROS JSON, no cadenas

```json
"MontoEstimado": 192000000.0
```

Antes de arreglarlo, el código pasaba el valor por texto y quitaba los puntos
"porque en Chile son separador de miles". Con un número JSON, `"192000000.0"`
se convertía en `1920000000`: **diez veces más alto**. Y `"Cantidad": 1.0` en
`10`.

Se lee con `GetDecimal()`, que es exacto e interpretable. En T-SQL,
`TRY_CONVERT(decimal(19,4), JSON_VALUE(...))`: el punto es siempre el separador
del JSON, con independencia de la configuración regional.

Nunca `float`/`double` para dinero: `192000000.0` en double es
`191999999.99999997`. En SQL, `decimal(19,4)`; en C#, `decimal`.

## 3. Un ticket inválido responde `HTTP 203`, no `401`

Es el que más caro sale, porque es un `2xx`:

```json
{"Codigo":203,"Mensaje":"Ticket no válido."}
```

Cualquier comprobación `Codigo == 200 || Codigo == 201 || ... || Codigo == 299`
—o un `BETWEEN 200 AND 299`— **lo da por buena**. En el importador pasó:
informaba de *"3 días preguntados con éxito"* con un ticket inventado, sin haber
mirado ni una licitación.

- Solo un **`200` exacto** con cuerpo no vacío es una respuesta válida.
- El `203` se trata como rechazo de ticket: **no se reintenta**. Sin eso, cada
  día consumía la escalera entera de esperas (2, 4, 8, 16, 30, 30 s). Medido:
  3 días tardaban **185 s** y dejaban 18 filas en `MpConsulta` para recibir 18
  veces el mismo "no". Ahora: 4 s y 3 filas.
- El `401`/`403` tampoco se reintentan, por el mismo motivo: un ticket malo no
  mejora con insistir.

## 4. `CodigoProveedor` solo filtra si mandas `fecha`

Con `estado=activas` el filtro se **ignora** y devuelve el total del país (~4.600)
con cualquier código, incluso uno inexistente. No da error: parece que funciona.

## 5. El `429` es un límite de ritmo, medido

Doce peticiones seguidas, sin pausa, contra la API real:

| Pausa | Resultado |
|---|---|
| ninguna | `203 429 203 429 203 429 429…` (una de cada tres) |
| 400 ms | casi todas 429 |
| 800 ms | la mitad |
| 1500 ms | diez de doce bien |

Es un **cupo de ráfaga**, no un límite de duración: el rechazo llega en ~280 ms.

Se mide el intervalo entre el **principio** de una petición y el principio de la
siguiente, no una espera tras cada respuesta. Como las respuestas reales tardan
1,4–1,6 s, el intervalo ya se cumple solo y no se espera nada salvo que algo
vuelva más rápido de lo previsto. Lo implementa `RitmoDeLlamadas`, como
**singleton**: en `scoped` cada petición HTTP tendría el suyo y no se limitarían
entre sí.

Y en paralelo es peor que en serie, medido: dos semanas en serie 13,2 s, en
paralelo 23,5 s. Por eso los días van en serie y el candado es único.

## 6. Dos códigos de estado significan cosas distintas

Esto no es un detalle de la API, es la regla que sostiene toda la pantalla:

- **"No hay licitaciones"** → se pudo preguntar, la API respondió, no había nada.
- **"No se pudo comprobar"** → la API no respondió. La lista está incompleta y
  hay que decirlo.

Nunca conviertas lo segundo en lo primero. `RespuestaSemana` lleva
`DiasSinRespuesta` justamente para que la diferencia llegue al cliente. En SQL,
`MpConsulta.Exito = 0` más el mensaje en `Mensaje` y `CodigoApi`.

## 7. Los fines de semana y los días futuros no se preguntan

La API responde `500` a un día que no existe. No es una optimización: consultar
un sábado cuesta reintentos, y contarlo como fallido sería mentira. Los días
futuros son lo mismo pero sin motivo de red.

## Detalles del formato que ya están resueltos

Están en `Leer`, `LeerRuta` y `LeerItems` del cliente. **No los reimplementes**:

- `Adjudicacion` de primer nivel es el **ACTA** (fecha, número, oferentes,
  enlace) y **no lleva ningún monto**. El monto está en
  `Items.Listado[].Adjudicacion.MontoUnitario`.
- Hay dos funciones de lectura y se usan para cosas distintas:
  `Leer(el, "CodigoExterno", "CodigoLicitacion")` busca entre claves
  **alternativas del mismo objeto**; `LeerRuta(el, "Comprador", "Nombre")`
  baja por la jerarquía. Confundirlas es un fallo silencioso: con una ruta,
  `("CodigoExterno","CodigoLicitacion")` buscaría un `CodigoLicitacion` *dentro*
  de `CodigoExterno`, no encontraría nada, y las 4.631 licitaciones se
  descartarían todas con HTTP 200 y lista vacía.
- El error de la API viene en español y con dos formas:
  `{"Codigo":…,"Mensaje":"…"}` y `{"error":{"message":…},"Codigo":…}`. Hay que
  leer **`Mensaje`**; si solo se lee `error.message`/`Message`, el primer formato
  se pierde entero y el log pone `(null)`.
- Un item sin `MontoUnitario` se descarta. Es la regla de `LeerItems`: sin monto
  no aporta al total y solo llena la tabla de ruido.
- Las fechas del bloque `Fechas` son `DateTimeOffset` con desfase de Chile
  (`-03:00`/`-04:00`). Leerlas en `DateTime` pierde ese desfase.

## ⚠️ "Especificaciones del comprador" se llama `Descripcion`

La página de MERCADOPUBLICO rotula un campo del ítem como **"Especificaciones
del comprador"**. En la API ese campo se llama **`Descripcion`**, y **no existe
ninguna clave `Especificacion`**. Comprobado contra la API.

Claves que trae un `Items.Listado[]`, medidas:

```
Correlativo  CodigoProducto  CodigoCategoria  Categoria
NombreProducto  Descripcion  UnidadMedida  Cantidad  Adjudicacion
```

Y por qué importa tanto acertar el nombre: es **lo único que distingue dos
líneas del mismo producto**. `2342-28-LR24` tiene ocho líneas, las ocho con el
mismo `NombreProducto`, repartidas entre dos empresas, y todas se separan solo
por `Descripcion`:

```
Línea a) Sistemas Computacionales Juzgados - TÉCNICO RESIDENTE
Línea a) Sistemas Computacionales Juzgados - MANTENCIÓN MENSUAL
Línea b) Sistemas Computacionales Recursos Humanos - IMPLEMENTACIÓN
```

**Leerla como `Especificacion` no da ningún error**: sale `null`, la fila se
pinta igual, y la única pista es que no hay texto donde debería haberlo. Es de
los fallos que se buscan durante media tarde. Va con pruebas
(`LecturaDeItemsTests`) y con el JSON real, porque un nombre de clave escrito a
ojo es justo lo que un test evita.

## Antes de dar por buena una modificación

1. ¿El rango de fechas que vas a probar incluye **algún día del 1 al 9**?
2. ¿El importe que comparas viene de `MontoEstimado` (presupuesto) o de la suma
   de los items (adjudicado)? Son distintos y la pantalla enseña el segundo.
3. ¿Tu condición de éxito acepta algún 2xx que no sea 200?
4. ¿Puedes distinguir "no había nada" de "no se pudo comprobar"?
5. `FormatoDeFechaTests` cubre los 365 días por si toca la aritmética.

Ver más en `references/casos-reales.md`, que está el historial real de los
fallos con su síntoma y su causa.