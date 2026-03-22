using System;
using System.Collections.Generic;
using UnityEngine;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Static cache of sprites, materials, and colors for shrapnel rendering.
    /// Single source of truth for all visual constants used by both
    /// server ShrapnelProjectile and client mirrors.
    /// </summary>
    public static class ShrapnelVisuals
    {
        #region Shared Visual Constants

        /// <summary>Heat cooling rate per second (orange→cold transition).</summary>
        public const float HeatCoolRate = 0.42f;

        /// <summary>Heat threshold above which Unlit material is used.</summary>
        public const float HotThreshold = 0.5f;

        /// <summary>Outline child scale relative to parent. Increased for visibility.</summary>
        public const float OutlineScale = 1.6f;

        /// <summary>Base outline alpha. Increased from 0.35 for ground visibility.</summary>
        public const float OutlineAlphaBase = 0.55f;

        /// <summary>Outline alpha pulsation amplitude.</summary>
        public const float OutlineAlphaAmplitude = 0.2f;

        /// <summary>Outline pulsation speed in radians per second.</summary>
        public const float OutlinePulseSpeed = 3.14f;

        /// <summary>Outline red channel.</summary>
        public const float OutlineR = 0.9f;

        /// <summary>Outline green channel.</summary>
        public const float OutlineG = 0.1f;

        /// <summary>Outline blue channel.</summary>
        public const float OutlineB = 0.05f;

        /// <summary>
        /// Per-weight debris lifetime multiplier. Light fragments vanish faster.
        /// Indexed by (int)ShrapnelWeight.
        /// </summary>
        public static readonly float[] DebrisLifetimeMultiplier = { 0.13f, 0.5f, 1f, 1f, 0.03f };
        // Hot=0.13 (≈2min), Medium=0.5, Heavy=1.0, Massive=1.0, Micro=0.03 (≈30s)

        #endregion

        #region Outline Colors

        /// <summary>
        /// Returns outline color with pulsating alpha.
        /// </summary>
        /// <param name="time">Current time (Time.time).</param>
        /// <param name="phaseOffset">Per-instance phase offset in radians.</param>
        /// <returns>Pulsating outline color.</returns>
        public static Color GetOutlineColor(float time, float phaseOffset = 0f)
        {
            float alpha = OutlineAlphaBase
                + Mathf.Sin(time * OutlinePulseSpeed + phaseOffset) * OutlineAlphaAmplitude;
            return new Color(OutlineR, OutlineG, OutlineB, alpha);
        }

        /// <summary>Returns outline color at base alpha (no pulsation).</summary>
        public static Color GetOutlineBaseColor()
        {
            return new Color(OutlineR, OutlineG, OutlineB, OutlineAlphaBase);
        }

        #endregion

        #region Sprite Cache

        private static readonly Dictionary<string, Sprite> _spriteCache = new(6);

        #endregion

        #region Materials

        private static Material _litMaterial;
        private static Material _unlitMaterial;
        private static Material _trailMaterial;
        private static Material _additiveParticleMaterial;
        private static Material _debrisParticleMaterial;
        private static Texture2D _particleTexture;
        private static bool _litAttempted;
        private static bool _unlitAttempted;
        private static bool _preWarmed;

        /// <summary>
        /// Pre-warms all materials and sprite cache. Guarded against multiple calls.
        /// </summary>
        public static void PreWarm()
        {
            if (_preWarmed) return;
            _preWarmed = true;

            try
            {
                var lit      = LitMaterial;
                var unlit    = UnlitMaterial;
                var trail    = TrailMaterial;
                var additive = AdditiveParticleMaterial;
                var debris   = DebrisParticleMaterial;

                for (int i = 0; i < 6; i++)
                {
                    try { GetTriangleSprite((TriangleShape)i); }
                    catch (Exception e)
                    {
                        Console.Error($"[Visuals] Sprite {i} failed: {e.Message}");
                    }
                }

                Console.Log($"[Visuals] PreWarm complete." +
                    $" Lit:{lit != null} Unlit:{unlit != null} Trail:{trail != null}" +
                    $" AdditivePart:{additive != null} DebrisPart:{debris != null}");
            }
            catch (Exception e)
            {
                Console.Error($"[Visuals] PreWarm exception: {e.Message}");
            }
        }

        /// <summary>URP 2D Sprite-Lit material.</summary>
        public static Material LitMaterial
        {
            get
            {
                if (_litMaterial != null && _litMaterial.shader != null)
                    return _litMaterial;

                _litAttempted = false;
                _litMaterial = TryCreateMaterial("LitMaterial",
                    "Universal Render Pipeline/2D/Sprite-Lit-Default",
                    "Sprites/Lit", "Sprites/Default");

                if (_litMaterial == null && !_litAttempted)
                {
                    _litAttempted = true;
                    _litMaterial = TryCloneFromScene("LitMaterial");
                }
                return _litMaterial;
            }
        }

        /// <summary>URP 2D Sprite-Unlit material (self-illuminated).</summary>
        public static Material UnlitMaterial
        {
            get
            {
                if (_unlitMaterial != null && _unlitMaterial.shader != null)
                    return _unlitMaterial;

                _unlitAttempted = false;
                _unlitMaterial = TryCreateMaterial("UnlitMaterial",
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

        /// <summary>Trail material for TrailRenderer.</summary>
        public static Material TrailMaterial
        {
            get
            {
                if (_trailMaterial != null && _trailMaterial.shader != null)
                    return _trailMaterial;

                _trailMaterial = TryCreateMaterial("TrailMaterial",
                    "Sprites/Default", "Legacy Shaders/Particles/Alpha Blended")
                    ?? TryCloneFromScene("TrailMaterial");

                return _trailMaterial;
            }
        }

        /// <summary>Additive particle material (sparks, embers).</summary>
        public static Material AdditiveParticleMaterial
        {
            get
            {
                if (_additiveParticleMaterial != null && _additiveParticleMaterial.shader != null)
                    return _additiveParticleMaterial;

                _additiveParticleMaterial = CreateParticleMaterial("AdditiveParticleMat", true);
                return _additiveParticleMaterial;
            }
        }

        /// <summary>Alpha-blended particle material (dirt, dust, smoke).</summary>
        public static Material DebrisParticleMaterial
        {
            get
            {
                if (_debrisParticleMaterial != null && _debrisParticleMaterial.shader != null)
                    return _debrisParticleMaterial;

                _debrisParticleMaterial = CreateParticleMaterial("DebrisParticleMat", false);
                return _debrisParticleMaterial;
            }
        }

        /// <summary>Procedural white circle texture (16×16, soft falloff).</summary>
        public static Texture2D ParticleTexture
        {
            get
            {
                if (_particleTexture != null) return _particleTexture;
                _particleTexture = CreateParticleTexture();
                return _particleTexture;
            }
        }

        /// <summary>Resets all cached materials on scene unload.</summary>
        public static void ResetMaterials()
        {
            _litMaterial              = null;
            _unlitMaterial            = null;
            _trailMaterial            = null;
            _additiveParticleMaterial = null;
            _debrisParticleMaterial   = null;
            _particleTexture          = null;
            _litAttempted             = false;
            _unlitAttempted           = false;
            _preWarmed                = false;
            Console.Log("[Visuals] Materials reset");
        }

        #endregion

        #region Material Creation

        private static Material CreateParticleMaterial(string label, bool additive)
        {
            if (!additive)
            {
                Material litClone = TryCreateLitParticleMaterial(label);
                if (litClone != null) return litClone;
            }

            string[] shaderNames = additive
                ? new[] { "Universal Render Pipeline/Particles/Unlit",
                          "Particles/Standard Unlit",
                          "Legacy Shaders/Particles/Additive" }
                : new[] { "Universal Render Pipeline/Particles/Unlit",
                          "Particles/Standard Unlit",
                          "Legacy Shaders/Particles/Alpha Blended" };

            Material mat = null;
            string usedShader = null;

            for (int i = 0; i < shaderNames.Length; i++)
            {
                try
                {
                    Shader s = Shader.Find(shaderNames[i]);
                    if (s != null && s.isSupported)
                    {
                        mat = new Material(s);
                        usedShader = shaderNames[i];
                        break;
                    }
                }
                catch (Exception e)
                {
                    Console.Error($"[Visuals] {label} shader '{shaderNames[i]}' failed: {e.Message}");
                }
            }

            if (mat == null)
            {
                Console.Error($"[Visuals] {label}: all shaders failed");
                return null;
            }

            Console.Log($"[Visuals] {label} using shader: {usedShader}");
            mat.mainTexture = ParticleTexture;

            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", additive
                ? (int)UnityEngine.Rendering.BlendMode.One
                : (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = additive ? 3100 : 3000;
            return mat;
        }

        private static Material TryCreateLitParticleMaterial(string label)
        {
            try
            {
                Shader litShader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
                if (litShader != null && litShader.isSupported)
                {
                    Material mat = new(litShader);
                    mat.SetTexture("_MainTex", ParticleTexture);
                    mat.renderQueue = 3000;
                    Console.Log($"[Visuals] {label}: using Sprite-Lit-Default for particles");
                    return mat;
                }
            }
            catch (Exception e)
            {
                Console.Error($"[Visuals] {label} Sprite-Lit direct: {e.Message}");
            }

            try
            {
                var renderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    Material srcMat = renderers[i].sharedMaterial;
                    if (srcMat == null || srcMat.shader == null) continue;
                    if (!srcMat.shader.name.Contains("Lit")) continue;

                    Material clone = new(srcMat.shader);
                    clone.CopyPropertiesFromMaterial(srcMat);
                    clone.SetTexture("_MainTex", ParticleTexture);
                    clone.renderQueue = 3000;
                    Console.Log($"[Visuals] {label}: cloned '{srcMat.shader.name}' from scene");
                    return clone;
                }
            }
            catch (Exception e)
            {
                Console.Error($"[Visuals] {label} scene clone: {e.Message}");
            }

            return null;
        }

        private static Texture2D CreateParticleTexture()
        {
            const int size = 16;
            Texture2D tex = new(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] pixels = new Color[size * size];
            float center = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float dist = Mathf.Sqrt(dx * dx + dy * dy) / center;
                float alpha = Mathf.Clamp01(1f - dist);
                alpha *= alpha;
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static Material TryCreateMaterial(string label, params string[] shaderNames)
        {
            for (int i = 0; i < shaderNames.Length; i++)
            {
                try
                {
                    Shader s = Shader.Find(shaderNames[i]);
                    if (s != null)
                    {
                        Material mat = new(s);
                        Console.Log($"[Visuals] {label} using: {shaderNames[i]}");
                        return mat;
                    }
                }
                catch (Exception e)
                {
                    Console.Error($"[Visuals] {label} shader '{shaderNames[i]}' failed: {e.Message}");
                }
            }
            return null;
        }

        private static Material TryCloneFromScene(string label)
        {
            try
            {
                SpriteRenderer sr = UnityEngine.Object.FindObjectOfType<SpriteRenderer>();
                if (sr != null && sr.sharedMaterial != null && sr.sharedMaterial.shader != null)
                {
                    Material cloned = new(sr.sharedMaterial.shader);
                    Console.Log($"[Visuals] {label} cloned from scene SpriteRenderer");
                    return cloned;
                }
            }
            catch (Exception e)
            {
                Console.Error($"[Visuals] {label} scene clone failed: {e.Message}");
            }

            Console.Error($"[Visuals] {label}: all init methods failed");
            return null;
        }

        #endregion

        #region Triangle Sprites

        /// <summary>Shape variants for procedural triangle sprites.</summary>
        public enum TriangleShape { Acute, Right, Obtuse, Shard, Needle, Chunk }

        /// <summary>
        /// Returns or creates a cached sprite for the given shape.
        /// </summary>
        /// <param name="shape">Triangle shape variant.</param>
        /// <returns>Cached sprite for the shape.</returns>
        public static Sprite GetTriangleSprite(TriangleShape shape)
        {
            string key = shape.ToString();
            if (_spriteCache.TryGetValue(key, out Sprite cached))
                return cached;

            const int size = 32;
            Texture2D tex = new(size, size, TextureFormat.RGBA32, false)
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
            for (int x = 0; x < size; x++)
                if (PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), scaled))
                    pixels[y * size + x] = Color.white;

            tex.SetPixels(pixels);
            tex.Apply();

            Sprite sprite = Sprite.Create(tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 32f);
            sprite.name = $"Shrapnel_{shape}";
            _spriteCache[key] = sprite;
            return sprite;
        }

        private static Vector2[] GetShapeVertices(TriangleShape shape) => shape switch
        {
            TriangleShape.Acute => new[] {
                new Vector2(0.45f, 1f), new Vector2(0.7f, 0.6f),
                new Vector2(0.9f, 0.15f), new Vector2(0.1f, 0f), new Vector2(0.2f, 0.4f) },
            TriangleShape.Right => new[] {
                new Vector2(0f, 0.95f), new Vector2(0.15f, 0.5f),
                new Vector2(0f, 0.05f), new Vector2(0.75f, 0f), new Vector2(0.6f, 0.3f) },
            TriangleShape.Obtuse => new[] {
                new Vector2(0.25f, 0.85f), new Vector2(0.55f, 0.7f),
                new Vector2(1f, 0.1f), new Vector2(0.6f, 0f),
                new Vector2(0f, 0.05f), new Vector2(0.1f, 0.5f) },
            TriangleShape.Shard => new[] {
                new Vector2(0.35f, 1f), new Vector2(0.55f, 0.65f),
                new Vector2(0.85f, 0.05f), new Vector2(0.4f, 0.15f),
                new Vector2(0f, 0.2f), new Vector2(0.15f, 0.55f) },
            TriangleShape.Needle => new[] {
                new Vector2(0.5f, 1f), new Vector2(0.6f, 0.5f),
                new Vector2(0.55f, 0f), new Vector2(0.4f, 0.3f), new Vector2(0.45f, 0.7f) },
            TriangleShape.Chunk => new[] {
                new Vector2(0.1f, 0.9f), new Vector2(0.5f, 0.95f),
                new Vector2(0.9f, 0.7f), new Vector2(0.95f, 0.15f),
                new Vector2(0.4f, 0f), new Vector2(0f, 0.1f), new Vector2(0.15f, 0.5f) },
            _ => new[] { new Vector2(0.5f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) },
        };

        private static bool PointInPolygon(Vector2 p, Vector2[] polygon)
        {
            bool inside = false;
            int j = polygon.Length - 1;
            for (int i = 0; i < polygon.Length; j = i++)
            {
                if ((polygon[i].y > p.y) != (polygon[j].y > p.y) &&
                    p.x < (polygon[j].x - polygon[i].x)
                        * (p.y - polygon[i].y)
                        / (polygon[j].y - polygon[i].y)
                        + polygon[i].x)
                    inside = !inside;
            }
            return inside;
        }

        #endregion

        #region Colors

        /// <summary>Cold/resting color by material type.</summary>
        public static Color GetColdColor(ShrapnelProjectile.ShrapnelType type) => type switch
        {
            ShrapnelProjectile.ShrapnelType.Metal => new Color(0.30f, 0.30f, 0.35f),
            ShrapnelProjectile.ShrapnelType.HeavyMetal => new Color(0.15f, 0.15f, 0.20f),
            ShrapnelProjectile.ShrapnelType.Stone => new Color(0.50f, 0.45f, 0.40f),
            ShrapnelProjectile.ShrapnelType.Wood => new Color(0.55f, 0.35f, 0.15f),
            ShrapnelProjectile.ShrapnelType.Electronic => new Color(0.10f, 0.60f, 0.30f),
            _ => Color.gray,
        };

        /// <summary>Hot/glowing color (orange).</summary>
        public static Color GetHotColor() => new(1f, 0.55f, 0.1f);

        #endregion
    }

    /// <summary>
    /// Weight/size categories for shrapnel fragments.
    /// Values serialized — DO NOT reorder.
    /// </summary>
    public enum ShrapnelWeight
    {
        Hot     = 0,
        Medium  = 1,
        Heavy   = 2,
        Massive = 3,
        Micro   = 4
    }
}