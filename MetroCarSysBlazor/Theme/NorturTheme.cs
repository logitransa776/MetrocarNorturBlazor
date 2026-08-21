using MudBlazor;

namespace MetroCarSysBlazor.Theme;

/// <summary>
/// Paleta corporativa NORTUR (de app.py del Streamlit), llevada a MudTheme.
///   AZUL        #003AA0  (principal)
///   AZUL_NOCHE  #112F5B  (header)
///   NARANJA     #F99410  (acento)
///   AZUL_CLARO  #E8EFF9  (highlights)
/// </summary>
public static class NorturColors
{
    public const string Azul = "#003AA0";
    public const string AzulNoche = "#112F5B";
    public const string Naranja = "#F99410";
    public const string AzulClaro = "#E8EFF9";
    public const string Verde = "#16A34A";
    public const string Rojo = "#DC2626";
    /// <summary>Quinto acento para KPIs (los 4 clásicos ya estaban tomados). Hue de la paleta dataviz.</summary>
    public const string Violeta = "#7C3AED";
}

public static class NorturTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = NorturColors.Azul,
            Secondary = NorturColors.Naranja,
            AppbarBackground = NorturColors.AzulNoche,
            Background = "#D5D9E2",
            Surface = "#FFFFFF",
            DrawerBackground = "#B5C4D8",
            Success = NorturColors.Verde,
            Error = NorturColors.Rojo,
            TextPrimary = NorturColors.AzulNoche,
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = new[] { "Roboto", "Segoe UI", "sans-serif" } },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
        },
    };
}
