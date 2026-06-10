namespace MetroCarSysBlazor.Services;

public class AuthService
{
    private readonly ReportService _reports;

    public AuthService(ReportService reports)
    {
        _reports = reports;
    }

    public Task<bool> ValidarCredencialesAsync(string usuario, string password)
        => _reports.ValidarCredencialesAsync(usuario, password);
}
