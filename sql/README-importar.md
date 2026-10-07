# Importar a SQL Server

Un procedimiento almacenado que descarga de Mercado Público las licitaciones de
un rango de fechas y las guarda en las tablas de `01-esquema-mercadopublico.sql`.

## Archivos

| Archivo | Qué es |
|---|---|
| `01-esquema-mercadopublico.sql` | Las tablas, vistas e índices |
| `02-registrar-ensamblado.sql` | Registra el ensamblado CLR (una vez por instalación) |
| `03-procedimiento-importar.sql` | `MpImportarRango`, que es el procedimiento en sí |
| `04-funciones-auxiliares.sql` | Lectura del JSON de error y del contador |
| `98-prueba-importar.sql` | Pruebas del camino de error, contra la API real |
| `99-prueba-esquema.sql` | Pruebas de las tablas y las vistas |
| `clr/` | El ensamblado CLR: una función de HTTP |

## Puesta en marcha

```powershell
# 1. Compilar el ensamblado CLR (necesita el SDK de .NET)
dotnet build sql\clr\MercadoPublico.Http.csproj -c Release -o build

# 2. Crear la base
sqlcmd -S localhost\SQLEXPRESS -E -Q "CREATE DATABASE WatchMercadoPublico"

# 3. Decirle dónde está el .dll
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMercadoPublico -Q `
  "IF OBJECT_ID('dbo.MpConfiguracion','U') IS NULL CREATE TABLE dbo.MpConfiguracion (Clave nvarchar(50) PRIMARY KEY, Valor nvarchar(400) NOT NULL);"
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMercadoPublico -Q `
  "INSERT dbo.MpConfiguracion VALUES ('RutaEnsamblado', N'<ruta>\build\MercadoPublico.Http.dll');"

# 4. Tablas, funciones y procedimiento, en este orden
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMercadoPublico -b -f 65001 -i sql\01-esquema-mercadopublico.sql
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMercadoPublico -b -f 65001 -i sql\04-funciones-auxiliares.sql
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMercadoPublico -b -f 65001 -i sql\03-procedimiento-importar.sql

# 5. Registrar el ensamblado (este es el que activa el CLR)
sqlcmd -S localhost\SQLEXPRESS -E -d WatchMercadoPublico -b -f 65001 -i sql\02-registrar-ensamblado.sql
```

El paso 5 hace una prueba de humo contra la API con un ticket falso, y lo que
tiene que salir es:

```
   HTTP 203  |  Ticket no válido.  |  red: ok
```

Un 200 ahí sería una mala noticia: significaría que la API aceptó un ticket que
no existe.

## Uso

```sql
EXEC dbo.MpImportarRango
    @desde           = '2026-10-01',
    @hasta           = '2026-10-05',
    @codigoProveedor = '71284',
    @ticket          = '<tu ticket>',
    @resultado       = @salida OUTPUT;

SELECT @salida;
```

| Parámetro | Por defecto | Qué hace |
|---|---|---|
| `@desde` / `@hasta` | — | El rango. Máximo 400 días |
| `@codigoProveedor` | — | Obligatorio |
| `@ticket` | — | Obligatorio |
| `@conDetalle` | `1` | Además del listado, la ficha completa de cada una |
| `@soloFaltantes` | `0` | Saltar días ya descargados |
| `@maxIntentosDia` | `6` | Igual que en la aplicación |
| `@maxIntentosDetalle` | `3` | Igual que en la aplicación |
| `@segundosEntreLlamadas` | `2` | El ritmo, medido |

## Decisiones que no son obvias

### El transporte es CLR; todo lo demás es T-SQL

T-SQL no puede llamar a una API HTTP. Las dos vías son el CLR y OLE Automation,
y se eligió el CLR porque:

- **Soporta TLS 1.2**, que la API exige. Con OLE Automation la llamada falla con
  *"No se puede crear un canal seguro SSL/TLS"* antes de salir. Se probó en esta
  máquina: MSXML2 y WinHttp fallaron los dos.
- **El mapeo del JSON queda a la vista.** `OPENJSON` de SQL Server 2022 llega de
  sobra al formato de la API, y el campo a campo se lee en el procedimiento. En
  un ensamblado, un error de mapeo solo se ve recompilando.

El ensamblado hace **una sola cosa**: un GET. Nada de reintentos, nada de JSON,
nada de tablas. Es lo mínimo que hay que auditar antes de darle `PERMISSION_SET =
UNSAFE`.

### El ritmo es el que se midió en la aplicación

Dos peticiones seguidas se ganan un `429`. Medido: sin pausa falla una de cada
tres; con 400 ms, casi todas; con 1500 ms, diez de doce pasan.

Se mide el intervalo entre el **principio** de una petición y el principio de la
siguiente, no una espera después de cada respuesta. Como contra la API real las
respuestas tardan más de 1,4 s, el intervalo ya se cumple solo y no se espera
nada salvo que algo vuelva más rápido de lo debido.

### Un rechazo de ticket no se reintenta, aunque la API no lo diga con un 4xx

Mercado Público responde **`HTTP 203`** a un ticket que no vale, en lugar de un
`401`. Es un `2xx`, así que cualquier comprobación del tipo `BETWEEN 200 AND 299`
lo da por bueno.

Esto se vio por prueba, no por revisión: el procedimiento informaba de
*"3 días preguntados con éxito"* con un ticket inventado, sin haber mirado ni una
licitación. La corrección es exigir un `200` exacto, y tratar el `203` como lo
que es: un rechazo de ticket.

Sin las dos correcciones, la prueba de 3 días tardaba **185 segundos** y dejaba
**18 filas** en `MpConsulta` (6 por día) para recibir 18 veces el mismo *"no"*. Con
las dos, tarda **4 segundos** y deja **3 filas**. Un ticket inválido se sabe en el
primer intento.

### Un día que falla no tira el resto del rango

Se anota y se sigue, que es la misma regla que aplica la aplicación con los días
de una semana. Perder un día no puede costar los otros cuatro.

Y un día sin respuesta **no** significa que no hubiera licitaciones. El resumen lo
dice con esas palabras, y los días afectados quedan en `MpConsulta` con
`Exito = 0`.

### Los fines de semana y los días futuros no se preguntan

No es una optimización: la API responde `500` a un día que no existe, y
contarlos como fallidos sería mentira.

### La fecha va en `DDMMAAAA`, con los dos campos rellenos

El 4 de octubre se manda `04102026`. Con `dMMyyyy` serían siete dígitos y la API
respondería `{"Codigo":10300}`. Solo falla entre el 1 y el 9, que es lo que lo
hace parecer intermitente. La prueba comprueba los 365 días del año contra
`FORMAT`.

## Qué rellena

| Origen | Tabla |
|---|---|
| Un GET del listado de un día | `MpConsulta` (una fila **por intento**) + `MpLicitacion` |
| Un GET del detalle de una licitación | `MpConsulta` + `MpLicitacionDetalle` + `MpLicitacionItem` |
| Un cambio de estado | `MpLicitacionEstadoHistorico` |

`MpConsulta` lleva **una fila por intento**, no una por día: con seis intentos,
seis filas. Es lo que hace que *"este día falló 3 veces"* y *"este día costó 18
peticiones"* sean la misma información.

## Consultar lo importado

```sql
-- Que se ha leido y con que resultado
SELECT FechaHora, TipoConsulta, FechaDia, CodigoLicitacion,
       NumeroIntento, Exito, CodigoHttp, CodigoApi, Mensaje
FROM dbo.MpConsulta
ORDER BY FechaHora DESC;

-- Solo los fallos, con el codigo propio de la API
SELECT FechaHora, CodigoHttp, CodigoApi, Mensaje
FROM dbo.MpConsulta WHERE Exito = 0 ORDER BY FechaHora DESC;

-- Las licitaciones de una semana
SELECT CodigoExterno, Nombre, Estado, FechaCierre, NombreOrganismo, TotalAdjudicado
FROM dbo.vwMpLicitacion
WHERE FechaPublicacion BETWEEN '2026-10-05' AND '2026-10-11'
ORDER BY FechaCierre;

-- Todo un detalle, con los productos
SELECT CodigoExterno, NombreProducto, Cantidad, CantidadAdjudicada,
       MontoUnitario, Subtotal, NombreProveedor
FROM dbo.vwMpDetalle
WHERE CodigoExterno = '7000-D1-D2L510'
ORDER BY Correlativo;

-- Que se adjudico a cada proveedor
SELECT NombreProveedor, COUNT(*) AS Items, SUM(Subtotal) AS Total
FROM dbo.MpLicitacionItem
WHERE NombreProveedor IS NOT NULL
GROUP BY NombreProveedor ORDER BY Total DESC;

-- Historico de estados
SELECT CodigoEstado, Estado, VistoEn
FROM dbo.MpLicitacionEstadoHistorico
WHERE CodigoExterno = '7000-D1-D2L510' ORDER BY VistoEn;
```

## Problemas frecuentes

**"CLR is not enabled"** — hay que activar el CLR y **reiniciar el servicio de
SQL Server**. El script lo avisa con un mensaje que lo dice. No reinicia solo: no
debe hacerlo un script.

**"Assembly is not trusted"** — hay que rehacer el registro después de recompilar
el `.dll`, porque el hash es del fichero. El script lo detecta y sustituye solo.

**"Cannot specify date data type as a substitution parameter"** — un problema ya
resuelto dentro del procedimiento; si aparece, es que se está llamando a otra
versión.

**"Incorrect syntax near 'LOCAL'"** — un `DECLARE CURSOR` dentro de un `CREATE
PROCEDURE`. Resuelto: el recorrido es un `WHILE`, sin cursor.

**El `DROP PROCEDURE` no suelta el ensamblado.** Al probarlo, `DROP ASSEMBLY`
seguía diciendo que el procedimiento lo referencia con el procedimiento ya
borrado. Es un comportamiento conocido de los procedimientos `EXTERNAL NAME`. En
la práctica, `02-registrar-ensamblado.sql` instala una sola vez; para actualizar
hay que quitar el procedimiento y el ensamblado a mano, o empezar de cero con la
base.

## Notas de la prueba

`98-prueba-importar.sql` usa un ticket **falso** a propósito: lo que se prueba es
el camino de error, que es la mitad del producto, y es contra el servidor de
verdad. La respuesta correcta es que falle.

Lo que **no** se prueba ahí: el parseo de una ficha real, porque la API no
devuelve nada a un ticket falso. Eso está en `99-prueba-esquema.sql`, con datos
con la forma exacta de una respuesta verdadera. Para comprobar el camino de
éxito hace falta un ticket real, que es lo que se ejecuta en producción.
