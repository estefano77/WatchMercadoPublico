---
name: Guiones de PowerShell
description: Usar al tocar scripts/ - arrancar.ps1, cargar-base-remota.ps1, instalar-tarea.ps1, tarea-sincronizar.ps1 o Publicar-Iis.ps1. Cubre los cuatro errores de PowerShell que fallan en silencio, los interruptores de sqlcmd y bcp que no son lo que parecen, cómo probar la carga sin tocar la base real, y por qué la base remota se actualiza con una tarea de Windows y no con SQL Agent.
---

# Guiones de PowerShell

Los cinco guiones de `scripts/` hacen lo que en C# no se puede: hablar con
`sqlcmd` y `bcp`, mover una base entera a un hosting, registrar una tarea del
Programador de tareas. Aquí **casi todo se falla en silencio**, y ese es el
motivo de que esta skill exista.

> **Convención: los comentarios de los `.ps1` van SIN tildes**, a diferencia del
> resto del repositorio. No es descuido: de 130 líneas de comentario en
> `cargar-base-remota.ps1` y 58 en `arrancar.ps1`, ninguna las tiene. Se escribe
> "ano", "dia", "razon". Mantenlo, y no lo pases a UTF-8 con tildes "para
> mejorarlo".

## Qué hay aquí

| Guion | Qué hace |
|---|---|
| `arrancar.ps1` | Compila Tailwind, publica en `publicacion/`, pasa la configuración por variables de entorno y arranca. Ver la skill de la Blazor. |
| `Publicar-Iis.ps1` | Deja el sitio en IIS. **Necesita PowerShell como administrador**: `appcmd` no lee la configuración sin privilegios. |
| `cargar-base-remota.ps1` | Compara esquemas, borra el destino y copia la base local a la remota con `bcp`. |
| `tarea-sincronizar.ps1` | El envoltorio que ejecuta la tarea programada. **No se ejecuta a mano.** Existe por lo de más abajo. |
| `instalar-tarea.ps1` | Instala, quita o lanza la tarea diaria. |

---

## Los interruptores que no son lo que parecen

Los tres engañan, y cada uno sale del color de la ayuda:

| Interruptor | Qué es, porque no es lo que parece |
|---|---|
| `bcp -E` | **Conservar los identity**, no la conexión de confianza. Eso es `-T`. En `sqlcmd` es al revés, y `bcp` **no lee `SQLCMDPASSWORD`**: se la pide por consola, una vez por tabla. Hay que pasarle `-P`. |
| `bcp -q` | Pone `QUOTED_IDENTIFIER ON`. Sin él **toda** la carga falla, porque seis índices son filtrados y `MpLicitacionItem.Subtotal` es una columna calculada. En `sqlcmd` lo pone `-I`, y hace falta también: **el `DELETE` falla igual que el `INSERT`**. |
| `bcp -c -C 65001` | UTF-8 en los dos lados. Además **distingue `NULL` de cadena vacía**: `NULL` es un campo de longitud cero y la vacía es el byte `00`. Con `-k` se pierde esa diferencia, y no se usa. |

### ⚠️ `bcp -c` usa TAB y `\r\n` como separadores, y los dos pueden estar DENTRO del dato

El texto libre no tiene por qué estar en una línea ni sin tabuladores.
`bcp -c` usa `\t` para separar **campos** y `\r\n` para separar **filas**, y los
dos pueden aparecer dentro de un valor.

Son **dos bugs distintos**, con dos errores distintos, y arreglar uno no arregla
el otro:

| Qué hay dentro del dato | Qué se rompe | El error que sale |
|---|---|---|
| `\r\n` | Se parte la **fila**; la segunda mitad cae en una columna numérica | `Invalid character value for cast specification` |
| `\t` | Se parte el **campo**; el trozo cae en una columna corta | `String data, right truncation` |

Medido con datos de verdad, en dos tablas distintas:

- `MpLicitacionItem.Descripcion` (la especificación del comprador) con **CR LF**.
  Se perdieron 6 filas de 58. El resto entró bien, así que la copia *parece*
  funcionar.
- `MpLicitacionDetalle.Descripcion` (`nvarchar(max)`, un pliego entero) con un
  **TABULADOR**. Cayó en `Tipo nvarchar(20)`.

Ninguno de los dos errores menciona saltos de línea ni tabuladores, y **ninguno
es ruidoso**: son filas que no se copian y el resto entra bien. Solo los pilla la
comparación de recuentos del final, cuando el destino ya se ha vaciado y
rellenado.

El arreglo son los dos flags, con **el mismo valor al exportar y al importar**:

```powershell
$terminadorCampo = '%%F%%'
$terminadorFila  = '%%R%%'
... 'out', $fichero, '-c', '-C', '65001', '-t', $terminadorCampo, '-r', $terminadorFila
... 'in',  $fichero, '-c', '-C', '65001', '-t', $terminadorCampo, '-r', $terminadorFila
```

`bcp` acepta **más de un carácter** en `-t` y en `-r`, que es lo que hace
posible esto. Con `-r` solo, el TAB sigue partiendo campos.

Medido con las dos tablas reales (53 detalles con 1 TAB y 9 CR LF; 62 ítems con 6
CR LF y 2 descripciones vacías): todas las filas, y el **hash idéntico entre
origen y copia** comparando por bytes. Los TAB y los CR LF se conservan dentro
del texto y las vacías siguen siendo vacías, no `NULL`.

No se limpia el dato para que quepa en el formato: **se cambia el formato**. Un
`REPLACE` de saltos o tabuladores perdería información que escribió el comprador,
que es justo lo que esa columna existe para guardar.

> **Cualquier columna de texto libre puede disparar esto.** No es un caso
> raro: es la primera vez que este proyecto tiene una columna donde el dato
> puede llevar un salto de línea o un tabulador, y `bcp` lleva veinte años dando
> por supuesto que no. Al añadir una columna `nvarchar` de texto libre, copiar
> contra una base real y **mirar los avisos de bcp**, no solo el recuento final.

### ⚠️ `CHECKSUM()` sobre `nvarchar` cambia entre servidores con collation distinta

Al comparar la base local con la del hosting, `CHECKSUM_AGG(CHECKSUM(ItemId,
Descripcion))` daba **distinto en las dos** con los datos idénticos:

```
local   -2065125037     Modern_Spanish_CI_AS
remoto  -2065125165     SQL_Latin1_General_CP1_CI_AS
```

`CHECKSUM()` sobre texto depende de la intercalación, y las dos bases no la
comparten. Para comparar entre servidores hay que pasar por **bytes**:

```sql
CHECKSUM_AGG(CHECKSUM(ItemId, CONVERT(varbinary(8000), Descripcion)))
```

Con eso: `-899995822` en las dos. Antes de dar por rota una copia, comprobar si
la intercalación es la misma.

Sin `-E` cada tabla recibe IDs nuevos y las claves foráneas quedan apuntando a
las filas equivocadas, sin ningún error. Sin `-q`/**`-I`** el error dice
`INSERT failed because the following SET options have incorrect settings`, que
no menciona índices ni columnas calculadas y hace sospechar del archivo de
datos, que es inocente.

Y `sqlcmd -f 65001` en todo lo que se ejecuta con `-i`: sin él lee el fichero con
la página de códigos del sistema.

**El orden lo manda la clave foránea, no el que se te ocurra.** `bcp` no las
tiene en cuenta: el padre antes que el hijo al cargar, y **al revés al borrar**,
que si no `MpLicitacionItem` deja filas apuntando a un `MpLicitacionDetalle` que
ya no existe.

---

## Cuatro cosas que fallan en silencio

### Un array splateado es POSICIONAL; un hash va por nombre

```powershell
$a = @('-Ingerir', '-Si');  & guion.ps1 @a     # Origen = '-Ingerir', Ingerir = False
$p = @{ Ingerir = $true };  & guion.ps1 @p     # Ingerir = True
```

Lo primero se come el primer parámetro del guion. En `cargar-base-remota.ps1` el
primer parámetro es `[string] $Origen`, así que `-Si` acabó ahí, y lo que se veía
desde fuera fue un error de sqlcmd que no decía nada de esto:

```
No se pudo entrar en el origen: Sqlcmd: '-S': Missing argument.
```

Un array a un **ejecutable nativo** (`sqlcmd`, `bcp`) sí está bien: ahí no hay
parámetros con nombre, y es como se sigue haciendo.

### `2>&1` no ve el `Write-Host`

`Write-Host` escribe en el **flujo 6**, no en el 1. Con `2>&1` el pipeline no
recibe ni una línea de un guion que lo cuente todo con `Write-Host`, y el
resultado es un registro con la cabecera, el pie, y **un hueco en medio**. Con
`*>&1` entra todo.

### `$LASTEXITCODE` es pegajoso

Medido: tras un guion que hace `exit 7`, al que sigue sin llamar a `exit`,
`$LASTEXITCODE` **seguía valiendo 7**. Sin ponerlo a cero antes de la llamada, una
tarea que acaba bien se reporta como fallida.

```powershell
$global:LASTEXITCODE = 0
& $guion @opciones
$codigo = $LASTEXITCODE
```

### `if (comando nativo)` mira la SALIDA, no el código

```powershell
if (git check-ignore -q $f) { 'ignorado' }   # siempre falso: -q no imprime nada
if ($LASTEXITCODE -eq 0) { 'ignorado' }      # esto
```

Con `-q`, `-s` o cualquier modo silencioso, el `if` vale siempre falso y parece
que el comando no funciona. Pasó de verdad: `.gitignore` estaba bien y la
comprobación dijo que no.

---

## Un fallo al invocar un guion no lo ve NINGUNA redirección

Esto es lo que obliga a que exista `tarea-sincronizar.ps1`:

```powershell
$salida = & guion.ps1 -NoExiste 2>&1
$salida.Count     # 0
```

Un error al **invocar** el guion -un parámetro mal escrito, el fichero que no
está, una cadena que no vale- lo escribe el motor **antes de montar la tubería de
salida**. Ni `2>&1` ni `*>&1` lo ven. Solo un `try`/`catch` lo ve.

Lo que se veía era una tarea con el código 1 y un registro de **cero bytes**: la
peor forma de fallar, porque no hay nada que leer y el Programador de tareas
tampoco lo explica. El envoltorio lo captura, escribe cabecera con la hora y pie
con el resultado, y propaga el código de salida.

**Antes de escribir una acción de tarea con una redirección, lee esto.** El guion
que se ejecuta tiene que ser el envoltorio, no el de carga.

---

## La sintaxis que muerde al editar

- **El terminador de un here-string va SOLO en su línea.** Un `"@)` al final de
  una línea no lo cierra: el here-string se come el resto del fichero y los
  errores aparecen cincuenta líneas más abajo, con un `El operador '<' está
  reservado para uso futuro` que no habla de un rango de fechas.
- **`[int] @(...)[0]` con un here-string de varias líneas castea el ARRAY
  entero** y falla con `No se puede convertir el valor "System.Object[]" al tipo
  "System.Int32"`, que no dice nada del SQL. Sepáralo en dos sentencias.
- **`Get-Date -Day 1` conserva la hora del momento**, y el guion llegaba a
  imprimir `Rango: 04/01/2026 15:59:59 a 04/30/2026 15:59:59`. A medianoche con
  `-Hour 0 -Minute 0 -Second 0`, aunque al procedimiento solo le llegue
  `yyyyMMdd`.
- **`$fecha - 1` no es aritmética de fechas**: `Operand type clash: date is
  incompatible with int`. Es `$fecha.AddDays(-1)`.
- **`-like` trata `[OK]` como comodín**, así que `Select-String`-os y pruebas por
  coincidencia de salida usan `.Contains()`.
- **Los ficheros de `scripts/` van con CRLF.** Al crear uno nuevo, escríbelo así;
  con LF el editor de edición no lo encuentra y no dice por qué.

---

## Probar la carga sin tocar la base real

La base de desarrollo tiene datos que no se regeneran solos. Para probar contra
una copia:

```powershell
$dir = 'C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\DATA'
# BACKUP/RESTORE solo funciona DENTRO de la carpeta de datos de SQL Server:
# el servicio no tiene permiso en %TEMP%, y lo dice con un error que no
# menciona permisos.
```

Y para borrar el `.bak` hace falta `cmd /c del /f /q`: PowerShell da
"Acceso denegado" sobre ficheros que el servicio de SQL Server tiene abiertos.

**Con la copia, probar el camino de error sale gratis.** Un ticket que no vale
hace que la API conteste 203, y el procedimiento **no reintenta un 203**: se
ejercita toda la llamada, el resumen y los avisos sin descargar nada. Un UUID
inventado del tipo `11111111-2222-3333-4444-555555555555` basta, y no uses
`CAMBIAR-ESTE-VALOR`: ese es el marcador de la plantilla, y el guion lo rechaza a
propósito para no gastar cuota con una cadena vacía.

---

## La base remota se actualiza con una tarea de Windows

```powershell
.\scripts\instalar-tarea.ps1                # diario a las 06:30, con ingesta
.\scripts\instalar-tarea.ps1 -SinIngesta     # solo copia, sin gastar cuota
.\scripts\instalar-tarea.ps1 -EjecutarAhora  # la lanza hoy
.\scripts\instalar-tarea.ps1 -Quitar
```

**No se usa SQL Server Agent porque la instalación es Express**, y Express no
trae el servicio `SQLSERVERAGENT`: no es que no se pueda configurar, es que no
existe. Y no se sube `MinutosEntreIngestas` a propósito, por tres razones que
conviene repetir antes de "arreglarlo":

1. **El ticket no sube al hosting.** Con la ingesta en el servidor habría que
   subirlo, y es una credencial con un tope de 10.000 consultas al día.
2. **La ingesta del servidor solo ocurre con la aplicación abierta.** Un domingo
   por la tarde la base remota seguiría con los datos del viernes.
3. Se puede apagar sin tocar la aplicación.

Tres ajustes que se configuraron a conciencia:

| Ajuste | Por qué |
|---|---|
| `IgnoreNew` | Si una pasada sigue cuando llega la siguiente, no arrancar la nueva. Si no, se encadenan dos copias que se borran el destino mutuamente: no dan error, solo datos a medias. |
| `StartWhenAvailable` | Si el equipo estaba apagado, que corra al encenderlo. |
| `Interactive` | `sqlcmd -E` es autenticación de Windows y necesita la identidad del usuario. **La contrapartida: con la sesión cerrada la tarea no corre**, y espera a que se inicie. |

Y el registro va a `%LOCALAPPDATA%\WatchMerPub`, **fuera del repositorio**: es un
fichero que se rellena solo y cada línea puede llevar la cadena de conexión con
su contraseña. Hay un `*.log` en `.gitignore` como red.

### Lo que hace la copia, y lo que borra

```powershell
.\scripts\cargar-base-remota.ps1 -SoloComprobar                  # compara y no toca nada
.\scripts\cargar-base-remota.ps1 -Ingerir -Si                   # últimos 30 días
.\scripts\cargar-base-remota.ps1 -Ingerir -Anio 2026 -Mes 3 -Si  # un mes entero
```

- **`-SoloComprobar` antes que nada.** Y `-Ingerir -SoloComprobar` es la forma de
  rellenar la base local sin que la copia se lleve por delante nada.
- **El rango por defecto son los últimos 30 días, no el mes en curso**, porque con
  el mes entero se gastaría cuota en días que ya están. Un mes concreto sí va
  entero, porque si se pide marzo es porque falta marzo.
- **El atajo de "ese mes ya está" cuenta días hábiles, y en los dos lados.** El
  procedimiento no pregunta los fines de semana, así que comparar contra los 30
  días de calendario hacía que el atajo **no se activara nunca**: 22 consultados
  nunca llegan a 30. Y el recuento de consultados filtra también por día hábil,
  porque en `MpConsulta` hay fines de semana de cuando alguien importó a mano, y
  el mensaje llegaba a decir "27 días hábiles consultados de 22".
- **La copia BORRA el destino antes de rellenarlo.** Quien esté mirando la
  página ve una base vacía durante esos segundos. Es el precio de la
  automatización, y la razón de que el defecto sea a las 06:30.
