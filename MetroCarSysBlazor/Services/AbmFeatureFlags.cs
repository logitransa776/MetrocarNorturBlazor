namespace MetroCarSysBlazor.Services;

/// <summary>
/// Interruptores de ESCRITURA de los ABMs migrados con "andamiaje" (pantalla + editor + métodos
/// en <see cref="AbmService"/> ya construidos, pero la escritura queda apagada hasta que el
/// cliente autorice y la tabla pase de dueño a Buslink — regla strangler, skill abm-metrocar).
///
/// Un flag en <c>false</c> significa: el botón Grabar del editor está deshabilitado Y el método
/// <c>Grabar()</c> aborta antes de tocar la base (doble candado). La botonera Agregar/Modificar/
/// Eliminar de la lista sigue deshabilitada aparte (se habilita a mano al activar).
///
/// ─── CÓMO ACTIVAR UN ABM (checklist, ver docs/buslink/ACTIVAR_ABM_VEHICULOS_CHOFERES.md) ───
///  1. Poner su flag en true acá.
///  2. Quitar el <c>Disabled="true"</c> de los botones Agregar/Modificar/Eliminar en la lista.
///  3. Bloquear el ABM correspondiente en FoxPro (permisos 2/3/4 o sacar la barra del menú).
///  4. Apagar la sync DBF→SQL de esa tabla (si no, pisa lo que escriba Blazor).
///  5. Validar alta/modifica/baja con el protocolo de la skill testing-nortur (dos señales +
///     datos ZZTEST reversibles sobre el server local).
///  Fleteros además: coordinar con Facturación (catálogo compartido, un solo dueño).
///
/// Nota: son <c>static readonly</c> (no <c>const</c>) a propósito, para que el compilador NO
/// marque la rama de grabado como código muerto — esa lógica YA existe y debe compilarse.
/// </summary>
public static class AbmFeatureFlags
{
    /// <summary>Escritura del ABM de Fleteros (tabla <c>fletero</c>). Autorización pendiente.</summary>
    public static readonly bool FleterosAbmActivo = false;

    /// <summary>Escritura del ABM de Tipo de Vehículos (tabla <c>vehiculo_tipo</c>). Autorización pendiente.</summary>
    public static readonly bool TiposVehiculoAbmActivo = false;

    // ── Módulo Tráfico: Cabeceras · Francos · Viáticos (05/07/2026) ──
    // ⚠ Estas 5 tablas hacen BAJA FÍSICA (DELETE, sin f_delete) y hoy NO están replicadas en el
    // server nuevo (172.25.69.217) → replicarlas allá antes de activar. Ver skill modulo-trafico.

    /// <summary>Escritura del ABM de Cabeceras/Recorridos (tabla <c>cabecera</c>). Autorización pendiente.</summary>
    public static readonly bool CabecerasAbmActivo = false;

    /// <summary>Escritura del ABM de Francos (tabla <c>chofer_franco</c>). Autorización pendiente.</summary>
    public static readonly bool FrancosAbmActivo = false;

    /// <summary>Escritura del ABM de Viáticos (tabla <c>chofer_viatico</c>). Autorización pendiente.</summary>
    public static readonly bool ViaticosAbmActivo = false;

    /// <summary>Escritura de los catálogos de Viático (tablas <c>chofer_viatico_motivo</c> y
    /// <c>chofer_viatico_liquida</c>). Autorización pendiente.</summary>
    public static readonly bool ViaticoCatalogosAbmActivo = false;

    // ── Módulo Reservas: Operadores · Grupos · Destinos (06/07/2026) ──
    // Baja FÍSICA (DELETE, sin f_delete). Grupo A del plan Buslink: destino + cliente_operador
    // (cutover temprano); Grupo B: cliente_grupo (cambia de dueño el día D — su cascada toca
    // `viaje`, así que se activa junto con el circuito). Ver docs/buslink/PLAN_MIGRACION_BUSLINK.md.

    /// <summary>Escritura del ABM de Operadores (tabla <c>cliente_operador</c>). Autorización pendiente.</summary>
    public static readonly bool OperadoresAbmActivo = false;

    /// <summary>Escritura del ABM de Destinos (tabla <c>destino</c> + <c>destino_localidad</c>). Autorización pendiente.</summary>
    public static readonly bool DestinosAbmActivo = false;

    /// <summary>Escritura del ABM de Grupos (tabla <c>cliente_grupo</c>; su modifica/baja cancela
    /// viajes en cascada sobre <c>viaje</c>). Se activa con el circuito viaje el día D. Autorización pendiente.</summary>
    public static readonly bool GruposAbmActivo = false;

    // ── Módulo Tráfico: Voucher · Guardia · Contactos y Proveedores (06/07/2026) ──
    // Baja FÍSICA (DELETE, sin f_delete) en viaje_guardia / estacion / estacion_rubro; los INSERT
    // solo setean _deleted = 0. id = MAX(id)+1 (no identity). Estas 3 tablas SÍ están replicadas en
    // el server activo (172.25.69.217), a diferencia de las de Francos/Viáticos.
    // ⚠ `estacion` (Contactos) es catálogo COMPARTIDO con el módulo Combustible → coordinar dueño
    //   único al activar (como Fleteros con Facturación).

    /// <summary>Escritura del ABM de Guardias (tabla <c>viaje_guardia</c>). Autorización pendiente.</summary>
    public static readonly bool GuardiaAbmActivo = false;

    /// <summary>Escritura del ABM de Contactos/Proveedores (tabla <c>estacion</c>, compartida con
    /// Combustible). Autorización pendiente.</summary>
    public static readonly bool ContactosAbmActivo = false;

    /// <summary>Escritura del ABM de Rubros de contacto (tabla <c>estacion_rubro</c>). Autorización pendiente.</summary>
    public static readonly bool RubrosContactoAbmActivo = false;

    /// <summary>Escritura de la marca de recepción de Voucher (UPDATE <c>viaje.voucher_re</c>).
    /// Toca la tabla <c>viaje</c> → se enciende con el circuito el DÍA D, no como catálogo suelto.
    /// Autorización pendiente.</summary>
    public static readonly bool VoucherRecepcionActivo = false;

    // ── Módulo Combustible (07/07/2026) ──
    // vehiculo_sobre es la tabla VIVA del módulo (~8.000 cargas/año, dueño FoxPro). La conciliación
    // toca vehiculo_sobre.n_sobre + el numerador GLOBAL parametro.lote_sobre (compartido) → se activa
    // con el circuito el día D, coordinando dueño único. vehiculo_estacion_saldo (depósitos) hace
    // BAJA FÍSICA (DELETE, sin f_delete) y es circuito sin uso desde 2017. Ver
    // docs/PlanoFoxPro/combustible/COMBUSTIBLE_ABM_MENU.md.

    /// <summary>Escritura de la conciliación de cargas (UPDATE <c>vehiculo_sobre.n_sobre</c> +
    /// numerador <c>parametro.lote_sobre</c>) y del ABM de una carga (<c>vehiculo_sobre</c>).
    /// Tabla viva compartida con FoxPro. Autorización pendiente.</summary>
    public static readonly bool ConciliacionCombustibleAbmActivo = false;

    /// <summary>Escritura del ABM de Depósitos de estación (tabla <c>vehiculo_estacion_saldo</c>,
    /// baja física). Circuito histórico sin uso desde 2017. Autorización pendiente.</summary>
    public static readonly bool DepositosCombustibleAbmActivo = false;

    /// <summary>Escritura del ABM de Artículos por rubro de consumo (tabla
    /// <c>estacion_rubro_articulo</c>, baja física, id no-identity). Para rubro 1 son los tipos de
    /// combustible del combo de la carga. Autorización pendiente.</summary>
    public static readonly bool ArticulosRubroAbmActivo = false;

    // ── Módulo Reservas: Reservas Especiales · Plantillas · Armado (07/07/2026) ──
    // Estas 3 son PUERTAS DE ALTA al circuito `viaje` (no catálogos): insertan filas en `viaje`
    // (origen 'T' el alta manual, origen 'P' el armado). Son Fase 4 del plan Buslink y cambian de
    // dueño el DÍA D junto con Tráfico y el Graba de Facturación — NO se activan como catálogo
    // suelto. La lógica de escritura está codificada completa y fiel a los planos
    // (RESERVA_TRANSPORTACION.md / RESERVA_PLANTILLAS.md) pero cada método aborta si su flag es
    // false. reserva_plantilla hace BAJA FÍSICA (DELETE, sin f_delete), id no-identity (MAX(id)+1).
    // Ver docs/buslink/PLAN_MIGRACION_BUSLINK.md.

    /// <summary>Alta manual de reservas especiales (INSERT en <c>viaje</c>, origen 'T', + cliente_grupo
    /// + viaje_log + viaje_adicional + guia). Circuito viaje → día D. Autorización pendiente.</summary>
    public static readonly bool ReservasEspecialesAbmActivo = false;

    /// <summary>ABM de las filas de plantilla (tabla <c>reserva_plantilla</c>, baja FÍSICA, id
    /// no-identity). Grupo B del plan Buslink: se construye antes pero corta el día D (el circuito
    /// FoxPro la escribe hasta el corte). Autorización pendiente.</summary>
    public static readonly bool PlantillasAbmActivo = false;

    /// <summary>Armado masivo desde plantilla (INSERT en <c>viaje</c>, origen 'P', por lote —
    /// <c>parametro.lote_plant</c>). Circuito viaje → día D. Autorización pendiente.</summary>
    public static readonly bool ArmadoPlantillasActivo = false;
}
