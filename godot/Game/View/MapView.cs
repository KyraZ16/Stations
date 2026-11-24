using Godot;
using Stations.Core.Sim;

namespace Stations.View;

/// <summary>
/// The 2D world: static map, roads, network and a camera with mouse-wheel zoom
/// (towards the cursor) and drag-to-pan. World units are meters.
/// </summary>
public partial class MapView : Node2D
{
    public Game Game = null!;

    private Camera2D _cam = null!;
    private RoadLayer _roads = null!;
    private NetworkLayer _network = null!;
    private float _minZoom = 0.05f;
    private const float MaxZoom = 8f;
    private bool _dragging;
    private Vector2 _dragStart;
    private bool _dragMoved;

    /// <summary>A click (press and release without dragging) at a world position.</summary>
    public event Action<Vector2, MouseButton>? Clicked;
    /// <summary>The mouse moved over the map (world position).</summary>
    public event Action<Vector2>? Hovered;

    public float Zoom => _cam.Zoom.X;
    public NetworkLayer Network => _network;

    public override void _Ready()
    {
        AddChild(new StaticMapLayer { Map = Game.Map });
        _roads = new RoadLayer { Map = Game.Map };
        AddChild(_roads);
        AddChild(new OutsideMask { Half = (float)Game.Map.Half });
        _network = new NetworkLayer { Game = Game };
        AddChild(_network);
        _cam = new Camera2D { PositionSmoothingEnabled = false };
        AddChild(_cam);
        _cam.MakeCurrent();
        FitToMap();
    }

    private void FitToMap()
    {
        var size = GetViewportRect().Size;
        float fit = Mathf.Min(size.X, size.Y) / (float)(Game.Map.Half * 2.1);
        _minZoom = fit * 0.6f;
        SetZoom(fit);
        _cam.Position = Vector2.Zero;
    }

    private void SetZoom(float z)
    {
        z = Mathf.Clamp(z, _minZoom, MaxZoom);
        _cam.Zoom = new Vector2(z, z);
        _roads.Zoom = z;
        _network.Zoom = z;
    }

    public void CenterOn(Vector2 world) => _cam.Position = ClampToMap(world);

    public void ZoomBy(float factor) => SetZoom(_cam.Zoom.X * factor);

    private Vector2 ClampToMap(Vector2 p)
    {
        float h = (float)Game.Map.Half;
        return new Vector2(Mathf.Clamp(p.X, -h, h), Mathf.Clamp(p.Y, -h, h));
    }

    /// <summary>Screen position (viewport pixels) to world meters.</summary>
    public Vector2 ScreenToWorld(Vector2 screen) =>
        _cam.Position + (screen - GetViewportRect().Size / 2) / _cam.Zoom.X;

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
            {
                var before = ScreenToWorld(wheel.Position);
                float factor = wheel.ButtonIndex == MouseButton.WheelUp ? 1.2f : 1 / 1.2f;
                SetZoom(_cam.Zoom.X * factor);
                // keep the point under the cursor fixed
                _cam.Position = ClampToMap(_cam.Position + before - ScreenToWorld(wheel.Position));
                GetViewport().SetInputAsHandled();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } btn:
                if (btn.Pressed)
                {
                    _dragging = true;
                    _dragMoved = false;
                    _dragStart = btn.Position;
                }
                else if (_dragging)
                {
                    _dragging = false;
                    if (!_dragMoved) Clicked?.Invoke(ScreenToWorld(btn.Position), btn.ButtonIndex);
                }
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion motion:
                if (_dragging && (_dragMoved || motion.Position.DistanceTo(_dragStart) > 4))
                {
                    _dragMoved = true;
                    _cam.Position = ClampToMap(_cam.Position - motion.Relative / _cam.Zoom.X);
                }
                Hovered?.Invoke(ScreenToWorld(motion.Position));
                break;
        }
    }
}
