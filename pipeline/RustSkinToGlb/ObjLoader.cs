using System.Globalization;

namespace RustSkinToGlb;

// bare bones OBJ loader, only does what the WorkshopSource files need (v, vt, vn, f)
public static class ObjLoader
{
    // firstObjectOnly stops at the second 'o' line (the fridge has an open and a closed variant in one file)
    // objectIndex picks one object, -1 = all merged
    // geometryGroup only keeps faces in the `g` group / `usemtl` material with that name
    public static RawMesh Load(string path, bool firstObjectOnly = false, int objectIndex = -1, string? geometryGroup = null)
    {
        var positions = new List<(float x, float y, float z)>();
        var uvs = new List<(float u, float v)>();
        var normals = new List<(float x, float y, float z)>();

        // the final vertex data with duplicates removed, in the order the faces use them
        var outPositions = new List<(float, float, float)>();
        var outUvs = new List<(float, float)>();
        var outNormals = new List<(float, float, float)>();
        var indices = new List<int>();

        // so the same (v, vt, vn) combination reuses one output vertex
        var cache = new Dictionary<(int, int, int), int>();

        int currentObjectIndex = -1;
        string currentGroup = ""; // the active `g <name>`, for the geometryGroup filter
        string currentMaterial = ""; // the active `usemtl <name>`, geometryGroup matches this too
        bool collecting = (objectIndex < 0 && !firstObjectOnly); // -1 = take everything from the start
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (parts[0] == "o")
            {
                currentObjectIndex++;
                if (firstObjectOnly) { if (currentObjectIndex > 0) break; collecting = true; }
                else if (objectIndex >= 0) { if (currentObjectIndex > objectIndex) break; collecting = (currentObjectIndex == objectIndex); }
                continue;
            }
            // vertices, uvs and normals are always read (OBJ indices count across all objects),
            // only the faces get skipped when not collecting

            switch (parts[0])
            {
                case "v":
                    positions.Add((
                        F(parts[1]), F(parts[2]), F(parts[3])));
                    break;

                case "vt":
                    uvs.Add((F(parts[1]), parts.Length > 2 ? F(parts[2]) : 0f));
                    break;

                case "vn":
                    normals.Add((F(parts[1]), F(parts[2]), F(parts[3])));
                    break;

                case "g":
                    currentGroup = parts.Length > 1 ? parts[1] : "";
                    break;

                case "usemtl":
                    currentMaterial = parts.Length > 1 ? parts[1] : "";
                    break;

                case "f":
                    {
                        if (!collecting) break; // faces of another object
                        // with a geometry group set, only keep faces that are in it
                        if (geometryGroup != null && currentGroup != geometryGroup && currentMaterial != geometryGroup) break;
                        // fan triangulation for anything with more than 3 verts (rare in these files)
                        var faceVerts = new int[parts.Length - 1];
                        for (int i = 1; i < parts.Length; i++)
                        {
                            faceVerts[i - 1] = ResolveVertex(parts[i], positions, uvs, normals,
                                outPositions, outUvs, outNormals, cache);
                        }

                        for (int i = 1; i < faceVerts.Length - 1; i++)
                        {
                            indices.Add(faceVerts[0]);
                            indices.Add(faceVerts[i]);
                            indices.Add(faceVerts[i + 1]);
                        }

                        break;
                    }

                    // everything else (s, mtllib, l, ...) is ignored
            }
        }

        return new RawMesh(outPositions, outUvs, outNormals, indices);
    }

    private static int ResolveVertex(
        string token,
        List<(float x, float y, float z)> positions,
        List<(float u, float v)> uvs,
        List<(float x, float y, float z)> normals,
        List<(float, float, float)> outPositions,
        List<(float, float)> outUvs,
        List<(float, float, float)> outNormals,
        Dictionary<(int, int, int), int> cache)
    {
        // the token can be v, v/vt, v/vt/vn or v//vn
        var bits = token.Split('/');

        int vi = int.Parse(bits[0], CultureInfo.InvariantCulture);
        int ti = bits.Length > 1 && bits[1].Length > 0 ? int.Parse(bits[1], CultureInfo.InvariantCulture) : 0;
        int ni = bits.Length > 2 && bits[2].Length > 0 ? int.Parse(bits[2], CultureInfo.InvariantCulture) : 0;

        // OBJ indices start at 1 and can be negative (counted back from the end)
        vi = vi > 0 ? vi - 1 : positions.Count + vi;
        ti = ti != 0 ? (ti > 0 ? ti - 1 : uvs.Count + ti) : -1;
        ni = ni != 0 ? (ni > 0 ? ni - 1 : normals.Count + ni) : -1;

        var key = (vi, ti, ni);
        if (cache.TryGetValue(key, out var existing))
            return existing;

        int newIndex = outPositions.Count;
        outPositions.Add(positions[vi]);
        outUvs.Add(ti >= 0 ? uvs[ti] : (0f, 0f));
        outNormals.Add(ni >= 0 ? normals[ni] : (0f, 0f, 0f));

        cache[key] = newIndex;
        return newIndex;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}

public sealed record RawMesh(
    List<(float x, float y, float z)> Positions,
    List<(float u, float v)> Uvs,
    List<(float x, float y, float z)> Normals,
    List<int> Indices)
{
    public bool HasNormals => Normals.Count > 0 && Normals.Any(n => n != (0f, 0f, 0f));

    // drops islands far from the main body (the one with the most triangles), for the python's floating
    // speed loader crane. Dropping small islands would kill its 20+ real small parts.
    // maxDistanceFraction is relative to the main body's bounding box diagonal
    public RawMesh StripDistantIslands(float maxDistanceFraction = 0.5f)
    {
        int totalTris = Indices.Count / 3;
        if (totalTris == 0) return this;

        // union-find over the vertex indices
        int n = Positions.Count;
        int[] parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }
        for (int i = 0; i < Indices.Count; i += 3)
            { Union(Indices[i], Indices[i + 1]); Union(Indices[i], Indices[i + 2]); }

        // per island: triangle count and the sum of positions (for the centre)
        var triCount = new Dictionary<int, int>();
        var sumPos = new Dictionary<int, (float x, float y, float z)>();
        for (int i = 0; i < Indices.Count; i += 3)
        {
            int root = Find(Indices[i]);
            triCount.TryGetValue(root, out int c); triCount[root] = c + 1;
            var p = Positions[Indices[i]];
            sumPos.TryGetValue(root, out var s);
            sumPos[root] = (s.x + p.x, s.y + p.y, s.z + p.z);
        }
        if (triCount.Count == 1) return this; // only one island anyway

        // main body = the island with the most triangles
        int mainRoot = triCount.MaxBy(kv => kv.Value).Key;
        int mainTris = triCount[mainRoot];
        var mainSum = sumPos[mainRoot];
        float cx = mainSum.x / mainTris, cy = mainSum.y / mainTris, cz = mainSum.z / mainTris;

        // bounding box of the main body
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < Indices.Count; i += 3)
            if (Find(Indices[i]) == mainRoot)
            {
                var p = Positions[Indices[i]];
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
                minZ = Math.Min(minZ, p.z); maxZ = Math.Max(maxZ, p.z);
            }
        float diag = MathF.Sqrt(
            (maxX-minX)*(maxX-minX) + (maxY-minY)*(maxY-minY) + (maxZ-minZ)*(maxZ-minZ));
        float maxDist = diag * maxDistanceFraction;

        // keep the islands whose centre is within maxDist of the main body's centre
        var keep = new HashSet<int>();
        foreach (var (root, tris) in triCount)
        {
            var s = sumPos[root];
            float icx = s.x / tris, icy = s.y / tris, icz = s.z / tris;
            float dist = MathF.Sqrt((icx-cx)*(icx-cx) + (icy-cy)*(icy-cy) + (icz-cz)*(icz-cz));
            if (dist <= maxDist) keep.Add(root);
        }
        if (keep.Count == triCount.Count) return this;

        var vertexRemap = new Dictionary<int, int>();
        var newPos = new List<(float, float, float)>();
        var newUv = new List<(float, float)>();
        var newNrm = new List<(float, float, float)>();
        var newIdx = new List<int>();
        int Remap(int old)
        {
            if (!vertexRemap.TryGetValue(old, out int mapped))
            {
                mapped = newPos.Count;
                newPos.Add(Positions[old]); newUv.Add(Uvs[old]); newNrm.Add(Normals[old]);
                vertexRemap[old] = mapped;
            }
            return mapped;
        }
        for (int i = 0; i < Indices.Count; i += 3)
        {
            if (!keep.Contains(Find(Indices[i]))) continue;
            newIdx.Add(Remap(Indices[i])); newIdx.Add(Remap(Indices[i+1])); newIdx.Add(Remap(Indices[i+2]));
        }
        return new RawMesh(newPos, newUv, newNrm, newIdx);
    }

    // the island with this rank (0 = biggest), or everything except it with invert
    // splits one OBJ into two primitives with different materials, the riot helmet shell and visor
    // are separate islands and usemtl can't split them
    public RawMesh ExtractByIslandRank(int rank, bool invert = false)
    {
        int n = Positions.Count;
        int[] parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }
        for (int i = 0; i < Indices.Count; i += 3)
            { Union(Indices[i], Indices[i+1]); Union(Indices[i], Indices[i+2]); }

        var triCount = new Dictionary<int, int>();
        for (int i = 0; i < Indices.Count; i += 3)
        { int r = Find(Indices[i]); triCount.TryGetValue(r, out int c); triCount[r] = c + 1; }

        var ranked = triCount.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var targetRoot = ranked.Count > rank ? ranked[rank] : -1;
        var keep = new HashSet<int>();
        if (!invert && targetRoot >= 0) keep.Add(targetRoot);
        else if (invert) foreach (var (r, _) in triCount) if (r != targetRoot) keep.Add(r);

        var vmap = new Dictionary<int, int>();
        var np = new List<(float,float,float)>(); var nu = new List<(float,float)>();
        var nn = new List<(float,float,float)>(); var ni = new List<int>();
        int Remap(int old) { if (!vmap.TryGetValue(old, out int m)) { m = np.Count; np.Add(Positions[old]); nu.Add(Uvs[old]); nn.Add(Normals[old]); vmap[old]=m; } return m; }
        for (int i = 0; i < Indices.Count; i += 3)
        { if (!keep.Contains(Find(Indices[i]))) continue; ni.Add(Remap(Indices[i])); ni.Add(Remap(Indices[i+1])); ni.Add(Remap(Indices[i+2])); }
        return new RawMesh(np, nu, nn, ni);
    }

    // Removes just the one island with the fewest triangles. More exact than StripSmallIslands,
    // no threshold to guess and everything else stays as it is.
    public RawMesh StripSmallestIsland()
    {
        int n = Positions.Count;
        int[] parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }
        for (int i = 0; i < Indices.Count; i += 3)
            { Union(Indices[i], Indices[i+1]); Union(Indices[i], Indices[i+2]); }

        var triCount = new Dictionary<int, int>();
        for (int i = 0; i < Indices.Count; i += 3)
        { int r = Find(Indices[i]); triCount.TryGetValue(r, out int c); triCount[r] = c + 1; }

        if (triCount.Count <= 1) return this;
        int smallestRoot = triCount.MinBy(kv => kv.Value).Key;
        int smallestTris = triCount[smallestRoot];

        var vmap = new Dictionary<int, int>();
        var np = new List<(float,float,float)>(); var nu = new List<(float,float)>();
        var nn = new List<(float,float,float)>(); var ni = new List<int>();
        int Remap(int old) { if (!vmap.TryGetValue(old, out int m)) { m = np.Count; np.Add(Positions[old]); nu.Add(Uvs[old]); nn.Add(Normals[old]); vmap[old]=m; } return m; }
        for (int i = 0; i < Indices.Count; i += 3)
        { if (Find(Indices[i]) == smallestRoot) continue; ni.Add(Remap(Indices[i])); ni.Add(Remap(Indices[i+1])); ni.Add(Remap(Indices[i+2])); }
        Console.WriteLine($"    StripSmallestIsland: removed {smallestTris} tris");
        return new RawMesh(np, nu, nn, ni);
    }

    // keeps the triangles whose centre Z is between minZ and maxZ (either can be null)
    // rough fallback split for a merged OBJ with no groups and no clean islands. The real boundary
    // isn't a straight Z plane so there's a small seam error
    public RawMesh FilterByZRange(float? minZ, float? maxZ)
    {
        var np = new List<(float,float,float)>(); var nu = new List<(float,float)>();
        var nn = new List<(float,float,float)>(); var ni = new List<int>();
        var vmap = new Dictionary<int, int>();
        int Remap(int old) { if (!vmap.TryGetValue(old, out int m)) { m = np.Count; np.Add(Positions[old]); nu.Add(Uvs[old]); nn.Add(Normals[old]); vmap[old]=m; } return m; }
        for (int i = 0; i < Indices.Count; i += 3)
        {
            float z = (Positions[Indices[i]].Item3 + Positions[Indices[i+1]].Item3 + Positions[Indices[i+2]].Item3) / 3f;
            if (minZ.HasValue && z < minZ.Value) continue;
            if (maxZ.HasValue && z > maxZ.Value) continue;
            ni.Add(Remap(Indices[i])); ni.Add(Remap(Indices[i+1])); ni.Add(Remap(Indices[i+2]));
        }
        return new RawMesh(np, nu, nn, ni);
    }

    // drops islands whose X centre is further than maxAbsX from the middle (X = 0)
    // for the bolt rifle, its obj has two bullets floating next to the gun at X around 0.174 while every
    // real part is within 0.02 of the centre. Only the sideways offset counts so parts along the barrel are safe
    public RawMesh StripIslandsByXOffset(float maxAbsX)
    {
        int n = Positions.Count;
        int[] parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }
        for (int i = 0; i < Indices.Count; i += 3)
            { Union(Indices[i], Indices[i+1]); Union(Indices[i], Indices[i+2]); }

        var triCount = new Dictionary<int, int>();
        var sumX = new Dictionary<int, float>();
        for (int i = 0; i < Indices.Count; i += 3)
        {
            int r = Find(Indices[i]);
            triCount.TryGetValue(r, out int c); triCount[r] = c + 1;
            sumX.TryGetValue(r, out float sx); sumX[r] = sx + Positions[Indices[i]].Item1;
        }
        var keep = new HashSet<int>(triCount.Keys.Where(r => Math.Abs(sumX[r] / triCount[r]) <= maxAbsX));
        if (keep.Count == triCount.Count) return this;

        var vmap = new Dictionary<int, int>();
        var np = new List<(float,float,float)>(); var nu = new List<(float,float)>(); var nn = new List<(float,float,float)>(); var ni = new List<int>();
        int Remap(int old) { if (!vmap.TryGetValue(old, out int m)) { m = np.Count; np.Add(Positions[old]); nu.Add(Uvs[old]); nn.Add(Normals[old]); vmap[old]=m; } return m; }
        for (int i = 0; i < Indices.Count; i += 3)
        { if (!keep.Contains(Find(Indices[i]))) continue; ni.Add(Remap(Indices[i])); ni.Add(Remap(Indices[i+1])); ni.Add(Remap(Indices[i+2])); }
        return new RawMesh(np, nu, nn, ni);
    }

    // drops islands with less than minFraction of the triangles, for small floating props
    // (the pump shotgun shells are ~50 tris against ~9000). Use this over StripDistantIslands when the prop is close to the body
    public RawMesh StripSmallIslands(float minFraction = 0.01f)
    {
        int totalTris = Indices.Count / 3;
        if (totalTris == 0) return this;

        int n = Positions.Count;
        int[] parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }
        for (int i = 0; i < Indices.Count; i += 3)
            { Union(Indices[i], Indices[i+1]); Union(Indices[i], Indices[i+2]); }

        var triCount = new Dictionary<int, int>();
        for (int i = 0; i < Indices.Count; i += 3)
        { int r = Find(Indices[i]); triCount.TryGetValue(r, out int c); triCount[r] = c + 1; }

        int threshold = (int)Math.Ceiling(totalTris * minFraction);
        var keep = new HashSet<int>(triCount.Where(kv => kv.Value >= threshold).Select(kv => kv.Key));
        if (Environment.GetEnvironmentVariable("RS_DEBUG_ISLANDS") == "1")
        {
            var sumPos = new Dictionary<int, (float x, float y, float z)>();
            for (int i = 0; i < Indices.Count; i += 3)
            {
                int root = Find(Indices[i]);
                var p = Positions[Indices[i]];
                sumPos.TryGetValue(root, out var s);
                sumPos[root] = (s.x + p.x, s.y + p.y, s.z + p.z);
            }
            foreach (var kv in triCount.OrderByDescending(kv => kv.Value))
            {
                var s = sumPos[kv.Key];
                Console.WriteLine($"    island tris={kv.Value} centroid=({s.x/kv.Value:F3},{s.y/kv.Value:F3},{s.z/kv.Value:F3})");
            }
        }
        if (keep.Count == triCount.Count) return this;

        var vmap = new Dictionary<int, int>();
        var np = new List<(float,float,float)>(); var nu = new List<(float,float)>(); var nn = new List<(float,float,float)>(); var ni = new List<int>();
        int Remap(int old) { if (!vmap.TryGetValue(old, out int m)) { m = np.Count; np.Add(Positions[old]); nu.Add(Uvs[old]); nn.Add(Normals[old]); vmap[old]=m; } return m; }
        for (int i = 0; i < Indices.Count; i += 3)
        { if (!keep.Contains(Find(Indices[i]))) continue; ni.Add(Remap(Indices[i])); ni.Add(Remap(Indices[i+1])); ni.Add(Remap(Indices[i+2])); }
        return new RawMesh(np, nu, nn, ni);
    }
}