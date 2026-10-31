using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Continuum;

/// <summary>Tablet area as configured in OpenTabletDriver (millimetres, position is the centre).</summary>
internal readonly record struct AreaInfo(float Width, float Height, float CenterX, float CenterY, float Rotation)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Width:0.###}x{Height:0.###}mm @ ({CenterX:0.###}, {CenterY:0.###}) rot {Rotation:0.###}°");
}

/// <summary>
/// Reads the active tablet area out of OpenTabletDriver's settings.json.
/// A filter does not get access to the output mode, but the area is required to know
/// which piece of the tablet is repeated.
/// </summary>
internal static class OtdAreaSource
{
    private static readonly object Sync = new();

    private static string? _cachedPath;

    public static bool TryGetArea(string? tabletName, out AreaInfo area, out DateTime stamp)
    {
        area = default;
        stamp = default;

        string? path = FindSettingsFile();
        if (path == null)
            return false;

        try
        {
            stamp = File.GetLastWriteTimeUtc(path);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("Profiles", out var profiles) ||
                profiles.ValueKind != JsonValueKind.Array)
                return false;

            JsonElement? selected = null;
            JsonElement? first = null;

            foreach (var profile in profiles.EnumerateArray())
            {
                first ??= profile;

                if (tabletName != null &&
                    profile.TryGetProperty("Tablet", out var name) &&
                    string.Equals(name.GetString(), tabletName, StringComparison.OrdinalIgnoreCase))
                {
                    selected = profile;
                    break;
                }
            }

            selected ??= first;
            if (selected is not { } profileElement)
                return false;

            if (!profileElement.TryGetProperty("AbsoluteModeSettings", out var absolute) ||
                !absolute.TryGetProperty("Tablet", out var tablet) ||
                tablet.ValueKind != JsonValueKind.Object)
                return false;

            float width = ReadFloat(tablet, "Width");
            float height = ReadFloat(tablet, "Height");

            if (width <= 0f || height <= 0f)
                return false;

            area = new AreaInfo(
                width,
                height,
                ReadFloat(tablet, "X"),
                ReadFloat(tablet, "Y"),
                ReadFloat(tablet, "Rotation"));

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when settings.json was rewritten since the last successful read.</summary>
    public static bool HasChanged(DateTime stamp)
    {
        string? path = _cachedPath ?? FindSettingsFile();
        if (path == null)
            return false;

        try
        {
            return File.GetLastWriteTimeUtc(path) != stamp;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Last write time of the settings file (default when there is none).</summary>
    public static DateTime CurrentStamp
    {
        get
        {
            string? path = FindSettingsFile();
            if (path == null)
                return default;

            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return default;
            }
        }
    }

    private static float ReadFloat(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0f;

        return value.ValueKind switch
        {
            JsonValueKind.Number => (float)value.GetDouble(),
            JsonValueKind.String when float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0f
        };
    }

    private static string? FindSettingsFile()
    {
        lock (Sync)
        {
            if (_cachedPath != null && File.Exists(_cachedPath))
                return _cachedPath;

            foreach (var candidate in EnumerateCandidates())
            {
                if (File.Exists(candidate))
                {
                    _cachedPath = candidate;
                    return candidate;
                }
            }

            return null;
        }
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        // Portable install: <OpenTabletDriver folder>/userdata/settings.json
        yield return Path.Combine(AppContext.BaseDirectory, "userdata", "settings.json");

        // Regular install
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(appData))
            yield return Path.Combine(appData, "OpenTabletDriver", "settings.json");
    }
}
