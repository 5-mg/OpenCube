using System.Globalization;

namespace OpenCube.Services;

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var format = unit == 0 ? "0" : value >= 100 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
