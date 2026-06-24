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

// Servicios de aplicación
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ExcelExportService>();

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
app.MapPost("/auth/login", async (HttpContext http, AuthService auth) =>
{
    var form = await http.Request.ReadFormAsync();
    var usuario = form["usuario"].ToString();
    var password = form["password"].ToString();
    var destino = form["destino"].ToString();

    var res = await auth.LoginAsync(usuario, password);
    if (!res.Exito)
        return Results.Redirect($"/login?error={Uri.EscapeDataString(res.Error ?? "Error de acceso")}");

    var principal = NorturIdentityFactory.Crear(res.Usuario, res.Acceso, res.Nivel, res.Operador);
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

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(NorturIdentityFactory.AuthScheme);
    return Results.Redirect("/login");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
