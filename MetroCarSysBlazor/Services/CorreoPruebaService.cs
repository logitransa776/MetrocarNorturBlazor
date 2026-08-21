using System.Net;
using System.Net.Mail;

namespace MetroCarSysBlazor.Services;

/// <summary>
/// Prueba de la configuración SMTP de Parámetros Empresa — el botón «Probar envio correo»
/// del form <c>parametro_empresa.scx</c>, migrado <b>corregido</b> (decisión 12/08/2026).
///
/// El original tenía tres defectos (§2.4 de docs/PlanoFoxPro/sistema/PARAMETROS.md):
///  ① <b>Grababa los 5 campos SMTP en la base ANTES de probar</b> → si después el usuario
///     apretaba Cancelar, el SMTP quedaba cambiado igual. Acá la prueba <b>no escribe nada</b>:
///     usa exactamente lo que hay en pantalla.
///  ② El destinatario estaba <b>hardcodeado</b> a <c>jlsilvamtb@gmail.com</c> (la casilla del
///     desarrollador original del FoxPro). Acá lo elige el usuario.
///  ③ Usaba CDO (COM de Windows) forzando SSL implícito sobre el puerto 25, combinación
///     contradictoria que muy probablemente no funcionaba. Acá se intenta primero con
///     STARTTLS y, si el servidor no lo soporta, se reintenta en claro — informando cuál
///     de las dos anduvo, que es justamente lo que un botón de diagnóstico tiene que decir.
///
/// Nota: <see cref="SmtpClient"/> está marcado obsoleto (SYSLIB0014) pero sigue siendo
/// funcional y evita sumar una dependencia nueva para un botón de diagnóstico. Si algún día
/// Buslink manda correos de verdad (p. ej. el F2 del libro de guardia, hoy NO migrado),
/// conviene pasar a MailKit.
/// </summary>
public class CorreoPruebaService
{
    /// <summary>Resultado de la prueba: éxito + detalle legible para mostrar en pantalla.</summary>
    public sealed record Resultado(bool Ok, string Mensaje);

    /// <summary>Config SMTP tal como está en la pantalla (sin pasar por la base).</summary>
    public sealed record Config(string Servidor, int Puerto, string Usuario, string Password, string Remitente);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Manda un correo de prueba con la config recibida. No toca la base de datos.
    /// </summary>
    public async Task<Resultado> ProbarAsync(Config cfg, string destinatario, string usuarioBuslink)
    {
        if (string.IsNullOrWhiteSpace(cfg.Servidor))
            return new(false, "Falta el servidor de correo.");
        if (cfg.Puerto is <= 0 or > 65535)
            return new(false, "El puerto de correo no es válido.");
        if (string.IsNullOrWhiteSpace(destinatario) || !destinatario.Contains('@'))
            return new(false, "Cargá una dirección de correo válida para la prueba.");

        var remitente = ExtraerDireccion(cfg.Remitente) ?? ExtraerDireccion(cfg.Usuario);
        if (remitente is null)
            return new(false,
                "No se pudo determinar la dirección del remitente. Revisá los campos «Nombre» y «Usuario» del correo.");

        // Mismo cuerpo que el FoxPro (más quién disparó la prueba, que allá no se decía).
        var cuerpo =
            "Prueba de correo electronico\r\n\r\n" +
            "Se estan probando las configuraciones\r\n" +
            $"Fecha del envio: {DateTime.Now:dd/MM/yyyy} a las {DateTime.Now:HH:mm:ss}\r\n" +
            $"Enviado desde Buslink por el usuario: {usuarioBuslink}\r\n\r\n" +
            "¡ NO CONTESTE ESTE CORREO !";

        // Primero STARTTLS (lo correcto en el puerto 25/587); si el servidor no lo soporta,
        // se reintenta sin cifrar. Informamos cuál anduvo: es un botón de diagnóstico.
        var (okTls, errTls) = await IntentarAsync(cfg, remitente, destinatario, cuerpo, ssl: true);
        if (okTls)
            return new(true, $"Correo de prueba enviado a {destinatario} (con STARTTLS).");

        var (okPlano, errPlano) = await IntentarAsync(cfg, remitente, destinatario, cuerpo, ssl: false);
        if (okPlano)
            return new(true,
                $"Correo de prueba enviado a {destinatario}, pero SIN cifrado: el servidor " +
                $"{cfg.Servidor}:{cfg.Puerto} rechazó STARTTLS ({errTls}).");

        return new(false,
            $"No se pudo enviar el correo a {destinatario}.\r\n" +
            $"Con STARTTLS: {errTls}\r\nSin cifrado: {errPlano}");
    }

    private static async Task<(bool Ok, string Error)> IntentarAsync(
        Config cfg, string remitente, string destinatario, string cuerpo, bool ssl)
    {
        try
        {
#pragma warning disable SYSLIB0014 // SmtpClient obsoleto: ver comentario de la clase.
            using var cliente = new SmtpClient(cfg.Servidor.Trim(), cfg.Puerto)
            {
                EnableSsl = ssl,
                Timeout = (int)Timeout.TotalMilliseconds,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };
#pragma warning restore SYSLIB0014
            if (!string.IsNullOrWhiteSpace(cfg.Usuario))
            {
                cliente.UseDefaultCredentials = false;
                cliente.Credentials = new NetworkCredential(cfg.Usuario.Trim(), cfg.Password ?? "");
            }

            using var msg = new MailMessage(remitente, destinatario)
            {
                Subject = "Correo de prueba de configuración",
                Body = cuerpo,
                IsBodyHtml = false,
            };
            await cliente.SendMailAsync(msg).WaitAsync(Timeout);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, (ex.InnerException?.Message ?? ex.Message).Trim());
        }
    }

    /// <summary>
    /// Saca la dirección de un valor tipo <c>Dto. Trafico Nortur SRL &lt;traficonortur@nrumbos.com.ar&gt;</c>
    /// (así está cargado <c>smtp_nombr</c> en producción) o de una dirección pelada.
    /// Devuelve <c>null</c> si no hay nada parecido a un mail.
    /// </summary>
    public static string? ExtraerDireccion(string? valor)
    {
        valor = (valor ?? "").Trim();
        if (valor.Length == 0) return null;

        var ini = valor.IndexOf('<');
        var fin = valor.IndexOf('>', ini + 1);
        if (ini >= 0 && fin > ini)
            valor = valor.Substring(ini + 1, fin - ini - 1).Trim();

        return valor.Contains('@') && !valor.Contains(' ') ? valor : null;
    }
}
