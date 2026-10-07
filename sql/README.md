# Persistencia en SQL Server

Guardar en base de datos todo lo que la aplicación lee de Mercado Público, para
poder consultarlo con SQL en vez de con la pantalla.

## Aviso importante

Estos archivos **crean las tablas, nada más**. La aplicación **sigue sin tocar la
base**: hoy funciona sin base de datos, como está en el README del proyecto, y
sigue funcionando así. Para que escriba haría falta otro cambio: añadir el
paquete `Microsoft.Data.SqlClient`, una cadena de conexión en la configuración y
las llamadas de `INSERT`/`UPDATE` en `MercadoPublicoCliente` y
`LicitacionesEndpoints`.

## Los archivos

| Archivo | Qué es |
|---|---|
| `01-esquema-mercadopublico.sql` | Las tablas, las vistas y los índices |
| `99-prueba-esquema.sql` | Mete datos de ejemplo y comprueba 11 cosas |

## Cómo usarlo

```powershell
# Crear la base (una vez)
sqlcmd -S localhost\SQLEXPRESS -E -Q "CREATE DATABASE WatchMercadoPublico"

# Crear el esquema (se puede repetir las veces que haga falta)
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMercadoPublico -b -f 65001 `
        -i sql\01-esquema-mercadopublico.sql

# Comprobarlo
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMercadoPublico -b -f 65001 `
        -i sql\99-prueba-esquema.sql
```

El `-f 65001` no es adorno: sin él, `sqlcmd` lee el fichero con la página de
códigos del sistema y los acentos de los comentarios se descuelgan. En Management
Studio no hace falta; ahí se pega el fichero entero.

## Qué guarda cada cosa

| Tabla | Una fila por | Qué guarda |
|---|---|---|
| `MpEmpresa` | empresa vigilada | Nombre, RUT, código de proveedor |
| `MpConsulta` | **petición a la API** | Tipo, fecha/código, intento, HTTP, código de error, duración |
| `MpLicitacion` | licitación vista | Los 4 campos del listado, primera/última vez vista, cuántas veces |
| `MpLicitacionDetalle` | licitación | Organismo, montos, las 7 fechas, adjudicación |
| `MpLicitacionItem` | producto adjudicado | Cantidad, precio unitario, proveedor |
| `MpLicitacionEstadoHistorico` | **cambio de estado** | cuándo pasó de Publicada a Adjudicada |

Vistas ya montadas: `vwMpLicitacion` (una fila por licitación, con el total
adjudicado) y `vwMpDetalle` (la ficha con los items en filas).

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

`99-prueba-esquema.sql` mete datos con la forma exacta que produce la aplicación
(los códigos y montos son los de una respuesta real), comprueba 11 cosas y se
deshace todo. La base queda con las tablas y sin datos.

Va entero en **un solo lote, sin `GO`**. Dos cosas de `GO` que costaron un rato:

- Una variable declarada antes de un `GO` no existe después.
- Y una transacción abierta antes de un `GO` **tampoco sobrevive**: el
  `ROLLBACK` del final está en otro lote y revierte una transacción vacía
  mientras las filas se quedan. Pasó de verdad, y solo se vio mirando la base
  después de ejecutar, consultando: el mensaje decía que no quedaba nada y
  seguían 13 filas.

Por eso el paso final comprueba que la base quedó vacía y avisa si no.