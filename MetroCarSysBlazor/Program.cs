using MetroCarSysBlazor.Components;
using MetroCarSysBlazor.Data;
using MetroCarSysBlazor.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using ApexCharts;
using MudBlazor.Services;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Razor Components (Blazor Server interactivo)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MudBlazor
builder.Services.AddMudServices();

// ApexCharts
builder.Services.AddApexCharts();

// Acceso a datos: DbContextFactory (en Blazor Server conviene factory por el
// ciclo de vida de los circuitos) contra replicaVPF.
// NO usar AddPooledDbContextFactory: ReportService hace `await using var conn =
// db.Database.GetDbConnection()`, que dispone la conexión del DbContext. Con un
// contexto del pool eso deja la conexión muerta (ConnectionString vacío) para el
// próximo uso → InvalidOperationException intermitente. Factory normal = un contexto
// nuevo por operación, disponer su conexión no afecta a nadie.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContextFactory<NorturDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddMemoryCache();

// Warmup del pool de conexiones (ítem 4 del diagnóstico de performance del Zoom).
// El connection string usa Pooling=True con Min Pool Size=2; este servicio pre-abre
// esas conexiones al arrancar para que el handshake TLS + resolución de instancia
// (\SQLEXPRESS vía SQL Browser) + login se paguen UNA vez en el arranque y no en el
// primer doble clic del operador. Sin esto, el primer Zoom de la sesión seguiría
// pagando el ciclo completo de conexión en frío.
builder.Services.AddHostedService<DbWarmupService>();

// Barrido periódico que cierra las sesiones que superaron las 8 hs (VENCIDA) y
// registra su evento en la bitácora usuarios_logs.
builder.Services.AddHostedService<SesionesVencidasService>();

// Servicios de aplicación
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ExcelExportService>();
// Capa de escritura (INSERT/UPDATE). Estrena la escritura con el ABM de Usuarios.
builder.Services.AddScoped<AbmService>();
// Envío de correos del Libro de Novedades (form libro_novedad_envia_correo.scx). Scoped porque
// depende de ReportService y AbmService, que también lo son. Ver AbmFeatureFlags.EnvioCorreosActivo.
builder.Services.AddScoped<CorreoNovedadesService>();
// Control de acceso por origen de red (LAN vs Internet). Singleton: parsea los rangos
// CIDR de RedInterna una sola vez. Dormido mientras RedInterna:Activo = false.
builder.Services.AddSingleton<AccesoRedService>();

// Forwarded headers — para cuando Buslink quede detrás de un reverse proxy (IIS ARR, nginx,
// Cloudflare). Solo se confía en X-Forwarded-For si la conexión viene de un proxy CONOCIDO
// (RedInterna:ProxiesConfiables). Sin proxies configurados, el header se ignora y se usa la
// IP real de la conexión TCP (no falsificable). Esto es lo que hace fiable el control de
// acceso por red: un cliente de Internet no puede mentir su IP con un header.
builder.Services.Configure<ForwardedHeadersOptions>(opt =>
{
    opt.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opt.ForwardLimit = 1;
    opt.KnownProxies.Clear();
    opt.KnownIPNetworks.Clear();
    foreach (var p in builder.Configuration.GetSection("RedInterna:ProxiesConfiables").Get<string[]>()
                      ?? Array.Empty<string>())
        if (System.Net.IPAddress.TryParse(p, out var addr)) opt.KnownProxies.Add(addr);
});
// Adjuntos de viajes (Tráfico → Ver Datos Extras → Ver Adjunto). Singleton: solo lee config.
builder.Services.AddSingleton<AdjuntoService>();

// Logo de la empresa (Sistema → Parámetros → Empresa). Mismo problema que los adjuntos:
// parametro.logo guarda una ruta con unidad de red mapeada que el servidor no ve.
builder.Services.AddSingleton<LogoEmpresaService>();

// Prueba de la configuración SMTP (botón «Probar envío» de Parámetros Empresa). Sin estado.
builder.Services.AddSingleton<CorreoPruebaService>();

// Diagnóstico del SQL EXTERNO del sistema de GPS (Parámetros → solapa GPS). Sin estado.
builder.Services.AddSingleton<GpsSqlService>();

// Cierre de sesión al cerrar el navegador. El tracker (singleton) vigila los circuitos
// vivos de cada sesión; el handler + el contexto son por circuito. Ver
// SesionCircuitoTracker para el porqué del diseño y la gracia de 5 minutos.
builder.Services.AddSingleton<SesionCircuitoTracker>();
builder.Services.AddScoped<SesionCircuitoContexto>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, SesionCircuitHandler>();

// Autenticación por COOKIE — el navegador la manda en cada petición HTTP (pestaña
// nueva / F5 incluidas), así el servidor reconoce al usuario antes de renderizar.
// Expiración DESLIZANTE de 8h (jornada laboral): cada actividad renueva el reloj.
// La cookie es DE SESIÓN (no persistente, ver IsPersistent en /auth/login): al cerrar el
// navegador el propio navegador la borra, así que al volver a entrar pide usuario y clave.
// El tope de 8 hs sigue rigiendo dentro de la misma corrida del navegador.
builder.Services.AddAuthentication(NorturIdentityFactory.AuthScheme)
    .AddCookie(NorturIdentityFactory.AuthScheme, opt =>
    {
        opt.Cookie.Name = "Nortur.Auth";
        opt.Cookie.HttpOnly = true;
        opt.Cookie.SameSite = SameSiteMode.Lax;
        opt.ExpireTimeSpan = TimeSpan.FromHours(8);
        opt.SlidingExpiration = true;
        opt.LoginPath = "/login";
        opt.LogoutPath = "/auth/logout";
        // Es una SPA Blazor: ante 401/403 no redirigir con HTML, devolver el código.
        opt.Events.OnRedirectToLogin = ctx => { ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask; };
    });

// Estado de auth para Blazor: nuestro provider lee el ClaimsPrincipal de la cookie.
builder.Services.AddScoped<NorturAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<NorturAuthStateProvider>());
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Reescribe RemoteIpAddress con la IP real del cliente cuando la petición viene de un proxy
// conocido (ver ForwardedHeadersOptions). Debe ir ANTES de todo lo que lea la IP (auth, login).
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

// Sirve archivos de wwwroot en runtime (no depende del manifiesto de MapStaticAssets).
// Necesario para assets agregados después de la build, como videos .mp4.
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// ── Endpoints HTTP de auth ────────────────────────────────────────────────
// SignInAsync/SignOutAsync escriben la cookie y SOLO funcionan en contexto de
// request HTTP (no en un circuito interactivo). Por eso el login es un POST a
// este endpoint, no un @onclick de Blazor.
app.MapPost("/auth/login", async (HttpContext http, AuthService auth, AbmService abm, AccesoRedService red) =>
{
    var form = await http.Request.ReadFormAsync();
    var usuario = form["usuario"].ToString();
    var password = form["password"].ToString();
    var destino = form["destino"].ToString();

    // La IP sale del HttpContext; el hostname se resuelve por DNS inverso (best-effort,
    // con timeout corto — en web el nombre de la máquina NO llega del navegador como en
    // la LAN FoxPro). Se calcula antes para poder registrar también los intentos fallidos.
    var ip = ObtenerIpCliente(http);
    var hostname = await ResolverHostnameAsync(ip);

    var res = await auth.LoginAsync(usuario, password);
    if (!res.Exito)
    {
        // Registrar el intento RECHAZADO en la bitácora (usuarios_logs), con el motivo.
        await abm.RegistrarLoginFallidoAsync(usuario, ip, hostname, res.Error ?? "Rechazado");
        return Results.Redirect($"/login?error={Uri.EscapeDataString(res.Error ?? "Error de acceso")}");
    }

    // Control de acceso por origen de red (LAN vs Internet). Dormido si RedInterna:Activo=false.
    // El permiso de acceso remoto es la letra 'I' del string `acceso` (reemplazó a Scheduler).
    // Credenciales OK pero IP externa y usuario SIN la letra 'I' → se rechaza y se registra
    // (fail-closed: IP indeterminable se trata según la política de RedInterna).
    if (!red.PermitirIngreso(ip, PermisosCatalogo.Tiene(res.Acceso, 'I')))
    {
        await abm.RegistrarLoginFallidoAsync(usuario, ip, hostname, "Acceso por Internet no autorizado");
        return Results.Redirect($"/login?error={Uri.EscapeDataString("Tu usuario no está habilitado para ingresar desde fuera de la red de la empresa.")}");
    }

    // Registrar el ingreso ANTES de crear la cookie: así obtenemos el session_id (GUID)
    // y lo guardamos como claim en la cookie, para poder cruzar el logout con su login.
    var sessionId = await abm.RegistrarLoginAsync(res.Usuario, ip, hostname);

    var principal = NorturIdentityFactory.Crear(res.Usuario, res.Acceso, res.Nivel, res.Operador, sessionId);
    await http.SignInAsync(NorturIdentityFactory.AuthScheme, principal,
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            // IsPersistent = false → cookie de sesión: muere cuando se cierra el navegador
            // (al reabrir, login de nuevo). El ticket igual vence a las 8 hs.
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        });

    // Validar que el destino sea una ruta local (anti open-redirect).
    var url = (!string.IsNullOrEmpty(destino) && Uri.IsWellFormedUriString(destino, UriKind.Relative))
        ? destino : "/";
    return Results.Redirect(url);
});

app.MapPost("/auth/logout", async (HttpContext http, AbmService abm) =>
{
    // Cerrar la sesión abierta en el historial ANTES de borrar la cookie (después el
    // claim del usuario ya no está disponible). El session_id (GUID) del claim identifica
    // exactamente qué sesión cerrar.
    var usuario = http.User?.Identity?.Name;
    var sidClaim = http.User?.FindFirst(NorturClaims.Sesion)?.Value;
    Guid? sessionId = Guid.TryParse(sidClaim, out var g) ? g : null;
    if (!string.IsNullOrWhiteSpace(usuario))
        await abm.RegistrarLogoutAsync(usuario, sessionId);

    await http.SignOutAsync(NorturIdentityFactory.AuthScheme);
    return Results.Redirect("/login");
});

// ── Adjunto de un viaje (Tráfico → Ver Datos Extras → Ver Adjunto) ──────────
// Sirve el archivo de viaje.file mapeado a la carpeta accesible por el servidor.
// Requiere sesión iniciada (la cookie Nortur.Auth la manda el navegador). Devuelve el
// archivo inline (PDF/imagen se ven en el navegador; lo demás lo descarga). El front abre
// /adjunto/{idViaje}?f=yyyy-MM-dd en una pestaña nueva; si algo falla, devuelve el motivo
// en texto plano (réplica del MessageBox "No se encontró el archivo adjunto" del FoxPro).
app.MapGet("/adjunto/{idViaje:int}", async (
    int idViaje, string? f, HttpContext http, ReportService reports, AdjuntoService adjuntos) =>
{
    if (http.User?.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    DateOnly? fReserva = DateOnly.TryParse(f, out var d) ? d : null;
    var rutaFox = await reports.GetRutaAdjuntoViajeAsync(idViaje, fReserva);
    var res = adjuntos.Resolver(rutaFox);
    if (!res.Ok)
        return Results.Text(res.Error ?? "No se pudo abrir el adjunto.", "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status404NotFound);

    var stream = new FileStream(res.RutaFisica!, FileMode.Open, FileAccess.Read, FileShare.Read);
    return Results.File(stream, AdjuntoService.ContentType(res.NombreArchivo!),
        fileDownloadName: null,            // null = inline (no fuerza descarga)
        enableRangeProcessing: true);
});

// ── Logo de la empresa (Sistema → Parámetros → Empresa) ────────────────────
// Sirve la imagen de parametro.logo mapeada a la carpeta accesible por el servidor, para
// poder mostrar la vista previa que el FoxPro tiene en el form. Requiere sesión iniciada.
// Si no está configurado o no se encuentra, devuelve 404 con el motivo en texto plano
// (la pantalla muestra un cartel en su lugar, no se rompe).
app.MapGet("/logo-empresa", async (
    HttpContext http, ReportService reports, LogoEmpresaService logos) =>
{
    if (http.User?.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var p = await reports.GetParametrosEmpresaAsync();
    var res = logos.Resolver(p.Logo);
    if (!res.Ok)
        return Results.Text(res.Error ?? "No se pudo abrir el logo.", "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status404NotFound);

    var stream = new FileStream(res.RutaFisica!, FileMode.Open, FileAccess.Read, FileShare.Read);
    return Results.File(stream, AdjuntoService.ContentType(res.NombreArchivo!));
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// ── Helpers de registro de sesión (usados por /auth/login) ──────────────────

// IP real del cliente. NO se lee X-Forwarded-For a mano (sería falsificable): el middleware
// UseForwardedHeaders ya reescribió RemoteIpAddress con la IP del cliente cuando la petición
// vino de un proxy CONOCIDO. Se normaliza IPv4-mapped-IPv6 (::ffff:192.168.0.8 → 192.168.0.8)
// para que el log y el control de red muestren/comparen la IPv4 tal cual.
static string? ObtenerIpCliente(HttpContext http)
{
    var ip = http.Connection.RemoteIpAddress;
    if (ip is null) return null;
    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
    return ip.ToString();
}

// Nombre de la máquina por DNS inverso (best-effort). En web NO llega del navegador
// como en la LAN FoxPro, así que se intenta resolver por la IP. Timeout corto para no
// demorar el login si el DNS no responde; devuelve null si no resuelve.
static async Task<string?> ResolverHostnameAsync(string? ip)
{
    if (string.IsNullOrWhiteSpace(ip)) return null;
    if (!System.Net.IPAddress.TryParse(ip, out var addr)) return null;
    // localhost / loopback: no tiene sentido resolver.
    if (System.Net.IPAddress.IsLoopback(addr)) return "localhost";
    try
    {
        var lookup = System.Net.Dns.GetHostEntryAsync(addr);
        var done = await Task.WhenAny(lookup, Task.Delay(700));
        if (done == lookup && lookup.IsCompletedSuccessfully)
        {
            var name = lookup.Result.HostName;
            // Quedarse con el nombre corto de la máquina (sin el dominio DNS).
            return string.IsNullOrWhiteSpace(name) ? null
                 : name.Split('.')[0];
        }
    }
    catch { /* DNS no resuelve → hostname queda NULL, no es un error */ }
    return null;
}
