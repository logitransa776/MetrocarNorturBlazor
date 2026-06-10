using Microsoft.EntityFrameworkCore;

namespace MetroCarSysBlazor.Data;

/// <summary>
/// DbContext del dashboard — solo sirve como puerta de acceso a la conexión
/// SQL. Toda la lógica de queries está en ReportService usando SQL crudo,
/// lo que evita los conflictos de mapeo de los models de FoxPro (80+ props).
/// </summary>
public class NorturDbContext : DbContext
{
    public NorturDbContext(DbContextOptions<NorturDbContext> options) : base(options) { }
}
