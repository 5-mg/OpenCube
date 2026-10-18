using System.Text.RegularExpressions;

namespace OpenCube.Services;

/// <summary>How sure we are that the item is unnecessary for most people.</summary>
public enum Confidence { High, Medium, Low }

public sealed record BloatRule(string Id, string Title, string Reason, Confidence Confidence, AppSource Source, Regex Pattern);

public sealed record BloatFinding(InstalledApp App, BloatRule Rule);

/// <summary>
/// A short curated list of things many people never asked for. It is a heuristic: it only suggests,
/// the user always decides, and every entry explains why it is listed.
/// </summary>
public static class BloatCatalog
{
    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<BloatRule> Rules { get; } =
    [
        // ---- Store apps that mostly show content or promotions ----
        new("bing-news", "Noticias, tiempo y finanzas de Bing", "Aplicaciones de contenido de Bing; solo muestran noticias y anuncios.", Confidence.Medium, AppSource.Store, R(@"^Microsoft\.Bing(News|Weather|Finance|Sports|Travel|FoodAndDrink|HealthAndFitness)$")),
        new("get-help", "Obtener ayuda y Consejos", "Aplicaciones de ayuda de Microsoft que casi nadie abre.", Confidence.High, AppSource.Store, R(@"^Microsoft\.(GetHelp|Getstarted)$")),
        new("feedback-hub", "Centro de opiniones", "Sirve para enviar comentarios a Microsoft; no hace falta para usar Windows.", Confidence.High, AppSource.Store, R(@"^Microsoft\.WindowsFeedbackHub$")),
        new("solitaire", "Microsoft Solitaire Collection", "Juego con anuncios y suscripción; si no lo usas, sobra.", Confidence.Medium, AppSource.Store, R(@"^Microsoft\.MicrosoftSolitaireCollection$")),
        new("office-hub", "Centro de Office", "Acceso directo promocional a Office; no es Office en sí.", Confidence.Medium, AppSource.Store, R(@"^Microsoft\.MicrosoftOfficeHub$")),
        new("mixed-reality", "Portal de realidad mixta", "Solo sirve si tienes unas gafas de realidad mixta compatibles.", Confidence.High, AppSource.Store, R(@"^Microsoft\.MixedReality\.Portal$")),
        new("3d-apps", "Visor 3D / Paint 3D / 3D Builder", "Herramientas 3D descontinuadas por Microsoft.", Confidence.High, AppSource.Store, R(@"^Microsoft\.(Microsoft3DViewer|Print3D|3DBuilder)$")),
        new("people-skype", "Contactos y Skype (versión de la Tienda)", "Apps de comunicación preinstaladas que mucha gente ya no usa.", Confidence.Medium, AppSource.Store, R(@"^Microsoft\.(People|SkypeApp)$")),
        new("teams-consumer", "Microsoft Teams (versión personal)", "Chat personal preinstalado; si usas Teams de trabajo es otra app.", Confidence.Medium, AppSource.Store, R(@"^(MicrosoftTeams|MSTeams)$")),
        new("xbox-console", "Xbox Console Companion", "Aplicación antigua para consolas Xbox; el resto de componentes Xbox no se tocan.", Confidence.Medium, AppSource.Store, R(@"^Microsoft\.XboxApp$")),
        new("cortana", "Cortana", "Asistente que Microsoft ya retiró.", Confidence.Medium, AppSource.Store, R(@"^Microsoft\.549981C3F5F10$")),
        new("king-games", "Juegos de King (Candy Crush y similares)", "Juegos promocionales que Windows instala por su cuenta.", Confidence.High, AppSource.Store, R(@"^king\.com\.")),
        new("promo-apps", "Aplicaciones promocionales preinstaladas", "TikTok, Facebook, Instagram, Twitter, LinkedIn, Disney+, Netflix o Prime Video instalados de fábrica; si no las usas, sobran.", Confidence.Medium, AppSource.Store, R(@"(TikTok|Facebook|Instagram|Twitter|LinkedIn|Disney|Netflix|PrimeVideo|Hulu|SlingTV)")),

        // ---- Programs: trials, toolbars, "optimizers" and software without support ----
        new("mcafee", "McAfee", "Suele venir como prueba en equipos nuevos. Windows Defender ya protege; quítalo solo si no tienes una licencia activa.", Confidence.Medium, AppSource.Program, R(@"McAfee")),
        new("norton", "Norton", "Suele venir como prueba en equipos nuevos. Windows Defender ya protege; quítalo solo si no tienes una licencia activa.", Confidence.Medium, AppSource.Program, R(@"^Norton")),
        new("wildtangent", "WildTangent Games", "Juegos con anuncios que traen algunos equipos de marca.", Confidence.High, AppSource.Program, R(@"WildTangent")),
        new("toolbars", "Barras de herramientas y buscadores añadidos", "Programas conocidos por cambiar el buscador o la página de inicio del navegador.", Confidence.High, AppSource.Program, R(@"(Ask\s+Toolbar|Babylon|Conduit|Search\s*Protect|Delta\s+(Search|Toolbar)|Web\s*Companion)")),
        new("fake-optimizers", "«Optimizadores» y limpiadores de dudosa utilidad", "Este tipo de programas suele mostrar errores exagerados para empujarte a pagar. No es una afirmación de que sea malware: revisa si lo instalaste tú.", Confidence.Medium, AppSource.Program, R(@"(SpeedUpMyPC|PC\s+Optimizer\s+Pro|MyCleanPC|Reimage|RegClean\s+Pro|Registry\s+Booster|Driver\s+Updater|DriverPack)")),
        new("flash", "Adobe Flash Player", "Descontinuado desde 2021 y sin parches de seguridad. No funciona en ningún navegador actual.", Confidence.High, AppSource.Program, R(@"Adobe\s+Flash\s+Player")),
        new("quicktime", "Apple QuickTime para Windows", "Apple dejó de darle soporte en 2016 y tiene fallos de seguridad conocidos.", Confidence.High, AppSource.Program, R(@"^QuickTime")),
        new("silverlight", "Microsoft Silverlight", "Sin soporte desde octubre de 2021; casi ninguna web lo usa ya.", Confidence.Medium, AppSource.Program, R(@"Microsoft\s+Silverlight")),
        new("old-java", "Java 6 / 7", "Versiones sin soporte desde hace años. Si algún programa antiguo lo necesita, no lo quites.", Confidence.Low, AppSource.Program, R(@"^Java(\(TM\))?\s+(6|7)(\s|$)")),
    ];

    public static IReadOnlyList<BloatFinding> Detect(IEnumerable<InstalledApp> apps)
    {
        var findings = new List<BloatFinding>();
        foreach (var app in apps)
        {
            var rule = Rules.FirstOrDefault(r => r.Source == app.Source && r.Pattern.IsMatch(app.Name));
            if (rule is not null) findings.Add(new BloatFinding(app, rule));
        }
        return findings
            .OrderBy(f => f.Rule.Confidence)
            .ThenBy(f => f.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
