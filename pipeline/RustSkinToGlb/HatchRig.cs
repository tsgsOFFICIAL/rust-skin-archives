using System.Text.Json.Nodes;

namespace RustSkinToGlb;

// Ladder hatches come as one rigged mesh in the game (frame, lid, lower lid, telescoping ladder). The OBJ export has no
// bones, so the pieces are cut apart by island here and each piece becomes its own glTF node, the site animates the nodes
// (open / close button in the viewer, docs/js/viewer3d.js). The pivots and angles come from the game's rig
// (armoured_ladder_hatch_rig: upperHatch / lowerHatch / ladderMid / ladderEnd bones, the closed pose is the
// armoured_ladder_hatch_*_rig_closed prefab), the in-game clips themselves are not in the bundle's exportable assets.

// axis aligned bounding box of one island (a connected piece of the mesh, welded by position)
public readonly record struct IslandBox(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)
{
    public float CenterX => (MinX + MaxX) * 0.5f;
    public float CenterY => (MinY + MaxY) * 0.5f;
}

// How a node moves between its closed and its open pose, relative to the geometry as it sits in the file. The rotation
// turns around the line through Pivot along Axis (in the node's own frame, before the item's orientation), Move is added
// after. Start / End is the part of the open timeline (0..1) the movement happens in, closing plays it backwards. Both poses are stored so the viewer needs no idea which one the OBJ was authored in.
public sealed record HatchAnim(
    (float x, float y, float z) Pivot,
    (float x, float y, float z) Axis,
    float ClosedDeg, float OpenDeg,
    (float x, float y, float z) ClosedMove = default,
    (float x, float y, float z) OpenMove = default,
    float Start = 0f, float End = 1f)
{
    public JsonObject ToJson()
    {
        static JsonArray V((float x, float y, float z) v) => new() { v.x, v.y, v.z };
        return new JsonObject
        {
            ["pivot"] = V(Pivot),
            ["axis"] = V(Axis),
            ["rot"] = new JsonArray { ClosedDeg, OpenDeg },
            ["move"] = new JsonArray { V(ClosedMove), V(OpenMove) },
            ["win"] = new JsonArray { Start, End },
        };
    }
}

public static class HatchRig
{
    // the armored hatches' rig mesh is authored open: the lid stands up at the -X edge, the lower lid hangs down inside the
    // shaft and the ladder (three nested sections, each one further out in X) hangs next to it
    public const int ArmoredLid = 0, ArmoredLower = 1, ArmoredSec1 = 2, ArmoredSec2 = 3, ArmoredSec3 = 4;

    public static int ClassifyArmoredRig(IslandBox b)
    {
        if (b.MinY >= 0.1f) return ArmoredLid;
        float cx = b.CenterX;
        if (cx < -0.75f) return ArmoredLower;
        if (cx < -0.635f) return ArmoredSec1;
        if (cx < -0.535f) return ArmoredSec2;
        return ArmoredSec3;
    }

    // the ladder (and its brackets) of the game's placement guide mesh: everything left of the hatch, the slab and lid
    // islands reach to +X so they are dropped
    public static bool KeepGuideLadder(IslandBox b) => b.MaxX < -0.2f;

    // the guide ladder is three sections nested in X, the outermost (most +X) one is the lowest: 0 top, 1 middle, 2 bottom
    public static int ClassifyGuideLadder(IslandBox b)
    {
        float cx = b.CenterX;
        if (cx > -0.365f) return 2;
        if (cx > -0.44f) return 1;
        return 0;
    }

    // computes the islands once (vertices welded by position, because they're split at UV seams and hard edges)
    private static (int[] root, Dictionary<int, IslandBox> boxes) Islands(RawMesh m)
    {
        int n = m.Positions.Count;
        int[] parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var firstAt = new Dictionary<(int, int, int), int>();
        for (int v = 0; v < n; v++)
        {
            var (px, py, pz) = m.Positions[v];
            var key = ((int)MathF.Round(px * 10000f), (int)MathF.Round(py * 10000f), (int)MathF.Round(pz * 10000f));
            if (firstAt.TryGetValue(key, out int first)) parent[Find(v)] = Find(first); else firstAt[key] = v;
        }
        for (int i = 0; i < m.Indices.Count; i += 3)
        {
            parent[Find(m.Indices[i])] = Find(m.Indices[i + 1]);
            parent[Find(m.Indices[i])] = Find(m.Indices[i + 2]);
        }
        var root = new int[n];
        var boxes = new Dictionary<int, IslandBox>();
        for (int v = 0; v < n; v++)
        {
            int r = root[v] = Find(v);
            var (x, y, z) = m.Positions[v];
            boxes[r] = boxes.TryGetValue(r, out var b)
                ? new IslandBox(MathF.Min(b.MinX, x), MathF.Min(b.MinY, y), MathF.Min(b.MinZ, z), MathF.Max(b.MaxX, x), MathF.Max(b.MaxY, y), MathF.Max(b.MaxZ, z))
                : new IslandBox(x, y, z, x, y, z);
        }
        return (root, boxes);
    }

    // the triangles of the islands accepted by keep, as a new mesh
    public static RawMesh KeepIslands(this RawMesh m, Func<IslandBox, bool> keep)
    {
        var (root, boxes) = Islands(m);
        var vmap = new Dictionary<int, int>();
        var np = new List<(float, float, float)>(); var nu = new List<(float, float)>();
        var nn = new List<(float, float, float)>(); var ni = new List<int>();
        int Remap(int old)
        {
            if (!vmap.TryGetValue(old, out int v)) { v = np.Count; np.Add(m.Positions[old]); nu.Add(m.Uvs[old]); nn.Add(m.Normals[old]); vmap[old] = v; }
            return v;
        }
        for (int i = 0; i < m.Indices.Count; i += 3)
        {
            if (!keep(boxes[root[m.Indices[i]]])) continue;
            ni.Add(Remap(m.Indices[i])); ni.Add(Remap(m.Indices[i + 1])); ni.Add(Remap(m.Indices[i + 2]));
        }
        return new RawMesh(np, nu, nn, ni);
    }

    // the triangles of the islands that classify puts into group
    public static RawMesh SplitIslands(this RawMesh m, Func<IslandBox, int> classify, int group)
        => m.KeepIslands(b => classify(b) == group);
}
