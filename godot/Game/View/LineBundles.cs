using Godot;
using Stations.Core.Sim;

namespace Stations.View;

/// <summary>
/// Lines that share a street are drawn side by side like on a network map.
/// For every street piece we know which lines use it; each line gets a slot across the street.
/// The side is taken from the piece's canonical direction, so both directions of a line stay together.
/// Offsets are stored in "slots" and scaled to pixels when drawing.
/// </summary>
public sealed class LineBundles
{
    public readonly Dictionary<Run, (Vector2[] Pts, Vector2[] Off)> Runs = new();
    private string _signature = "";

    private static (int, int, int, int, bool Flipped) Key(Vector2 a, Vector2 b)
    {
        int ax = Mathf.RoundToInt(a.X * 2), ay = Mathf.RoundToInt(a.Y * 2);
        int bx = Mathf.RoundToInt(b.X * 2), by = Mathf.RoundToInt(b.Y * 2);
        bool flip = ax > bx || (ax == bx && ay > by);
        return flip ? (bx, by, ax, ay, true) : (ax, ay, bx, by, false);
    }

    /// <summary>Recompute when lines were added, removed or rerouted.</summary>
    public void Update(Game game)
    {
        string sig = string.Join(";", game.Lines.Values.OrderBy(l => l.Id).Select(l => $"{l.Id}:{l.Version}:{l.Circular}:{string.Join(",", l.StopIds)}"));
        if (sig == _signature) return;
        _signature = sig;
        Runs.Clear();

        var lines = game.Lines.Values.OrderBy(l => l.Id).ToList();
        var pts = new Dictionary<Run, Vector2[]>();
        var usage = new Dictionary<(int, int, int, int), List<int>>();
        foreach (var line in lines)
        {
            foreach (var run in line.Runs)
            {
                var p = new Vector2[run.Path.Count];
                for (int i = 0; i < p.Length; i++) p[i] = new Vector2((float)run.Path.Xs[i], (float)run.Path.Ys[i]);
                pts[run] = p;
                for (int i = 0; i + 1 < p.Length; i++)
                {
                    if (p[i].DistanceSquaredTo(p[i + 1]) < 0.01f) continue;
                    var (a, b, c, d, _) = Key(p[i], p[i + 1]);
                    if (!usage.TryGetValue((a, b, c, d), out var list)) usage[(a, b, c, d)] = list = new List<int>();
                    if (!list.Contains(line.Id)) list.Add(line.Id); // lines are visited in id order, so lists stay sorted
                }
            }
        }

        foreach (var line in lines)
        {
            foreach (var run in line.Runs)
            {
                var p = pts[run];
                int n = p.Length;
                var segOff = new Vector2[Math.Max(n - 1, 0)];
                for (int i = 0; i + 1 < n; i++)
                {
                    if (p[i].DistanceSquaredTo(p[i + 1]) < 0.01f) continue;
                    var (a, b, c, d, flipped) = Key(p[i], p[i + 1]);
                    var list = usage[(a, b, c, d)];
                    float slot = list.IndexOf(line.Id) - (list.Count - 1) / 2f;
                    var dir = (flipped ? p[i] - p[i + 1] : p[i + 1] - p[i]).Normalized();
                    segOff[i] = new Vector2(-dir.Y, dir.X) * slot;
                }
                // Zero-length pieces take the neighbour's offset
                for (int i = 1; i < segOff.Length; i++) if (segOff[i] == Vector2.Zero) segOff[i] = segOff[i - 1];
                var off = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    if (segOff.Length == 0) break;
                    if (i == 0) off[i] = segOff[0];
                    else if (i == n - 1) off[i] = segOff[^1];
                    else off[i] = (segOff[i - 1] + segOff[i]) / 2;
                }
                Runs[run] = (p, off);
            }
        }
    }

    /// <summary>Run points shifted by their slot; slotWidth is in world units.</summary>
    public Vector2[] Shifted(Run run, float slotWidth)
    {
        if (!Runs.TryGetValue(run, out var r)) return Array.Empty<Vector2>();
        var res = new Vector2[r.Pts.Length];
        for (int i = 0; i < res.Length; i++) res[i] = r.Pts[i] + r.Off[i] * slotWidth;
        return res;
    }
}
