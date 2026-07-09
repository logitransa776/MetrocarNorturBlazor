using MetroCarSysBlazor.Components;
using MetroCarSysBlazor.Data;
using MetroCarSysBlazor.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
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
// Adjuntos de viajes (Tráfico → Ver Datos Extras → Ver Adjunto). Singleton: solo lee config.
builder.Services.AddSingleton<AdjuntoService>();

// Autenticación por COOKIE — el navegador la manda en cada petición HTTP (pestaña
// nueva / F5 incluidas), así el servidor reconoce al usuario antes de renderizar.
// Expiración DESLIZANTE de 8h (jornada laboral): cada actividad renueva el reloj.
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
app.MapPost("/auth/login", async (HttpContext http, AuthService auth, AbmService abm) =>
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

    // Registrar el ingreso ANTES de crear la cookie: así obtenemos el session_id (GUID)
    // y lo guardamos como claim en la cookie, para poder cruzar el logout con su login.
    var sessionId = await abm.RegistrarLoginAsync(res.Usuario, ip, hostname);

    var principal = NorturIdentityFactory.Crear(res.Usuario, res.Acceso, res.Nivel, res.Operador, sessionId);
    await http.SignInAsync(NorturIdentityFactory.AuthScheme, principal,
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            IsPersistent = true,
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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// ── Helpers de registro de sesión (usados por /auth/login) ──────────────────

// IP del cliente. Respeta X-Forwarded-For si la app corre detrás de un proxy/IIS
// (toma la primera IP de la cadena); si no, la IP remota de la conexión.
static string? ObtenerIpCliente(HttpContext http)
{
    var fwd = http.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(fwd))
        return fwd.Split(',')[0].Trim();
    return http.Connection.RemoteIpAddress?.ToString();
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
