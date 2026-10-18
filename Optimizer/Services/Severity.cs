namespace OpenCube.Services;

public enum Severity { Low, Medium, High }

public static class SeverityInfo
{
    public static string Label(Severity s) => s switch
    {
        Severity.Low => "Gravedad baja",
        Severity.Medium => "Gravedad media",
        _ => "Gravedad alta",
    };
}
