/* ===========================================================================
   Funciones auxiliares de la importacion.

   Son TRES cosas y podrian estar dentro del procedimiento, pero estan fuera por
   una razon concreta: usarlas dos veces. El mensaje de error de Mercado Publico
   se busca en el cuerpo de la respuesta al registrar cada intento fallido, y el
   numero de resultados al registrar el exito. Si estuvieran escritas dentro del
   bucle se acabarian duplicadas, y dos copias de una regla del formato del error
   se desincronizan solas en cuanto se toca una.

   =========================================================================== */


/* ---------------------------------------------------------------------------
   MpMensajeApi: el texto de error de Mercado Publico.

   La API responde sus errores en espanol y con DOS formatos distintos:

     {"Codigo":10300,"Mensaje":"El formato del parametro fechas es incorrecto"}
     {"error":{"message":"..."},"Codigo":10300}

   Hay que leer "Mensaje". Si solo se leyera "error.message" o "Message", el
   primer formato se perderia entero: el log pondria "(null)" y la pantalla
   culparia a Mercado Publico de estar con problemas cuando el fallo era
   nuestra URL con la fecha mal formada. Leer "Mensaje" es lo que permitio
   encontrar eso, y ya se cometio el error de no leerlo.
   ------------------------------------------------------------------------- */
/* SIN TRY/CATCH, y no es descuido: T-SQL los PROHIBE dentro de una funcion
   ("Invalid use of a side-effecting operator 'BEGIN TRY' within a function").

   No hacen falta, ademas. JSON_VALUE y TRY_CONVERT ya devuelven NULL cuando el
   JSON no se puede leer o el valor no encaja, que es justo lo que se quiere
   devolver. Un cuerpo que no es JSON devuelve NULL de la busqueda y cae en la
   rama del recorte, que es el comportamiento correcto. */
CREATE OR ALTER FUNCTION dbo.MpMensajeApi (@cuerpo nvarchar(max))
RETURNS nvarchar(1000)
AS
BEGIN
    IF @cuerpo IS NULL OR LEN(LTRIM(@cuerpo)) = 0
        RETURN NULL;

    DECLARE @mensaje nvarchar(1000);

    SELECT @mensaje =
        COALESCE(
            JSON_VALUE(@cuerpo, '$.error.message'),
            JSON_VALUE(@cuerpo, '$.Mensaje'),
            JSON_VALUE(@cuerpo, '$.Descripcion'),
            JSON_VALUE(@cuerpo, '$.message'),
            JSON_VALUE(@cuerpo, '$.Message')
        );

    IF @mensaje IS NOT NULL
        RETURN LEFT(LTRIM(RTRIM(@mensaje)), 1000);

    /* Si el cuerpo empieza por llave o corchete ES json y no tenia ningun
       campo de los de arriba: no hay mensaje que sacar. Si NO empieza por eso,
       es otra cosa —un HTML de error de un proxy, un texto plano— y las
       primeras 300 letras suelen decir de que va. */
    IF LEFT(LTRIM(@cuerpo), 1) IN ('{', '[')
        RETURN NULL;

    RETURN LEFT(LTRIM(@cuerpo), 300);
END
GO


/* ---------------------------------------------------------------------------
   MpCodigoApi: el "Codigo" del error de Mercado Publico.

   El 10300 es "el formato del parametro fechas es incorrecto", que fue error
   NUESTRO y no de la API. Es el dato mas util que se puede guardar cuando algo
   falla, y por eso tiene columna propia en MpConsulta en vez de ir dentro del
   mensaje.
   ------------------------------------------------------------------------- */
CREATE OR ALTER FUNCTION dbo.MpCodigoApi (@cuerpo nvarchar(max))
RETURNS int
AS
BEGIN
    IF @cuerpo IS NULL OR LEN(LTRIM(@cuerpo)) = 0
        RETURN NULL;

    RETURN COALESCE(
        TRY_CONVERT(int, JSON_VALUE(@cuerpo, '$.Codigo')),
        TRY_CONVERT(int, JSON_VALUE(@cuerpo, '$.error.code'))
    );
END
GO


/* ---------------------------------------------------------------------------
   MpCantidadApi: el numero de resultados que anuncia la respuesta.

   Viene en "Cantidad" y es distinto de contar las filas del listado: la API
   cuenta TODAS las que hay en ese dia para ese proveedor, y el listado puede
   venir recortado. Guardar los dos numeros y que se vean distintos es
   informacion; guardar solo uno deja a alguien creyendo que la API no dijo
   nada.
   ------------------------------------------------------------------------- */
CREATE OR ALTER FUNCTION dbo.MpCantidadApi (@cuerpo nvarchar(max))
RETURNS int
AS
BEGIN
    IF @cuerpo IS NULL OR LEN(LTRIM(@cuerpo)) = 0
        RETURN NULL;

    RETURN TRY_CONVERT(int, JSON_VALUE(@cuerpo, '$.Cantidad'));
END
GO