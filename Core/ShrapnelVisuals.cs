using System;
using System.Collections.Generic;
using UnityEngine;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Static cache of sprites, materials, and colors for shrapnel.
    ///
    /// Sprites are procedurally generated on first request and cached forever.
    /// Materials are created once with robust fallback chain.
    ///
    /// Initialization order:
    /// 1. Try named shaders via Shader.Find
    /// 2. Fallback: clone material from existing SpriteRenderer in scene
    /// 3. If all fail: return null, spawn methods skip gracefully
    /// </summary>
    public static class ShrapnelVisuals
    {
        private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();

        private static Material _litMaterial;
        private static Material _unlitMaterial;
        private static Material _trailMaterial;

        // WHY: Track initialization attempts to avoid spamming FindObjectOfType
        private static bool _litAttempted;
        private static bool _unlitAttempted;

        /// <summary>
        /// Pre-warms all materials and sprite cache.
        /// Call when game is fully loaded to prevent first-explosion failures.
        /// Safe to call multiple times - no-op if already warmed.
        /// </summary>
        public static void PreWarm()
        {
            try
            {
                // Force material creation - these getters handle their own null checks
                var lit = LitMaterial;
                var unlit = UnlitMaterial;
                var trail = TrailMaterial;

                // Pre-generate all sprite shapes
                for (int i = 0; i < 6; i++)
                {
                    try
                    {
                        GetTriangleSprite((TriangleShape)i);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"[Visuals] Sprite {i} failed: {e.Message}");
                    }
                }

                Plugin.Log.LogInfo($"[Visuals] PreWarm complete. Lit:{lit != null}" +
                                   $" Unlit:{unlit != null} Trail:{trail != null}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Visuals] PreWarm exception: {e.Message}");
            }
        }

        // ── MATERIALS ──

        public static Material LitMaterial
        {
            get
            {
                if (_litMaterial != null) return _litMaterial;

                _litMaterial = TryCreateMaterial(
                    "LitMaterial",
                    "Universal Render Pipeline/2D/Sprite-Lit-Default",
                    "Sprites/Lit",
                    "Sprites/Default");

                // WHY: Shader.Find can fail early in game lifecycle.
                // Fallback: clone material from an existing SpriteRenderer in the scene.
                if (_litMaterial == null && !_litAttempted)
                {
                    _litAttempted = true;
                    _litMaterial = TryCloneFromScene("LitMaterial");
                }

                return _litMaterial;
            }
        }

        public static Material UnlitMaterial
        {
            get
            {
                if (_unlitMaterial != null) return _unlitMaterial;

                _unlitMaterial = TryCreateMaterial(
                    "UnlitMaterial",
                    "Universal Render Pipeline/2D/Sprite-Unlit-Default",
                    "Sprites/Default");

                if (_unlitMaterial == null && !_unlitAttempted)
                {
                    _unlitAttempted = true;
                    _unlitMaterial = TryCloneFromScene("UnlitMaterial");
                }

                return _unlitMaterial;
            }
        }

        public static Material TrailMaterial
        {
            get
            {
                if (_trailMaterial != null) return _trailMaterial;

                _trailMaterial = TryCreateMaterial(
                    "TrailMaterial",
                    "Sprites/Default",
                    "Legacy Shaders/Particles/Alpha Blended");

                if (_trailMaterial == null)
                    _trailMaterial = TryCloneFromScene("TrailMaterial");

                return _trailMaterial;
            }
        }

        /// <summary>
        /// Tries to create a Material from a list of shader names.
        /// Returns null if all shaders fail — never throws.
        /// </summary>
        private static Material TryCreateMaterial(string label, params string[] shaderNames)
        {
            for (int i = 0; i < shaderNames.Length; i++)
            {
                try
                {
                    Shader s = Shader.Find(shaderNames[i]);
                    if (s != null)
                    {
                        Material mat = new Material(s);
                        Plugin.Log.LogInfo($"[Visuals] {label} using: {shaderNames[i]}");
                        return mat;
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"[Visuals] {label} shader '{shaderNames[i]}' failed: {e.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Fallback: finds any SpriteRenderer in the scene and clones its material.
        /// Guarantees a working material if the game has any sprites loaded.
        /// </summary>
        private static Material TryCloneFromScene(string label)
        {
            try
            {
                SpriteRenderer sr = UnityEngine.Object.FindObjectOfType<SpriteRenderer>();
                if (sr != null && sr.sharedMaterial != null && sr.sharedMaterial.shader != null)
                {
                    Material cloned = new Material(sr.sharedMaterial.shader);
                    Plugin.Log.LogInfo($"[Visuals] {label} cloned from scene SpriteRenderer");
                    return cloned;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Visuals] {label} scene clone failed: {e.Message}");
            }

            Plugin.Log.LogWarning($"[Visuals] {label}: all init methods failed, will retry next call");
            return null;
        }

        /// <summary>
        /// Resets material cache. Forces re-initialization on next access.
        /// Call on scene reload when materials may become invalid.
        /// </summary>
        public static void ResetMaterials()
        {
            _litMaterial = null;
            _unlitMaterial = null;
            _trailMaterial = null;
            _litAttempted = false;
            _unlitAttempted = false;
            Plugin.Log.LogInfo("[Visuals] Materials reset");
        }

        // ── TRIANGLE SPRITES ──

        public enum TriangleShape { Acute, Right, Obtuse, Shard, Needle, Chunk }

        /// <summary>
        /// Procedurally generated shrapnel sprite. Cached forever.
        /// 32x32, FilterMode.Point. Batch SetPixels for performance.
        /// </summary>
        public static Sprite GetTriangleSprite(TriangleShape shape)
        {
            string key = shape.ToString();
            if (_spriteCache.TryGetValue(key, out Sprite cached))
                return cached;

            const int size = 32;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] pixels = new Color[size * size];
            Vector2[] verts = GetShapeVertices(shape);

            Vector2[] scaled = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                scaled[i] = verts[i] * size;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), scaled))
                        pixels[y * size + x] = Color.white;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
            sprite.name = $"Shrapnel_{shape}";
            _spriteCache[key] = sprite;
            return sprite;
        }

        private static Vector2[] GetShapeVertices(TriangleShape shape)
        {
            switch (shape)
            {
                case TriangleShape.Acute:
                    return new[] {
                        new Vector2(0.45f, 1f),   new Vector2(0.7f, 0.6f),
                        new Vector2(0.9f, 0.15f),  new Vector2(0.1f, 0f),
                        new Vector2(0.2f, 0.4f)
                    };
                case TriangleShape.Right:
                    return new[] {
                        new Vector2(0f, 0.95f),    new Vector2(0.15f, 0.5f),
                        new Vector2(0f, 0.05f),    new Vector2(0.75f, 0f),
                        new Vector2(0.6f, 0.3f)
                    };
                case TriangleShape.Obtuse:
                    return new[] {
                        new Vector2(0.25f, 0.85f), new Vector2(0.55f, 0.7f),
                        new Vector2(1f, 0.1f),     new Vector2(0.6f, 0f),
                        new Vector2(0f, 0.05f),    new Vector2(0.1f, 0.5f)
                    };
                case TriangleShape.Shard:
                    return new[] {
                        new Vector2(0.35f, 1f),    new Vector2(0.55f, 0.65f),
                        new Vector2(0.85f, 0.05f), new Vector2(0.4f, 0.15f),
                        new Vector2(0f, 0.2f),     new Vector2(0.15f, 0.55f)
                    };
                case TriangleShape.Needle:
                    return new[] {
                        new Vector2(0.5f, 1f),     new Vector2(0.6f, 0.5f),
                        new Vector2(0.55f, 0f),    new Vector2(0.4f, 0.3f),
                        new Vector2(0.45f, 0.7f)
                    };
                case TriangleShape.Chunk:
                    return new[] {
                        new Vector2(0.1f, 0.9f),   new Vector2(0.5f, 0.95f),
                        new Vector2(0.9f, 0.7f),   new Vector2(0.95f, 0.15f),
                        new Vector2(0.4f, 0f),     new Vector2(0f, 0.1f),
                        new Vector2(0.15f, 0.5f)
                    };
                default:
                    return new[] {
                        new Vector2(0.5f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f)
                    };
            }
        }

        private static bool PointInPolygon(Vector2 p, Vector2[] polygon)
        {
            bool inside = false;
            int j = polygon.Length - 1;
            for (int i = 0; i < polygon.Length; j = i++)
            {
                if ((polygon[i].y > p.y) != (polygon[j].y > p.y) &&
                    p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y)
                           / (polygon[j].y - polygon[i].y) + polygon[i].x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        // ── COLORS ──

        /// <summary>Cold color (Heat = 0) by material type.</summary>
        public static Color GetColdColor(ShrapnelProjectile.ShrapnelType type)
        {
            switch (type)
            {
                case ShrapnelProjectile.ShrapnelType.Metal: return new Color(0.30f, 0.30f, 0.35f);
                case ShrapnelProjectile.ShrapnelType.HeavyMetal: return new Color(0.15f, 0.15f, 0.20f);
                case ShrapnelProjectile.ShrapnelType.Stone: return new Color(0.50f, 0.45f, 0.40f);
                case ShrapnelProjectile.ShrapnelType.Wood: return new Color(0.55f, 0.35f, 0.15f);
                case ShrapnelProjectile.ShrapnelType.Electronic: return new Color(0.10f, 0.60f, 0.30f);
                default: return Color.gray;
            }
        }

        /// <summary>Hot color (Heat = 1) - glowing orange.</summary>
        public static Color GetHotColor() => new Color(1f, 0.55f, 0.1f);
    }

    /// <summary>
    /// Weight categories for shrapnel fragments.
    /// </summary>
    public enum ShrapnelWeight { Hot, Medium, Heavy, Massive }
}