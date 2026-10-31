using System;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace Continuum;

/// <summary>
/// Repeats (tiles) the configured tablet area over the whole tablet surface.
/// Leaving the area on one side makes the cursor appear from the opposite side,
/// like the "wrap" behaviour of infinite canvases.
/// </summary>
/// <remarks>
/// The filter runs before OpenTabletDriver applies the tablet area, so it works in raw
/// digitizer coordinates. The area (size, centre, rotation) is read from OpenTabletDriver's
/// own settings.json, so the tiling always matches the area the user tuned in the UI.
/// Плагин сгенерировал нейронкой ибо сам не шарю в кодинге, вроде работает классно. Есчё DeepSeek Harness делал. (Целый юань потратил)
/// </remarks>
[PluginName("Continuum")]
public class ContinuumFilter : IPositionedPipelineElement<IDeviceReport>
{
    private const string LogGroup = "Continuum";

    private AreaInfo _area;
    private float _millimetresPerUnitX;
    private float _millimetresPerUnitY;
    private bool _ready;
    private bool _initialised;
    private bool _areaMissingLogged;
    private DateTime _areaStamp;
    private long _nextAreaCheck;

    [TabletReference]
    public TabletReference? Tablet { get; set; }

    [Resolved]
    public IDriver? Driver { get; set; }

    // BooleanPropertyAttribute already derives from PropertyAttribute, declaring both would make
    // reflection throw AmbiguousMatchException in the UX control generator.
    [BooleanProperty("Enable", "Tile the area across the whole tablet"),
     DefaultPropertyValue(true),
     ToolTip("When enabled, the area repeats over the whole tablet and the cursor wraps around at its edges.")]
    public bool Wrap { get; set; } = true;

    public PipelinePosition Position => PipelinePosition.PreTransform;

    public event Action<IDeviceReport?>? Emit;

    /// <summary>
    /// Settings are applied and the tablet reference is injected before this is invoked,
    /// so the tiling can be prepared (and reported in the log) right away.
    /// </summary>
    [OnDependencyLoad]
    public void OnLoad()
    {
        try
        {
            _initialised = true;
            _nextAreaCheck = Environment.TickCount64 + 500;
            Rebuild();
        }
        catch (Exception ex)
        {
            _ready = false;
            Log.Exception(ex);
        }
    }

    public void Consume(IDeviceReport? obj)
    {
        if (Wrap && obj is IAbsolutePositionReport report)
        {
            try
            {
                EnsureReady();
                if (_ready)
                    report.Position = Map(report.Position);
            }
            catch (Exception ex)
            {
                // A filter must never take the whole pipeline down.
                _ready = false;
                Log.Exception(ex);
            }
        }

        Emit?.Invoke(obj);
    }

    private void EnsureReady()
    {
        long now = Environment.TickCount64;

        if (!_initialised)
        {
            _initialised = true;
            _nextAreaCheck = now + 500;
            Rebuild();
            return;
        }

        if (now < _nextAreaCheck)
            return;

        _nextAreaCheck = now + 500;

        if (_ready)
        {
            // Pick up area edits saved by the UX without rebuilding the plugin.
            if (OtdAreaSource.HasChanged(_areaStamp))
                Rebuild();
        }
        else
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _ready = false;

        var digitizer = Tablet?.Properties?.Specifications?.Digitizer;
        if (digitizer == null)
        {
            Log.Write(LogGroup, "Tablet digitizer specifications are unavailable, filter disabled.", LogLevel.Error);
            return;
        }

        if (digitizer.MaxX <= 0f || digitizer.MaxY <= 0f || digitizer.Width <= 0f || digitizer.Height <= 0f)
        {
            Log.Write(LogGroup, "Tablet digitizer has invalid dimensions, filter disabled.", LogLevel.Error);
            return;
        }

        string tabletName = Tablet?.Properties?.Name ?? string.Empty;

        if (!AreaResolver.TryResolve(Driver, tabletName, out var area, out var source))
        {
            if (!_areaMissingLogged)
            {
                _areaMissingLogged = true;
                Log.Write(LogGroup, "Could not resolve the tablet area, filter disabled.", LogLevel.Error);
            }
            return;
        }

        _areaMissingLogged = false;
        _area = area;
        _areaStamp = OtdAreaSource.CurrentStamp;
        _millimetresPerUnitX = digitizer.Width / digitizer.MaxX;
        _millimetresPerUnitY = digitizer.Height / digitizer.MaxY;
        _ready = true;

        Log.Write(LogGroup, $"Enabled. Repeating area {area} ({source}) across the whole tablet.");
    }

    private Vector2 Map(Vector2 raw)
    {
        var area = _area;

        // Raw digitizer units -> millimetres -> area-local, un-rotated frame.
        var millimetres = new Vector2(raw.X * _millimetresPerUnitX, raw.Y * _millimetresPerUnitY);
        var local = millimetres - new Vector2(area.CenterX, area.CenterY);
        var oriented = Rotate(local, -area.Rotation);

        // Wrap inside the area rectangle, then move back.
        float x = WrapCoordinate(oriented.X + area.Width * 0.5f, area.Width) - area.Width * 0.5f;
        float y = WrapCoordinate(oriented.Y + area.Height * 0.5f, area.Height) - area.Height * 0.5f;

        var wrapped = Rotate(new Vector2(x, y), area.Rotation) + new Vector2(area.CenterX, area.CenterY);

        return new Vector2(
            wrapped.X / _millimetresPerUnitX,
            wrapped.Y / _millimetresPerUnitY);
    }

    /// <summary>Positive modulo, so negative coordinates wrap into the next tile instead of clamping.</summary>
    private static float WrapCoordinate(float value, float size)
    {
        float remainder = value % size;
        if (remainder < 0f)
            remainder += size;

        // Guard against "size" landing exactly on the wrapped value because of float rounding.
        if (remainder >= size)
            remainder = 0f;

        return remainder;
    }

    private static Vector2 Rotate(Vector2 value, float degrees)
    {
        if (degrees == 0f)
            return value;

        float radians = degrees * (MathF.PI / 180f);
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Vector2(value.X * cos - value.Y * sin, value.X * sin + value.Y * cos);
    }
}
