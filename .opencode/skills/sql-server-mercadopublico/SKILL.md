---
name: SQL Server y T-SQL
description: Usar al tocar sql/ - el esquema, el importador MpImportarRango, o el ensamblado CLR de sql/clr. Cubre QUOTED_IDENTIFIER por ámbito, lo que T-SQL prohíbe dentro de funciones y procedimientos, y el registro del CLR en Windows.
---

# SQL Server y T-SQL

El proyecto usa SQL Server 2022 para guardar lo que la app lee de Mercado
Público. Está en `sql/`. **La aplicación sigue sin base de datos**: esto es lo
que permite la importación, no lo que la mueve.

Si quieres leer la API, mira `sql/03-procedimiento-importar.sql`. Si quieres
hablar con la API desde el procedimiento, mira `sql/clr/Peticion.cs`.

## El orden de instalación, y por qué importa

```
01-esquema-mercadopublico.sql   tablas, vistas, índices
04-funciones-auxiliares.sql     funciones del JSON de error
03-procedimiento-importar.sql   MpImportarRango
02-registrar-ensamblado.sql     el CLR (una vez)
```

El `02` va al final porque necesita que las tablas existan (crea `MpConfiguracion`)
y porque activa el CLR en el servidor.

**Antes del paso 1**, el CLR tiene que estar activado:

```sql
EXEC sp_configure 'clr enabled', 1; RECONFIGURE;
```

Y esto **requiere reiniciar el servicio de SQL Server**. El script lo avisa, pero
no reinicia solo: no debe hacerlo un script. Si el valor sigue en 0 después del
`RECONFIGURE`, el reinicio está pendiente.

---

## Lo que T-SQL NO permite (y el error no lo dice)

Esto es lo que más tiempo costó, porque **ninguno de los mensajes señala la
causa real**.

### `QUOTED_IDENTIFIER` se reinicia en cada ámbito

Al llamar a un procedimiento almacenado, la sesión crea un **ámbito nuevo** con
los valores por defecto de `SET`. El `SET QUOTED_IDENTIFIER ON` del script que
llama **no se hereda**. Y ponerlo dentro del procedimiento tampoco basta:

```
INSERT failed because the following SET options have incorrect settings:
'QUOTED_IDENTIFIER'
```

Ese error culpa a las columnas calculadas y a los índices filtrados, que están
perfectamente bien. La causa es que hay que ponerlo **antes** del `CREATE`, para
que el módulo se compile con la opción correcta:

```sql
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
CREATE OR ALTER PROCEDURE ...
```

En un script con varios lotes, el `SET` va **después de cada `GO`**: las opciones
no se heredan de un lote al siguiente. Por eso los scripts de `sql/` repiten la
triada unas quince veces, y parece redundante.

### `TRY/CATCH` no existe dentro de una función

```
Invalid use of a side-effecting operator 'BEGIN TRY' within a function
```

Suele no hacer falta: `JSON_VALUE` y `TRY_CONVERT` ya devuelven `NULL` cuando no
pueden leer. Una función que devuelve `nvarchar` tampoco puede llevar `RETURN`
de nada que no sea escalar, así que el último tiene que ser un `RETURN` simple.

### `DECLARE CURSOR` no existe dentro de un `CREATE PROCEDURE`

```
Incorrect syntax near 'LOCAL'
```

El mensaje parece de sintaxis en la palabra `LOCAL`, no de un cursor. Recorre con
un `WHILE` sobre un `IDENTITY` de tabla y te ahorras además el `OPEN`/`CLOSE`/
`DEALLOCATE`, que se queda abierto si algo lanza por el camino.

### `WAITFOR DELAY` no admite expresiones

`WAITFOR DELAY DATEADD(SECOND, @espera, 0)` da `Incorrect syntax near
'DATEADD'`. Solo admite constante o variable: `WAITFOR DELAY @espera`.

### `RAISERROR` no admite `date` ni expresiones

```
Cannot specify date data type (parameter 4) as a substitution parameter
```

Los argumentos tienen que ser **variables**, y no pueden ser de tipo `date`.
Convierte antes:

```sql
DECLARE @txt nvarchar(10) = CONVERT(nvarchar(10), @desde, 23);
RAISERROR('... %s ...', 16, 1, @txt);
```

---

## El CLR: qué se necesita y qué no

Un procedimiento de T-SQL no puede llamar a una API HTTP. Hace falta un
ensamblado. Se eligió el CLR y **no** OLE Automation porque este último no habla
TLS 1.2, que la API exige: la petición falla antes de salir con *"No se puede
crear un canal seguro SSL/TLS"*.

El ensamblado hace **una sola cosa**: un GET. Los reintentos, el JSON y las
tablas están en T-SQL. Es lo mínimo que hay que auditar antes de darle
`PERMISSION_SET = UNSAFE`.

### `net48`, no `netstandard2.0`

El proyecto tiene que apuntar a `net48`. Con `netstandard2.0` falla al registrar:

```
Msg 10301: Assembly 'X' references assembly 'netstandard, version=2.0.0.0'
which is not present in the current database.
```

Y `net10.0` directamente no se registra: SQL Server corre el CLR del .NET
Framework 4.8.

### TLS 1.2 hay que pedirlo explícitamente

En `Peticion.Get`, antes de la petición:

```csharp
ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
// ... y restaurarlo en el finally
```

Sin eso, `codigoHttp = 0` y cuerpo vacío. **No lo subas globalmente**: es un
ajuste de todo el `AppDomain`, que desde T-SQL comparte el servidor entero.

### El hash de confianza es SHA-512, y no lo dice

`sp_add_trusted_assembly` pide `@hash` como `binary(64)`. El SHA-256 son 32
bytes, y pasarlos da:

```
Procedure expects parameter 'hash' of type 'binary(64)/varbinary(64)'
```

...que no menciona que falte recorrerlo. Y si lo rellenas con ceros a 64, el
`sp_add_trusted_assembly` **lo acepta** pero `CREATE ASSEMBLY` responde que no
es de confianza, porque compara los 64 contra los 32 que él calcula.

La solución: el hash que quiere **es el SHA-512**, que sí son 64 bytes.
`SHA512_FILE()` no existe; usa `HASHBYTES('SHA2_512', ...)` sobre el binario
leído con `OPENROWSET(BULK)`, envuelto en `sp_executesql` porque `OPENROWSET`
exige una cadena literal, no una variable.

El hash es del **fichero**: si recompilas, hay que volver a registrar. El script
lo detecta y lo sustituye.

### `sys.trusted_assemblies` usa `description`

La columna con el nombre del ensamblado es `description`, **no** `assembly_name`.
Con `assembly_name` sale `Invalid column name`, que no dice que la columna exista
con otro nombre.

### El nombre del procedimiento va en tres corchetes

```sql
EXTERNAL NAME [Ensamblado].[Espacio.Clase].[Metodo];
```

El punto del espacio de nombres va **dentro** del corchete del medio. Con un solo
corchete el error es `Incorrect syntax near ';'` y no dice nada del formato.

### `DROP PROCEDURE` no siempre suelta el ensamblado

Al probarlo, `DROP ASSEMBLY` seguía diciendo que el procedimiento lo referencia
**con el procedimiento ya borrado**. Es un comportamiento conocido de los
`EXTERNAL NAME`. En la práctica `02-registrar-ensamblado.sql` instala una vez;
para actualizar hay que quitar ambos a mano, o empezar de cero con la base.

### `PERMISSION_SET`

`UNSAFE` es lo único que permite salir a la red. `EXTERNAL_ACCESS` llega a los
recursos de Windows pero no a otro servidor. Es una concesión real de permisos de
sistema: concédela solo si el ensamblado es pequeño y auditable.

---

## El diseño de las tablas

Los detalles están en `sql/README.md`. Lo que hay que respetar al tocar SQL:

- **El identificador de la licitación es clave foránea, no `IDENTITY`.** En las
  tres tablas. Se hizo así después de que la primera versión emparejara
  identificadores con el mismo nombre pero contadores separados: como las filas se
  insertaron en orden, ambos juegos salieron `1, 2, 3` y **los items se
  atribuyeron a la licitación equivocada sin dar ningún error**. Cada tabla
  estaba bien; solo el cruce mentía.
- **`MpConsulta` lleva una fila por *intento*, no por día.** Con seis intentos,
  seis filas. Es lo que hace que "este día falló 3 veces" y "este día costó 18
  peticiones" sean deducibles.
- **`TotalAdjudicado` es `NULL` cuando no hay items**, no `0`. "No se adjudicó
  nada todavía" y "se adjudicó cero" no son lo mismo.
- **`Subtotal` es una columna calculada `PERSISTED`**, no un número guardado. Si
  se guardara, al corregir una cantidad el subtotal se quedaría viejo y el total
  dejaría de cuadrar con sus propios items.
- **Las fechas de la API son `datetimeoffset`, no `datetime2`.** Traen el desfase
  de Chile. Las marcas de auditoría (`FechaHora`, `PrimeraVezVista`) sí son
  `datetime2` en UTC, porque son instantes de esta máquina y no datos de la API.

## Las pruebas

```powershell
sqlcmd -S localhost\SQLEXPRESS -E -d <base> -b -f 65001 -i sql\99-prueba-esquema.sql
sqlcmd -S localhost\SQLEXPRESS -E -d <base> -b -f 65001 -i sql\98-prueba-importar.sql
```

- El `-f 65001` no es adorno: sin él, `sqlcmd` lee el fichero con la página de
  códigos del sistema y los acentos de los comentarios se descuelgan.
- `98` usa un ticket **falso** a propósito: prueba el camino de error contra la
  API real, que es la mitad del producto. Lo correcto es que falle.
- El **camino de éxito no está probado** y no se puede probar sin un ticket de
  verdad. No lo des por bueno.

Y una trampa del `GO`: **un lote y sus variables**. Una transacción abierta
antes de un `GO` no sobrevive al siguiente, y el `ROLLBACK` del final revierte
una transacción vacía mientras las filas se quedan. Se vio: el mensaje decía que
no quedaba nada y había 13 filas. Por eso `99-prueba-esquema.sql` va entero en
un solo lote, sin `GO`, y comprueba al final que la base quedó vacía.
