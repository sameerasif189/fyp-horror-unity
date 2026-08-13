using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps outdoor/nature renderers from inheriting haunted-house UV warping.
/// Restores original tiling and clears material property blocks every frame.
/// </summary>
public class EnvironmentTextureLock : MonoBehaviour
{
    struct Slot
    {
        public Material material;
        public Vector2 baseScale;
        public Vector2 baseOffset;
        public Vector2 mainScale;
        public Vector2 mainOffset;
        public bool hasBase;
        public bool hasMain;
    }

    readonly List<Renderer> _renderers = new List<Renderer>();
    readonly Dictionary<Material, Slot> _orig = new Dictionary<Material, Slot>();

    void OnEnable() => Capture();

    void Start() => Capture();

    public void Capture()
    {
        _renderers.Clear();
        _orig.Clear();
        GetComponentsInChildren(true, _renderers);
        for (int i = 0; i < _renderers.Count; i++)
        {
            var r = _renderers[i];
            if (r == null) continue;
            var mats = r.sharedMaterials;
            if (mats == null) continue;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null || _orig.ContainsKey(mat)) continue;
                var slot = new Slot { material = mat };
                if (mat.HasProperty("_BaseMap"))
                {
                    slot.hasBase = true;
                    slot.baseScale = mat.GetTextureScale("_BaseMap");
                    slot.baseOffset = mat.GetTextureOffset("_BaseMap");
                }
                if (mat.HasProperty("_MainTex"))
                {
                    slot.hasMain = true;
                    slot.mainScale = mat.GetTextureScale("_MainTex");
                    slot.mainOffset = mat.GetTextureOffset("_MainTex");
                }
                _orig[mat] = slot;
            }
            r.SetPropertyBlock(null);
        }
    }

    void LateUpdate() => Restore();

    public void Restore()
    {
        for (int i = 0; i < _renderers.Count; i++)
        {
            var r = _renderers[i];
            if (r != null)
                r.SetPropertyBlock(null);
        }

        foreach (var kv in _orig)
        {
            var mat = kv.Key;
            if (mat == null) continue;
            var slot = kv.Value;
            if (slot.hasBase)
            {
                mat.SetTextureScale("_BaseMap", slot.baseScale);
                mat.SetTextureOffset("_BaseMap", slot.baseOffset);
            }
            if (slot.hasMain)
            {
                mat.SetTextureScale("_MainTex", slot.mainScale);
                mat.SetTextureOffset("_MainTex", slot.mainOffset);
            }
        }
    }

    public static void EnsureOnExterior()
    {
        var house = GameObject.Find("HauntedHouse");
        if (house != null)
        {
            var nested = house.transform.Find("Yard");
            if (nested != null)
            {
                nested.SetParent(null, true);
                nested.name = "HauntedYard";
            }
        }

        EnsureNamed("HauntedYard");
        EnsureNamed("Yard");
    }

    static void EnsureNamed(string name)
    {
        var go = GameObject.Find(name);
        if (go != null)
            EnsureOn(go);
    }

    static void EnsureOn(GameObject go)
    {
        if (go == null) return;
        var lockCmp = go.GetComponent<EnvironmentTextureLock>();
        if (lockCmp == null)
            lockCmp = go.AddComponent<EnvironmentTextureLock>();
        lockCmp.Capture();
        lockCmp.Restore();
    }
}
