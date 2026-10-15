using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace Mira.Desktop.Services;

/// <summary>This PC's graphics cards, as Windows lists them (Win32_VideoController, read without rights).</summary>
internal static class GraphicsCards
{
    /// <summary>Their names (« NVIDIA GeForce RTX 3060 », « Intel(R) UHD Graphics 770 »); empty when WMI does not answer.</summary>
    public static List<string> Names() => Read(withDriver: false);
    /// <summary>Their names and driver versions (« NVIDIA GeForce RTX 3060 32.0.15.6094 »), which change with each driver update.</summary>
    public static List<string> Drivers() => Read(withDriver: true);
    private static List<string> Read(bool withDriver)
    {
        var names = new List<string>();
        try
        {
            // By name through IDispatch, as the firewall's network profiles (JellyfinAutostart.NetworkCategories).
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic service = locator.ConnectServer(".", @"root\cimv2");
            foreach (dynamic card in service.ExecQuery("SELECT Name, DriverVersion FROM Win32_VideoController"))
            {
                if ((object?)card.Properties_.Item("Name").Value is not string name || name.Length == 0) continue;
                names.Add(withDriver && (object?)card.Properties_.Item("DriverVersion").Value is string driver && driver.Length > 0 ? $"{name} {driver}" : name);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or RuntimeBinderException or InvalidCastException or UnauthorizedAccessException
            or ArgumentException or TypeLoadException or NotSupportedException or System.Reflection.TargetInvocationException)
        { }
        return names;
    }
}
