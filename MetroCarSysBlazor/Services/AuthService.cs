namespace MetroCarSysBlazor.Services;

public class AuthService
{
    private readonly ReportService _reports;

    public AuthService(ReportService reports)
    {
        _reports = reports;
    }

    public Task<LoginResultDto> LoginAsync(string usuario, string password)
        => _reports.LoginAsync(usuario, password);
}
