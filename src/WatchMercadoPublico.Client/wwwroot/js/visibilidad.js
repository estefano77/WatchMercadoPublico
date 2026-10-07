// Escucha de visibilidad de la pestaña.
//
// Va en un archivo propio y no dentro de Home.razor por una razón práctica:
// para leer document.hidden hay que pasar por JavaScript, y eso significa un
// módulo de .js aparte. Si estuviera dentro del componente, Blazor lo
// empaquetaría con un nombre que cambia con cada compilación, y el
// import("./archivo") del interop se quedaría apuntando a un archivo que ya no
// existe tras el siguiente despliegue.
//
// Un módulo con nombre fijo, en wwwroot/js/, se puede importar siempre.

const manejadores = new Map();

// Se registra el manejador una sola vez por id, no en cada import.
// Si Home.razor volviera a llamar a observar con el mismo id, el navegador lo
// anotaría dos veces y al volver a la pestaña se harían DOS consultas por el
// mismo hecho. Se lleva la cuenta con esta tabla.
export function observar(objeto, id) {
    if (!id) throw new Error("Falta el identificador de la pantalla.");

    if (!manejadores.has(id)) {
        const fn = () => {
            // La promesa que devuelve invokeMethodAsync se vigila. Sin esto,
            // cualquier fallo es un rechazo sin manejar: no aparece en ninguna
            // parte, no deja rastro y no reintenta nada. Pasa, por ejemplo,
            // cuando la pantalla ya no existe y Blazor rechaza la llamada.
            const trabajo = objeto.invokeMethodAsync("AlCambiarVisibilidad");

            if (trabajo && typeof trabajo.catch === "function") {
                trabajo.catch((error) => {
                    console.error("No se pudo atender el cambio de visibilidad: " + error);
                });
            }
        };
        document.addEventListener("visibilitychange", fn);
        manejadores.set(id, fn);
    }
}

// Suelta la suscripción. Sin esto, cada recarga deja un manejador vivo
// apuntando a una pantalla que ya no existe.
export function soltar(id) {
    const fn = manejadores.get(id);
    if (fn) {
        document.removeEventListener("visibilitychange", fn);
        manejadores.delete(id);
    }
}

// Si el documento está oculto.
export function oculto() {
    return document.hidden;
}