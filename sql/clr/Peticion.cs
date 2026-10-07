using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

// El proyecto trata los avisos como errores, y este es el que mas se repite:
// los generadores de NET Framework marcan la clase como obsoleta aunque
// funcione. Sigue siendo el camino correcto dentro de SQL Server, porque
// HttpClient vive fuera del CLR de .NET Framework 4.8.
#pragma warning disable CS0618

namespace MercadoPublico.Sql
{
    /// <summary>
    /// Peticion HTTP GET a Mercado Publico.
    ///
    /// Es TODO lo que hace este ensamblado. El JSON, los reintentos y las
    /// tablas estan en T-SQL; aqui solo se habla HTTP.
    ///
    /// Se firma UNICAMENTE para poder registrar el ensamblado. Ver
    /// 02-registrar-ensamblado.sql, donde el motivo esta escrito entero: sin
    /// firma, CREATE ASSEMBLY responde que el ensamblado no es de confianza.
    /// </summary>
    public static class Peticion
    {
#pragma warning restore CS0618
        /// <summary>
        /// Hace un GET y devuelve codigo, cuerpo y error.
        ///
        /// NO LANZA NUNCA. Si falla algo, lo devuelve en <paramref name="error"/>
        /// con codigo 0, para que sea el procedimiento de T-SQL quien decida si
        /// eso merece un reintento. Que el CLR lance excepciones es la forma
        ///rapida de que un error de red se convierta en un error 500 sin
        /// contexto, y aqui los errores de red son NORMALES: Mercado Publico
        /// devuelve 429 de vez en cuando y eso hay que verlo, no tragarselo.
        /// </summary>
        /// <param name="url">URL completa, con el ticket ya dentro.</param>
        /// <param name="segundosTimeout">Techo de la peticion.</param>
        /// <param name="codigoHttp">Codigo de respuesta, o 0 si no hubo respuesta.</param>
        /// <param name="cuerpo">Cuerpo de la respuesta, vacio si no hubo.</param>
        /// <param name="error">Mensaje del fallo, o cadena vacia.</param>
        public static void Get(
            string url,
            int segundosTimeout,
            out int codigoHttp,
            out string cuerpo,
            out string error)
        {
            cuerpo = string.Empty;
            error = string.Empty;
            codigoHttp = 0;

            if (string.IsNullOrWhiteSpace(url))
            {
                error = "La URL va vacia.";
                return;
            }

            // TLS 1.2 HAY QUE PEDIRLO EXPRESAMENTE, y sin esto no funciona
            // ninguna llamada https.
            //
            // Es el punto donde mas rato se perdio. El sintoma es
            //
            //   codigo_http = 0, cuerpo vacio, error:
            //   "Anulada la solicitud: No se puede crear un canal seguro SSL/TLS"
            //
            // que parece un problema de red y del servidor, y no lo es: el .NET
            // Framework 4.8 hereda de Windows los protocolos negociables y en
            // una imagen de Windows actual eso deja SSL 3.0 y TLS 1.0, que
            // Mercado Publico rechaza. La peticion no llega a salir.
            //
            // No se sube ServicePointManager.SecurityProtocol globalmente: es
            // un ajuste de TODO el AppDomain, y desde T-SQL ese AppDomain lo
            // comparte todo el servidor. Si otro procedimiento CLR necesitara
            // TLS 1.0 para hablar con un sistema viejo, se lo romperia. Se fija
            // el valor antes de la peticion y se restaura despues.
            var protocoloAnterior = ServicePointManager.SecurityProtocol;
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;

                hacerLaPeticion(url, segundosTimeout, out codigoHttp, out cuerpo, out error);
            }
            finally
            {
                ServicePointManager.SecurityProtocol = protocoloAnterior;
            }
        }

        private static void hacerLaPeticion(
            string url,
            int segundosTimeout,
            out int codigoHttp,
            out string cuerpo,
            out string error)
        {
            // Los tres se inicializan ANTES de tocar la red, para que cualquier
            // excepcion que se escape leave valores validos y no una excepcion
            // de "variable sin asignar" al volver al T-SQL, que seria un fallo
            // que no tiene nada que ver con el de verdad.
            cuerpo = string.Empty;
            error = string.Empty;
            codigoHttp = 0;

            // El timeout del cliente y el del T-SQL son el mismo numero. Si solo
            // se pusiera aqui, el error seria una excepcion sin relacion con el
            // reintento que el procedimiento ya decidio hacer.
            var reloj = Stopwatch.StartNew();
            var peticion = (HttpWebRequest)WebRequest.Create(url);

            try
            {
                peticion.Method = "GET";
                peticion.Timeout = Math.Max(1, segundosTimeout) * 1000;
                peticion.ReadWriteTimeout = Math.Max(1, segundosTimeout) * 1000;
                peticion.UserAgent = "WatchMercadoPublico/1.0";
                peticion.Accept = "application/json";
                peticion.KeepAlive = false;
                peticion.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (var respuesta = (HttpWebResponse)peticion.GetResponse())
                {
                    codigoHttp = (int)respuesta.StatusCode;
                    cuerpo = Leer(respuesta);
                }
            }
            catch (WebException ex)
            {
                // Un 429 o un 500 LLEGAN como WebException, no como respuesta
                // buena. El cuerpo sigue estando en ex.Response y es donde esta
                // el "Codigo": 10300 de Mercado Publico, que vale oro para
                // diagnosticar. Si se ignorara, un 500 con mensaje se
                // registraria como si no tuviera cuerpo.
                if (ex.Response is HttpWebResponse respuestaConError)
                {
                    try
                    {
                        codigoHttp = (int)respuestaConError.StatusCode;
                        cuerpo = Leer(respuestaConError);
                    }
                    finally
                    {
                        respuestaConError.Close();
                    }
                }
                else
                {
                    // Sin respuesta: DNS, conexion rechazada, TLS, tiempo
                    // agotado. Aqui no hay cuerpo que leer y codigoHttp se
                    // queda en 0, que el T-SQL trata como "no llego nada".
                    error = Describir(ex);
                }
            }
            catch (Exception ex)
            {
                // Red de seguridad. Un fallo raro aqui no debe tumbar el
                // procedimiento entero: se devuelve como error y el T-SQL
                // decide.
                error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                reloj.Stop();

                // No se cierra la peticion a mano, y no es descuido.
                // HttpWebRequest NO es IDisposable en .NET Framework 4.8 (lo es
                // a partir de .NET Core) y tampoco tiene Close(): administra su
                // propio grupo de conexiones. Con KeepAlive = false cada GET
                // cierra la suya al terminar, que es justo lo que se quiere
                // aqui, porque las peticiones van espaciadas y no en paralelo.
            }
        }

        /// <summary>
        /// Lee el cuerpo de la respuesta como texto.
        ///
        /// UTF-8 SIEMPRE, y no la codificacion que venga en la cabecera. Mercado
        /// Publico manda los acentos en UTF-8 y el T-SQL los tiene que poder
        /// escribir tal cual; si se leyera con la del sistema, "Antofagasta"
        /// entraria con signos rotos y eso acabaria en la tabla.
        /// </summary>
        private static string Leer(HttpWebResponse respuesta)
        {
            var flujo = respuesta.GetResponseStream();
            if (flujo == null) return string.Empty;

            using (var lector = new StreamReader(flujo, new UTF8Encoding(false), true))
            {
                return lector.ReadToEnd();
            }
        }

        /// <summary>
        /// El mensaje de un WebException es casi siempre "The remote name could
        /// not be resolved" o similar sin contexto. Se añade la causa, que es
        /// donde esta el detalle util.
        /// </summary>
        private static string Describir(WebException ex)
        {
            if (ex.Status == WebExceptionStatus.Timeout)
                return "Tiempo de espera agotado (" + ex.Message + ")";

            if (ex.InnerException != null)
                return ex.Message + " / " + ex.InnerException.Message;

            return ex.Message;
        }
    }
}