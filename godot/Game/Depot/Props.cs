using Godot;
using Stations.Core;

namespace Stations.DepotView;

/// <summary>
/// Procedural 3D props for the depot yard: vehicles, buildings, chargers, trees and lamps.
/// Built from simple meshes; materials are shared and the "lit" ones (windows, lamps, displays)
/// are dimmed or brightened by <see cref="SetNight"/>.
/// </summary>
public sealed class Props
{
    private readonly Dictionary<string, StandardMaterial3D> _mats = new();
    private readonly List<(StandardMaterial3D Mat, float Day, float Night)> _lit = new();

    // Shared materials
    public StandardMaterial3D Glass => Mat("glass", () => new StandardMaterial3D
    {
        AlbedoColor = new Color("1d2733"), Metallic = 0.2f, Roughness = 0.08f, MetallicSpecular = 0.8f,
    });
    public StandardMaterial3D Rubber => Mat("rubber", () => new StandardMaterial3D { AlbedoColor = new Color("1b1b1d"), Roughness = 0.9f });
    public StandardMaterial3D Hub => Mat("hub", () => new StandardMaterial3D { AlbedoColor = new Color("a7abb0"), Metallic = 0.7f, Roughness = 0.35f });
    public StandardMaterial3D Steel => Mat("steel", () => new StandardMaterial3D { AlbedoColor = new Color("6f7b88"), Metallic = 0.6f, Roughness = 0.45f });
    public StandardMaterial3D DarkSteel => Mat("darksteel", () => new StandardMaterial3D { AlbedoColor = new Color("3a3f46"), Metallic = 0.5f, Roughness = 0.5f });
    public StandardMaterial3D Roof => Mat("roof", () => new StandardMaterial3D { AlbedoColor = new Color("d9dadc"), Roughness = 0.6f });

    public StandardMaterial3D Headlight => Lit("headlight", new Color("fff6dc"), 0.6f, 5f);
    public StandardMaterial3D Taillight => Lit("taillight", new Color("ff2a1a"), 0.5f, 3f);
    public StandardMaterial3D Display => Lit("display", new Color("ffae1a"), 1.2f, 3.5f);
    public StandardMaterial3D Window => Lit("window", new Color("ffe2a8"), 0.0f, 1.4f, new Color("4c5f73"));
    public StandardMaterial3D HallLight => Lit("halllight", new Color("eaf4ff"), 0.3f, 3f);
    public StandardMaterial3D LampHead => Lit("lamphead", new Color("ffd99a"), 0.0f, 6f);
    public StandardMaterial3D ChargerScreen => Lit("chargerscreen", new Color("39e07a"), 1.0f, 2.5f);

    public StandardMaterial3D Solid(string hex, float roughness = 0.75f, float metallic = 0) =>
        Mat($"c{hex}{roughness}{metallic}", () => new StandardMaterial3D
        {
            AlbedoColor = new Color(hex), Roughness = roughness, Metallic = metallic,
        });

    /// <summary>Paint for vehicle bodies: a bit glossy.</summary>
    public StandardMaterial3D Paint(string hex) => Solid(hex, 0.35f, 0.15f);

    /// <summary>Surface with a tiling noise texture in world space (asphalt, concrete, grass).</summary>
    public StandardMaterial3D Textured(string key, string baseHex, string darkHex, float scale, float roughness = 0.95f) =>
        Mat("tex" + key, () =>
        {
            var noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular, Frequency = 0.08f, Seed = key.GetHashCode() };
            var ramp = new Gradient();
            ramp.SetColor(0, new Color(darkHex));
            ramp.SetColor(1, new Color(baseHex));
            var tex = new NoiseTexture2D { Noise = noise, Width = 256, Height = 256, Seamless = true, ColorRamp = ramp };
            return new StandardMaterial3D
            {
                AlbedoTexture = tex, Roughness = roughness, Uv1WorldTriplanar = true, Uv1Triplanar = true,
                Uv1Scale = new Vector3(scale, scale, scale),
            };
        });

    private StandardMaterial3D Mat(string key, Func<StandardMaterial3D> make)
    {
        if (!_mats.TryGetValue(key, out var m)) _mats[key] = m = make();
        return m;
    }

    /// <summary>Emissive material whose glow follows day/night.</summary>
    private StandardMaterial3D Lit(string key, Color glow, float day, float night, Color? albedo = null) =>
        Mat(key, () =>
        {
            var m = new StandardMaterial3D
            {
                AlbedoColor = albedo ?? glow, EmissionEnabled = true, Emission = glow, EmissionEnergyMultiplier = day, Roughness = 0.3f,
            };
            _lit.Add((m, day, night));
            return m;
        });

    /// <summary>0 = day, 1 = night: windows, lamps and lights glow accordingly.</summary>
    public void SetNight(float night)
    {
        foreach (var (m, day, n) in _lit) m.EmissionEnergyMultiplier = Mathf.Lerp(day, n, night);
    }

    // ---------------------------------------------------------------- building blocks

    public static MeshInstance3D Box(Node parent, Vector3 center, Vector3 size, Material mat, bool shadow = true)
    {
        var m = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size }, Position = center, MaterialOverride = mat,
            CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(m);
        return m;
    }

    public static MeshInstance3D Cylinder(Node parent, Vector3 center, float radius, float height, Material mat, Vector3? rotation = null, int segments = 16)
    {
        var m = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height, RadialSegments = segments, Rings = 1 },
            Position = center, MaterialOverride = mat, Rotation = rotation ?? Vector3.Zero,
        };
        parent.AddChild(m);
        return m;
    }

    // ---------------------------------------------------------------- vehicles

    /// <summary>
    /// Vehicle model, length along local +X (front), width along Z, standing on y = 0.
    /// Wheels are returned so the caller can spin them while driving.
    /// </summary>
    public Node3D Vehicle(VehicleDef def, ModeId mode, string color, out List<Node3D> wheels)
    {
        wheels = new List<Node3D>();
        return mode == ModeId.Tram ? Tram(def, color, wheels) : Bus(def, color, wheels);
    }

    private Node3D Bus(VehicleDef def, string color, List<Node3D> wheels)
    {
        float L = (float)def.Length, W = (float)def.Width, H = 3.15f, floor = 0.32f;
        var n = new Node3D();
        var paint = Paint(color);
        var white = Paint("f2f2ee");

        // Body: coloured skirt, white upper part, dark glass band with pillars
        Box(n, new Vector3(0, floor + 0.55f, 0), new Vector3(L, 1.1f, W), paint);
        Box(n, new Vector3(0, floor + 1.1f + 0.15f, 0), new Vector3(L, 0.3f, W), white);
        Box(n, new Vector3(0, floor + 1.95f, 0), new Vector3(L - 0.1f, 1.1f, W - 0.04f), Glass);
        for (float x = -L / 2 + 1.4f; x < L / 2 - 1.2f; x += 1.75f)
            Box(n, new Vector3(x, floor + 1.95f, 0), new Vector3(0.16f, 1.1f, W + 0.02f), white);
        Box(n, new Vector3(0, floor + 2.65f, 0), new Vector3(L, 0.35f, W), paint);
        Box(n, new Vector3(0, H - 0.04f, 0), new Vector3(L - 0.3f, 0.1f, W - 0.15f), Roof);

        // Front: windscreen, destination display, lights, bumper
        Box(n, new Vector3(L / 2 + 0.02f, floor + 1.75f, 0), new Vector3(0.06f, 1.5f, W - 0.25f), Glass);
        Box(n, new Vector3(L / 2 + 0.03f, floor + 2.65f, 0), new Vector3(0.05f, 0.28f, W * 0.72f), Display);
        foreach (float z in new[] { -W / 2 + 0.35f, W / 2 - 0.35f })
        {
            Box(n, new Vector3(L / 2 + 0.03f, floor + 0.55f, z), new Vector3(0.05f, 0.2f, 0.4f), Headlight, shadow: false);
            Box(n, new Vector3(-L / 2 - 0.03f, floor + 0.7f, z), new Vector3(0.05f, 0.35f, 0.25f), Taillight, shadow: false);
        }
        Box(n, new Vector3(L / 2 + 0.05f, floor + 0.15f, 0), new Vector3(0.12f, 0.3f, W), DarkSteel);
        Box(n, new Vector3(-L / 2 - 0.05f, floor + 0.15f, 0), new Vector3(0.12f, 0.3f, W), DarkSteel);
        Box(n, new Vector3(-L / 2 - 0.02f, floor + 1.95f, 0), new Vector3(0.05f, 0.9f, W - 0.5f), Glass);

        // Doors on the right side (-Z when facing +X)
        int doors = L > 15 ? 4 : 3;
        for (int i = 0; i < doors; i++)
        {
            float x = L / 2 - 1.6f - i * (L - 3.6f) / Math.Max(doors - 1, 1);
            Box(n, new Vector3(x, floor + 1.25f, -W / 2 - 0.015f), new Vector3(1.15f, 2.3f, 0.04f), Glass);
            Box(n, new Vector3(x, floor + 1.25f, -W / 2 - 0.03f), new Vector3(0.05f, 2.3f, 0.02f), DarkSteel);
        }

        // Roof equipment
        Box(n, new Vector3(L * 0.18f, H + 0.15f, 0), new Vector3(2.4f, 0.3f, W * 0.62f), Roof);
        if (def.Electric)
        {
            Box(n, new Vector3(-L * 0.18f, H + 0.18f, 0), new Vector3(3.2f, 0.35f, W * 0.7f), Solid("5f9bd1", 0.4f));
            Box(n, new Vector3(L * 0.38f, H + 0.25f, 0), new Vector3(0.6f, 0.4f, 0.5f), DarkSteel); // charging rail
        }

        // Articulated bus: bellows in the middle
        if (L > 15)
        {
            float jx = L * 0.08f;
            for (int k = -2; k <= 2; k++)
                Box(n, new Vector3(jx + k * 0.18f, floor + 1.45f, 0), new Vector3(0.12f, 2.7f, W + (k % 2 == 0 ? 0.06f : 0)), Solid("202226", 0.9f));
        }

        // Wheels
        var axles = L > 15 ? new[] { L / 2 - 2.6f, 0.5f, -L / 2 + 2.9f } : new[] { L / 2 - 2.6f, -L / 2 + 3.0f };
        foreach (float x in axles)
            foreach (float z in new[] { -W / 2 + 0.22f, W / 2 - 0.22f })
            {
                var wheel = new Node3D { Position = new Vector3(x, 0.5f, z) };
                n.AddChild(wheel);
                Cylinder(wheel, Vector3.Zero, 0.5f, 0.32f, Rubber, new Vector3(Mathf.Pi / 2, 0, 0), 18);
                Cylinder(wheel, new Vector3(0, 0, z > 0 ? 0.17f : -0.17f), 0.28f, 0.04f, Hub, new Vector3(Mathf.Pi / 2, 0, 0), 12);
                Box(wheel, new Vector3(0, 0, z > 0 ? 0.19f : -0.19f), new Vector3(0.5f, 0.06f, 0.02f), DarkSteel, shadow: false); // spoke to show rotation
                wheels.Add(wheel);
            }
        return n;
    }

    private Node3D Tram(VehicleDef def, string color, List<Node3D> wheels)
    {
        float L = (float)def.Length, W = (float)def.Width, floor = 0.35f;
        var n = new Node3D();
        var paint = Paint(color);
        var white = Paint("f2f2ee");
        int sections = 3;
        float gap = 0.5f, seg = (L - gap * (sections - 1)) / sections;
        for (int s = 0; s < sections; s++)
        {
            float cx = -L / 2 + seg / 2 + s * (seg + gap);
            Box(n, new Vector3(cx, floor + 0.55f, 0), new Vector3(seg, 1.1f, W), paint);
            Box(n, new Vector3(cx, floor + 1.9f, 0), new Vector3(seg - 0.1f, 1.6f, W - 0.04f), Glass);
            for (float x = cx - seg / 2 + 1.2f; x < cx + seg / 2 - 0.8f; x += 1.6f)
                Box(n, new Vector3(x, floor + 1.9f, 0), new Vector3(0.14f, 1.6f, W + 0.02f), white);
            Box(n, new Vector3(cx, floor + 2.95f, 0), new Vector3(seg, 0.5f, W), white);
            if (s < sections - 1)
                Box(n, new Vector3(cx + seg / 2 + gap / 2, floor + 1.6f, 0), new Vector3(gap, 2.8f, W - 0.2f), Solid("202226", 0.9f));
            // bogie under each section
            var bogie = new Node3D { Position = new Vector3(cx, 0, 0) };
            n.AddChild(bogie);
            Box(bogie, new Vector3(0, 0.3f, 0), new Vector3(2.2f, 0.3f, W - 0.3f), DarkSteel);
            foreach (float bx in new[] { -0.75f, 0.75f })
                foreach (float z in new[] { -W / 2 + 0.3f, W / 2 - 0.3f })
                {
                    var wheel = new Node3D { Position = new Vector3(bx, 0.35f, z) };
                    bogie.AddChild(wheel);
                    Cylinder(wheel, Vector3.Zero, 0.33f, 0.14f, Hub, new Vector3(Mathf.Pi / 2, 0, 0), 14);
                    Box(wheel, new Vector3(0, 0, z > 0 ? 0.08f : -0.08f), new Vector3(0.36f, 0.05f, 0.02f), DarkSteel, shadow: false);
                    wheels.Add(wheel);
                }
        }
        // Front and rear: windscreen, display, lights
        foreach (int dir in new[] { 1, -1 })
        {
            Box(n, new Vector3(dir * (L / 2 + 0.03f), floor + 1.9f, 0), new Vector3(0.06f, 1.6f, W - 0.3f), Glass);
            Box(n, new Vector3(dir * (L / 2 + 0.04f), floor + 2.95f, 0), new Vector3(0.05f, 0.28f, W * 0.65f), Display);
            foreach (float z in new[] { -W / 2 + 0.35f, W / 2 - 0.35f })
                Box(n, new Vector3(dir * (L / 2 + 0.04f), floor + 0.6f, z), new Vector3(0.05f, 0.22f, 0.35f), dir > 0 ? Headlight : Taillight, shadow: false);
        }
        // Pantograph on the middle section
        float top = floor + 3.2f;
        Box(n, new Vector3(0, top + 0.1f, 0), new Vector3(1.6f, 0.15f, 1.2f), DarkSteel);
        var arm = Box(n, new Vector3(-0.3f, top + 0.75f, 0), new Vector3(0.08f, 1.4f, 0.08f), Steel);
        arm.RotationDegrees = new Vector3(0, 0, 35);
        Box(n, new Vector3(0.05f, top + 1.35f, 0), new Vector3(0.12f, 0.06f, 1.6f), Steel);
        return n;
    }

    // ---------------------------------------------------------------- buildings

    /// <summary>Office: storeys with ribbon windows (glowing at night), entrance, parapet.</summary>
    public Node3D Office(Vector3 size, string color)
    {
        var n = new Node3D();
        var wall = Solid(color, 0.85f);
        Box(n, new Vector3(0, size.Y / 2, 0), size, wall);
        int storeys = Math.Max(1, (int)(size.Y / 3.2f));
        for (int s = 0; s < storeys; s++)
        {
            float y = 1.6f + s * 3.2f;
            Box(n, new Vector3(0, y, 0), new Vector3(size.X + 0.04f, 1.25f, size.Z * 0.82f), Window, shadow: false);
            Box(n, new Vector3(0, y, 0), new Vector3(size.X * 0.82f, 1.25f, size.Z + 0.04f), Window, shadow: false);
        }
        Box(n, new Vector3(0, 1.15f, size.Z / 2 + 0.03f), new Vector3(1.6f, 2.3f, 0.06f), Glass); // entrance
        Box(n, new Vector3(0, 2.45f, size.Z / 2 + 0.4f), new Vector3(2.4f, 0.12f, 0.9f), DarkSteel); // canopy
        Box(n, new Vector3(0, size.Y + 0.25f, 0), new Vector3(size.X + 0.2f, 0.5f, size.Z + 0.2f), Solid("7d766c"));
        Box(n, new Vector3(size.X * 0.2f, size.Y + 0.7f, -size.Z * 0.15f), new Vector3(1.4f, 0.8f, 1.0f), Steel); // roof unit
        return n;
    }

    /// <summary>Open steel hall (workshop or wash bay) so vehicles inside stay visible.</summary>
    public Node3D Hall(Vector3 size, bool vertical, string accent, bool wash, out List<Node3D> brushes)
    {
        brushes = new List<Node3D>();
        var n = new Node3D();
        float px = size.X / 2 - 0.25f, pz = size.Z / 2 - 0.25f, h = size.Y;
        // Steel frame: pillars along the long sides
        int bays = Math.Max(2, (int)((vertical ? size.Z : size.X) / 4.5f));
        for (int i = 0; i <= bays; i++)
        {
            float t = -0.5f + (float)i / bays;
            var a = vertical ? new Vector3(-px, h / 2, t * 2 * pz) : new Vector3(t * 2 * px, h / 2, -pz);
            var b = vertical ? new Vector3(px, h / 2, t * 2 * pz) : new Vector3(t * 2 * px, h / 2, pz);
            Box(n, a, new Vector3(0.35f, h, 0.35f), Steel);
            Box(n, b, new Vector3(0.35f, h, 0.35f), Steel);
            // roof beam across
            Box(n, (a + b) / 2 + new Vector3(0, h / 2 - 0.2f, 0), vertical ? new Vector3(size.X, 0.35f, 0.25f) : new Vector3(0.25f, 0.35f, size.Z), Steel);
        }
        // Low wall on one long side, colored panel band on top of it
        var side = vertical ? new Vector3(-px, 1.1f, 0) : new Vector3(0, 1.1f, -pz);
        Box(n, side, vertical ? new Vector3(0.25f, 2.2f, size.Z) : new Vector3(size.X, 2.2f, 0.25f), Solid("b6b1a8", 0.9f));
        Box(n, side + new Vector3(0, h - 2.0f, 0), vertical ? new Vector3(0.3f, 1.2f, size.Z) : new Vector3(size.X, 1.2f, 0.3f), Solid(accent, 0.6f));
        // Translucent roof and lights underneath
        var roof = new StandardMaterial3D
        {
            AlbedoColor = new Color("c9d6e2") { A = 0.38f }, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.2f,
        };
        Box(n, new Vector3(0, h + 0.05f, 0), new Vector3(size.X + 0.4f, 0.12f, size.Z + 0.4f), roof, shadow: false);
        for (int i = 0; i < bays; i++)
        {
            float t = -0.5f + (i + 0.5f) / bays;
            Box(n, (vertical ? new Vector3(0, h - 0.5f, t * 2 * pz) : new Vector3(t * 2 * px, h - 0.5f, 0)),
                new Vector3(1.4f, 0.08f, 0.35f), HallLight, shadow: false);
        }
        // Gate frames at both short ends
        foreach (int s in new[] { -1, 1 })
        {
            var end = vertical ? new Vector3(0, h - 0.6f, s * pz) : new Vector3(s * px, h - 0.6f, 0);
            Box(n, end, vertical ? new Vector3(size.X - 0.5f, 1.2f, 0.25f) : new Vector3(0.25f, 1.2f, size.Z - 0.5f), Solid("d9a21b", 0.5f, 0.3f));
        }
        if (wash)
        {
            // Rotating brushes either side of the lane
            foreach (int s in new[] { -1, 1 })
            {
                var pivot = new Node3D { Position = vertical ? new Vector3(s * 1.75f, h * 0.38f, 0) : new Vector3(0, h * 0.38f, s * 1.75f) };
                n.AddChild(pivot);
                Cylinder(pivot, Vector3.Zero, 0.45f, h * 0.7f, Solid("3f7fd0", 0.95f), segments: 10);
                Box(pivot, new Vector3(0.48f, 0, 0), new Vector3(0.1f, h * 0.68f, 0.12f), Solid("2a5fa8", 0.95f));
                brushes.Add(pivot);
            }
        }
        else
        {
            // Workbenches and tool cabinets along the wall, a lane marking on the floor
            for (int i = 0; i < 3; i++)
            {
                float t = -0.3f + i * 0.3f;
                var pos = vertical ? new Vector3(-px + 0.7f, 0.5f, t * 2 * pz) : new Vector3(t * 2 * px, 0.5f, -pz + 0.7f);
                Box(n, pos, vertical ? new Vector3(0.8f, 1.0f, 1.8f) : new Vector3(1.8f, 1.0f, 0.8f), Solid(i == 1 ? "c62828" : "4b5563", 0.6f));
            }
            Box(n, new Vector3(0, 0.03f, 0), vertical ? new Vector3(0.15f, 0.02f, size.Z - 1) : new Vector3(size.X - 1, 0.02f, 0.15f), Solid("e0b21c", 0.6f), shadow: false);
        }
        return n;
    }

    public Node3D Charger()
    {
        var n = new Node3D();
        Box(n, new Vector3(0, 0.1f, 0), new Vector3(1.1f, 0.2f, 0.8f), Solid("9a958c"));
        Box(n, new Vector3(0, 1.05f, 0), new Vector3(0.55f, 1.7f, 0.4f), Solid("e8e8e4", 0.5f));
        Box(n, new Vector3(0, 1.45f, 0.21f), new Vector3(0.38f, 0.3f, 0.02f), ChargerScreen, shadow: false);
        Box(n, new Vector3(0, 1.95f, 0), new Vector3(0.6f, 0.12f, 0.45f), Solid("3fae6a", 0.5f));
        Box(n, new Vector3(0.32f, 0.9f, 0), new Vector3(0.08f, 0.5f, 0.08f), Rubber); // cable
        return n;
    }

    // ---------------------------------------------------------------- surroundings

    public Node3D Tree(Random rng)
    {
        var n = new Node3D();
        float h = 5 + (float)rng.NextDouble() * 4;
        Cylinder(n, new Vector3(0, h * 0.3f, 0), 0.22f, h * 0.6f, Solid("6b4a32", 0.95f), segments: 8);
        var leaf = Solid(rng.Next(3) switch { 0 => "4f7d3a", 1 => "5d8c43", _ => "456f33" }, 0.95f);
        var crown = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = h * 0.32f, Height = h * 0.55f, RadialSegments = 10, Rings = 6 },
            Position = new Vector3(0, h * 0.72f, 0), MaterialOverride = leaf,
        };
        n.AddChild(crown);
        return n;
    }

    /// <summary>Street lamp; returns the light so day/night can switch it.</summary>
    public Node3D Lamp(out OmniLight3D light)
    {
        var n = new Node3D();
        Cylinder(n, new Vector3(0, 3.5f, 0), 0.09f, 7, DarkSteel, segments: 8);
        Box(n, new Vector3(0.6f, 7.0f, 0), new Vector3(1.3f, 0.12f, 0.12f), DarkSteel);
        Box(n, new Vector3(1.15f, 6.9f, 0), new Vector3(0.6f, 0.15f, 0.3f), LampHead, shadow: false);
        light = new OmniLight3D
        {
            Position = new Vector3(1.15f, 6.5f, 0), LightColor = new Color("ffd59a"), OmniRange = 18, LightEnergy = 0, ShadowEnabled = false,
        };
        n.AddChild(light);
        return n;
    }
}
