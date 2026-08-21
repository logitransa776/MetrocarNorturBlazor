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

    /// <summary>Alta de novedades del libro de guardia (<b>F2</b>, INSERT en <c>libro_novedad</c>).
    /// A diferencia del resto de Tráfico, esta tabla NO es del circuito <c>viaje</c>: es propia y
    /// autocontenida, así que podría hacer cutover ANTES del día D (bloquear el alta en FoxPro +
    /// apagar la sync de esta tabla y listo). Se deja apagada por consistencia hasta decidirlo.
    /// El envío de correo al cliente NO se migró — sigue en FoxPro.
    /// Plano: <c>docs/PlanoFoxPro/trafico/TRAFICO_F2_NOVEDADES.md</c>.</summary>
    public static readonly bool NovedadesAbmActivo = false;

    // ── Submenú Tráfico → Libro de Novedades (19/08/2026) ──
    // Plano: docs/PlanoFoxPro/trafico/LIBRO_NOVEDADES.md
    // Los tres ítems del submenú. Ninguna de las dos tablas (`libro_novedad`,
    // `libro_novedad_parametro`) pertenece al circuito `viaje` — las dos son autocontenidas,
    // así que técnicamente podrían cortar antes del día D (como hicieron `usuario` y
    // `parametro`). Se dejan apagadas por consistencia, decisión del usuario 19/08/2026.

    /// <summary>Escritura del <b>ABM de destinatarios</b> de los correos internos
    /// (tabla <c>libro_novedad_parametro</c>, 12 contactos: gerencia, monitoreo, tráfico…).
    /// PK lógica <c>contacto</c> (la tabla no tiene id), baja FÍSICA (DELETE, sin f_delete),
    /// truncado <c>combustible</c> → <c>combustibl</c>. Autorización pendiente.</summary>
    public static readonly bool DestinatariosCorreoAbmActivo = false;

    /// <summary>
    /// <b>Envío de correos</b> del libro (form <c>libro_novedad_envia_correo.scx</c>): manda las
    /// novedades y los siniestros pendientes a la lista de distribución interna y estampa
    /// <c>f_envio</c> en <c>libro_novedad</c> / <c>siniestro</c>.
    ///
    /// 🔴 A diferencia del resto de los flags, éste NO espera solo al día D: es una <b>acción
    /// hacia afuera</b>. Con el flag apagado, Buslink arma y muestra el correo exacto que
    /// saldría y a quiénes, pero no abre el SMTP ni toca <c>f_envio</c> — el Metrocar sigue
    /// siendo el que manda. <b>Encenderlo exige bloquear el ítem en FoxPro el mismo día</b>, o
    /// gerencia y monitoreo reciben cada novedad dos veces.
    ///
    /// Alcance construido: los dos bloques de texto (NOVEDADES y SINIESTROS). Los de
    /// Combustible y Taller (con adjunto Excel/PDF, gobernados por <c>parametro.f_ult_envi</c>)
    /// quedaron para una segunda tanda. Autorización pendiente.</summary>
    public static readonly bool EnvioCorreosActivo = false;

    /// <summary>Escritura del <b>cambio de CRONOGRAMA</b> (teclas F6-F9 / Ctrl+F8 y menú
    /// contextual): UPDATE <c>viaje.cronograma</c> (+ <c>cronogram2</c> en modo diagramador) +
    /// <c>chequeo = 0</c> + <c>viaje_log</c> motivo CBIO UNIDAD en modo operador.
    /// Es la operación MÁS FRECUENTE del circuito (154 cambios/día en 2026) y la de menor
    /// riesgo técnico (no toca <c>vehiculo</c>, ni odómetro, ni GPS), pero escribe en
    /// <c>viaje</c> → día D. Plano: <c>docs/PlanoFoxPro/trafico/TRAFICO_CRONOGRAMA.md</c>.</summary>
    public static readonly bool CronogramaAbmActivo = false;

    /// <summary>Escritura del <b>F4 · Aviso sobre el viaje</b> (UPDATE <c>viaje.hs_aviso</c> +
    /// <c>viaje_log</c> motivo AVISO). Es la escritura de menor superficie de todo el circuito
    /// (una sola columna), pero toca <c>viaje</c> → se enciende el DÍA D con el resto: hasta
    /// entonces la réplica DBF→SQL pisaría lo que escriba Blazor.
    /// El lado LECTURA del F4 (motor de alarmas, popup, columna H.Avi) NO depende de este flag
    /// y ya está activo. Plano: <c>docs/PlanoFoxPro/trafico/TRAFICO_F4_AVISO.md</c>.</summary>
    public static readonly bool AvisoViajeActivo = false;

    /// <summary>Escritura de la marca de recepción de Voucher (UPDATE <c>viaje.voucher_re</c>).
    /// Toca la tabla <c>viaje</c> → se enciende con el circuito el DÍA D, no como catálogo suelto.
    /// Autorización pendiente.</summary>
    public static readonly bool VoucherRecepcionActivo = false;

    // ── Menú contextual del panel BUSES (04/08/2026) ──
    // Plano: docs/PlanoFoxPro/trafico/TRAFICO_BUSES_MENU.md
    // Las tres escrituras del menú pegan sobre `vehiculo` (o sobre el franco del conductor que
    // está logoneado en ella), que es tabla del circuito viaje → cambian de dueño el DÍA D.

    /// <summary>Escritura del <b>logoneo / deslogoneo de conductores</b> desde el panel Buses:
    /// UPDATE <c>vehiculo.id_chofer</c>/<c>id_chofer2</c>/<c>nombre_cho</c>/<c>franco</c>/<c>id_zona</c>
    /// + INSERT en <c>viaje_log_chofer</c> (operación LOGONEO/DESLOGONEO).
    /// ⛔ <b>Bloqueante extra:</b> <c>viaje_log_chofer</c> NO está replicada en SQL (75.001 filas en
    /// el DBF). Si se activa este flag sin la tabla, el UPDATE de <c>vehiculo</c> se graba pero la
    /// bitácora se pierde — el método avisa, pero hay que replicarla ANTES del día D.
    /// Autorización pendiente.</summary>
    public static readonly bool LogoneoAbmActivo = false;

    /// <summary>Escritura de <b>Toma Franco</b> desde el panel Buses (INSERT en <c>chofer_franco</c>
    /// con <c>codigo='F'</c>, <c>motivo='FRANCO'</c>, fecha de hoy).
    /// <c>chofer_franco</c> NO es del circuito <c>viaje</c> —es autocontenida y ya tiene su ABM en
    /// <c>/francos</c>—, así que técnicamente podría hacer cutover antes; se deja apagada por
    /// consistencia con el resto del menú (decisión del usuario, 04/08/2026).
    /// Autorización pendiente.</summary>
    public static readonly bool TomaFrancoActivo = false;

    /// <summary>Escritura de <b>Liberar unidad</b> (UPDATE <c>vehiculo</c>: estado LIBERADO,
    /// <c>hs_inicio</c> NULL, <c>id_viaje</c> 0).
    /// 🔴 Ojo con el nombre del ítem del FoxPro ("pasa a Sin Asignar"): <b>NO toca <c>viaje</c></b>
    /// — ese bloque está comentado en el fuente. Es una liberación de emergencia de la UNIDAD.
    /// Autorización pendiente.</summary>
    public static readonly bool LiberarUnidadActivo = false;

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

    // ── Módulo Sistema: Parámetros Empresa y Generales (12/08/2026) ──
    // Plano: docs/PlanoFoxPro/sistema/PARAMETROS.md · skill modulo-sistema.

    /// <summary>Escritura de <b>Parámetros Empresa, Generales y GPS</b> (UPDATE de columnas
    /// puntuales de la fila única de <c>parametro</c>).
    ///
    /// ✅ <b>ACTIVO desde el 12/08/2026</b> — 2º ABM de escritura real del proyecto, después de
    /// Usuarios. El cliente <b>desconectó <c>parametro</c> del watcher</b> (sync DBF→SQL apagada
    /// para esta tabla), así que su dueño pasa a ser SQL y lo que escriba Buslink se queda.
    ///
    /// 🔴 <b>Deuda que deja este corte anticipado:</b> en la misma fila viven los <b>contadores
    /// vivos</b> del circuito (<c>id_viaje_i</c>, <c>lote_plant</c>, <c>lote_sobre</c>,
    /// <c>stock_movi</c>). Con la sync apagada, FoxPro los sigue incrementando en su DBF y esos
    /// incrementos <b>ya no llegan a SQL</b>: las dos copias divergen. No molesta hoy (Buslink
    /// todavía no genera lotes), pero <b>hay que resincronizar esos 4 números en el día D</b>, o
    /// el primer lote que arme Buslink saldrá repetido. Ver el plan de migración.
    ///
    /// La escritura corrige 3 bugs del fuente FoxPro (§3.4 del plano) y no replica el parseo
    /// Maquina/Instancia de la pantalla de GPS (§4.3: borra la dirección del servidor).</summary>
    public static readonly bool ParametrosAbmActivo = true;

    /// <summary>Botón <b>Vaciar tabla</b> de la solapa GPS de Parámetros (TRUNCATE de la tabla
    /// del SQL EXTERNO del sistema de GPS).
    /// ⛔ A diferencia del resto de los flags, éste NO espera al día D: no toca ninguna tabla
    /// de <c>replicaVPF</c>. Está apagado porque es una operación <b>destructiva sobre un
    /// servidor de terceros</b> — borra el estado de seguimiento de los <b>136 clientes</b>
    /// con <c>cliente.envia_gps = 1</c> (entre ellos AEROLINEAS), que hoy recibe el 93 % de
    /// los viajes vía <c>gps_xlm()</c>. Encender SOLO con autorización explícita y sabiendo
    /// quién consume esa tabla. Plano: <c>docs/PlanoFoxPro/trafico/GPS_XLM.md</c>.</summary>
    public static readonly bool GpsTruncateActivo = false;
}
