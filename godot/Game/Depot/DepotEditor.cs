using Godot;
using Stations.Core;
using Stations.Core.Sim;
using Stations.UI;

namespace Stations.DepotView;

/// <summary>
/// 3D yard editor for one depot: place driveways, parking spots and buildings on the grid,
/// see which parts are connected to the gate, and watch parked vehicles.
/// Left click builds (drag to paint driveways), R rotates, right/middle drag orbits, wheel zooms.
/// </summary>
public partial class DepotEditor : Node3D
{
    public Game Game = null!;
    public Depot Depot = null!;
    public event Action? Closed;

    private const float Cell = (float)DepotLayout.CellSize;

    private DepotLayout L => Depot.Layout;
    private Camera3D _cam = null!;
    private Node3D _modulesRoot = null!;
    private Node3D _vehiclesRoot = null!;
    private MeshInstance3D _ghost = null!;
    private StandardMaterial3D _ghostMat = null!;
    private MeshInstance3D _grid = null!;

    // Camera orbit
    private float _yaw = -0.6f, _pitch = 0.85f, _dist = 95;
    private Vector3 _focus = Vector3.Zero;
    private bool _orbiting;

    // Tool state: a module type, or null for bulldozing
    private ModuleType? _tool = ModuleType.Road;
    private bool _bulldoze;
    private int _rot;
    private (int X, int Y)? _hoverCell;
    private bool _painting;
    private (int, int)? _lastPainted;

    // UI
    private Label _stats = null!, _status = null!, _toolInfo = null!;
    private VBoxContainer _palette = null!;
    private double _vehicleTimer;

    private readonly Dictionary<string, StandardMaterial3D> _mats = new();

    public override void _Ready()
    {
        BuildEnvironment();
        BuildGround();
        _modulesRoot = new Node3D();
        AddChild(_modulesRoot);
        _vehiclesRoot = new Node3D();
        AddChild(_vehiclesRoot);

        _ghostMat = new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _ghost = new MeshInstance3D { Mesh = new BoxMesh(), MaterialOverride = _ghostMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_ghost);

        _cam = new Camera3D { Fov = 50, Far = 2000 };
        AddChild(_cam);
        _cam.MakeCurrent();
        _dist = Mathf.Max(L.Width, L.Height) * Cell * 1.1f;
        UpdateCamera();

        BuildUi();
        if (Depot.Mode == ModeId.Tram) _tool = ModuleType.Road;
        RebuildModules();
        RebuildVehicles();
        Game.Changed += OnGameChanged;
    }

    public override void _ExitTree() => Game.Changed -= OnGameChanged;

    private void OnGameChanged()
    {
        RebuildModules();
        RefreshStats();
    }

    // ---------------------------------------------------------------- scene

    private StandardMaterial3D Mat(string hex, float roughness = 0.9f)
    {
        if (_mats.TryGetValue(hex, out var m)) return m;
        m = new StandardMaterial3D { AlbedoColor = new Color(hex), Roughness = roughness };
        _mats[hex] = m;
        return m;
    }

    private void BuildEnvironment()
    {
        var sky = new Sky { SkyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color("6f9fd6"), SkyHorizonColor = new Color("d8e4ee"),
            GroundBottomColor = new Color("8a8f7a"), GroundHorizonColor = new Color("d8e4ee"),
        } };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky, Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("b9c6d6"), AmbientLightEnergy = 0.45f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic, TonemapExposure = 0.85f,
            SsaoEnabled = true, SsaoRadius = 2, SsaoIntensity = 1.5f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.0f, LightColor = new Color("fff4e0"), DirectionalShadowMaxDistance = 400 };
        sun.RotationDegrees = new Vector3(-55, 35, 0);
        AddChild(sun);
    }

    private Vector3 CellCenter(double x, double y) =>
        new((float)((x + 0.5) * Cell - L.Width * Cell / 2), 0, (float)((y + 0.5) * Cell - L.Height * Cell / 2));

    private void AddBox(Node parent, Vector3 center, Vector3 size, Material mat, bool shadow = true)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size }, Position = center, MaterialOverride = mat,
            CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private void BuildGround()
    {
        float w = L.Width * Cell, h = L.Height * Cell;
        AddBox(this, new Vector3(0, -0.06f, 0), new Vector3(600, 0.1f, 600), Mat("7d9461"), shadow: false); // grass
        AddBox(this, new Vector3(0, -0.02f, 0), new Vector3(w, 0.04f, h), Mat("a39e94"), shadow: false); // yard concrete

        // Street in front of the gate
        AddBox(this, new Vector3(0, -0.03f, h / 2 + 6), new Vector3(w + 80, 0.05f, 9), Mat("4f545b"), shadow: false);

        // Fence with a gap for the gate
        var fence = Mat("6d6a63");
        float gx0 = (L.Width / 2 - 1) * Cell - w / 2, gx1 = (L.Width / 2 + 1) * Cell - w / 2;
        AddBox(this, new Vector3(0, 1, -h / 2), new Vector3(w, 2, 0.15f), fence);
        AddBox(this, new Vector3(-w / 2, 1, 0), new Vector3(0.15f, 2, h), fence);
        AddBox(this, new Vector3(w / 2, 1, 0), new Vector3(0.15f, 2, h), fence);
        AddBox(this, new Vector3((-w / 2 + gx0) / 2, 1, h / 2), new Vector3(gx0 + w / 2, 2, 0.15f), fence);
        AddBox(this, new Vector3((gx1 + w / 2) / 2, 1, h / 2), new Vector3(w / 2 - gx1, 2, 0.15f), fence);
        // Gate posts and barrier
        var gate = Mat("d9a21b");
        AddBox(this, new Vector3(gx0, 1.4f, h / 2), new Vector3(0.4f, 2.8f, 0.4f), Mat("3a3d42"));
        AddBox(this, new Vector3(gx1, 1.4f, h / 2), new Vector3(0.4f, 2.8f, 0.4f), Mat("3a3d42"));
        AddBox(this, new Vector3(gx0 + 2.5f, 1.1f, h / 2 + 0.3f), new Vector3(5, 0.15f, 0.15f), gate);
        // Gate cells are drivable
        foreach (var (gxc, gyc) in L.GateCells())
            AddBox(this, CellCenter(gxc, gyc) + new Vector3(0, 0.03f, 0), new Vector3(Cell, 0.06f, Cell), Mat("4a4f56"), shadow: false);

        // Grid lines (shown while building)
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int x = 0; x <= L.Width; x++)
        {
            im.SurfaceAddVertex(new Vector3(x * Cell - w / 2, 0.05f, -h / 2));
            im.SurfaceAddVertex(new Vector3(x * Cell - w / 2, 0.05f, h / 2));
        }
        for (int y = 0; y <= L.Height; y++)
        {
            im.SurfaceAddVertex(new Vector3(-w / 2, 0.05f, y * Cell - h / 2));
            im.SurfaceAddVertex(new Vector3(w / 2, 0.05f, y * Cell - h / 2));
        }
        im.SurfaceEnd();
        _grid = new MeshInstance3D
        {
            Mesh = im, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1, 1, 1, 0.35f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        };
        AddChild(_grid);
    }

    /// <summary>Center and size of a module footprint in world space.</summary>
    private (Vector3 Center, Vector3 Size) Footprint(int x, int y, int w, int h, float height)
    {
        var c = CellCenter(x + (w - 1) / 2.0, y + (h - 1) / 2.0);
        return (c + new Vector3(0, height / 2, 0), new Vector3(w * Cell, height, h * Cell));
    }

    private void RebuildModules()
    {
        foreach (var c in _modulesRoot.GetChildren()) c.QueueFree();
        var reach = L.Reachable();
        foreach (var m in L.Modules)
        {
            var def = DepotLayout.Def(m.Type);
            bool ok = L.IsConnected(m, reach);
            var node = new Node3D();
            _modulesRoot.AddChild(node);
            switch (m.Type)
            {
                case ModuleType.Road:
                {
                    var (c, s) = Footprint(m.X, m.Y, 1, 1, 0.06f);
                    AddBox(node, c, s, Mat(ok ? "4a4f56" : "7a5a58"), shadow: false);
                    break;
                }
                case ModuleType.BusSpot or ModuleType.TramSpot:
                {
                    var (c, s) = Footprint(m.X, m.Y, m.W, m.H, 0.05f);
                    AddBox(node, c, s, Mat(ok ? def.Color : "d8a49c"), shadow: false);
                    // white side markings along the long edges (or rails for trams)
                    bool vertical = m.Rot % 2 == 0;
                    var lineSize = vertical ? new Vector3(0.15f, 0.06f, s.Z - 0.6f) : new Vector3(s.X - 0.6f, 0.06f, 0.15f);
                    var off = vertical ? new Vector3(s.X / 2 - 0.2f, 0, 0) : new Vector3(0, 0, s.Z / 2 - 0.2f);
                    var mark = m.Type == ModuleType.TramSpot ? Mat("3a3d42", 0.4f) : Mat("f4f4f0");
                    if (m.Type == ModuleType.TramSpot) off *= 0.35f; // rails closer together
                    AddBox(node, c + off + new Vector3(0, 0.03f, 0), lineSize, mark, shadow: false);
                    AddBox(node, c - off + new Vector3(0, 0.03f, 0), lineSize, mark, shadow: false);
                    break;
                }
                default:
                {
                    var (c, s) = Footprint(m.X, m.Y, m.W, m.H, (float)def.Height);
                    var body = new Vector3(s.X - 0.6f, s.Y, s.Z - 0.6f);
                    AddBox(node, c, body, Mat(ok ? def.Color : "d8a49c"));
                    AddBox(node, c + new Vector3(0, s.Y / 2 + 0.15f, 0), body + new Vector3(0.3f, -s.Y + 0.3f, 0.3f), Mat("6b6660"));
                    if (m.Type is ModuleType.Workshop or ModuleType.Wash)
                    {
                        // gates on the short sides
                        bool vertical = m.Rot % 2 == 0;
                        var door = vertical ? new Vector3(body.X * 0.7f, s.Y * 0.7f, 0.1f) : new Vector3(0.1f, s.Y * 0.7f, body.Z * 0.7f);
                        var dOff = vertical ? new Vector3(0, -s.Y * 0.15f, body.Z / 2) : new Vector3(body.X / 2, -s.Y * 0.15f, 0);
                        AddBox(node, c + dOff, door, Mat("3e4752"));
                        AddBox(node, c - dOff, door, Mat("3e4752"));
                    }
                    node.AddChild(new Label3D
                    {
                        Text = def.Name, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 64, PixelSize = 0.02f,
                        Position = c + new Vector3(0, s.Y / 2 + 2, 0), Modulate = Colors.White, OutlineSize = 12,
                        OutlineModulate = new Color(0, 0, 0, 0.7f), NoDepthTest = true,
                    });
                    break;
                }
            }
            if (!ok && m.Type != ModuleType.Road)
            {
                var (c, s) = Footprint(m.X, m.Y, m.W, m.H, (float)def.Height);
                node.AddChild(new Label3D
                {
                    Text = "⚠ kein Anschluss", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 48, PixelSize = 0.02f,
                    Position = c + new Vector3(0, s.Y / 2 + (m.Type is ModuleType.BusSpot or ModuleType.TramSpot ? 1.5f : 4), 0),
                    Modulate = new Color("ffb4a8"), OutlineSize = 10, OutlineModulate = new Color(0, 0, 0, 0.7f), NoDepthTest = true,
                });
            }
        }
        RebuildVehicles();
    }

    /// <summary>Vehicles in the depot stand on the connected spots, in a stable order.</summary>
    private void RebuildVehicles()
    {
        foreach (var c in _vehiclesRoot.GetChildren()) c.QueueFree();
        var parked = Game.Vehicles.Values.Where(v => v.DepotId == Depot.Id && v.State == VehicleState.Depot).OrderBy(v => v.Id).ToList();
        var spots = L.ConnectedSpots(Depot.Mode);
        var def = Modes.Get(Depot.Mode).Vehicle;
        for (int i = 0; i < Math.Min(parked.Count, spots.Count); i++)
        {
            var v = parked[i];
            var s = spots[i];
            var color = v.LineId is int lid && Game.Lines.TryGetValue(lid, out var l) ? l.Color : "8d939b";
            var (c, _) = Footprint(s.X, s.Y, s.W, s.H, 0);
            bool vertical = s.Rot % 2 == 0;
            float len = (float)def.Length, wid = (float)def.Width, height = 3.1f;
            var size = vertical ? new Vector3(wid, height, len) : new Vector3(len, height, wid);
            var bus = new Node3D();
            _vehiclesRoot.AddChild(bus);
            AddBox(bus, c + new Vector3(0, height / 2 + 0.35f, 0), size, Mat(color, 0.5f));
            // window band and roof
            var band = vertical ? new Vector3(wid + 0.04f, 0.9f, len - 1.2f) : new Vector3(len - 1.2f, 0.9f, wid + 0.04f);
            AddBox(bus, c + new Vector3(0, height * 0.72f + 0.35f, 0), band, Mat("2b3440", 0.2f));
            AddBox(bus, c + new Vector3(0, height + 0.4f, 0), size with { Y = 0.12f } * new Vector3(0.95f, 1, 0.95f), Mat("e8e8e8"));
        }
    }

    // ---------------------------------------------------------------- camera and input

    private void UpdateCamera()
    {
        var offset = new Vector3(
            Mathf.Cos(_pitch) * Mathf.Sin(_yaw),
            Mathf.Sin(_pitch),
            Mathf.Cos(_pitch) * Mathf.Cos(_yaw)) * _dist;
        _cam.Position = _focus + offset;
        _cam.LookAt(_focus, Vector3.Up);
    }

    /// <summary>Grid cell under a screen position, or null outside the yard.</summary>
    private (int X, int Y)? CellAt(Vector2 screen)
    {
        var from = _cam.ProjectRayOrigin(screen);
        var dir = _cam.ProjectRayNormal(screen);
        if (Mathf.Abs(dir.Y) < 1e-4f) return null;
        float t = -from.Y / dir.Y;
        if (t < 0) return null;
        var p = from + dir * t;
        int x = Mathf.FloorToInt((p.X + L.Width * Cell / 2) / Cell);
        int y = Mathf.FloorToInt((p.Z + L.Height * Cell / 2) / Cell);
        return x >= 0 && y >= 0 && x < L.Width && y < L.Height ? (x, y) : null;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } w:
                _dist = Mathf.Clamp(_dist * (w.ButtonIndex == MouseButton.WheelUp ? 0.88f : 1.13f), 15, 260);
                UpdateCamera();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right or MouseButton.Middle } b:
                _orbiting = b.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                _painting = b.Pressed;
                _lastPainted = null;
                if (b.Pressed && CellAt(b.Position) is { } cell) Apply(cell);
                break;
            case InputEventMouseMotion m:
                if (_orbiting)
                {
                    _yaw -= m.Relative.X * 0.006f;
                    _pitch = Mathf.Clamp(_pitch + m.Relative.Y * 0.005f, 0.25f, 1.45f);
                    UpdateCamera();
                }
                _hoverCell = CellAt(m.Position);
                // Driveways and bulldozing can be painted by dragging
                if (_painting && _hoverCell is { } hc && (_bulldoze || _tool == ModuleType.Road) && _lastPainted != hc) Apply(hc);
                UpdateGhost();
                break;
            case InputEventKey { Pressed: true, Echo: false } k:
                switch (k.Keycode)
                {
                    case Key.R: _rot = (_rot + 1) % 2; UpdateGhost(); break;
                    case Key.Q: _yaw += 0.3f; UpdateCamera(); break;
                    case Key.E: _yaw -= 0.3f; UpdateCamera(); break;
                    case Key.Escape: Closed?.Invoke(); break;
                    case Key.X: SetTool(null, bulldoze: true); break;
                    default: return;
                }
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    private void Apply((int X, int Y) cell)
    {
        _lastPainted = cell;
        string? err;
        if (_bulldoze) err = Game.RemoveModuleAt(Depot, cell.X, cell.Y);
        else if (_tool is ModuleType t)
        {
            if (t == ModuleType.Road && L.ModuleAt(cell.X, cell.Y)?.Type == ModuleType.Road) return; // already a driveway
            err = Game.PlaceModule(Depot, t, cell.X, cell.Y, _rot);
        }
        else return;
        SetStatus(err, err != null);
        UpdateGhost();
    }

    private void UpdateGhost()
    {
        if (_hoverCell is not { } c)
        {
            _ghost.Visible = false;
            return;
        }
        _ghost.Visible = true;
        if (_bulldoze)
        {
            var m = L.ModuleAt(c.X, c.Y);
            var (center, size) = m == null ? Footprint(c.X, c.Y, 1, 1, 0.3f)
                : Footprint(m.X, m.Y, m.W, m.H, (float)Math.Max(DepotLayout.Def(m.Type).Height, 0.3) + 0.2f);
            ((BoxMesh)_ghost.Mesh).Size = size + new Vector3(0.2f, 0, 0.2f);
            _ghost.Position = center;
            _ghostMat.AlbedoColor = new Color(0.9f, 0.2f, 0.15f, m == null ? 0.15f : 0.45f);
            return;
        }
        if (_tool is not ModuleType t) return;
        var def = DepotLayout.Def(t);
        int w = _rot % 2 == 0 ? def.W : def.H, h = _rot % 2 == 0 ? def.H : def.W;
        var (pos, sz) = Footprint(c.X, c.Y, w, h, (float)Math.Max(def.Height, 0.25));
        ((BoxMesh)_ghost.Mesh).Size = sz;
        _ghost.Position = pos;
        bool fits = L.CanPlace(t, c.X, c.Y, _rot) == null && Game.CanAfford(def.Cost);
        _ghostMat.AlbedoColor = fits ? new Color(0.2f, 0.8f, 0.35f, 0.45f) : new Color(0.9f, 0.2f, 0.15f, 0.45f);
    }

    public override void _Process(double delta)
    {
        _vehicleTimer += delta;
        if (_vehicleTimer > 0.5)
        {
            _vehicleTimer = 0;
            RebuildVehicles();
            RefreshStats();
        }
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var root = new Control { Theme = Ui.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(root);
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Top bar
        var top = new PanelContainer();
        top.AddThemeStyleboxOverride("panel", Ui.CardStyle(10, 14));
        top.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        top.OffsetLeft = 10;
        top.OffsetRight = -10;
        top.OffsetTop = 10;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        top.AddChild(row);
        row.AddChild(Ui.Btn("← Zurück zur Karte", () => Closed?.Invoke()));
        row.AddChild(Ui.Label(Depot.Name, 17, bold: true));
        _stats = Ui.Label("", 14);
        _stats.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(_stats);
        root.AddChild(top);

        // Palette
        var pal = new PanelContainer();
        pal.AddThemeStyleboxOverride("panel", Ui.CardStyle(10, 12));
        pal.Position = new Vector2(10, 78);
        pal.CustomMinimumSize = new Vector2(250, 0);
        _palette = new VBoxContainer();
        _palette.AddThemeConstantOverride("separation", 6);
        pal.AddChild(_palette);
        root.AddChild(pal);
        RebuildPalette();

        // Status / hint at the bottom
        var hint = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        hint.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.14f, 0.16f, 0.88f),
            CornerRadiusTopLeft = 16, CornerRadiusTopRight = 16, CornerRadiusBottomLeft = 16, CornerRadiusBottomRight = 16,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 6,
        });
        _status = Ui.Label("", 13, Colors.White);
        hint.AddChild(_status);
        hint.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        hint.GrowHorizontal = Control.GrowDirection.Both;
        hint.GrowVertical = Control.GrowDirection.Begin;
        hint.OffsetBottom = -14;
        root.AddChild(hint);
        SetStatus(null, false);
        RefreshStats();
    }

    private void RebuildPalette()
    {
        foreach (var c in _palette.GetChildren()) c.QueueFree();
        _palette.AddChild(Ui.Section("Bauen"));
        var types = new List<ModuleType> { ModuleType.Road, Depot.Mode == ModeId.Tram ? ModuleType.TramSpot : ModuleType.BusSpot,
            ModuleType.Office, ModuleType.Workshop, ModuleType.Wash };
        foreach (var t in types)
        {
            var def = DepotLayout.Def(t);
            var b = PaletteButton($"{def.Name}  ·  {Util.Money(def.Cost)}", !_bulldoze && _tool == t, () => SetTool(t, false));
            b.TooltipText = def.Effect;
            _palette.AddChild(b);
        }
        _palette.AddChild(PaletteButton("✕  Abreißen (X)", _bulldoze, () => SetTool(null, true)));
        _toolInfo = Ui.Label(_bulldoze ? "Klick (oder ziehen) entfernt Bauteile." : _tool is ModuleType tt ? DepotLayout.Def(tt).Effect : "", 12, Ui.Muted);
        _toolInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toolInfo.CustomMinimumSize = new Vector2(226, 0);
        _palette.AddChild(_toolInfo);
        _palette.AddChild(Ui.Section("Steuerung"));
        var help = Ui.Label("Linksklick: bauen (Fahrweg: ziehen)\nR: drehen\nRechte Maustaste ziehen: Kamera drehen\nMausrad: zoomen · Q/E: schwenken\nEsc: zurück zur Karte", 12, Ui.Muted);
        _palette.AddChild(help);
    }

    private static Button PaletteButton(string text, bool on, Action press)
    {
        var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, Flat = !on };
        if (on)
        {
            b.AddThemeStyleboxOverride("normal", Ui.ButtonStyle(Ui.Accent));
            b.AddThemeStyleboxOverride("hover", Ui.ButtonStyle(Ui.Accent.Lightened(0.1f)));
            foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color" })
                b.AddThemeColorOverride(c, Colors.White);
        }
        b.Pressed += press;
        return b;
    }

    private void SetTool(ModuleType? t, bool bulldoze)
    {
        _tool = t;
        _bulldoze = bulldoze;
        RebuildPalette();
        UpdateGhost();
    }

    private void SetStatus(string? error, bool isError)
    {
        _status.Text = error ?? "Verbinde Stellplätze und Gebäude über Fahrwege mit der Einfahrt (unten). Rot markierte Teile haben keinen Anschluss.";
        _status.AddThemeColorOverride("font_color", isError ? new Color("ffb4a8") : Colors.White);
    }

    private void RefreshStats()
    {
        int vehicles = Game.Vehicles.Values.Count(v => v.DepotId == Depot.Id);
        int home = Game.Vehicles.Values.Count(v => v.DepotId == Depot.Id && v.State == VehicleState.Depot);
        int drivers = Game.DriversOf(Depot.Id).Count;
        string spot = Depot.Mode == ModeId.Tram ? "Abstellgleise" : "Stellplätze";
        _stats.Text = $"{spot}: {vehicles} / {L.VehicleCapacity(Depot.Mode)} belegt ({home} im Hof)   ·   " +
                      $"Fahrer: {drivers} / {L.DriverCapacity()}   ·   Kontostand {Util.Money(Game.Money)}";
    }
}
