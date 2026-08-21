using MudBlazor;

namespace MetroCarSysBlazor.Services;

/// <summary>Cómo se pinta un bloque de ayuda.</summary>
public enum AyudaTono
{
    /// <summary>Explicación común.</summary>
    Normal,

    /// <summary>
    /// Lo que el informe NO dice. Va destacado en ámbar y SIEMPRE al final: es el bloque que
    /// evita que alguien lleve un número a una reunión sin saber qué le falta.
    /// </summary>
    Limite,

    /// <summary>Dato de contexto sobre el origen de los datos.</summary>
    Datos,
}

/// <summary>Una línea de ayuda: un término de la pantalla y qué significa.</summary>
/// <param name="Termino">
/// El texto EXACTO que el usuario ve en pantalla (el label del KPI, el encabezado de la
/// columna). Si no coincide, la ayuda no se encuentra sola.
/// </param>
public sealed record AyudaItem(string Termino, string Explicacion);

/// <summary>
/// Un bloque de la ayuda de un informe. El <paramref name="Id"/> es el ancla: un panel de la
/// pantalla pone <c>&lt;AyudaReporte Bloque="ranking" /&gt;</c> y el modal abre directo acá.
/// </summary>
public sealed record AyudaBloque(
    string Id,
    string Titulo,
    string Icono,
    IReadOnlyList<AyudaItem> Items,
    AyudaTono Tono = AyudaTono.Normal,
    string Intro = "");

/// <summary>La ayuda completa de un informe.</summary>
public sealed record AyudaInforme(
    string Ruta,
    string Resumen,
    IReadOnlyList<AyudaBloque> Bloques);

/// <summary>
/// AYUDA CONTEXTUAL DE LOS INFORMES — el texto que explica cada pantalla, parte por parte.
///
/// Vive en C# (y no en Markdown ni en la base) a propósito: habla de campos, filtros y
/// límites del dato, así que la escribe quien toca el código y se versiona con él. Un informe
/// cuyo cálculo cambia y cuya ayuda no, es peor que no tener ayuda.
///
/// Se consume desde el componente <c>&lt;AyudaReporte /&gt;</c>: el botón "?" del título abre
/// el modal completo y los íconos ⓘ de cada panel lo abren posicionado en su bloque.
///
/// 🔴 REGLA DE CONTENIDO: el bloque de tono <see cref="AyudaTono.Limite"/> NO es opcional.
/// Todo informe tiene algo que no dice, y ese es el bloque que lo declara. Lo que hoy está
/// escrito como advertencia en el comentario del service va acá, en castellano de usuario.
/// </summary>
public static class InformesAyuda
{
    private static readonly List<AyudaInforme> _todas = new()
    {
        // ── PANEL DE TERCERIZACIÓN ─────────────────────────────────────────
        new AyudaInforme(
            "panel-tercerizacion",
            "Muestra qué parte de los viajes los hace la flota de NORTUR y qué parte se le da a "
            + "fleteros (transportistas contratados), y dónde se apoya más esa contratación.",
            new List<AyudaBloque>
            {
                new("kpis", "Los indicadores de arriba", Icons.Material.Filled.Dashboard,
                    new List<AyudaItem>
                    {
                        new("Viajes prestados",
                            "Todos los viajes del período que salieron con una unidad asignada, "
                            + "sean propios o de terceros. No incluye los cancelados."),
                        new("Tercerizado",
                            "Qué porcentaje de esos viajes lo hizo un fletero y no la flota propia. "
                            + "Es la cifra principal del informe."),
                        new("Fleteros",
                            "Cuántos transportistas distintos prestaron al menos un viaje, y cuánto "
                            + "se lleva el más grande de todos ellos."),
                        new("Pax de terceros",
                            "Pasajeros transportados por fleteros, y qué parte del total representan."),
                        new("Km de terceros",
                            "Kilómetros hechos por fleteros. Si este porcentaje es bastante mayor "
                            + "que el de viajes, quiere decir que lo que se terceriza son los "
                            + "recorridos más largos."),
                        new("Sin cubrir",
                            "Viajes que quedaron SIN ASIGNAR: nunca tuvieron unidad, ni propia ni "
                            + "de tercero. Es demanda que no se llegó a cubrir y por eso se cuenta "
                            + "aparte."),
                    }),

                new("ranking", "Quién presta los viajes", Icons.Material.Filled.FormatListNumbered,
                    new List<AyudaItem>
                    {
                        new("La lista",
                            "NORTUR (la flota propia) y cada fletero, ordenados por cantidad de "
                            + "viajes. La barra es proporcional al primero de la lista."),
                        new("Clic en una fila",
                            "Enfoca todo el tablero en ese prestador: los indicadores, el desglose "
                            + "y la ficha pasan a mostrar solo lo suyo. Se vuelve atrás con "
                            + "«Quitar filtro» o haciendo clic de nuevo en el mismo."),
                        new("La ficha de abajo",
                            "Con un fletero enfocado aparece su detalle: cuántas unidades puso, "
                            + "para cuántos clientes trabajó, cuántos días operó y cuántos viajes "
                            + "hizo cada unidad suya."),
                    },
                    Intro: "El reparto de la operación entre la flota propia y cada transportista."),

                new("evolucion", "Propio vs tercerizado, mes a mes", Icons.Material.Filled.BarChart,
                    new List<AyudaItem>
                    {
                        new("Las barras",
                            "Cada mes muestra los viajes propios (azul) apilados con los "
                            + "tercerizados (naranja). El alto total es el volumen del mes."),
                        new("Los porcentajes de abajo",
                            "El porcentaje tercerizado de cada uno de los últimos meses. Sirve para "
                            + "ver si la dependencia de terceros sube o baja en el tiempo."),
                    }),

                new("desglose", "Tercerización por cliente / servicio / tipo", Icons.Material.Filled.TableChart,
                    new List<AyudaItem>
                    {
                        new("Desglosar por",
                            "El selector de la barra de filtros cambia el criterio de la tabla "
                            + "entre cliente, servicio y tipo de unidad. Se aplica al instante."),
                        new("Reparto",
                            "La barra azul/naranja muestra de un vistazo la mezcla entre propio y "
                            + "tercerizado de esa fila."),
                        new("Otras N",
                            "La cola de categorías con poco volumen, agrupada en una sola fila para "
                            + "que el TOTAL cierre exactamente con los indicadores de arriba."),
                    },
                    Intro: "Dónde se apoya la operación en terceros. Ordenado por viajes tercerizados."),

                new("oportunidad", "¿Se contrató afuera teniendo unidades sin salir?", Icons.Material.Filled.CompareArrows,
                    new List<AyudaItem>
                    {
                        new("Unid. propias",
                            "Cuántas unidades activas de NORTUR hay de ese tipo de vehículo."),
                        new("Días-unidad sin salir",
                            "La suma de días en que una unidad propia de ese tipo no registró ningún "
                            + "viaje. Si una unidad no salió en 10 días, aporta 10."),
                        new("Revisar",
                            "Aparece cuando se tercerizaron viajes de un tipo del que además hubo "
                            + "unidades propias paradas. Es una señal para mirar, no un problema "
                            + "confirmado."),
                        new("Por qué se abre por tipo",
                            "Porque tercerizar una VAN no se cubre con un BUS parado: comparar el "
                            + "total sin distinguir el tipo de vehículo daría una conclusión falsa."),
                    },
                    Intro: "El cruce entre lo que se contrató afuera y la flota propia que no trabajó."),

                new("datos", "De dónde salen los datos", Icons.Material.Filled.Storage,
                    new List<AyudaItem>
                    {
                        new("Quién prestó el servicio",
                            "Se toma del titular de la unidad que quedó asignada al viaje. Es el "
                            + "dato que carga el diagramador al asignar la unidad."),
                        new("El período",
                            "Filtra por fecha del viaje (no por fecha de carga de la reserva)."),
                        new("Qué se excluye",
                            "Los viajes cancelados no se cuentan en ninguna cifra del informe."),
                        new("Nombres de los fleteros",
                            "La razón social sale del catálogo de Fleteros; si un código no "
                            + "estuviera en el catálogo, se muestra el código tal cual."),
                    },
                    Tono: AyudaTono.Datos),

                new("limites", "Qué NO dice este informe", Icons.Material.Filled.WarningAmber,
                    new List<AyudaItem>
                    {
                        new("No dice cuánto CUESTA tercerizar",
                            "El informe mide volumen (viajes, pax, km), no dinero. La liquidación a "
                            + "fleteros no se usa desde diciembre de 2023, así que no hay contra qué "
                            + "valorizar lo tercerizado."),
                        new("«Días-unidad sin salir» no es capacidad disponible",
                            "Una unidad sin viajes ese día pudo estar en el taller, sin chofer "
                            + "asignado o el chofer de franco. Y en un feriado figura parada toda "
                            + "la flota: el día con más unidades quietas del año suele ser el 1 de "
                            + "enero. El número sirve para preguntar, nunca para concluir."),
                        new("No mide si el fletero era evitable",
                            "Un viaje puede haberse dado a un tercero por contrato con el cliente, "
                            + "por zona o por tipo de servicio, y no por falta de unidades."),
                    },
                    Tono: AyudaTono.Limite),
            }),

        // ── PANEL DE FLOTA ─────────────────────────────────────────────────
        new AyudaInforme(
            "panel-flota",
            "Contesta tres preguntas sobre la flota: qué unidades hay y de qué tipo, de cuáles "
            + "falta (medido por los viajes que quedaron sin unidad) y cuáles no se están usando.",
            new List<AyudaBloque>
            {
                new("kpis", "Los indicadores de arriba", Icons.Material.Filled.Dashboard,
                    new List<AyudaItem>
                    {
                        new("Unidades",
                            "Cuántas unidades hay en el universo elegido (propias, contratadas y/o "
                            + "dadas de baja). Es una foto de HOY, no del período."),
                        new("Butacas",
                            "La capacidad instalada: la suma de asientos de esas unidades."),
                        new("Sin uso",
                            "Unidades activas que no hicieron un solo viaje en el período. Son las "
                            + "candidatas a revisar: puede ser flota de más, o una unidad rota que "
                            + "nadie dio de baja."),
                        new("Viajes sin asignar",
                            "Viajes que se pidieron y quedaron sin unidad. Es la medida del "
                            + "faltante de flota que usa este informe."),
                        new("Antigüedad promedio",
                            "Años promedio según el año de modelo. Las unidades sin año cargado no "
                            + "entran en el promedio."),
                    }),

                new("filtros", "Los filtros de arriba", Icons.Material.Filled.FilterAlt,
                    new List<AyudaItem>
                    {
                        new("Universo",
                            "Qué unidades entran en el conteo: propias, contratadas y/o dadas de "
                            + "baja (vendidas o desafectadas). Las bajas están para mirar el "
                            + "histórico. Se aplica al instante, sin volver a consultar."),
                        new("Desglosar por",
                            "Con qué criterio abrir la flota: por tipo de vehículo, por titular, "
                            + "por antigüedad o por capacidad de butacas. Cambia el KPI del medio, "
                            + "el gráfico y la tabla de detalle."),
                        new("El período",
                            "Afecta SOLO la actividad (viajes, días trabajados, km, sin asignar). "
                            + "La cantidad de unidades es siempre la de hoy."),
                    }),

                new("oferta", "Oferta vs demanda por tipo de vehículo", Icons.Material.Filled.CompareArrows,
                    new List<AyudaItem>
                    {
                        new("Unidades / Trabajaron",
                            "Cuántas unidades activas hay de ese tipo y cuántas de ellas hicieron "
                            + "al menos un viaje."),
                        new("Pedidos",
                            "Viajes que pidieron ese tipo de vehículo en el período."),
                        new("Sin asignar y % sin cubrir",
                            "Cuántos de esos pedidos quedaron sin unidad, y qué porcentaje "
                            + "representan. Un porcentaje alto marca un tipo de vehículo que falta."),
                        new("Viajes/unidad",
                            "Carga media de trabajo por unidad de ese tipo. Sirve para comparar un "
                            + "tipo exigido contra otro subutilizado."),
                        new("El tipo pedido puede no existir en la flota",
                            "Lo que se pide en la reserva y lo que hay en el padrón son dos listas "
                            + "distintas: puede haber un tipo muy pedido del que no hay unidades, y "
                            + "unidades de un tipo que nadie pide por ese nombre."),
                    },
                    Intro: "El cruce entre lo que hay y lo que se pidió, tipo por tipo."),

                new("detalle", "Detalle por la dimensión elegida", Icons.Material.Filled.TableChart,
                    new List<AyudaItem>
                    {
                        new("Propias / Contratadas / De baja",
                            "Cómo se reparte cada categoría entre esos tres universos."),
                        new("Sin uso",
                            "Unidades de esa categoría que no registraron viajes en el período."),
                        new("Clic en una fila o en el gráfico",
                            "Enfoca todo el tablero en esa categoría. Se quita con el chip «Quitar "
                            + "filtro» o haciendo clic de nuevo en la misma."),
                    }),

                new("datos", "De dónde salen los datos", Icons.Material.Filled.Storage,
                    new List<AyudaItem>
                    {
                        new("Las unidades",
                            "Del padrón de vehículos, cruzando por dominio (patente) con los viajes "
                            + "del período."),
                        new("Los kilómetros",
                            "De las lecturas mensuales de odómetro, no de los viajes."),
                        new("Qué se excluye",
                            "Los viajes cancelados no cuentan como actividad de la unidad."),
                    },
                    Tono: AyudaTono.Datos),

                new("limites", "Qué NO dice este informe", Icons.Material.Filled.WarningAmber,
                    new List<AyudaItem>
                    {
                        new("El plantel es la foto de HOY, no del período",
                            "El sistema no guarda la historia de altas y bajas de la flota, así que "
                            + "no se puede saber cuántas unidades había en marzo. Si movés el "
                            + "Desde–Hasta cambian los viajes y los km, pero NO la cantidad de "
                            + "unidades."),
                        new("«Sin uso» no siempre es flota de sobra",
                            "Una unidad puede no haber salido porque está en reparación, esperando "
                            + "papeles o sin chofer. El informe marca cuáles son para que alguien "
                            + "las revise, no afirma que sobren."),
                        new("Los kilómetros pueden estar incompletos",
                            "Las lecturas de odómetro se cargan a mano y tienen errores de tipeo "
                            + "gruesos. Los meses con un recorrido imposible se descartan del total "
                            + "y se cuentan aparte, así que el km de una unidad puede corresponder "
                            + "a menos meses que los del período."),
                        new("La antigüedad depende de un dato flojo",
                            "El año de modelo está mal cargado o vacío en parte del padrón. Esas "
                            + "unidades quedan como «sin dato» en vez de inventarles una edad."),
                    },
                    Tono: AyudaTono.Limite),
            }),

        // ── PANEL DE CLIENTES ──────────────────────────────────────────────
        new AyudaInforme(
            "panel-clientes",
            "Muestra qué clientes sostienen el negocio y cuánto vale cada uno, cuáles se están "
            + "yendo o cayendo, y en qué estado está el padrón de clientes.",
            new List<AyudaBloque>
            {
                new("vistas", "Las tres vistas del informe", Icons.Material.Filled.ViewCarousel,
                    new List<AyudaItem>
                    {
                        new("Cartera",
                            "El ranking de clientes del período: cuánto factura, cuántos viajes y "
                            + "pasajeros mueve cada uno, y qué tan concentrado está el negocio."),
                        new("Retención y riesgo",
                            "Compara el período elegido contra un período base para detectar quién "
                            + "se fue, quién cayó fuerte y quién es nuevo."),
                        new("Salud del padrón",
                            "El estado del catálogo de clientes: cuántos operan de verdad, cuántos "
                            + "están incompletos y qué hay para depurar. Esta vista NO usa el "
                            + "período: el padrón es el catálogo completo."),
                    }),

                new("cartera", "Vista Cartera", Icons.Material.Filled.Leaderboard,
                    new List<AyudaItem>
                    {
                        new("Facturado",
                            "Lo devengado en el período, calculado del detalle de cada liquidación "
                            + "y asignado al mes en que se hizo el VIAJE (no al mes en que se emitió "
                            + "la factura)."),
                        new("Por viaje",
                            "Facturación promedio por viaje. Sirve para comparar clientes que "
                            + "mueven volúmenes parecidos pero dejan muy distinta plata."),
                        new("Top 5",
                            "Qué parte del total se llevan los cinco clientes más grandes: la "
                            + "medida de cuán concentrado está el negocio."),
                        new("Métrica y Agrupar por",
                            "Cambian qué se mide (facturado, viajes, pasajeros) y con qué criterio "
                            + "se agrupa. Se aplican al instante."),
                    }),

                new("retencion", "Vista Retención y riesgo", Icons.Material.Filled.TrendingDown,
                    new List<AyudaItem>
                    {
                        new("Comparar contra",
                            "Elegís el período base: el anterior de igual duración, o el mismo "
                            + "período del año pasado. El año pasado saca el efecto de la "
                            + "temporada, que en turismo pesa mucho."),
                        new("Se fueron y Perdido",
                            "Clientes que facturaban en el período base y en este no facturaron "
                            + "nada, y cuánta plata representaban."),
                        new("Cayeron fuerte",
                            "Clientes que siguen operando pero bajaron más de un 40% respecto del "
                            + "período base."),
                        new("Dependencia",
                            "Qué parte del negocio depende del cliente más grande."),
                        new("Matriz ABC",
                            "Clasifica por peso real: A son los que suman el primer 80% de la "
                            + "métrica, B hasta el 95%, C la cola. Los que se fueron conservan la "
                            + "clase que tenían ANTES: si no, todos caerían en C y el caso grave "
                            + "pasaría desapercibido."),
                    }),

                new("padron", "Vista Salud del padrón", Icons.Material.Filled.FactCheck,
                    new List<AyudaItem>
                    {
                        new("Operan hoy / Nunca operaron",
                            "Cuántos clientes del catálogo tuvieron actividad reciente y cuántos no "
                            + "tuvieron un solo viaje en toda su historia."),
                        new("Los dos números de cada problema",
                            "Cada fila muestra cuántos registros tienen ese problema y —lo "
                            + "importante— cuántos de esos operan hoy. Eso separa lo urgente de lo "
                            + "histórico: 405 clientes sin contacto asusta menos cuando se ve que "
                            + "solo 49 están operando."),
                        new("Grupos por CUIT",
                            "Varios códigos de cliente con el mismo CUIT NO son fichas duplicadas: "
                            + "son el mismo grupo con un código por centro de facturación. Ojo que "
                            + "en el ranking de Cartera se cuentan por separado, así que el grupo "
                            + "pesa más de lo que parece."),
                    }),

                new("datos", "De dónde sale la plata", Icons.Material.Filled.Storage,
                    new List<AyudaItem>
                    {
                        new("Se calcula del detalle, no del total",
                            "Cada línea de la liquidación se suma como importe más incremento menos "
                            + "descuento. El total de cabecera no se usa porque tiene cargas "
                            + "corruptas que lo hacen inservible."),
                        new("Cada línea con su moneda",
                            "Una misma liquidación puede mezclar líneas en pesos y en dólares; cada "
                            + "una se convierte con la cotización de su comprobante."),
                        new("Se imputa al mes del viaje",
                            "La plata se asigna al mes en que se prestó el servicio, no al mes en "
                            + "que se facturó. Así el informe habla de operación y no de "
                            + "administración."),
                    },
                    Tono: AyudaTono.Datos),

                new("limites", "Qué NO dice este informe", Icons.Material.Filled.WarningAmber,
                    new List<AyudaItem>
                    {
                        new("No dice quién PAGÓ",
                            "La fecha de pago está vacía en el sistema desde 2024, así que no se "
                            + "puede calcular deuda, mora ni cobranza. Todo lo que se ve acá es "
                            + "facturado, no cobrado."),
                        new("Los últimos meses parecen caer y no siempre caen",
                            "La liquidación va detrás del viaje: los meses más recientes tienen "
                            + "servicios prestados que todavía no se facturaron. Cuando la "
                            + "cobertura es baja, el informe lo avisa arriba — sin ese aviso, el "
                            + "número engaña."),
                        new("Los importes son pesos corrientes",
                            "No están ajustados por inflación: comparar un mes de hace dos años "
                            + "contra hoy mezcla crecimiento real con aumento de precios. La "
                            + "cotización guardada en el sistema quedó congelada en 2019, así que "
                            + "tampoco se puede dolarizar la serie."),
                        new("No hay un campo «tipo de cliente»",
                            "Las categorías (línea de negocio, tipo fiscal, segmento) se deducen de "
                            + "otros datos, no están cargadas como tales."),
                    },
                    Tono: AyudaTono.Limite),
            }),

        // ── PANEL DEL OPERADOR ─────────────────────────────────────────────
        new AyudaInforme(
            "panel-operador",
            "Muestra quién carga las reservas en el sistema, con cuánta anticipación al viaje, "
            + "con qué calidad, y quién entra a modificar lo que cargó otro.",
            new List<AyudaBloque>
            {
                new("kpis", "Los indicadores de arriba", Icons.Material.Filled.Dashboard,
                    new List<AyudaItem>
                    {
                        new("Reservas cargadas",
                            "Cuántas reservas se cargaron en el período. Ojo: cargadas, no viajadas."),
                        new("Operadores",
                            "Cuántas personas distintas cargaron al menos una reserva. Si además hay "
                            + "alguien que solo corrigió, se avisa al lado."),
                        new("Concentración",
                            "Qué porcentaje de las altas hizo la persona que más cargó. Es el "
                            + "indicador de riesgo: si una sola persona carga casi todo, su ausencia "
                            + "es un problema."),
                        new("Antelación media",
                            "Cuántos días antes del viaje se cargan las reservas, en promedio, "
                            + "ponderado por volumen."),
                        new("Modificaciones",
                            "Cuántas de esas reservas fueron modificadas después de cargadas."),
                        new("Sobre lo de otro",
                            "De esas modificaciones, cuántas las hizo alguien distinto de quien "
                            + "cargó la reserva. Es el indicador de cuánto se pisan entre sí."),
                    }),

                new("ranking", "Altas por operador", Icons.Material.Filled.FormatListNumbered,
                    new List<AyudaItem>
                    {
                        new("La lista",
                            "Cada persona con la cantidad de reservas que cargó, ordenada de mayor "
                            + "a menor."),
                        new("Clic en una fila",
                            "Enfoca todo el tablero en ese operador: los indicadores, la evolución "
                            + "y los clientes pasan a mostrar solo lo suyo."),
                    }),

                new("evolucion", "Cuándo se carga", Icons.Material.Filled.ShowChart,
                    new List<AyudaItem>
                    {
                        new("La curva",
                            "Reservas cargadas por día (o por mes, si el período es largo). Los "
                            + "picos suelen ser las cargas masivas por plantilla."),
                        new("Los días de la semana",
                            "El total cargado en cada día de la semana. Muestra si la carga se "
                            + "concentra en pocos días y si se trabaja los fines de semana."),
                    }),

                new("perfil", "Perfil de cada operador", Icons.Material.Filled.TableChart,
                    new List<AyudaItem>
                    {
                        new("Días y Altas/día",
                            "En cuántos días distintos cargó y cuántas reservas por día trabajado "
                            + "(no por día del calendario)."),
                        new("Antelación",
                            "Días promedio entre que esa persona cargó la reserva y la fecha del "
                            + "viaje."),
                        new("Retroact.",
                            "Reservas que se cargaron DESPUÉS de que el viaje ya había ocurrido."),
                        new("Cancel. y Sin asignar",
                            "Qué porcentaje de lo que cargó terminó cancelado o sigue sin unidad. "
                            + "Sirve para ver si un operador carga cosas que después se caen, pero "
                            + "depende mucho del tipo de cliente que atiende."),
                        new("Modificó / De otros / Le tocaron",
                            "Cuántas reservas modificó en total, cuántas de ellas eran de otra "
                            + "persona, y cuántas de las suyas terminó modificando alguien más."),
                        new("sin padrón",
                            "La etiqueta roja marca a alguien que cargó reservas con un usuario que "
                            + "hoy no figura en Usuarios y Permisos, o que fue dado de baja. Vale "
                            + "la pena revisarlo."),
                    }),

                new("matriz", "Quién modifica lo de quién", Icons.Material.Filled.GridOn,
                    new List<AyudaItem>
                    {
                        new("Cómo se lee",
                            "Las filas son quién cargó la reserva y las columnas quién la modificó. "
                            + "El cruce dice cuántas veces pasó."),
                        new("La diagonal en gris",
                            "Es cada uno corrigiéndose a sí mismo. Suele ser la mayoría y no es "
                            + "fricción: lo que hay que leer es lo de afuera de la diagonal."),
                    }),

                new("clientes", "Qué carga cada operador", Icons.Material.Filled.Groups,
                    new List<AyudaItem>
                    {
                        new("Para qué sirve",
                            "Es el contexto de la concentración. Que una persona cargue el 80% de "
                            + "las reservas suena grave, pero si casi todo es un contrato grande y "
                            + "repetitivo, es un tema distinto que si atiende a media empresa."),
                    }),

                new("datos", "De dónde salen los datos", Icons.Material.Filled.Storage,
                    new List<AyudaItem>
                    {
                        new("El período es por FECHA DE CARGA",
                            "Es el único informe del sistema que filtra por cuándo se cargó la "
                            + "reserva y no por cuándo viaja. Una reserva cargada hoy para "
                            + "diciembre cuenta en el día de hoy."),
                        new("Los cuatro campos de auditoría",
                            "Cada reserva guarda quién la creó, cuándo, quién la modificó por "
                            + "última vez y cuándo. De ahí sale todo el informe."),
                    },
                    Tono: AyudaTono.Datos),

                new("limites", "Qué NO dice este informe", Icons.Material.Filled.WarningAmber,
                    new List<AyudaItem>
                    {
                        new("No hay hora de carga",
                            "El sistema guarda la fecha, no la hora. No se puede saber si alguien "
                            + "carga a la mañana, a la noche o fuera de horario."),
                        new("Las modificaciones son un piso, no el total",
                            "Solo se guarda la ÚLTIMA persona que tocó cada reserva. Si la "
                            + "modificaron tres veces, se ve una sola, y si la tocó otro después, "
                            + "el primero desaparece. Los números reales son mayores."),
                        new("No se sabe cuánto tarda una reserva en asignarse",
                            "El sistema no guarda el historial de estados: no existe registro de "
                            + "cuándo una reserva pasó a estar asignada."),
                        new("Cargar mucho no es trabajar más",
                            "Una carga masiva por plantilla genera cientos de reservas con un solo "
                            + "movimiento. El ranking mide reservas, no esfuerzo."),
                        new("No dice quién dio de baja",
                            "Las reservas no se borran, se cancelan, así que no hay un registro de "
                            + "bajas para auditar."),
                    },
                    Tono: AyudaTono.Limite),
            }),

        // ── LIBRO DE NOVEDADES ─────────────────────────────────────────────
        new AyudaInforme(
            "libro-novedades",
            "Es el libro de guardia de la mesa de tráfico: lo que cada turno anotó sobre un "
            + "servicio o una unidad para que el turno siguiente se entere. Unas 5 anotaciones "
            + "por día, desde 2012.",
            new List<AyudaBloque>
            {
                new("kpis", "Los indicadores de arriba", Icons.Material.Filled.Dashboard,
                    new List<AyudaItem>
                    {
                        new("Novedades", "Cuántas anotaciones hay en el rango de fechas elegido."),
                        new("Con reserva",
                            "Las que cuelgan de una reserva concreta (tienen Nº Viaje). Se cargan "
                            + "con F2 desde la planilla de Tráfico, parado sobre el servicio."),
                        new("De unidad",
                            "Las que hablan de un interno y no de un servicio. No hay campo "
                            + "«interno» en el libro: la unidad va escrita dentro del asunto, con "
                            + "el formato «int: 8 dom: AD255RA chof:…»."),
                        new("Operadores", "Cuántas personas distintas cargaron novedades en el período."),
                        new("Sin enviar",
                            "Las que todavía no salieron en ningún correo a la lista interna. Son "
                            + "las que va a juntar la próxima corrida de «Envío de correos»."),
                    }),

                new("filtros", "Los filtros", Icons.Material.Filled.FilterAlt,
                    new List<AyudaItem>
                    {
                        new("Desde / Hasta",
                            "Filtran por FECHA DE CARGA de la novedad, no por la fecha del servicio "
                            + "del que habla. Arranca en los últimos 30 días."),
                        new("Usuario", "El operador que la cargó. Solo aparecen los de los últimos 3 años."),
                        new("Origen",
                            "«Con reserva» = las que tienen Nº Viaje. «Sin reserva» = las sueltas y "
                            + "las de unidad juntas, que son casi la mitad del libro."),
                        new("Buscar",
                            "Busca el texto tanto en el asunto como en el cuerpo del mensaje. Es lo "
                            + "que en el Metrocar hay que hacer scrolleando a mano."),
                    }),

                new("columnas", "Las columnas", Icons.Material.Filled.TableChart,
                    new List<AyudaItem>
                    {
                        new("Asunto",
                            "Lo precarga el sistema al abrir la novedad: el nombre del cliente si "
                            + "cuelga de una reserva, los datos de la unidad si es de un interno, y "
                            + "el nombre de la empresa si es suelta."),
                        new("Nº Viaje", "La reserva a la que se refiere. Vacío = novedad suelta."),
                        new("Enviada",
                            "La fecha en que la novedad salió por correo a la lista interna. «—» "
                            + "significa que todavía está pendiente de envío."),
                    }),

                new("limites", "Qué NO dice este informe", Icons.Material.Filled.WarningAmber,
                    new List<AyudaItem>
                    {
                        new("«Enviada» no prueba que el correo haya llegado",
                            "El Metrocar marca la fecha de envío al terminar el proceso, aunque el "
                            + "servidor de correo haya fallado con todos los destinatarios. Una "
                            + "novedad marcada como enviada puede no haber llegado a nadie."),
                        new("No se ve quién modificó una novedad",
                            "La tabla tiene campos de usuario que modifica y que da de baja, pero "
                            + "están vacíos en todo el libro: solo queda registrado quién la creó."),
                        new("El estado «finalizada» no se usa",
                            "Existe la marca en la tabla, pero no hay ninguna novedad finalizada en "
                            + "todo 2026: nadie cierra las anotaciones."),
                        new("Una novedad de unidad puede ser ambigua",
                            "Hay internos repetidos entre fleteros distintos. El dominio que va "
                            + "dentro del asunto es la única forma de saber de qué unidad se habla."),
                        new("No es un registro de incidentes",
                            "Es texto libre, sin categoría ni gravedad: no se puede contar «cuántas "
                            + "demoras» ni «cuántas fallas mecánicas» hubo sin leer una por una."),
                    },
                    Tono: AyudaTono.Limite),
            }),
    };

    private static readonly Dictionary<string, AyudaInforme> _porRuta =
        _todas.ToDictionary(a => a.Ruta, StringComparer.OrdinalIgnoreCase);

    /// <summary>La ayuda de una ruta, o <c>null</c> si ese informe todavía no la tiene escrita.</summary>
    public static AyudaInforme? De(string ruta) =>
        _porRuta.TryGetValue(ruta.Trim('/'), out var a) ? a : null;

    /// <summary>¿Este informe tiene ayuda? Lo usa el componente para no mostrar un botón vacío.</summary>
    public static bool Tiene(string ruta) => De(ruta) is not null;

    /// <summary>
    /// Texto plano de la ayuda de un informe, para que el buscador del hub encuentre un reporte
    /// por lo que explica y no solo por su título.
    /// </summary>
    public static string TextoBuscable(string ruta)
    {
        var a = De(ruta);
        if (a is null) return "";
        return a.Resumen + " " + string.Join(" ",
            a.Bloques.SelectMany(b => new[] { b.Titulo, b.Intro }
                .Concat(b.Items.SelectMany(i => new[] { i.Termino, i.Explicacion }))));
    }
}
