using System.Globalization;
using System.Numerics;
using System.Text;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;
using Continuum;

namespace Continuum.Tests;

internal static class Program
{
    private const float DigitizerWidthMm = 152f;
    private const float DigitizerHeightMm = 95f;
    private const float DigitizerMaxX = 15200f;
    private const float DigitizerMaxY = 9500f;

    // Area centred on the tablet, so whole tiles fit on the surface.
    private static readonly AreaInfo CenteredArea = new(60f, 40f, 76f, 47.5f, 0f);
    private static readonly AreaInfo CenteredAreaRotated = new(60f, 40f, 76f, 47.5f, 180f);

    private static int _failures;
    private static int _checks;
    private static string _settingsPath = string.Empty;

    private static int Main()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "userdata");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            WriteSettings(_settingsPath, CenteredArea, "Wacom CTL-472");

            TestAreaParsing();
            TestLiveAreaWinsOverFile();
            TestDisabledIsPassthrough();
            TestInsideAreaIsUntouched();
            TestWrapRightToLeft();
            TestWrapBottomToTop();
            TestFullTileIsIdentity();
            TestRotation180();
            TestBoundaryIsStable();
            TestWholeTabletHasNoNaN();
            TestFarOutsideCoordinates();
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* ignore */ }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECKS FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void TestAreaParsing()
    {
        Check(typeof(ContinuumFilter).FullName == "Continuum.ContinuumFilter",
            "plugin: type name matches the one written into settings.json");

        Check(OtdAreaSource.TryGetArea("Wacom CTL-472", out var area, out _), "settings: area is read");
        Check(MathF.Abs(area.Width - 60f) < 1e-3f && MathF.Abs(area.Height - 40f) < 1e-3f,
            "settings: width/height");
        Check(MathF.Abs(area.CenterX - 76f) < 1e-3f && MathF.Abs(area.CenterY - 47.5f) < 1e-3f,
            "settings: centre");
    }

    /// <summary>
    /// The area the user is editing with "Apply" (no Save yet) must win over the stale file.
    /// </summary>
    private static void TestLiveAreaWinsOverFile()
    {
        var driver = new FakeDriver();
        var device = new FakeDevice { Properties = { Name = "Wacom CTL-472" } };
        device.OutputMode.Input = new Area(60f, 40f, new Vector2(81.16f, 75f), 180f);
        driver.Devices.Add(device);

        Check(AreaResolver.TryResolve(driver, "Wacom CTL-472", out var live, out var source),
            "live area: resolved from the running driver");
        Check(MathF.Abs(live.CenterX - 81.16f) < 1e-2f && MathF.Abs(live.CenterY - 75f) < 1e-2f,
            "live area: takes the value applied in OTD, not the saved one");
        Check(source == "live OTD settings", "live area: reports its source");

        var filter = new ContinuumFilter { Tablet = CreateTablet(), Driver = driver, Wrap = true };

        // 130 mm is inside the saved area (122±30) but outside the live one (81.16±30),
        // so it must wrap: 48.84 mm past the centre -> 11.16 mm inside the mirrored edge.
        var report = new FakeReport { Position = MillimetresToRaw(new Vector2(130f, 75f)) };
        filter.Consume(report);

        Near(RawToMillimetres(report.Position), new Vector2(70f, 75f), 0.1f,
            "live area: filter wraps around the applied area, not the saved one");
    }

    private static void TestDisabledIsPassthrough()
    {
        var filter = CreateFilter(wrap: false);
        var report = new FakeReport { Position = new Vector2(14000f, 8000f) };
        filter.Consume(report);

        Near(report.Position, new Vector2(14000f, 8000f), 1e-3f, "disabled: passthrough");
    }

    private static void TestInsideAreaIsUntouched()
    {
        var filter = CreateFilter();

        foreach (var mm in new[]
        {
            new Vector2(76f, 47.5f), // centre
            new Vector2(50f, 30f),
            new Vector2(100f, 60f),
            new Vector2(46.5f, 27.9f),
        })
        {
            var report = new FakeReport { Position = MillimetresToRaw(mm) };
            filter.Consume(report);
            Near(RawToMillimetres(report.Position), mm, 0.05f, $"inside: {F(mm)} is unchanged");
        }
    }

    private static void TestWrapRightToLeft()
    {
        var filter = CreateFilter();

        // 2 mm past the right edge of the area -> 2 mm from the left edge.
        var report = new FakeReport { Position = MillimetresToRaw(new Vector2(108f, 47.5f)) };
        filter.Consume(report);

        Near(RawToMillimetres(report.Position), new Vector2(48f, 47.5f), 0.05f,
            "wrap: 2 mm past the right edge appears 2 mm from the left edge");

        // 10 mm past the left edge -> 10 mm from the right edge.
        var left = new FakeReport { Position = MillimetresToRaw(new Vector2(36f, 47.5f)) };
        filter.Consume(left);

        Near(RawToMillimetres(left.Position), new Vector2(96f, 47.5f), 0.05f,
            "wrap: 10 mm past the left edge appears 10 mm from the right edge");
    }

    private static void TestWrapBottomToTop()
    {
        var filter = CreateFilter();

        // 3 mm past the bottom edge -> 3 mm from the top edge.
        var report = new FakeReport { Position = MillimetresToRaw(new Vector2(76f, 70.5f)) };
        filter.Consume(report);

        Near(RawToMillimetres(report.Position), new Vector2(76f, 30.5f), 0.05f,
            "wrap: below the area appears at the top");
    }

    private static void TestFullTileIsIdentity()
    {
        var filter = CreateFilter();

        // Any whole number of tiles away must land on exactly the same place.
        var offsets = new[]
        {
            new Vector2(60f, 0f),
            new Vector2(0f, 40f),
            new Vector2(-120f, 0f),
            new Vector2(180f, 80f),
        };

        foreach (var offset in offsets)
        {
            var target = new Vector2(76f, 47.5f) + offset;
            var report = new FakeReport { Position = MillimetresToRaw(target) };
            filter.Consume(report);

            Near(RawToMillimetres(report.Position), new Vector2(76f, 47.5f), 0.05f,
                $"tiling: offset {F(offset)} maps back to the centre");
        }
    }

    private static void TestRotation180()
    {
        WriteSettings(_settingsPath, CenteredAreaRotated, "Wacom CTL-472");

        var filter = CreateFilter();

        // With a 180° rotated area the tablet's X axis is mirrored on screen, so the two
        // edges swap places: 2 mm past one edge must appear 2 mm inside the opposite one.
        var right = new FakeReport { Position = MillimetresToRaw(new Vector2(44f, 47.5f)) };
        filter.Consume(right);

        Near(RawToMillimetres(right.Position), new Vector2(104f, 47.5f), 0.05f,
            "wrap: works with a rotated area (44 mm -> 104 mm)");

        var left = new FakeReport { Position = MillimetresToRaw(new Vector2(108f, 47.5f)) };
        filter.Consume(left);

        Near(RawToMillimetres(left.Position), new Vector2(48f, 47.5f), 0.05f,
            "wrap: works with a rotated area (108 mm -> 48 mm)");

        WriteSettings(_settingsPath, CenteredArea, "Wacom CTL-472");
    }

    private static void TestBoundaryIsStable()
    {
        var filter = CreateFilter();
        bool ok = true;

        // Exactly on every edge the result must stay inside the area and be finite.
        foreach (var mm in new[]
        {
            new Vector2(106f, 47.5f), // right edge
            new Vector2(46f, 47.5f),  // left edge
            new Vector2(76f, 67.5f),  // bottom edge
            new Vector2(76f, 27.5f),  // top edge
            new Vector2(106f, 67.5f), // corner
        })
        {
            var report = new FakeReport { Position = MillimetresToRaw(mm) };
            filter.Consume(report);

            var result = RawToMillimetres(report.Position);
            if (float.IsNaN(result.X) || float.IsNaN(result.Y))
                ok = false;

            if (result.X < 45.9f || result.X > 106.1f || result.Y < 27.4f || result.Y > 67.6f)
                ok = false;
        }

        Check(ok, "boundary: edges map inside the area without NaN");
    }

    private static void TestWholeTabletHasNoNaN()
    {
        var filter = CreateFilter();
        bool ok = true;

        for (int y = 0; y <= 95; y += 5)
        {
            for (int x = 0; x <= 152; x += 5)
            {
                var report = new FakeReport { Position = MillimetresToRaw(new Vector2(x, y)) };
                filter.Consume(report);

                var result = RawToMillimetres(report.Position);
                if (float.IsNaN(result.X) || float.IsNaN(result.Y))
                    ok = false;
            }
        }

        Check(ok, "no NaN anywhere on the tablet");
    }

    private static void TestFarOutsideCoordinates()
    {
        var filter = CreateFilter();

        var report = new FakeReport { Position = MillimetresToRaw(new Vector2(76f + 600f, 47.5f + 400f)) };
        filter.Consume(report);

        var result = RawToMillimetres(report.Position);
        Check(!float.IsNaN(result.X) && !float.IsNaN(result.Y), "far outside: still finite");
        Check(result.X >= 45.9f && result.X <= 106.1f && result.Y >= 27.4f && result.Y <= 67.6f,
            "far outside: lands inside the area");
    }

    // -------------------------------------------------------------- helpers

    private static ContinuumFilter CreateFilter(bool wrap = true)
    {
        return new ContinuumFilter
        {
            Tablet = CreateTablet(),
            Wrap = wrap,
        };
    }

    private static TabletReference CreateTablet()
    {
        return new TabletReference(
            new TabletConfiguration
            {
                Name = "Wacom CTL-472",
                Specifications = new TabletSpecifications
                {
                    Digitizer = new DigitizerSpecifications
                    {
                        Width = DigitizerWidthMm,
                        Height = DigitizerHeightMm,
                        MaxX = DigitizerMaxX,
                        MaxY = DigitizerMaxY,
                    },
                    Pen = new PenSpecifications { MaxPressure = 8192, ButtonCount = 2 },
                },
                DigitizerIdentifiers =
                [
                    new DeviceIdentifier { VendorID = 0x056A, ProductID = 0x037A, InputReportLength = 10 },
                ],
            },
            []);
    }

    private static Vector2 RawToMillimetres(Vector2 raw) =>
        new(raw.X * (DigitizerWidthMm / DigitizerMaxX), raw.Y * (DigitizerHeightMm / DigitizerMaxY));

    private static Vector2 MillimetresToRaw(Vector2 millimetres) =>
        new(millimetres.X * (DigitizerMaxX / DigitizerWidthMm), millimetres.Y * (DigitizerMaxY / DigitizerHeightMm));

    private static void WriteSettings(string path, AreaInfo area, string tabletName)
    {
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"Revision\": \"0.6.8.1\",\n");
        json.Append("  \"Profiles\": [\n");
        json.Append("    {\n");
        json.Append($"      \"Tablet\": \"{tabletName}\",\n");
        json.Append("      \"Filters\": [],\n");
        json.Append("      \"AbsoluteModeSettings\": {\n");
        json.Append("        \"Display\": { \"Width\": 1920.0, \"Height\": 1080.0, \"X\": 960.0, \"Y\": 540.0, \"Rotation\": 0.0 },\n");
        json.Append("        \"Tablet\": { ");
        json.Append(FormattableString.Invariant($"\"Width\": {area.Width}, \"Height\": {area.Height}, "));
        json.Append(FormattableString.Invariant($"\"X\": {area.CenterX}, \"Y\": {area.CenterY}, \"Rotation\": {area.Rotation}"));
        json.Append(" }\n");
        json.Append("      }\n");
        json.Append("    }\n");
        json.Append("  ]\n");
        json.Append("}\n");

        File.WriteAllText(path, json.ToString());
    }

    private static void Near(Vector2 actual, Vector2 expected, float tolerance, string name)
        => Check(Vector2.Distance(actual, expected) <= tolerance,
            $"{name} (got {F(actual)}, expected {F(expected)})");

    private static string F(Vector2 v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine($"  ok   {name}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  FAIL {name}");
        }
    }

    private sealed class FakeReport : IAbsolutePositionReport
    {
        public Vector2 Position { get; set; }
        public byte[] Raw { get; set; } = [];
    }

    private sealed class FakeDriver : IDriver
    {
        public List<object> Devices { get; } = [];

        // Matches the property the plugin looks up by name on the real Driver.
        public IEnumerable<object> InputDevices => Devices;

        public event EventHandler<IEnumerable<TabletReference>>? TabletsChanged
        {
            add { } remove { }
        }

        public IEnumerable<TabletReference> Tablets => Array.Empty<TabletReference>();

        public bool Detect() => false;

        public IReportParser<IDeviceReport> GetReportParser(DeviceIdentifier identifier) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDevice
    {
        public FakeAbsoluteMode OutputMode { get; set; } = new();
        public FakeProperties Properties { get; set; } = new();
    }

    private sealed class FakeProperties
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class FakeAbsoluteMode : AbsoluteOutputMode
    {
        public override IAbsolutePointer? Pointer { get; set; }

        protected override Matrix3x2 CreateTransformationMatrix() => Matrix3x2.Identity;

        protected override IAbsolutePositionReport? Transform(IAbsolutePositionReport tabletReport) => tabletReport;

        protected override void OnOutput(IDeviceReport report)
        {
        }
    }
}
