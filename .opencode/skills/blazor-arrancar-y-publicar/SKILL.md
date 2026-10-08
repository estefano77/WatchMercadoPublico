---
name: Arrancar y publicar la Blazor WASM
description: Usar al arrancar la aplicación en local, al compilar Tailwind, al ejecutar los tests, o al publicar en Azure App Service o IIS. Cubre por qué dotnet run deja la página en Cargando, y el target SuperponerClienteBlazor.
---

# Arrancar y publicar la Blazor WASM

## Arrancar en local

```powershell
.\scripts\arrancar.ps1 -Modo demo -Puerto 5180
```

**No uses `dotnet run`.** Deja la aplicación en "Cargando" para siempre.

El target `SuperponerClienteBlazor`, que es lo que deja el sitio en condiciones
de funcionar, corre solo con `AfterTargets="Publish"` **y** con `PublishDir`
informado. Con `dotnet run` no se cumple ninguna de las dos. El script existe
justo para eso.

El script, por orden, hace: comprueba `dotnet` y `npm`, compila el CSS, publica
a `publicacion/`, pasa la configuración por variables de entorno y arranca el
`.exe`. No escribe el ticket en disco ni lo imprime.

## Modos

| Modo | Qué hace |
|---|---|
| `demo` (por defecto) | Datos inventados. No llama a la API ni gasta cupo. |
| `v1` | Datos reales. Necesita ticket en `appsettings.Development.json`. |

Para probar la interfaz sin nada más, `demo`. Para datos reales, copia la
plantilla de configuración y pon el ticket ahí:

```powershell
Copy-Item secrets\appsettings.Development.json.ejemplo `
        src\WatchMercadoPublico.Server\appsettings.Development.json
```

Ese fichero **no se versiona** (está en `.gitignore`). El ticket es una
credencial personal.

## Tailwind

```powershell
npm run css         # compila el CSS una vez
npm run css:watch   # en modo vigilancia
```

**Hay que hacerlo antes de publicar.** Sin esto, `app.css` se queda como está en
el repositorio y los estilos nuevos no se ven. El síntoma es desconcertante: la
página carga bien y solo parece que el CSS no funciona.

## Los tests

```powershell
dotnet test
```

xUnit v2, dos proyectos: `WatchMercadoPublico.Server.Tests` (108 casos) y
`WatchMercadoPublico.Client.Tests` (59). Los dos son puros: **no tocan la red ni
el ticket**.

> Nota: el README dice "134 tests" y en otro sitio "32". El número real medido
> es **167** casos (108 + 59). Si lo corriges, corrige los tres sitios.

Cinco tests usan el reloj de verdad (`Task.Delay` corto para comprobar que un
plazo o un intervalo se respeta). Son los únicos que dependen del tiempo, y
miden milisegundos, no fechas: sobreviven a que cambie el día.

Un solo test cambia el estado global del proceso, la cultura
(`FormatoDeFechaTests`), y la restaura en un `finally`. Si añades otro que toque
`CultureInfo.CurrentCulture`, haz lo mismo.

## Publicar

El flujo de GitHub Actions (`.github/workflows/master_watchmerpub.yml`):

1. `dotnet build --configuration Release` sobre la solución.
2. `dotnet publish -c Release -o ...`
3. Despliega en Azure Web App `watchmerpub`, slot `Production`.

El workflow **compila los proyectos de test pero no ejecuta `dotnet test`**. Si
tocas la API, los tests hay que pasarlos en local.

### No toques el target `SuperponerClienteBlazor`

Está en `src/WatchMercadoPublico.Server/WatchMercadoPublico.Server.csproj` y es
lo que hace que el sitio funcione tras un `dotnet publish`. Al publicar el
servidor, el SDK copia el `index.html` **original** del cliente (con el token
`#[.{fingerprint}]` sin sustituir) junto a los ficheros de `_framework` con
huella. El navegador pide `_framework/blazor.webassembly`, recibe un 404, y la
aplicación se queda en "Cargando".

El target superpone la `wwwroot` ya publicada del cliente en la salida final.

**Y lo hace con la tarea `MSBuild`, no con un `Exec`.** En MonsterASP la política
de grupo prohíbe lanzar procesos hijos, y con `Exec` el publish anidado moría con
`This program is blocked by group policy` y `MSB3073`. El `Exec` externo sí
pasaba, así que el fallo **parecía** del SDK y no del entorno.

### Antes de subir nada

- El `importmap` del `index.html` publicado tiene contenido. En desarrollo está
  vacío a propósito.
- No hay ficheros `.br` ni `.gz` en `_framework`. Si aparecen, en algunos hostings
  llegan vacíos y Blazor se queda en "Cargando" por un fallo de digest. Las
  siete propiedades de `Directory.Build.props` lo desactivan; **la quinta y la
  séptima son las que mandan**.
- Ningún fichero de `_framework` está a 0 bytes.
- El `web.config` es XML válido **y su estructura es válida para el esquema de
  IIS**. Un `[xml]` acepta cosas que IIS rechaza con un `500.19` que no dice
  dónde. `<httpProtocol>` es hermano de `<security>`, no hijo suyo.
- El `web.config` tiene **BOM**. Sin ella, IIS no lee un fichero con acentos y
  devuelve `500.19` con `0x8007000d` sin número de línea.

## En IIS

`scripts/Publicar-Iis.ps1` deja el sitio funcionando. **Debe correr en
PowerShell como administrador**: `appcmd` necesita privilegios incluso para leer
la configuración de IIS.

```powershell
.\scripts\Publicar-Iis.ps1 -Ticket <ticket> -CodigoProveedor <codigo>
```

El guion pone las variables de entorno en el `web.config` de la carpeta
publicada y las vuelve a leer para comprobar que han quedado. Hace eso porque
`appcmd set config` con corchetes y comillas anidadas falló con el código 1168 en
el equipo real, sin avisar de nada.

**El `web.config` del repositorio no lleva las variables.** Un host que lo
reinserte devolverá a leer marcadores de posición como si fueran valores reales.
Medido en `watchmerpub.runasp.net`: `servible=true` con
`empresa='PEGAR-NOMBRE-EMPRESA'`.

## Si algo falla

- **Página en blanco y 404 en la raíz**: casi siempre el proceso arrancó sin
  `WorkingDirectory` y no encuentra `wwwroot`. El `404` no lleva a ninguna parte.
- **"Cargando" para siempre**: el `importmap` o un `0 bytes`. Ver arriba.
- **Todas las rutas tumbadas al arrancar**: un servicio sin registrar, o
  `Configure<T>()` con `T` concreta como parámetro.
- **Los `<option>` de un `<select>` no tienen el color del tema**: no los dibuja
  la página. Hay que estilarlos a mano.
