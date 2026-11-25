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
    private Node3D _peopleRoot = null!;
    private Label3D _hoverLabel = null!;
    private Vector2 _mouse = new(-1000, -1000);
    /// <summary>Interior view: ceilings, upper storeys and hall roofs hidden (also automatic when zoomed in close).</summary>
    private bool _interior;
    private readonly List<Node3D> _roofs = new();
    private readonly List<Label3D> _buildingLabels = new();
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
    /// <summary>Start cell while a building is being drawn as a rectangle.</summary>
    private (int X, int Y)? _dragStart;

    // UI
    private Label _stats = null!, _status = null!, _toolInfo = null!;
    private VBoxContainer _palette = null!;
    private double _vehicleTimer;


    public override void _Ready()
    {
        BuildEnvironment();
        BuildGround();
        _modulesRoot = new Node3D();
        AddChild(_modulesRoot);
        _vehiclesRoot = new Node3D();
        AddChild(_vehiclesRoot);
        _peopleRoot = new Node3D();
        AddChild(_peopleRoot);
        _hoverLabel = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 44, PixelSize = 0.012f, NoDepthTest = true,
            Modulate = Colors.White, OutlineSize = 10, OutlineModulate = new Color(0, 0, 0, 0.8f), Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        };
        AddChild(_hoverLabel);

        _ghostMat = new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _ghost = new MeshInstance3D { Mesh = new BoxMesh(), MaterialOverride = _ghostMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_ghost);

        _cam = new Camera3D { Fov = 50, Far = 2000 };
        AddChild(_cam);
        _cam.MakeCurrent();
        _dist = Mathf.Max(L.Width, L.Height) * Cell * 1.1f;
        UpdateCamera();

        BuildUi();
        UpdateDayNight();
        if (Depot.Mode != ModeId.Bus) _tool = ModuleType.Road;
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

    private readonly Props _props = new();
    private DirectionalLight3D _sun = null!;
    private Godot.Environment _env = null!;
    private ProceduralSkyMaterial _sky = null!;
    private readonly List<OmniLight3D> _lamps = new();
    private readonly List<(Node3D Pivot, int ModX, int ModY, int Lane)> _brushes = new();

    private StandardMaterial3D Mat(string hex, float roughness = 0.9f) => _props.Solid(hex, roughness);

    private void BuildEnvironment()
    {
        _sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color("6f9fd6"), SkyHorizonColor = new Color("d8e4ee"),
            GroundBottomColor = new Color("8a8f7a"), GroundHorizonColor = new Color("d8e4ee"),
        };
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky, Sky = new Sky { SkyMaterial = _sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("b9c6d6"), AmbientLightEnergy = 0.45f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic, TonemapExposure = 0.85f,
            SsaoEnabled = true, SsaoRadius = 2, SsaoIntensity = 1.5f,
            GlowEnabled = true, GlowIntensity = 0.6f, GlowBloom = 0.05f,
            FogEnabled = true, FogDensity = 0.0007f, FogLightColor = new Color("c9d3dc"),
        };
        AddChild(new WorldEnvironment { Environment = _env });
        _sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.0f, LightColor = new Color("fff4e0"), DirectionalShadowMaxDistance = 300 };
        AddChild(_sun);
    }

    /// <summary>Sun, sky and lights follow the game clock: dawn, day, dusk and night.</summary>
    private void UpdateDayNight()
    {
        double h = Game.Time % 86400 / 3600;
        // Sun up from 06:00 to 20:00 (highest at 13:00), below the horizon in between
        float elev = h >= 6 && h < 20 ? Mathf.Sin(Mathf.Pi * (float)(h - 6) / 14) : -Mathf.Sin(Mathf.Pi * (float)((h + 24 - 20) % 24) / 10);
        float day = Mathf.SmoothStep(-0.12f, 0.25f, elev);
        float night = 1 - day;
        float warm = 1 - Mathf.SmoothStep(0.0f, 0.45f, Mathf.Abs(elev)); // dawn/dusk glow

        _sun.RotationDegrees = new Vector3(-Mathf.Max(elev, 0.08f) * 68, (float)(h - 13) * 13 + 35, 0);
        _sun.LightEnergy = 0.05f + 1.05f * day;
        _sun.LightColor = new Color("fff4e0").Lerp(new Color("ff9a52"), warm * day);
        _sun.ShadowEnabled = day > 0.15f;

        var dayHorizon = new Color("d8e4ee").Lerp(new Color("f6b27a"), warm);
        _sky.SkyTopColor = new Color("0b1426").Lerp(new Color("6f9fd6"), day);
        _sky.SkyHorizonColor = new Color("1b2740").Lerp(dayHorizon, day);
        _sky.GroundHorizonColor = _sky.SkyHorizonColor;
        _env.AmbientLightColor = new Color("41557a").Lerp(new Color("b9c6d6"), day);
        _env.AmbientLightEnergy = 0.25f + 0.2f * day;
        _env.FogLightColor = _sky.SkyHorizonColor;
        _props.SetNight(night);
        foreach (var l in _lamps) l.LightEnergy = 2.2f * Mathf.SmoothStep(0.3f, 0.8f, night);
        if (_grid.MaterialOverride is StandardMaterial3D gm) gm.AlbedoColor = new Color(1, 1, 1, 0.05f + 0.15f * day); // subtle at night
    }

    private Vector3 CellCenter(double x, double y) =>
        new((float)((x + 0.5) * Cell - L.Width * Cell / 2), 0, (float)((y + 0.5) * Cell - L.Height * Cell / 2));

    private static void AddBox(Node parent, Vector3 center, Vector3 size, Material mat, bool shadow = true) =>
        Props.Box(parent, center, size, mat, shadow);

    private StandardMaterial3D Asphalt => _props.Textured("asphalt", "565b62", "40444a", 0.35f);
    private StandardMaterial3D Concrete => _props.Textured("concrete", "aca79d", "938e85", 0.25f);
    private StandardMaterial3D Grass => _props.Textured("grass", "7d9a5f", "5f7c45", 0.12f);
    private StandardMaterial3D Pavement => _props.Textured("pavement", "bdb8ae", "a39e94", 0.6f);

    private void BuildGround()
    {
        float w = L.Width * Cell, h = L.Height * Cell;
        AddBox(this, new Vector3(0, -0.06f, 0), new Vector3(700, 0.1f, 700), Grass, shadow: false);
        AddBox(this, new Vector3(0, -0.02f, 0), new Vector3(w, 0.04f, h), Concrete, shadow: false);

        // Street in front of the gate: asphalt, sidewalks, dashed centre line
        float sz = h / 2 + 7.5f;
        AddBox(this, new Vector3(0, -0.03f, sz), new Vector3(w + 220, 0.05f, 10), Asphalt, shadow: false);
        AddBox(this, new Vector3(0, 0.02f, h / 2 + 1.25f), new Vector3(w + 220, 0.16f, 2.5f), Pavement, shadow: false);
        AddBox(this, new Vector3(0, 0.02f, sz + 6.25f), new Vector3(w + 220, 0.16f, 2.5f), Pavement, shadow: false);
        var paint = Mat("f2f0e8", 0.6f);
        for (float x = -w / 2 - 110; x < w / 2 + 110; x += 6)
            AddBox(this, new Vector3(x, 0.0f, sz), new Vector3(3, 0.02f, 0.18f), paint, shadow: false);
        AddBox(this, new Vector3(0, 0.03f, h / 2 + 1.25f), new Vector3(2 * Cell, 0.17f, 2.5f), Asphalt, shadow: false); // gate driveway

        // Fence posts and mesh with a gap for the gate
        var fence = new StandardMaterial3D
        {
            AlbedoColor = new Color("5d6066") { A = 0.55f }, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Metallic = 0.5f, Roughness = 0.5f,
        };
        float gx0 = (L.Width / 2 - 1) * Cell - w / 2, gx1 = (L.Width / 2 + 1) * Cell - w / 2;
        void FenceRun(Vector3 a, Vector3 b)
        {
            var mid = (a + b) / 2;
            float len = a.DistanceTo(b);
            bool alongX = Mathf.Abs(a.X - b.X) > Mathf.Abs(a.Z - b.Z);
            AddBox(this, mid + new Vector3(0, 1, 0), alongX ? new Vector3(len, 2, 0.05f) : new Vector3(0.05f, 2, len), fence, shadow: false);
            int posts = Mathf.Max(1, (int)(len / 3));
            for (int i = 0; i <= posts; i++)
                AddBox(this, a.Lerp(b, (float)i / posts) + new Vector3(0, 1.05f, 0), new Vector3(0.12f, 2.1f, 0.12f), _props.DarkSteel);
        }
        FenceRun(new Vector3(-w / 2, 0, -h / 2), new Vector3(w / 2, 0, -h / 2));
        FenceRun(new Vector3(-w / 2, 0, -h / 2), new Vector3(-w / 2, 0, h / 2));
        FenceRun(new Vector3(w / 2, 0, -h / 2), new Vector3(w / 2, 0, h / 2));
        FenceRun(new Vector3(-w / 2, 0, h / 2), new Vector3(gx0, 0, h / 2));
        FenceRun(new Vector3(gx1, 0, h / 2), new Vector3(w / 2, 0, h / 2));
        // Gate posts and a red/white barrier
        AddBox(this, new Vector3(gx0, 1.4f, h / 2), new Vector3(0.4f, 2.8f, 0.4f), _props.DarkSteel);
        AddBox(this, new Vector3(gx1, 1.4f, h / 2), new Vector3(0.4f, 2.8f, 0.4f), _props.DarkSteel);
        for (int i = 0; i < 5; i++)
            AddBox(this, new Vector3(gx0 + 0.5f + i, 1.1f, h / 2 + 0.3f), new Vector3(1, 0.15f, 0.15f), Mat(i % 2 == 0 ? "c62828" : "f2f2ee", 0.5f));
        foreach (var (gxc, gyc) in L.GateCells())
            AddBox(this, CellCenter(gxc, gyc) + new Vector3(0, 0.03f, 0), new Vector3(Cell, 0.06f, Cell), Asphalt, shadow: false);

        // Trees around the yard and along the street (same layout every time)
        var rng = new Random(Depot.Id * 7919);
        for (int i = 0; i < 46; i++)
        {
            float x, z;
            switch (i % 4)
            {
                case 0: x = (float)rng.NextDouble() * (w + 60) - w / 2 - 30; z = -h / 2 - 6 - (float)rng.NextDouble() * 25; break;
                case 1: x = -w / 2 - 6 - (float)rng.NextDouble() * 25; z = (float)rng.NextDouble() * h - h / 2; break;
                case 2: x = w / 2 + 6 + (float)rng.NextDouble() * 25; z = (float)rng.NextDouble() * h - h / 2; break;
                default: x = (float)rng.NextDouble() * (w + 120) - w / 2 - 60; z = sz + 9 + (float)rng.NextDouble() * 8; break;
            }
            var tree = _props.Tree(rng);
            tree.Position = new Vector3(x, 0, z);
            AddChild(tree);
        }

        // Street lamps along the street and at the yard corners
        void Lamp(Vector3 pos, float yawDeg)
        {
            var lamp = _props.Lamp(out var light);
            lamp.Position = pos;
            lamp.RotationDegrees = new Vector3(0, yawDeg, 0);
            AddChild(lamp);
            _lamps.Add(light);
        }
        for (float x = -w / 2 - 20; x <= w / 2 + 20; x += 24) Lamp(new Vector3(x, 0, h / 2 + 2.2f), -90);
        Lamp(new Vector3(-w / 2 + 1.5f, 0, -h / 2 + 1.5f), -45);
        Lamp(new Vector3(w / 2 - 1.5f, 0, -h / 2 + 1.5f), -135);

        // Grid lines (shown while building)
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int x = 0; x <= L.Width; x++)
        {
            im.SurfaceAddVertex(new Vector3(x * Cell - w / 2, 0.07f, -h / 2));
            im.SurfaceAddVertex(new Vector3(x * Cell - w / 2, 0.07f, h / 2));
        }
        for (int y = 0; y <= L.Height; y++)
        {
            im.SurfaceAddVertex(new Vector3(-w / 2, 0.07f, y * Cell - h / 2));
            im.SurfaceAddVertex(new Vector3(w / 2, 0.07f, y * Cell - h / 2));
        }
        im.SurfaceEnd();
        _grid = new MeshInstance3D
        {
            Mesh = im, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                // lit (not unshaded), so the grid fades with the light at night
                AlbedoColor = new Color(1, 1, 1, 0.22f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 1,
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

    /// <summary>
    /// A driveway cell of a tram or train depot is track: rails run from the cell center to every side where
    /// the next driveway cell, a siding in line or the gate joins (crossings where several meet).
    /// </summary>
    private void RailCell(Node3D node, PlacedModule m, Vector3 ground, Material ballast)
    {
        AddBox(node, ground + new Vector3(0, 0.025f, 0), new Vector3(Cell, 0.05f, Cell), ballast, shadow: false);
        bool Joins(int dx, int dy)
        {
            int x = m.X + dx, y = m.Y + dy;
            if (dy == 1 && y >= L.Height) return true; // gate
            var o = L.ModuleAt(x, y);
            if (o == null) return false;
            if (o.Type == ModuleType.Road) return Run(dx, dy) >= 3; // two driveways side by side stay two tracks
            bool siding = o.Type is ModuleType.TramSpot or ModuleType.MetroSpot or ModuleType.SBahnSpot or ModuleType.TrainSpot;
            return siding && (dy != 0) == (o.H >= o.W); // a siding joins along its length
        }
        bool IsRoad(int x, int y) => L.ModuleAt(x, y)?.Type == ModuleType.Road;
        // driveway cells in a row through this cell along the axis of (dx, dy)
        int Run(int dx, int dy)
        {
            int ax = Math.Abs(dx), ay = Math.Abs(dy), n = 1;
            for (int k = 1; IsRoad(m.X + ax * k, m.Y + ay * k); k++) n++;
            for (int k = 1; IsRoad(m.X - ax * k, m.Y - ay * k); k++) n++;
            return n;
        }
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Where(d => Joins(d.Item1, d.Item2)).ToList();
        if (dirs.Count == 0) dirs.AddRange(new[] { (0, 1), (0, -1) });
        if (dirs.Count == 1) dirs.Add((-dirs[0].Item1, -dirs[0].Item2)); // track end: still a full piece
        var sleeper = Mat("6b5a48");
        float half = Cell / 2;
        foreach (var (dx, dy) in dirs)
        {
            var along = new Vector3(dx, 0, dy);
            var across = new Vector3(dy, 0, dx); // perpendicular (sign irrelevant)
            var mid = ground + along * (half / 2);
            var railSize = dx != 0 ? new Vector3(half + 0.07f, 0.12f, 0.07f) : new Vector3(0.07f, 0.12f, half + 0.07f);
            AddBox(node, mid + across * 0.72f + new Vector3(0, 0.12f, 0), railSize, _props.Steel);
            AddBox(node, mid - across * 0.72f + new Vector3(0, 0.12f, 0), railSize, _props.Steel);
            foreach (float t in new[] { 0.5f, 1.5f })
                AddBox(node, ground + along * t + new Vector3(0, 0.05f, 0),
                    dx != 0 ? new Vector3(0.25f, 0.08f, 2.3f) : new Vector3(2.3f, 0.08f, 0.25f), sleeper, shadow: false);
        }
    }

    private void RebuildModules()
    {
        foreach (var c in _modulesRoot.GetChildren()) c.QueueFree();
        _brushes.Clear();
        _roofs.Clear();
        _buildingLabels.Clear();
        var reach = L.Reachable();
        var warn = Mat("d8a49c");
        foreach (var m in L.Modules)
        {
            var def = DepotLayout.Def(m.Type);
            bool ok = L.IsConnected(m, reach);
            var node = new Node3D();
            _modulesRoot.AddChild(node);
            var (ground, fp) = Footprint(m.X, m.Y, m.W, m.H, 0);
            bool vertical = m.H >= m.W;
            switch (m.Type)
            {
                case ModuleType.Road when Depot.Mode != ModeId.Bus:
                    RailCell(node, m, ground, ok ? Mat("8f877c") : warn);
                    break;
                case ModuleType.Road:
                    AddBox(node, ground + new Vector3(0, 0.03f, 0), new Vector3(Cell, 0.06f, Cell), ok ? Asphalt : warn, shadow: false);
                    break;
                case ModuleType.BusSpot or ModuleType.LongBusSpot or ModuleType.TramSpot or ModuleType.MetroSpot or ModuleType.SBahnSpot or ModuleType.TrainSpot:
                {
                    bool rails = m.Type is ModuleType.TramSpot or ModuleType.MetroSpot or ModuleType.SBahnSpot or ModuleType.TrainSpot;
                    AddBox(node, ground + new Vector3(0, 0.025f, 0), new Vector3(fp.X, 0.05f, fp.Z), !ok ? warn : rails ? Mat("8f877c") : Asphalt, shadow: false); // ballast under tracks
                    var lineSize = vertical ? new Vector3(0.14f, 0.06f, fp.Z - 0.4f) : new Vector3(fp.X - 0.4f, 0.06f, 0.14f);
                    if (rails)
                    {
                        // two rails on sleepers
                        var railOff = vertical ? new Vector3(0.72f, 0.12f, 0) : new Vector3(0, 0.12f, 0.72f);
                        var railSize = lineSize with { Y = 0.12f };
                        AddBox(node, ground + railOff, railSize, _props.Steel);
                        AddBox(node, ground - railOff + new Vector3(0, 0.24f, 0), railSize, _props.Steel);
                        float len = vertical ? fp.Z : fp.X;
                        for (float t = -len / 2 + 0.6f; t < len / 2; t += 1.2f)
                            AddBox(node, ground + (vertical ? new Vector3(0, 0.05f, t) : new Vector3(t, 0.05f, 0)),
                                vertical ? new Vector3(2.3f, 0.08f, 0.25f) : new Vector3(0.25f, 0.08f, 2.3f), Mat("6b5a48"), shadow: false);
                    }
                    else
                    {
                        var white = Mat("f4f4f0", 0.6f);
                        var off = vertical ? new Vector3(fp.X / 2 - 0.12f, 0.05f, 0) : new Vector3(0, 0.05f, fp.Z / 2 - 0.12f);
                        AddBox(node, ground + off, lineSize, white, shadow: false);
                        AddBox(node, ground - off + new Vector3(0, 0.1f, 0), lineSize, white, shadow: false);
                        // yellow marking at the far end
                        var markPos = ground + (vertical ? new Vector3(0, 0.06f, -fp.Z / 2 + 1.5f) : new Vector3(-fp.X / 2 + 1.5f, 0.06f, 0));
                        AddBox(node, markPos, vertical ? new Vector3(1.6f, 0.01f, 0.5f) : new Vector3(0.5f, 0.01f, 1.6f), Mat("e0b21c", 0.6f), shadow: false);
                    }
                    break;
                }
                case ModuleType.Office:
                {
                    var door = L.OfficeDoor(m, reach).Dir;
                    var b = _props.Office(DepotLayout.OfficePlanFor(m, door), (float)def.Height, ok ? "d9cbb0" : "d8a49c", out var upper);
                    b.Position = ground;
                    b.Rotation = new Vector3(0, Mathf.Atan2(door.X, door.Y), 0); // the door faces the driveway
                    node.AddChild(b);
                    _roofs.Add(upper);
                    break;
                }
                case ModuleType.PlanningOffice:
                {
                    var door = L.OfficeDoor(m, reach).Dir;
                    var po = _props.PlanningOffice(DepotLayout.DeskPlanFor(m, door), (float)def.Height, out var upper);
                    po.Position = ground;
                    po.Rotation = new Vector3(0, Mathf.Atan2(door.X, door.Y), 0);
                    node.AddChild(po);
                    _roofs.Add(upper);
                    break;
                }
                case ModuleType.ServiceCenter:
                {
                    var door = L.OfficeDoor(m, reach).Dir;
                    var sc = _props.ServiceCenter(DepotLayout.ServicePlanFor(m, door), (float)def.Height, out var upper);
                    sc.Position = ground;
                    sc.Rotation = new Vector3(0, Mathf.Atan2(door.X, door.Y), 0);
                    node.AddChild(sc);
                    _roofs.Add(upper);
                    break;
                }
                case ModuleType.Workshop or ModuleType.Wash:
                {
                    bool wash = m.Type == ModuleType.Wash;
                    var hall = _props.Hall(new Vector3(fp.X - 0.6f, (float)def.Height, fp.Z - 0.6f), vertical,
                        ok ? (wash ? "3f7fd0" : "c9852e") : "d8a49c", wash, DepotLayout.Lanes(m), out var brushes, out var roof);
                    hall.Position = ground;
                    node.AddChild(hall);
                    _roofs.Add(roof);
                    foreach (var (br, lane) in brushes) _brushes.Add((br, m.X, m.Y, lane));
                    for (int lane = 0; lane < DepotLayout.Lanes(m); lane++)
                    {
                        if (wash)
                        {
                            var (wx, wy, wa) = L.WashPost(m, lane);
                            var desk = _props.WashDesk();
                            // the desk stands in front of the operator
                            desk.Position = new Vector3((float)(wx + Math.Cos(wa) * 0.55), 0, (float)(wy + Math.Sin(wa) * 0.55));
                            desk.Rotation = new Vector3(0, -(float)wa, 0);
                            node.AddChild(desk);
                        }
                        else
                        {
                            var (tx, ty) = L.WorkshopTrolley(m, lane);
                            var trolley = _props.Trolley();
                            trolley.Position = new Vector3((float)tx, 0, (float)ty);
                            trolley.Rotation = new Vector3(0, vertical ? Mathf.Pi / 2 : 0, 0);
                            node.AddChild(trolley);
                        }
                    }
                    break;
                }
                case ModuleType.Charger:
                {
                    var ch = _props.Charger();
                    ch.Position = ground;
                    node.AddChild(ch);
                    if (!ok) AddBox(node, ground + new Vector3(0, 0.02f, 0), new Vector3(Cell, 0.04f, Cell), warn, shadow: false);
                    break;
                }
            }
            if (DepotLayout.IsDrawable(m.Type))
            {
                var label = new Label3D
                {
                    Text = def.Name, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 64, PixelSize = 0.02f,
                    Position = ground + new Vector3(0, (float)def.Height + 2.2f, 0), Modulate = Colors.White, OutlineSize = 12,
                    OutlineModulate = new Color(0, 0, 0, 0.7f), NoDepthTest = true,
                };
                node.AddChild(label);
                _buildingLabels.Add(label);
            }
            if (!ok && m.Type != ModuleType.Road)
            {
                node.AddChild(new Label3D
                {
                    Text = "⚠ kein Anschluss", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 48, PixelSize = 0.02f,
                    Position = ground + new Vector3(0, DepotLayout.IsSpot(m.Type) ? 1.5f : (float)def.Height + 4, 0),
                    Modulate = new Color("ffb4a8"), OutlineSize = 10, OutlineModulate = new Color(0, 0, 0, 0.7f), NoDepthTest = true,
                });
            }
        }
        RebuildVehicles();
    }

    private sealed class VehicleView
    {
        public required Node3D Node;
        public required string Key;
        public required List<Node3D> Wheels;
        public Vector3 LastPos;
    }

    private readonly Dictionary<int, VehicleView> _vehicleNodes = new();

    /// <summary>Kept for callers after layout changes; vehicles follow the simulation every frame.</summary>
    private void RebuildVehicles() => UpdateVehicles();

    /// <summary>One model per vehicle in the yard at its simulated position; wheels turn with the distance driven.</summary>
    private void UpdateVehicles()
    {
        var present = new HashSet<int>();
        foreach (var v in Game.Vehicles.Values)
        {
            if (v.DepotId != Depot.Id || !Game.InYard(v)) continue;
            present.Add(v.Id);
            string color = v.LineId is int lid && Game.Lines.TryGetValue(lid, out var l) ? l.Color : "8d939b";
            string key = $"{color}/{v.ModelId}";
            var pos = new Vector3((float)v.YardX, 0, (float)v.YardY);
            if (!_vehicleNodes.TryGetValue(v.Id, out var view) || view.Key != key)
            {
                view?.Node.QueueFree();
                var node = _props.Vehicle(v.Def, v.Mode, color, out var wheels);
                _vehiclesRoot.AddChild(node);
                view = new VehicleView { Node = node, Key = key, Wheels = wheels, LastPos = pos };
                _vehicleNodes[v.Id] = view;
            }
            float moved = pos.DistanceTo(view.LastPos);
            view.LastPos = pos;
            view.Node.Position = pos;
            view.Node.Rotation = new Vector3(0, -(float)v.YardAng, 0);
            if (moved > 0 && moved < 5)
                foreach (var wheel in view.Wheels) wheel.RotateObjectLocal(Vector3.Back, -moved / 0.5f);
        }
        foreach (int id in _vehicleNodes.Keys.Where(id => !present.Contains(id)).ToList())
        {
            _vehicleNodes[id].Node.QueueFree();
            _vehicleNodes.Remove(id);
        }
    }

    private sealed class PersonView
    {
        public required Props.Figure Fig;
    }

    private readonly Dictionary<int, PersonView> _peopleNodes = new();

    /// <summary>Staff on the grounds: walking, sitting in the break room, working at the lanes and the wash.</summary>
    private void UpdatePeople(double delta)
    {
        var present = new HashSet<int>();
        float t = (float)(Time.GetTicksMsec() / 1000.0);
        foreach (var p in Game.People.Values)
        {
            if (p.DepotId != Depot.Id || p.Hidden) continue;
            present.Add(p.Id);
            if (!_peopleNodes.TryGetValue(p.Id, out var view))
            {
                var fig = _props.Person(p.Role, p.Id);
                _peopleRoot.AddChild(fig.Root);
                fig.Root.Rotation = new Vector3(0, -(float)p.Ang, 0);
                view = new PersonView { Fig = fig };
                _peopleNodes[p.Id] = view;
            }
            var f = view.Fig;
            f.Root.Position = new Vector3((float)p.X, 0, (float)p.Y);
            float yaw = Mathf.LerpAngle(f.Root.Rotation.Y, -(float)p.Ang, Mathf.Min(1, (float)delta * 10));
            f.Root.Rotation = new Vector3(0, yaw, 0);

            bool walking = p.Act == PersonAct.Walking;
            bool seated = (p.Act is PersonAct.Resting or PersonAct.Waiting || (p.Act == PersonAct.Working && p.Role == StaffRole.Researcher)) && p.Seat >= 0 && p.Office != null;
            float swing = walking ? Mathf.Sin((float)(p.Walked * Mathf.Tau / 1.5)) * 0.5f : 0;
            float legs = seated ? 1.45f : swing;
            float arms = p.Act switch
            {
                PersonAct.Walking => -swing * 0.8f,
                PersonAct.Working when p.Role == StaffRole.Mechanic => 1.1f + Mathf.Sin(t * 7 + p.Id) * 0.3f,
                PersonAct.Working => 0.7f + Mathf.Sin(t * 2 + p.Id) * 0.1f,
                PersonAct.Changing => 0.5f + Mathf.Sin(t * 4 + p.Id) * 0.4f,
                _ => seated ? 0.55f : 0.05f,
            };
            f.LegL.Rotation = new Vector3(0, 0, legs);
            f.LegR.Rotation = new Vector3(0, 0, seated ? legs : -legs);
            f.ArmL.Rotation = new Vector3(0, 0, -arms * (seated || p.Act != PersonAct.Walking ? -1 : 1));
            f.ArmR.Rotation = new Vector3(0, 0, arms);
            f.Body.Position = new Vector3(seated ? -0.12f : 0, seated ? -0.42f : 0, 0);
        }
        foreach (int id in _peopleNodes.Keys.Where(id => !present.Contains(id)).ToList())
        {
            _peopleNodes[id].Fig.Root.QueueFree();
            _peopleNodes.Remove(id);
        }
        UpdateHover();
    }

    /// <summary>Name and activity of the person under the mouse.</summary>
    private void UpdateHover()
    {
        Person? best = null;
        float bestD = 26;
        foreach (var (id, view) in _peopleNodes)
        {
            var head = view.Fig.Root.Position + new Vector3(0, 1.2f, 0);
            if (_cam.IsPositionBehind(head) || !Game.People.TryGetValue(id, out var p)) continue;
            float d = _cam.UnprojectPosition(head).DistanceTo(_mouse);
            if (d < bestD)
            {
                bestD = d;
                best = p;
            }
        }
        _hoverLabel.Visible = best != null;
        if (best == null) return;
        _hoverLabel.Position = _peopleNodes[best.Id].Fig.Root.Position + new Vector3(0, 2.15f, 0);
        _hoverLabel.Text = PersonInfo(best);
    }

    private string PersonInfo(Person p)
    {
        if (p.Role == StaffRole.Customer)
        {
            string doing = p.Goal == PersonGoal.Gate ? "geht wieder" : p.Act switch
            {
                PersonAct.Walking => p.Goal == PersonGoal.Counter ? "wird aufgerufen" : "kommt ins Kundencenter",
                PersonAct.Working => "am Schalter: kauft ein Abo",
                PersonAct.Waiting => $"wartet seit {Math.Max(0, (Game.MaxCustomerWait - (p.Until - Game.Time)) / 60):F0} min",
                _ => "",
            };
            return $"Kunde\n{doing}";
        }
        string name = Game.Drivers.TryGetValue(p.Id, out var d) ? d.Name : Game.Workers.TryGetValue(p.Id, out var w) ? w.Name : "?";
        string role = Game.RoleName(p.Role);
        string shift = Game.Workers.TryGetValue(p.Id, out var ww) ? $" · {Game.ShiftName(ww.Shift)}" : "";
        string vehicle = p.VehicleId is int vid && Game.Vehicles.TryGetValue(vid, out var v) ? v.Name : "Fahrzeug";
        bool leaving = p.Goal == PersonGoal.Gate || p.AfterLocker == PersonGoal.Gate;
        string what = p.Act switch
        {
            PersonAct.Walking => p.Goal switch
            {
                PersonGoal.Locker => leaving ? "Feierabend: zur Umkleide" : "Dienstbeginn: zur Umkleide",
                PersonGoal.Seat => "geht in den Pausenraum",
                PersonGoal.Vehicle => $"geht zu {vehicle}",
                PersonGoal.Post => p.Role switch
                {
                    StaffRole.Mechanic => "geht an die Arbeit (Werkstatt)",
                    StaffRole.Service => "geht an den Schalter",
                    StaffRole.Researcher => "geht an den Schreibtisch",
                    _ => "geht zur Waschanlage",
                },
                PersonGoal.Clean => $"geht {vehicle} reinigen",
                PersonGoal.Gate => "geht nach Hause",
                _ => "unterwegs",
            },
            PersonAct.Changing => leaving ? "zieht sich um (Feierabend)" : "zieht sich um",
            PersonAct.Working => p.Role switch
            {
                StaffRole.Mechanic => "repariert",
                StaffRole.Service => "am Schalter",
                StaffRole.Researcher => Game.ActiveResearch is string rid ? $"forscht: {ResearchTree.Get(rid).Name}" : "am Schreibtisch (kein Projekt)",
                _ => p.Post != null ? "bedient die Waschanlage" : $"reinigt {vehicle}",
            },
            PersonAct.Waiting => "wartet auf das nächste Fahrzeug",
            _ => p.Office != null ? "Pause im Sozialgebäude" : "wartet",
        };
        return $"{name}\n{role}{shift}\n{what}";
    }

    /// <summary>Dev/screenshot helper: interior view, camera distance and focus on the first module of a type.</summary>
    public void SetView(bool interior, float? dist, ModuleType? focus, float? yaw = null, float? pitch = null)
    {
        _interior = interior;
        if (dist is float d) _dist = d;
        if (yaw is float y) _yaw = y;
        if (pitch is float p) _pitch = p;
        if (focus is ModuleType t && L.Modules.FirstOrDefault(m => m.Type == t) is { } mod)
            _focus = CellCenter(mod.X + (mod.W - 1) / 2.0, mod.Y + (mod.H - 1) / 2.0);
        UpdateCamera();
        RebuildPalette();
    }

    /// <summary>Hide ceilings and roofs in the interior view or when the camera is close.</summary>
    private void UpdateRoofs()
    {
        bool open = _interior || _dist < 40;
        foreach (var r in _roofs)
            if (IsInstanceValid(r)) r.Visible = !open;
        foreach (var l in _buildingLabels)
            if (IsInstanceValid(l)) l.Visible = !open; // names would float in the air above the open rooms
    }

    /// <summary>Wash brushes spin while a vehicle stands in that wash bay.</summary>
    private void SpinBrushes(double delta)
    {
        foreach (var (pivot, mx, my, lane) in _brushes)
        {
            bool busy = Game.Vehicles.Values.Any(v => v.DepotId == Depot.Id && v.State == VehicleState.Depot &&
                v.Bay == YardBay.Wash && v.BayX == mx && v.BayY == my && v.BayIndex == lane);
            if (busy) pivot.RotateY((float)delta * 6);
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
                if (!_bulldoze && _tool is ModuleType dt && DepotLayout.IsDrawable(dt))
                {
                    // Buildings: press, drag a rectangle, release (a plain click builds the standard size)
                    if (b.Pressed) _dragStart = CellAt(b.Position);
                    else if (_dragStart is { } start)
                    {
                        _dragStart = null;
                        var end = CellAt(b.Position) ?? _hoverCell ?? start;
                        if (end == start) Apply(start);
                        else ApplyRect(dt, start, end);
                    }
                    UpdateGhost();
                    break;
                }
                if (b.Pressed && CellAt(b.Position) is { } cell) Apply(cell);
                break;
            case InputEventMouseMotion m:
                _mouse = m.Position;
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
                    case Key.I: _interior = !_interior; RebuildPalette(); break;
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

    private static ((int X, int Y) Pos, int W, int H) Rect((int X, int Y) a, (int X, int Y) b) =>
        ((Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);

    private void ApplyRect(ModuleType t, (int X, int Y) a, (int X, int Y) b)
    {
        var (pos, w, h) = Rect(a, b);
        string? err = Game.PlaceModule(Depot, t, pos.X, pos.Y, 0, w, h);
        SetStatus(err ?? $"{DepotLayout.Def(t).Name} gebaut – {DepotLayout.SizeInfo(t, w, h)}", err != null);
    }

    private void UpdateGhost()
    {
        if (_dragStart is { } ds && _hoverCell is { } hc0 && _tool is ModuleType rt && !_bulldoze)
        {
            // Drawing a building: show the rectangle with size, places and price
            var (rp, rw, rh) = Rect(ds, hc0);
            var rdef = DepotLayout.Def(rt);
            var (rc, rs) = Footprint(rp.X, rp.Y, rw, rh, (float)rdef.Height);
            ((BoxMesh)_ghost.Mesh).Size = rs;
            _ghost.Position = rc;
            _ghost.Visible = true;
            double cost = DepotLayout.Cost(rt, rw, rh);
            string? err = L.CanPlace(rt, rp.X, rp.Y, 0, rw, rh) ?? (Game.CanAfford(cost) ? null : "Nicht genug Geld");
            _ghostMat.AlbedoColor = err == null ? new Color(0.2f, 0.8f, 0.35f, 0.45f) : new Color(0.9f, 0.2f, 0.15f, 0.45f);
            SetStatus($"{DepotLayout.SizeInfo(rt, rw, rh)} · {Util.Money(cost)}" + (err != null ? $" – {err}" : ""), err != null);
            return;
        }
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
        UpdateVehicles();
        UpdatePeople(delta);
        UpdateRoofs();
        SpinBrushes(delta);
        UpdateDayNight();
        _vehicleTimer += delta;
        if (_vehicleTimer > 0.5)
        {
            _vehicleTimer = 0;
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
        var types = Depot.Mode != ModeId.Bus
            ? new List<ModuleType> { ModuleType.Road, DepotLayout.SpotType(Depot.Mode), ModuleType.Office, ModuleType.Workshop, ModuleType.Wash, ModuleType.ServiceCenter, ModuleType.PlanningOffice }
            : new List<ModuleType> { ModuleType.Road, ModuleType.BusSpot, ModuleType.LongBusSpot, ModuleType.Charger,
                ModuleType.Office, ModuleType.Workshop, ModuleType.Wash, ModuleType.ServiceCenter, ModuleType.PlanningOffice };
        foreach (var t in types)
        {
            var def = DepotLayout.Def(t);
            string price = DepotLayout.IsDrawable(t) ? $"{Util.Money(def.Cost / (def.W * def.H))}/Feld" : Util.Money(def.Cost);
            if (ResearchTree.ForModule(t) is string need && !Game.Has(need))
            {
                var lockedBtn = PaletteButton($"🔒 {def.Name}  ·  Forschung", false, () => SetStatus(Game.NeedsResearch(need), true));
                lockedBtn.TooltipText = $"{def.Effect}\n{Game.NeedsResearch(need)}";
                lockedBtn.Modulate = new Color(1, 1, 1, 0.6f);
                _palette.AddChild(lockedBtn);
                continue;
            }
            var b = PaletteButton($"{def.Name}  ·  {price}", !_bulldoze && _tool == t, () => SetTool(t, false));
            b.TooltipText = def.Effect;
            _palette.AddChild(b);
        }
        _palette.AddChild(PaletteButton("✕  Abreißen (X)", _bulldoze, () => SetTool(null, true)));
        _toolInfo = Ui.Label(_bulldoze ? "Klick (oder ziehen) entfernt Bauteile." : _tool is ModuleType tt ? DepotLayout.Def(tt).Effect : "", 12, Ui.Muted);
        _toolInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toolInfo.CustomMinimumSize = new Vector2(226, 0);
        _palette.AddChild(_toolInfo);
        _palette.AddChild(Ui.Section("Ansicht"));
        _palette.AddChild(PaletteButton("◫  Innenansicht (I)", _interior, () => { _interior = !_interior; RebuildPalette(); }));
        _palette.AddChild(Ui.Section("Steuerung"));
        var help = Ui.Label("Linksklick: bauen (Fahrweg: ziehen)\nGebäude: Rechteck aufziehen (Klick = Standardgröße)\nR: drehen\nRechte Maustaste ziehen: Kamera drehen\nMausrad: zoomen · Q/E: schwenken\nNah heranzoomen oder I: Dächer aus\nMaus über Person: wer und was\nEsc: zurück zur Karte", 12, Ui.Muted);
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
        int home = Game.Vehicles.Values.Count(v => v.DepotId == Depot.Id && Game.InYard(v));
        int drivers = Game.DriversOf(Depot.Id).Count;
        int staff = Game.StaffCount(Depot.Id);
        int mech = Game.WorkersOf(Depot.Id, StaffRole.Mechanic).Count, clean = Game.WorkersOf(Depot.Id, StaffRole.Cleaner).Count;
        int svc = Game.WorkersOf(Depot.Id, StaffRole.Service).Count, res = Game.WorkersOf(Depot.Id, StaffRole.Researcher).Count;
        string spot = Depot.Mode != ModeId.Bus ? "Abstellgleise" : "Stellplätze";
        string service = $"Werkstatt: {Depot.Repairing}   ·   Wäsche: {Depot.Washing}" + (Depot.Chargers > 0 ? $"   ·   Laden: {Depot.Charging}" : "");
        _stats.Text = $"{spot}: {vehicles} / {L.VehicleCapacity(Depot.Mode)} belegt ({home} im Hof)   ·   " +
                      $"Personal: {staff} / {L.DriverCapacity()} ({drivers} Fahrer, {mech} Mechaniker, {clean} Reinigung, {svc} Service, {res} Forschung; {Depot.OnSite} vor Ort)   ·   {service}" +
                      (Depot.Counters > 0 ? $"   ·   Kunden: {Depot.Queue} warten, {Depot.ServedToday} bedient" : "") + $"   ·   {Util.Money(Game.Money)}";
    }
}
