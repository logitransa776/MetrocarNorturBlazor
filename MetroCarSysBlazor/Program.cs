using MetroCarSysBlazor.Components;
using MetroCarSysBlazor.Data;
using MetroCarSysBlazor.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using ApexCharts;
using MudBlazor.Services;

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

// Autenticación (estado en el circuito) + permisos por módulo (letras de usuario.acceso)
builder.Services.AddScoped<NorturAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<NorturAuthStateProvider>());
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddAuthorizationCore();
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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
