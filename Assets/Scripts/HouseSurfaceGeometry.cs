using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Milestone M4. Play-mode fix-ups for the builder's box geometry, applied when
/// <see cref="TextureCorruptionRunner"/> binds its surfaces:
///
///  - World-space UVs. Every house piece is a scaled Unity cube whose default UVs squeeze the whole
///    texture onto each face, so a 17 m wall and a 0.35 m header underside got the same number of
///    repeats and high-contrast corruption stretched "into oblivion". Each box gets its own mesh whose
///    UVs come from world position (one repeat per <see cref="TileMetres"/>), plus a per-plane offset so
///    separate walls don't repeat in lockstep. Coplanar faces share the offset.
///  - Coplanar overlap fix. The builder stacks boxes that share faces (floor slabs duplicated as
///    ceilings, overlapping wall runs) - ~550 face pairs. URP picks lights per renderer, so even
///    identical textures flicker. Faces are ranked (surface priority for that direction, then area,
///    then name) and each one sits <see cref="InsetStep"/> behind every overlapping face that beats it.
///
/// Only rendered meshes change; colliders, NavMesh and the scene file are untouched.
/// </summary>
public static class HouseSurfaceGeometry
{
    public const float InsetStep = 0.006f;
    public const string MeshPrefix = "WorldUV_";

    /// <summary>Metres of surface one texture repeat covers, by runner surface name.</summary>
    public static readonly Dictionary<string, float> TileMetres = new Dictionary<string, float>
    {
        { "Wall", 1.6f }, { "Floor", 1.5f }, { "FloorUpper", 1.5f }, { "FloorBasement", 1.6f }, { "Ceiling", 1.8f },
        { "Trim", 1.0f }, { "Stair", 1.0f }, { "Porch", 1.8f }, { "Tile", 1.2f },
    };

    // Which surface wins a coplanar tie, per face direction: floors on top, ceilings underneath, walls on the sides.
    static readonly Dictionary<string, int> TopPriority = new Dictionary<string, int>
    {
        { "Floor", 9 }, { "FloorUpper", 9 }, { "FloorBasement", 9 }, { "Tile", 9 }, { "Porch", 8 }, { "Stair", 7 }, { "Trim", 5 }, { "Wall", 4 }, { "Ceiling", 1 },
    };
    static readonly Dictionary<string, int> BottomPriority = new Dictionary<string, int>
    {
        { "Ceiling", 9 }, { "Stair", 6 }, { "Trim", 5 }, { "Wall", 4 }, { "Porch", 3 }, { "Tile", 2 }, { "FloorBasement", 2 }, { "Floor", 2 }, { "FloorUpper", 2 },
    };
    static readonly Dictionary<string, int> SidePriority = new Dictionary<string, int>
    {
        { "Wall", 9 }, { "Trim", 8 }, { "Stair", 7 }, { "Tile", 6 }, { "Porch", 5 }, { "FloorBasement", 4 }, { "Floor", 4 }, { "FloorUpper", 4 }, { "Ceiling", 3 },
    };

    /// <summary>One face of an axis-aligned house box. Extents are on axes ((axis+1)%3, (axis+2)%3).</summary>
    public sealed class Face
    {
        public MeshRenderer renderer;
        public string surface;
        public int axis, sign, priority, depth;
        public float plane, area;
        public Vector2 min, max;
    }

    /// <summary>
    /// Rebuild every house box with world UVs and resolve coplanar overlaps (skipped when
    /// <paramref name="rebuildMeshes"/> is false - the faces are still analysed). Meshes this creates are
    /// added to <paramref name="owned"/>; ones it replaces from an earlier call are destroyed.
    /// Returns every analysed face; <c>depth == 0</c> marks the visible front-most ones.
    /// </summary>
    public static List<Face> Apply(IList<string> surfaceNames, IList<List<Renderer>> renderersPerSurface, List<Mesh> owned,
                                   bool rebuildMeshes = true)
    {
        var boxes = new List<(MeshRenderer r, string surface)>();
        for (int si = 0; si < surfaceNames.Count && si < renderersPerSurface.Count; si++)
        {
            string name = surfaceNames[si];
            var list = renderersPerSurface[si];
            if (name == null || list == null || !TileMetres.ContainsKey(name)) continue;
            foreach (var r in list)
                if (r is MeshRenderer mr && IsBox(mr)) boxes.Add((mr, name));
        }

        var faces = Analyse(boxes);
        if (!rebuildMeshes) return faces;
        var depthOf = new Dictionary<(MeshRenderer, int, int), int>();
        foreach (var f in faces)
            if (f.depth > 0) depthOf[(f.renderer, f.axis, f.sign)] = f.depth;

        foreach (var (r, surface) in boxes)
            Rebuild(r, TileMetres[surface], depthOf, owned);
        return faces;
    }

    static bool IsBox(MeshRenderer r)
    {
        var mf = r.GetComponent<MeshFilter>();
        return mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount == 24 && mf.sharedMesh.subMeshCount == 1;
    }

    static bool AxisAligned(Transform t)
    {
        var e = t.eulerAngles;
        return Near90(e.x) && Near90(e.y) && Near90(e.z);
    }

    static bool Near90(float a) => Mathf.Abs(Mathf.DeltaAngle(a, Mathf.Round(a / 90f) * 90f)) < 0.5f;

    static List<Face> Analyse(List<(MeshRenderer r, string surface)> boxes)
    {
        var faces = new List<Face>();
        foreach (var (r, surface) in boxes)
        {
            var t = r.transform;
            if (!AxisAligned(t)) continue;
            // Exact box extents from the transform (ignores any inset a previous pass baked into the mesh).
            var a0 = t.TransformVector(Vector3.right * 0.5f);
            var a1 = t.TransformVector(Vector3.up * 0.5f);
            var a2 = t.TransformVector(Vector3.forward * 0.5f);
            var ext = new Vector3(Mathf.Abs(a0.x) + Mathf.Abs(a1.x) + Mathf.Abs(a2.x),
                                  Mathf.Abs(a0.y) + Mathf.Abs(a1.y) + Mathf.Abs(a2.y),
                                  Mathf.Abs(a0.z) + Mathf.Abs(a1.z) + Mathf.Abs(a2.z));
            var bmin = t.position - ext;
            var bmax = t.position + ext;
            for (int ax = 0; ax < 3; ax++)
            {
                int u = (ax + 1) % 3, v = (ax + 2) % 3;
                var mn = new Vector2(bmin[u], bmin[v]);
                var mx = new Vector2(bmax[u], bmax[v]);
                for (int s = -1; s <= 1; s += 2)
                {
                    var table = ax == 1 ? (s > 0 ? TopPriority : BottomPriority) : SidePriority;
                    faces.Add(new Face
                    {
                        renderer = r, surface = surface, axis = ax, sign = s,
                        plane = s > 0 ? bmax[ax] : bmin[ax], min = mn, max = mx,
                        area = (mx.x - mn.x) * (mx.y - mn.y),
                        priority = table.TryGetValue(surface, out int p) ? p : 0,
                    });
                }
            }
        }

        // Total order per direction; each face sits one step behind every overlapping face that beats it,
        // so no two overlapping faces can end up at the same depth.
        var groups = new Dictionary<(int, int), List<Face>>();
        foreach (var f in faces)
        {
            if (!groups.TryGetValue((f.axis, f.sign), out var g)) groups[(f.axis, f.sign)] = g = new List<Face>();
            g.Add(f);
        }
        foreach (var g in groups.Values)
        {
            g.Sort(CompareRank);
            for (int i = 0; i < g.Count; i++)
            {
                var f = g[i];
                for (int j = 0; j < i; j++)
                {
                    var w = g[j];
                    if (Mathf.Abs(w.plane - f.plane) > 0.003f) continue;
                    if (Mathf.Min(w.max.x, f.max.x) - Mathf.Max(w.min.x, f.min.x) <= 0.02f) continue;
                    if (Mathf.Min(w.max.y, f.max.y) - Mathf.Max(w.min.y, f.min.y) <= 0.02f) continue;
                    f.depth = Mathf.Max(f.depth, w.depth + 1);
                }
            }
        }
        return faces;
    }

    static int CompareRank(Face a, Face b)
    {
        int c = b.priority.CompareTo(a.priority);
        if (c != 0) return c;
        c = b.area.CompareTo(a.area);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.renderer.name, b.renderer.name);
        if (c != 0) return c;
        var pa = a.renderer.transform.position; var pb = b.renderer.transform.position;
        c = pa.x.CompareTo(pb.x);
        if (c != 0) return c;
        c = pa.y.CompareTo(pb.y);
        return c != 0 ? c : pa.z.CompareTo(pb.z);
    }

    static void Rebuild(MeshRenderer r, float tileMetres, Dictionary<(MeshRenderer, int, int), int> depthOf, List<Mesh> owned)
    {
        var mf = r.GetComponent<MeshFilter>();
        var src = mf.sharedMesh;
        var t = r.transform;
        var mat = r.sharedMaterial;
        var tiling = mat != null && mat.HasProperty("_BaseMap") ? mat.GetTextureScale("_BaseMap") : Vector2.one;
        if (Mathf.Abs(tiling.x) < 1e-4f || Mathf.Abs(tiling.y) < 1e-4f) tiling = Vector2.one;

        var normals = src.normals;
        var srcVerts = src.vertices;
        var verts = new Vector3[srcVerts.Length];
        var uvs = new Vector2[srcVerts.Length];
        for (int i = 0; i < srcVerts.Length; i++)
        {
            // Canonical cube corner - a previous pass may already have inset this vertex.
            var canon = new Vector3(Mathf.Sign(srcVerts[i].x) * 0.5f, Mathf.Sign(srcVerts[i].y) * 0.5f, Mathf.Sign(srcVerts[i].z) * 0.5f);
            verts[i] = canon;
            var wn = t.TransformDirection(normals[i]);
            int ax = Mathf.Abs(wn.x) >= Mathf.Abs(wn.y) && Mathf.Abs(wn.x) >= Mathf.Abs(wn.z) ? 0 : (Mathf.Abs(wn.y) >= Mathf.Abs(wn.z) ? 1 : 2);
            int sg = wn[ax] >= 0 ? 1 : -1;
            if (depthOf.TryGetValue((r, ax, sg), out int d))
            {
                float worldLen = Vector3.Scale(normals[i], t.lossyScale).magnitude;
                if (worldLen > 1e-4f) verts[i] -= normals[i] * (Mathf.Min(d * InsetStep, worldLen * 0.3f) / worldLen);
            }
            var w = t.TransformPoint(verts[i]);
            var wp = t.TransformPoint(canon);
            // Per-plane offset: separate walls don't repeat in lockstep, coplanar faces stay identical.
            uint h = unchecked((uint)(ax * 73856093) ^ (uint)((sg + 2) * 19349663) ^ (uint)(Mathf.RoundToInt(wp[ax] * 50f) * 83492791));
            h ^= h >> 13; h = unchecked(h * 0x5bd1e995u); h ^= h >> 15;
            var off = new Vector2((h & 0xffff) / 65536f, (h >> 16) / 65536f);
            var c = ax == 0 ? new Vector2(w.z, w.y) : ax == 1 ? new Vector2(w.x, w.z) : new Vector2(w.x, w.y);
            var fin = c / tileMetres + off;
            uvs[i] = new Vector2(fin.x / tiling.x, fin.y / tiling.y);
        }

        var mesh = new Mesh { name = MeshPrefix + r.name };
        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = src.triangles;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mf.sharedMesh = mesh;
        owned.Add(mesh);
        if (src.name.StartsWith(MeshPrefix) && owned.Remove(src))
            HorrorTextureCorruptor.SafeDestroy(src);
    }
}
