using System.Net;
using System.Net.Mail;
using System.Text;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// El proceso de <b>Envío de correos</b> del submenú Libro de Novedades
/// (form FoxPro <c>libro_novedad_envia_correo.scx</c>, botón «Enviar»).
///
/// Es un batch que la mesa de tráfico corre a diario: junta lo que todavía no salió, arma un
/// correo de TEXTO PLANO por bloque y lo manda a la lista de distribución interna
/// (<c>libro_novedad_parametro</c>), a los que tengan tildado ese bloque. Después estampa
/// <c>f_envio</c> para que la próxima corrida no lo repita.
///
/// ── Alcance migrado (19/08/2026, decisión del usuario) ──
/// De los 4 bloques del FoxPro se migran los <b>dos de texto puro</b>, que son los que corren
/// todos los días: <b>NOVEDADES</b> y <b>SINIESTROS</b>. Los de <b>Combustible</b> y <b>Taller</b>
/// quedan para una segunda tanda: generan un adjunto (Excel o PDF del reporte
/// <c>vehiculo_combustible_consumo</c>) y se gobiernan por <c>parametro.f_ult_envi</c>.
///
/// ── 🔒 ANDAMIAJE ──
/// <see cref="AbmFeatureFlags.EnvioCorreosActivo"/> está en <c>false</c>: la pantalla arma y
/// muestra el correo EXACTO que saldría y a quiénes, pero <see cref="EnviarAsync"/> aborta antes
/// de abrir el SMTP y antes de tocar <c>f_envio</c>. El Metrocar sigue siendo el que manda —
/// si los dos mandaran, gerencia y monitoreo recibirían todo duplicado.
///
/// ── Bugs del original que NO se replican ──
///  ① <b>El FoxPro estampa <c>f_envio</c> aunque el envío falle.</b> El UPDATE está fuera del
///     bucle de envío y no mira el resultado de <c>envio_correo_gmail()</c>: si el SMTP está
///     caído, las novedades quedan marcadas como enviadas y nadie las vuelve a ver. Acá el
///     <c>f_envio</c> se estampa <b>solo si al menos un destinatario recibió el correo</b>.
///  ② <b>Pisa el log de errores.</b> En el bucle, el ramal OK concatena
///     (<c>edit1.Value = edit1.Value + …</c>) pero el ramal de error <b>asigna</b>
///     (<c>edit1.Value = …</c>): un fallo borra todo lo reportado antes. Acá se acumula todo.
///  ③ <b>CDO con SSL implícito sobre el puerto 25.</b> Misma combinación contradictoria que
///     tenía el botón de prueba de Parámetros; se resuelve igual que allá (STARTTLS y, si el
///     servidor no lo soporta, en claro) — ver <see cref="CorreoPruebaService"/>.
///
/// Plano: <c>docs/PlanoFoxPro/trafico/LIBRO_NOVEDADES.md</c>.
/// </summary>
public class CorreoNovedadesService
{
    private readonly ReportService _reports;
    private readonly AbmService _abm;

    public CorreoNovedadesService(ReportService reports, AbmService abm)
    {
        _reports = reports;
        _abm = abm;
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    // ═══════════════════════════════════════════════════════════════════════════
    //  Preparar la tanda (equivale al Init del form)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Todo lo que la pantalla necesita mostrar antes de decidir si envía.</summary>
    public sealed class Tanda
    {
        public List<DestinatarioCorreoRow> Destinatarios = new();
        public List<NovedadEnvioRow> Novedades = new();
        public List<SiniestroRow> Siniestros = new();
        /// <summary>Siniestros pendientes que el INNER JOIN a chofer del FoxPro deja afuera.</summary>
        public int SiniestrosSinChofer;
        public SmtpConfigDto Smtp = new();

        /// <summary>Cuerpo exacto del correo de NOVEDADES (vacío si no hay tanda).</summary>
        public string CuerpoNovedades = "";
        /// <summary>Cuerpo exacto del correo de SINIESTROS (vacío si no hay tanda).</summary>
        public string CuerpoSiniestros = "";
        public string AsuntoNovedades = "";
        public string AsuntoSiniestros = "";

        /// <summary>
        /// <c>true</c> = el cuerpo de Novedades que se muestra NO es la tanda (no hay nada
        /// pendiente): es un <b>ejemplo</b> armado con las últimas novedades ya enviadas, para
        /// que se pueda ver el formato del correo. Nunca se manda: <see cref="Novedades"/> sigue
        /// vacía y <see cref="EnviarAsync"/> mira esa lista, no el cuerpo.
        /// </summary>
        public bool NovedadesEsEjemplo;

        public List<DestinatarioCorreoRow> DeNovedades => Destinatarios.Where(d => d.Novedad).ToList();
        public List<DestinatarioCorreoRow> DeSiniestros => Destinatarios.Where(d => d.Siniestro).ToList();

        /// <summary>Motivo por el que no se puede enviar nada, o <c>null</c> si se puede.</summary>
        public string? Bloqueo
        {
            get
            {
                if (Destinatarios.Count == 0)
                    return "No hay destinatarios cargados. Cargá al menos una dirección en «Correos Electrónicos Parámetros».";
                if (Destinatarios.All(d => d.Suscripciones == 0))
                    return "Hay destinatarios cargados pero ninguno tiene tildado un tipo de informe.";
                if (Novedades.Count == 0 && Siniestros.Count == 0)
                    return "No hay novedades ni siniestros pendientes de envío.";
                if (!Smtp.Configurado)
                    return "Falta la configuración del servidor de correo en Parámetros → Empresa.";
                return null;
            }
        }
    }

    /// <summary>Arma la tanda: qué está pendiente, quién lo recibe y con qué texto exacto.</summary>
    public async Task<Tanda> PrepararAsync()
    {
        var t = new Tanda
        {
            Destinatarios = await _reports.GetDestinatariosCorreoAsync(),
            Novedades = await _reports.GetNovedadesPendientesEnvioAsync(),
            Smtp = await _reports.GetSmtpConfigAsync(),
        };
        var (sin, sinChofer) = await _reports.GetSiniestrosPendientesEnvioAsync();
        t.Siniestros = sin;
        t.SiniestrosSinChofer = sinChofer;

        var ahora = DateTime.Now;
        if (t.Novedades.Count > 0)
        {
            t.AsuntoNovedades = $"Novedades : {ahora:dd/MM/yyyy} a las {ahora:HH:mm}";
            t.CuerpoNovedades = CuerpoNovedades(t.Novedades);
        }
        else
        {
            // Sin tanda pendiente (el estado normal un rato después de cada corrida) el
            // previsualizador quedaría en blanco. Se muestra un EJEMPLO con las últimas ya
            // enviadas, marcado como tal. No es enviable: la lista Novedades sigue vacía.
            var ejemplo = await _reports.GetUltimasNovedadesEnviadasAsync(10);
            if (ejemplo.Count > 0)
            {
                t.NovedadesEsEjemplo = true;
                t.AsuntoNovedades = $"Novedades : {ahora:dd/MM/yyyy} a las {ahora:HH:mm}";
                t.CuerpoNovedades = CuerpoNovedades(ejemplo);
            }
        }
        if (t.Siniestros.Count > 0)
        {
            t.AsuntoSiniestros = $"Siniestros : {ahora:dd/MM/yyyy} a las {ahora:HH:mm}";
            var fichas = new List<SiniestroDetalleDto>();
            foreach (var s in t.Siniestros)
            {
                var d = await _reports.GetSiniestroDetalleAsync(s.Id);
                if (d is not null) fichas.Add(d);
            }
            t.CuerpoSiniestros = CuerpoSiniestros(fichas);
        }
        return t;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Los cuerpos — réplica literal del envio.Click del FoxPro
    // ═══════════════════════════════════════════════════════════════════════════

    private const string Separador = "----------------------------------------------------------------------"; // Replicate("-",70)

    /// <summary>
    /// Cuerpo del bloque NOVEDADES. Réplica del primer <c>Do While</c> del <c>envio.Click</c>:
    /// por cada novedad, fecha+hora+usuario, asunto (con el número de reserva si cuelga de una),
    /// los datos del servicio, el mensaje y una línea de 70 guiones.
    /// </summary>
    public static string CuerpoNovedades(IReadOnlyList<NovedadEnvioRow> novedades)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        foreach (var n in novedades)
        {
            // FoxPro: Substr(Ttoc(f_carga,3),1,10) → "yyyy-mm-dd"; Substr(Ttoc(f_carga,2),1,5) → "hh:mm".
            var f = n.FCarga;
            sb.AppendLine($"{f:yyyy-MM-dd} a las {f:HH:mm} usuario: {n.Usuario}");

            if (n.IdViaje == 0)
            {
                sb.AppendLine($"asunto:  {n.Asunto}");
            }
            else
            {
                sb.AppendLine($"asunto:  {n.Asunto} Reserva: {n.IdViaje}");
                // El FoxPro solo agrega este bloque si encontró el viaje (_Tally # 0).
                if (n.Cliente.Length > 0 || n.Desde.Length > 0 || n.Interno != 0)
                {
                    if (n.Interno != 0)
                        sb.AppendLine($"nº interno: {n.Interno} - conductor: {n.Chofer}");
                    else
                        sb.AppendLine("unidad diagramada para el servicio: " +
                                      (n.Cronograma == "S/C" ? "SIN CRONOGRAMA" : n.Cronograma));

                    var recorrido = $"{n.Desde} / {n.Hasta}";
                    if (recorrido.Length > 70) recorrido = recorrido[..70];
                    sb.AppendLine($"recorrido: {recorrido} hora: {n.HoraServicio}");
                }
            }

            sb.AppendLine($"Mensaje: {n.Mensaje}");
            sb.AppendLine(Separador);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Cuerpo del bloque SINIESTROS. Réplica del segundo <c>Do While</c>: la ficha completa del
    /// parte de accidente, con los campos opcionales que solo aparecen si están cargados.
    /// </summary>
    public static string CuerpoSiniestros(IReadOnlyList<SiniestroDetalleDto> fichas)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        foreach (var s in fichas)
        {
            var fecha = s.Fecha?.ToString("yyyy-MM-dd") ?? "";
            sb.AppendLine($"{fecha} a las {s.HoraTexto} - tomo el siniestro: {s.UsuarioCreo}");
            sb.AppendLine($"Vehiculo de la empresa : {s.Dominio} - Interno: {s.Interno} - tomo el siniestro: {s.UsuarioCreo}");
            sb.AppendLine($"Conductor: {s.Conductor}");
            sb.AppendLine($"Tipo de Accidente: {s.TipoAcc}");
            sb.AppendLine($"Lugar: {s.Lugar}");
            sb.AppendLine($"Localidad del accidente : {s.Localidad.ToUpperInvariant()}");
            sb.AppendLine($"Provincia del accidente : {s.Provincia.ToUpperInvariant()}");

            Op(sb, s.Comisaria, v => $"Comisaria del acc : {v.ToUpperInvariant()}");
            Op(sb, s.AseguradoDano, v => $"Daños a la unidad : {v.ToUpperInvariant()}");
            Op(sb, s.TerConductor, v => $"Conductor : {v}");
            Op(sb, s.TerNdoc, v => $"Nº de documento: {s.TerTdoc} {v}" + (s.TerEdad != 0 ? $" Edad: {s.TerEdad}" : ""));
            Op(sb, s.TerDireccion, v => $"Direccion conductor : {v} - {s.TerLocalidad}");
            if (s.TerTelefono.Length > 0 || s.TerCelular.Length > 0)
                sb.AppendLine($"Telefonos : {s.TerTelefono} - {s.TerCelular}");
            Op(sb, s.TerRegistroNro, v => $"Nº de registro : {v} " +
                (s.TerRegistroVto is DateOnly r ? $" Venc: {r:dd/MM/yyyy}" : ""));
            if (s.TerDominio.Length > 0 || s.TerMarcaModelo.Length > 0)
                sb.AppendLine($"Vehiculo 3º : {s.TerDominio} - {s.TerMarcaModelo}" +
                              (s.TerAno != 0 ? $" Año/Modelo: {s.TerAno}" : ""));
            if (s.TerSeguroNombre.Length > 0 || s.TerSeguroPoliza.Length > 0)
                sb.AppendLine($"Seguro : {s.TerSeguroNombre} - " +
                              (s.TerSeguroPoliza.Length > 0 ? $" Nº de Poliza: {s.TerSeguroPoliza}" : "Sin Nro de poliza"));
            Op(sb, s.TerConductorDano, v => $"Daño vehiculo 3º : {v.ToUpperInvariant()}");
            Op(sb, s.PropNombre, v => $"Daño a la propiedad de : {v}");
            Op(sb, s.PropDireccion, v => $"Direccion de la propiedad : {v} - {s.PropLocalidad}");
            if (s.PropTelefono.Length > 0 || s.PropCelular.Length > 0)
                sb.AppendLine($"Telefonos : {s.PropTelefono} - {s.PropCelular}");
            Op(sb, s.PropDano, v => $"Daño a la propiedad : {v.ToUpperInvariant()}");
            sb.AppendLine($"Descripcion del accidente : {s.Descripcion.ToUpperInvariant()}");

            var testigos = s.Testigos.Where(t => t.Nombre.Trim().Length > 0).ToList();
            if (testigos.Count == 0)
            {
                sb.AppendLine("NO SE REGISTRAN TESTIGOS EN EL SINIESTRO ");
            }
            else
            {
                foreach (var t in testigos)
                {
                    sb.AppendLine($"Testigo {t.Orden}º: {t.Nombre.ToUpperInvariant()} Doc: {t.Tdoc.ToUpperInvariant()} {t.Ndoc.ToUpperInvariant()}");
                    sb.AppendLine($"Telefonos : {t.Telefono.ToUpperInvariant()} {t.Celular.ToUpperInvariant()}");
                }
            }
            sb.AppendLine(Separador);
        }
        return sb.ToString();

        static void Op(StringBuilder sb, string valor, Func<string, string> linea)
        {
            if (!string.IsNullOrWhiteSpace(valor)) sb.AppendLine(linea(valor.Trim()));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  El envío
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Una línea del log del proceso (lo que el FoxPro escribe en su editbox).</summary>
    public sealed record LineaLog(string Texto, bool EsError = false, bool EsTitulo = false);

    /// <summary>Resultado del proceso completo.</summary>
    public sealed class ResultadoEnvio
    {
        public List<LineaLog> Log = new();
        public int Enviados, Fallidos;
        public int NovedadesMarcadas, SiniestrosMarcados;
        public bool Bloqueado;
    }

    /// <summary>
    /// Manda los bloques pedidos y estampa <c>f_envio</c>. <b>Abortado por el flag</b>
    /// <see cref="AbmFeatureFlags.EnvioCorreosActivo"/>: mientras esté apagado no abre el SMTP
    /// ni toca la base — el correo lo sigue mandando el Metrocar.
    /// </summary>
    public async Task<ResultadoEnvio> EnviarAsync(Tanda tanda, bool novedades, bool siniestros)
    {
        var r = new ResultadoEnvio();

        if (!AbmFeatureFlags.EnvioCorreosActivo)
        {
            r.Bloqueado = true;
            r.Log.Add(new LineaLog(
                "El envío desde Buslink todavía no está habilitado — el correo sigue saliendo del Metrocar.",
                EsError: true));
            return r;
        }

        var remitente = CorreoPruebaService.ExtraerDireccion(tanda.Smtp.Remitente)
                     ?? CorreoPruebaService.ExtraerDireccion(tanda.Smtp.Usuario);
        if (remitente is null)
        {
            r.Log.Add(new LineaLog("No se pudo determinar la dirección del remitente (Parámetros → Empresa).", EsError: true));
            return r;
        }

        if (novedades && tanda.Novedades.Count > 0)
        {
            r.Log.Add(new LineaLog("Comienzo del envio: NOVEDADES", EsTitulo: true));
            var ok = await EnviarBloqueAsync(
                tanda.Smtp, remitente, tanda.DeNovedades, tanda.AsuntoNovedades, tanda.CuerpoNovedades, r);

            // ✅ Corrección del bug ①: el FoxPro estampa f_envio siempre. Acá, solo si llegó.
            if (ok)
            {
                var res = await _abm.MarcarNovedadesEnviadasAsync(tanda.Novedades.Select(n => n.Id).ToList());
                r.NovedadesMarcadas = res.Ok ? res.Id ?? 0 : 0;
                if (!res.Ok) r.Log.Add(new LineaLog(res.Error ?? "No se pudo marcar el envío.", EsError: true));
            }
            else
            {
                r.Log.Add(new LineaLog(
                    "Ningún destinatario recibió el correo → las novedades quedan pendientes para el próximo intento.",
                    EsError: true));
            }
            r.Log.Add(new LineaLog("Proceso finalizado : NOVEDADES", EsTitulo: true));
        }

        if (siniestros && tanda.Siniestros.Count > 0)
        {
            r.Log.Add(new LineaLog("Comienzo del envio: SINIESTROS", EsTitulo: true));
            var ok = await EnviarBloqueAsync(
                tanda.Smtp, remitente, tanda.DeSiniestros, tanda.AsuntoSiniestros, tanda.CuerpoSiniestros, r);
            if (ok)
            {
                var res = await _abm.MarcarSiniestrosEnviadosAsync(tanda.Siniestros.Select(s => s.Id).ToList());
                r.SiniestrosMarcados = res.Ok ? res.Id ?? 0 : 0;
                if (!res.Ok) r.Log.Add(new LineaLog(res.Error ?? "No se pudo marcar el envío.", EsError: true));
            }
            else
            {
                r.Log.Add(new LineaLog(
                    "Ningún destinatario recibió el correo → los siniestros quedan pendientes para el próximo intento.",
                    EsError: true));
            }
            r.Log.Add(new LineaLog("Proceso finalizado : SINIESTROS", EsTitulo: true));
        }

        return r;
    }

    /// <summary>Manda un bloque a cada destinatario suscripto. Devuelve true si al menos uno recibió.</summary>
    private static async Task<bool> EnviarBloqueAsync(
        SmtpConfigDto smtp, string remitente, IReadOnlyList<DestinatarioCorreoRow> destinos,
        string asunto, string cuerpo, ResultadoEnvio r)
    {
        var alguno = false;
        foreach (var d in destinos)
        {
            if (string.IsNullOrWhiteSpace(d.Email) || !d.Email.Contains('@'))
            {
                r.Log.Add(new LineaLog($"Correo para: {d.Contacto} OMITIDO (dirección inválida)", EsError: true));
                r.Fallidos++;
                continue;
            }

            var (ok, err) = await IntentarAsync(smtp, remitente, d.Email.Trim(), asunto, cuerpo);
            if (ok)
            {
                r.Log.Add(new LineaLog($"Correo para: {d.Email.Trim()} OK"));
                r.Enviados++;
                alguno = true;
            }
            else
            {
                // ✅ Corrección del bug ②: acá se ACUMULA (el FoxPro pisaba el log en el error).
                r.Log.Add(new LineaLog($"Correo para: {d.Email.Trim()} CON PROBLEMAS — {err}", EsError: true));
                r.Fallidos++;
            }
        }
        return alguno;
    }

    /// <summary>
    /// Un envío. Igual criterio que <see cref="CorreoPruebaService"/>: STARTTLS primero y, si el
    /// servidor no lo soporta, en claro (✅ corrección del bug ③ del CDO con SSL sobre el 25).
    /// </summary>
    private static async Task<(bool Ok, string Error)> IntentarAsync(
        SmtpConfigDto smtp, string remitente, string destino, string asunto, string cuerpo)
    {
        var (ok, err) = await UnIntentoAsync(smtp, remitente, destino, asunto, cuerpo, ssl: true);
        if (ok) return (true, "");
        var (ok2, err2) = await UnIntentoAsync(smtp, remitente, destino, asunto, cuerpo, ssl: false);
        return ok2 ? (true, "") : (false, $"STARTTLS: {err} · sin cifrado: {err2}");
    }

    private static async Task<(bool Ok, string Error)> UnIntentoAsync(
        SmtpConfigDto smtp, string remitente, string destino, string asunto, string cuerpo, bool ssl)
    {
        try
        {
#pragma warning disable SYSLIB0014 // SmtpClient obsoleto — mismo criterio que CorreoPruebaService.
            using var cliente = new SmtpClient(smtp.Servidor.Trim(), smtp.Puerto)
            {
                EnableSsl = ssl,
                Timeout = (int)Timeout.TotalMilliseconds,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };
#pragma warning restore SYSLIB0014
            if (!string.IsNullOrWhiteSpace(smtp.Usuario))
            {
                cliente.UseDefaultCredentials = false;
                cliente.Credentials = new NetworkCredential(smtp.Usuario.Trim(), smtp.Password ?? "");
            }
            using var msg = new MailMessage(remitente, destino)
            {
                Subject = asunto,
                Body = cuerpo,
                IsBodyHtml = false,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
            };
            await cliente.SendMailAsync(msg).WaitAsync(Timeout);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, (ex.InnerException?.Message ?? ex.Message).Trim());
        }
    }
}
