# Persistencia en SQL Server

Guardar en base de datos todo lo que se lee de Mercado Público, para poder
consultarlo con SQL y para que la aplicación **no tenga que preguntar a la API
cada vez**.

Hay dos caminos hacia los mismos datos y se elige con una clave de configuración:

```
MercadoPublico__FuenteDatos = "sql"
MercadoPublico__CadenaConexionSql = "Server=localhost\SQLEXPRESS;Database=WatchMerPub;Integrated Security=True"
```

Con `api` —lo de siempre— la base no se toca. Con `sql` la aplicación lee de
aquí y no necesita ni ticket. Ver [la fuente de datos](../README.md#la-fuente-de-datos-api--sql-server)
en el README del proyecto para lo que cambia en pantalla.

## Aviso importante

**El acceso a la base es solo por procedimiento almacenado.** No hay Entity
Framework, ni mapeo objeto-relacional, ni migraciones, ni un modelo que se pueda
desincronizar del esquema.

El precio de esa decisión se dice aquí y no en otro sitio: si mañana cambia el
esquema, hay que tocar los procedimientos a mano. Con EF el cambio se propagaría
solo. Se prefiere que el cambio sea **visible**.

## Los archivos

| Archivo | Qué es |
|---|---|
| `01-esquema-mercadopublico.sql` | Las tablas, las vistas y los índices |
| `02-registrar-ensamblado.sql` | Registra el CLR que hace el `GET` HTTP dentro de SQL Server |
| `03-procedimiento-importar.sql` | `MpImportarRango`: descarga días a la base |
| `04-funciones-auxiliares.sql` | Funciones auxiliares de la importación |
| `05-procedimientos-lectura.sql` | `mp.LeeMes`, `mp.LeeDetalle`, y la cuenta de días |
| `clr/` | El proyecto C# que hace la petición HTTP (`net48`) |
| `97-prueba-lectura.sql` | Comprueba 17 cosas **contra la base real** |
| `98-prueba-importar.sql` | Comprueba el importador contra la API |
| `99-prueba-esquema.sql` | Mete datos de ejemplo y comprueba 11 cosas |

Detalle del importador: [`README-importar.md`](README-importar.md).

## Cómo usarlo

```powershell
# Crear la base (una vez)
sqlcmd -S localhost\SQLEXPRESS -E -Q "CREATE DATABASE WatchMerPub"

# Crear el esquema (se puede repetir las veces que haga falta)
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMerPub -b -f 65001 `
        -i sql\01-esquema-mercadopublico.sql

# Registrar el CLR (una vez, y reinicia el servicio de SQL Server)
sqlcmd -S localhost\SQLEXPRESS -E -b -f 65001 `
        -i sql\02-registrar-ensamblado.sql

# Llenar la base
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMerPub -b -f 65001 `
        -Q "EXEC dbo.MpImportarRango @desde='2026-01-14', @hasta='2026-10-08', @codigoProveedor=N'71284', @ticket=N'...', @resultado=@r OUTPUT PRINT @r"

# Comprobar que se lee bien
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMerPub -b -f 65001 `
        -i sql\97-prueba-lectura.sql
```

El `-f 65001` no es adorno: sin él, `sqlcmd` lee el fichero con la página de
códigos del sistema y los acentos de los comentarios se descuelgan. En Management
Studio no hace falta; ahí se pega el fichero entero.

### ⚠️ Añadir una columna son DOS cosas, no una

`01-esquema-mercadopublico.sql` se puede repetir porque todo va guardado con
`IF OBJECT_ID(...) IS NULL`. Pero esa misma guarda significa que **un `CREATE
TABLE` guardado no añade columnas a una tabla que ya existe**: se lo salta
entero.

Para que una columna nueva llegue a una base ya instalada hacen falta las dos
cosas en `01`:

1. La columna dentro del `CREATE TABLE`, para las bases nuevas.
2. Un `ALTER TABLE ... ADD` guardado con `COL_LENGTH(...) IS NULL`, para las que
   ya hay.

Con solo la primera, la base de desarrollo y la del hosting se quedan sin la
columna **sin decir nada**: ni error ni aviso. El síntoma es que la copia se
negativa a continuar porque los esquemas no casan, o que un `SELECT` falla mucho
después con el nombre de la columna.

Y hay que acordarse de ejecutar `01` en **las dos** bases: la de desarrollo y la
del hosting. No es el mismo fichero aplicado a la misma base.

## Los procedimientos de lectura

| Procedimiento | Conjuntos | Para qué |
|---|---|---|
| `mp.LeeMes` | **5** | Lo que usa la aplicación: cabecera, días no comprobados, listado, detalles, items |
| `mp.CuentaDiasDelMes` | 1 | Los ocho números del mes |
| `mp.DiasSinComprobarDelMes` | 1 | Los días que no se pudieron mirar, con su motivo |
| `mp.LeeDetalle` | 3 | La ficha de una licitación |
| `mp.TodosLosDiasDelMes` | función | Los días del mes, del 0 al 31 |

### Por qué son cinco y no dos

`mp.LeeMes` devuelve cinco conjuntos, y eso hace que **no se pueda probar en
T-SQL**: `INSERT @tabla EXEC unProcedimiento` mete *todos* los conjuntos en la
misma tabla. No hay forma de coger solo el primero.

Y lo que hay que probar es exactamente la primera parte: que la cuenta de días
cuadre, para que la pantalla pueda decir la verdad sobre lo que no se comprobó.
Por eso esa cuenta vive en dos procedimientos de un solo conjunto, que sí se
pueden meter en una tabla.

De paso `mp.LeeMes` se queda corto: llama a los dos y pega lo que devuelven.

### Los cinco conjuntos, en orden

El orden **es parte del contrato**. La aplicación los lee por posición, con un
`NextResultAsync()` al final de cada vuelta del bucle:

0. Cabecera — `Anio, Mes, DiasHabiles, DiasConsultados, DiasFallidos, DiasPendientes, Total, Consultado`
1. Días no comprobados — `FechaDia, Motivo`
2. Listado
3. Detalles
4. Items

El `NextResultAsync()` va **al final** a propósito, porque
`ExecuteReaderAsync()` deja el lector ya sobre el primer conjunto. Un
`while (await NextResultAsync())` se come la cabecera y lee el conjunto
equivocado; sale como `IndexOutOfRangeException: DiasHabiles`, que no dice ni qué
conjunto es ni que hubo un salto. Con `sqlcmd` no se ve, porque imprime los cinco
bien.

### Por qué `mp.LeeMes` devuelve los días no comprobados

Porque **sin ellos el modo base de datos no puede ser honesto**.

Ocurrió de verdad: el 8 de octubre de 2026 se importaron los días 5, 6 y 7 y los
tres fueron rechazados con `203 / "Ticket no válido"`; los días 1 y 2 nunca se
preguntaron. Ese mes tiene cero licitaciones en la base. Un procedimiento que
devolviera solo las licitaciones habría hecho que la pantalla dijera *"no se
publicó nada en octubre"*, que es falso: no se miró.

Con los días sale un aviso que dice qué pasó y por qué:

```
No se pudo comprobar todo el mes de octubre
De 6 días hábiles, 6 no se pudieron consultar.
  2026-10-01 al 2026-10-02 — Nunca se consulto este dia
  2026-10-05 al 2026-10-06 al 2026-10-07 — Ticket no válido.
```

El motivo importa porque decide la reacción: un `429` se arregla esperando y un
ticket caducado, no.

### Dos trampas de T-SQL que salen aquí

**`DATEADD(...) - 1` no vale.** Restarle un entero a un `date` da:

```
Operand type clash: date is incompatible with int
```

El mensaje no menciona el `DATEADD` ni la aritmética, y es fácil que alguien
busque el `DATEADD` en otra parte. Se resta el día **dentro**:

```sql
DATEADD(DAY, -1, DATEADD(MONTH, 1, @primeroDelMes))
```

**`sys.all_objects` no sirve para generar números.** Es el truco que aparece en
los blogs —un `ROW_NUMBER()` sobre una vista del sistema como generador de días—
y depende de cuántas filas tenga esa vista. En una instancia recién instalada
puede no alcanzar para un mes entero, y entonces el recuento de días hábiles sale
**bajo, sin ningún error**. Un fallo que solo depende del servidor donde corra.
Aquí los días van en un `VALUES (0)…(31)` explícito.

## Qué guarda cada cosa

| Tabla | Una fila por | Qué guarda |
|---|---|---|
| `MpEmpresa` | empresa vigilada | Nombre, RUT, código de proveedor |
| `MpConsulta` | **petición a la API** | Tipo, fecha/código, intento, HTTP, código de error, duración |
| `MpLicitacion` | licitación vista | Los 4 campos del listado, primera/última vez vista, cuántas veces |
| `MpLicitacionDetalle` | licitación | Organismo, montos, las 7 fechas, adjudicación |
| `MpLicitacionItem` | producto adjudicado | Cantidad, precio unitario, proveedor y la **especificación del comprador** |
| `MpLicitacionEstadoHistorico` | **cambio de estado** | cuándo pasó de Publicada a Adjudicada |

Vistas ya montadas: `vwMpLicitacion` (una fila por licitación, con el total
adjudicado) y `vwMpDetalle` (la ficha con los items en filas).

`MpConsulta` es lo que hace posible la honestidad de la pantalla: sin ella no se
puede distinguir un día comprobado con cero resultados de un día que nadie
preguntó.

## Las cinco decisiones que no son obvias

### 1. `MpConsulta` es una fila por **intento**, no por barrido

La aplicación reintenta hasta 6 veces un día que falla, con esperas de 2, 4, 8,
16, 30 y 30 segundos. Si se guardara solo el resultado final, `diasFallidos = 3`
quedaría registrado y serían en realidad 18 peticiones.

La columna `Origen` distingue lo que salió de la caché en memoria (0 cupo) de lo
que fue una llamada real. Con el ticket limitado a 10.000 consultas diarias, esa
es la única forma de saber cuánto se gastó de verdad.

### 2. Las fechas de la API son `datetimeoffset`, no `datetime2`

El código lee el bloque `Fechas` con `DateTimeOffset` y las pasa a hora local, así
que traen `-03:00` o `-04:00` según horario de verano. Guardarlas sin zona
haría que dos licitaciones que cerraron con una hora de diferencia entre verano e
invierno se vieran con la misma hora, que es justo el dato que dice si algo
alcanzó a cerrarse a tiempo.

Las fechas de calendario (`FechaDia`, `FechaPublicacion`) sí son `date`: son un
día, no un instante.

### 3. Los montos son `decimal(19,4)`, nunca `float`

`"MontoEstimado": 192000000.0` en `double` acaba en `191999999.99999997`. Un
céntimo de diferencia entre lo que dice la API y lo que dice la base significa
que la base ya no es una copia de la API.

### 4. El identificador de la licitación es **clave foránea**, no `IDENTITY`

La primera versión tenía `IDENTITY` propio en las tres tablas y la vista
emparejaba `MpLicitacion.LicitacionId` con `MpLicitacionItem.LicitacionId`. Son
dos columnas que se llaman igual y cuentan por separado: como las filas se
insertaron en orden, ambos juegos salieron 1, 2, 3 y **los items se atribuyeron
a la licitación equivocada sin dar ningún error**.

Es el peor tipo de fallo. Cada tabla estaba bien; solo el cruce mentía, y ya
habría informes con números que nadie va a repasar. La comprobación 7.11 de la
prueba falla en aquel esquema y pasa en este.

### 5. `TotalAdjudicado` es `NULL` cuando no hay items, no `0`

"No se adjudicó nada todavía" y "se adjudicó cero" no son lo mismo. Es la misma
regla que `TotalAdjudicado` en `Models/Licitaciones.cs`.

Además el total se **calcula en la vista**, no se guarda: si se guardara como
columna, bastaría con que un item cambiara para que dejara de cuadrar con sus
propios items, sin forma de saber cuál de los dos números era el bueno.

## Consultas de ejemplo

```sql
-- Todo lo leído, con su error si lo hubo
SELECT FechaHora, TipoConsulta, FechaDia, CodigoLicitacion,
       NumeroIntento, Exito, CodigoHttp, CodigoApi, Mensaje
FROM dbo.MpConsulta
ORDER BY FechaHora DESC;

-- Cuánto cupo del ticket se gastó de verdad
SELECT CASE WHEN Origen = 0 THEN N'llamada real' ELSE N'de la caché' END AS Origen,
       COUNT(*) AS Peticiones
FROM dbo.MpConsulta
GROUP BY Origen;

-- Solo los fallos, con su código
SELECT FechaHora, CodigoHttp, CodigoApi, Mensaje
FROM dbo.MpConsulta
WHERE Exito = 0
ORDER BY FechaHora DESC;

-- Licitaciones de una semana
SELECT CodigoExterno, Nombre, Estado, FechaCierre,
       NombreOrganismo, TotalAdjudicado
FROM dbo.vwMpLicitacion
WHERE FechaPublicacion BETWEEN '2026-10-05' AND '2026-10-11'
ORDER BY FechaCierre;

-- La ficha completa de una, con los productos
SELECT CodigoExterno, NombreProducto, UnidadMedida,
       Cantidad, CantidadAdjudicada, MontoUnitario, Subtotal,
       NombreProveedor
FROM dbo.vwMpDetalle
WHERE CodigoExterno = '7000-D1-D2L510'
ORDER BY Correlativo;

-- Qué se adjudicó a cada proveedor
SELECT NombreProveedor, COUNT(*) AS Items,
       SUM(Subtotal) AS Total
FROM dbo.MpLicitacionItem
WHERE NombreProveedor IS NOT NULL
GROUP BY NombreProveedor
ORDER BY Total DESC;

-- Historial de estados de una licitación
SELECT CodigoEstado, Estado, VistoEn
FROM dbo.MpLicitacionEstadoHistorico
WHERE CodigoExterno = '7000-D1-D2L510'
ORDER BY VistoEn;

-- Cuántas veces se ha visto cada licitación
SELECT CodigoExterno, Nombre, NumeroVecesVista,
       PrimeraVezVista, UltimaVezVista
FROM dbo.MpLicitacion
ORDER BY NumeroVecesVista DESC;
```

## Cómo se escribiría el `UPSERT` de la aplicación

No está en el script, pero es lo que haría falta para llenarlas:

```sql
-- El listado diario
MERGE dbo.MpLicitacion AS destino
USING @licitaciones AS origen
   ON destino.CodigoProveedor = origen.CodigoProveedor
  AND destino.CodigoExterno   = origen.CodigoExterno
WHEN MATCHED THEN UPDATE SET
    Nombre = origen.Nombre, CodigoEstado = origen.CodigoEstado,
    FechaCierre = origen.FechaCierre, UltimaVezVista = SYSUTCDATETIME(),
    NumeroVecesVista = destino.NumeroVecesVista + 1
WHEN NOT MATCHED THEN INSERT
    (CodigoProveedor, CodigoExterno, Nombre, CodigoEstado,
     FechaCierre, FechaPublicacion, ModoConsulta)
VALUES
    (origen.CodigoProveedor, origen.CodigoExterno, origen.Nombre,
     origen.CodigoEstado, origen.FechaCierre, origen.FechaPublicacion,
     origen.ModoConsulta);
```

`MpLicitacionEstadoHistorico` solo escribe cuando `CodigoEstado` cambia. Si
escribiera en cada lectura serían miles de filas diciendo "sigue publicada", que
es justo lo que no hay que almacenar.

## Notas de la prueba

`99-prueba-esquema.sql` mete datos con la forma exacta que produce el importador
(los códigos y montos son los de una respuesta real), comprueba 14 cosas y se
deshace todo. La base queda con las tablas y sin datos.

Y **`98-prueba-importar.sql` también se deshace ahora**, y antes no. Tenía seis
`DELETE FROM` sin transacción ni `ROLLBACK`, y se llevó por delante una base de
desarrollo entera: 921 consultas, 48 licitaciones, 48 detalles y 57 ítems. Para
recuperar hubo que reimportar 2024-2026 desde la API. Que `99` estuviera
protegido y `98` no es justo el motivo por el que la siguiente vez no se pisa:
las dos son ficheros de pruebas que se ejecutan contra la base de desarrollo, y
que una esté documentada como inocua no la vuelve inocua.

Va entero en **un solo lote, sin `GO`**. Dos cosas de `GO` que costaron un rato:

- Una variable declarada antes de un `GO` no existe después.
- Y una transacción abierta antes de un `GO` **tampoco sobrevive**: el
  `ROLLBACK` del final está en otro lote y revierte una transacción vacía
  mientras las filas se quedan. Pasó de verdad, y solo se vio mirando la base
  después de ejecutar, consultando: el mensaje decía que no quedaba nada y
  seguían 13 filas.

Por eso el paso final comprueba que la base quedó vacía y avisa si no.

### `97-prueba-lectura.sql` va contra los datos reales

Esta prueba es la excepción a la regla de arriba, y a propósito:

- **No mete datos.** Solo lee. Si metiera, estaría probando los datos que ella
  misma acaba de escribir en vez de los que se importaron de verdad. Y lo que se
  comprueba es cómo se comporta el procedimiento con datos que nadie fabricó.
- **No lleva transacción**, porque no escribe nada. Una transacción alrededor de
  un `SELECT` solo añade la posibilidad de que se deshaga lo que no se ha
  tocado.
- **Falla si le faltan datos.** Si la base no tiene la licitación
  `1456839-6-LP26`, avisa con `[??]` y lanza, en vez de pasar en verde. Una
  prueba que se salta lo que no puede comprobar es una prueba que un día deja de
  comprobar y nadie lo nota.

Comprobaciones del caso crítico:

| | |
|---|---|
| Del 1 al 8 de octubre de 2026 solo cuentan los **6** días hábiles que pasaron | Los 22 del mes entero no, y los 16 que faltan salen como pendientes, no como fallidos |
| Octubre no cuenta **ningún** día como comprobado | Los días 5, 6 y 7 dieron `203`, que es un fallo pero es un intento |
| Salen los **6** días no comprobados, ni uno más | Ni uno de menos, que sería esconder un fallo; ni uno de más, que sería avisar de un día que sí se miró |
| Los días rechazados dicen que el ticket no valía | Sin el motivo no se puede decidir si reintentar tiene sentido |
| Los días nunca preguntados también salen | Fue justo lo del 1 y el 2 de octubre |