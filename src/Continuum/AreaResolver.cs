using System;
using System.Collections;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace Continuum;

/// <summary>
/// Resolves the tablet area that OpenTabletDriver is currently applying.
/// The live values are preferred: the user may have moved/resized the area in the UI
/// with "Apply" only, in which case settings.json on disk is already stale.
/// The file is used as a fallback when the live objects cannot be reached.
/// </summary>
internal static class AreaResolver
{
    public static bool TryResolve(IDriver? driver, string? tabletName, out AreaInfo area, out string source)
    {
        if (TryGetLiveArea(driver, tabletName, out area))
        {
            source = "live OTD settings";
            return true;
        }

        if (OtdAreaSource.TryGetArea(tabletName, out area, out _))
        {
            source = "settings.json";
            return true;
        }

        source = "none";
        return false;
    }

    private static bool TryGetLiveArea(IDriver? driver, string? tabletName, out AreaInfo area)
    {
        area = default;
        if (driver == null)
            return false;

        try
        {
            // InputDeviceTree lives in the core assembly, which a plugin cannot reference,
            // so the two hops below are resolved by name and everything after that is a
            // plain OpenTabletDriver.Plugin type.
            if (driver.GetType().GetProperty("InputDevices")?.GetValue(driver) is not IEnumerable devices)
                return false;

            AreaInfo? match = null;
            AreaInfo? first = null;
            int count = 0;

            foreach (var device in devices)
            {
                if (device == null)
                    continue;

                var deviceType = device.GetType();
                if (deviceType.GetProperty("OutputMode")?.GetValue(device) is not AbsoluteOutputMode mode)
                    continue;

                if (mode.Input is not { } input)
                    continue;

                var candidate = new AreaInfo(input.Width, input.Height, input.Position.X, input.Position.Y, input.Rotation);
                first ??= candidate;
                count++;

                var properties = deviceType.GetProperty("Properties")?.GetValue(device);
                if (properties?.GetType().GetProperty("Name")?.GetValue(properties) is string name &&
                    tabletName != null &&
                    string.Equals(name, tabletName, StringComparison.OrdinalIgnoreCase))
                {
                    match = candidate;
                    break;
                }
            }

            if (match is { } matched)
            {
                area = matched;
                return true;
            }

            if (count == 1 && first is { } only)
            {
                area = only;
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
