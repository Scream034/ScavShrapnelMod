using System;
using System.Collections.Generic;
using UnityEngine;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Static cache of sprites, materials, and colors for shrapnel.
    ///
    /// Materials:
    ///   LitMaterial / UnlitMaterial     — SpriteRenderer on physics shrapnel GameObjects.
    ///   AdditiveParticleMaterial         — ParticleSystemRenderer (sparks, embers, fire glow).
    ///   DebrisParticleMaterial           — ParticleSystemRenderer (dirt, dust, smoke).
    ///   TrailMaterial                    — TrailRenderer on physics shrapnel.
    ///
    /// CRITICAL: ParticleSystemRenderer is INCOMPATIBLE with sprite shaders.
    /// Sprite-Lit/Unlit-Default read texture from SpriteRenderer.sprite, not _MainTex.
    /// Particle materials MUST use particle-compatible shaders.
    ///
    /// SHARED VISUAL CONSTANTS:
    ///   All visual tuning values used by both ShrapnelProjectile (server) and
    ///   ClientMirrorShrapnel (client) live here as a single source of truth.
    ///   Change once — both peers update automatically.
    /// </summary>
    public static class ShrapnelVisuals
    {
        //  SHARED VISUAL CONSTANTS
        //  Single source of truth for ShrapnelProjectile + ClientMirrorShrapnel.

        /// <summary>
        /// Heat cooling rate per second. Controls orange=cold color transition.
        /// Server projectile and client mirror both use this value so they
        /// cool at identical rates without any network traffic.
        /// </summary>
        public const float HeatCoolRate = 0.42f;

        /// <summary>
        /// Heat threshold above which Unlit material is used (glowing fragment).
        /// Below this threshold, Lit material is used (cold, reacts to scene lighting).
        /// </summary>
        public const float HotThreshold = 0.5f;

        /// <summary>
        /// Scale of the outline child GameObject relative to the parent shrapnel.
        /// 1.4× produces a visible 2-3px border at typical fragment sizes.
        /// </summary>
        public const float OutlineScale = 1.4f;

        /// <summary>Base alpha for the pulsating red outline.</summary>
        public const float OutlineAlphaBase = 0.35f;

        /// <summary>Amplitude of alpha pulsation (±0.15 around base).</summary>
        public const float OutlineAlphaAmplitude = 0.15f;

        /// <summary>Pulsation speed in radians per second (π rad/s ≈ 0.5 Hz).</summary>
        public const float OutlinePulseSpeed = 3.14f;

        /// <summary>Outline red channel.</summary>
        public const float OutlineR = 0.9f;

        /// <summary>Outline green channel.</summary>
        public const float OutlineG = 0.1f;

        /// <summary>Outline blue channel.</summary>
        public const float OutlineB = 0.05f;

        /// <summary>
        /// Returns outline color with pulsating alpha. Zero-alloc.
        /// </summary>
        /// <param name="time">Current time (use Time.time).</param>
        /// <param name="phaseOffset">Per-instance phase offset to desync siblings (radians).</param>
        public static Color GetOutlineColor(float time, float phaseOffset = 0f)
        {
            float alpha = OutlineAlphaBase
                + Mathf.Sin(time * OutlinePulseSpeed + phaseOffset) * OutlineAlphaAmplitude;
            return new Color(OutlineR, OutlineG, OutlineB, alpha);
        }

        /// <summary>Returns outline color at base alpha (no pulsation). Used at creation.</summary>
        public static Color GetOutlineBaseColor()
        {
            return new Color(OutlineR, OutlineG, OutlineB, OutlineAlphaBase);
        }

        //  SPRITE CACHE

        private static readonly Dictionary<string, Sprite> _spriteCache =
            new(6);

        //  SPRITE RENDERER MATERIALS (for physics shrapnel GameObjects)

        private static Material _litMaterial;
        private static Material _unlitMaterial;
        private static Material _trailMaterial;
        private static bool _litAttempted;
        private static bool _unlitAttempted;

        //  PARTICLE SYSTEM MATERIALS (for GPU-batched visual effects)

        private static Material _additiveParticleMaterial;
        private static Material _debrisParticleMaterial;
        private static Texture2D _particleTexture;

        //  PRE-WARM

        /// <summary>
        /// Pre-warms all materials and sprite cache. Call once after scene load.
        /// </summary>
        public static void PreWarm()
        {
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

        //  SPRITE RENDERER MATERIALS

        /// <summary>
        /// URP 2D Sprite-Lit material. Reacts to scene lighting.
        /// Used for cold/medium-heat shrapnel fragments.
        /// </summary>
        public static Material LitMaterial
        {
            get
            {
                // Unity AssetBundle unloads can destroy the underlying shader
                // without making the Material null — check shader explicitly.
                if (_litMaterial != null && _litMaterial.shader != null)
                    return _litMaterial;

                _litAttempted = false;
                _litMaterial = TryCreateMaterial("LitMaterial",
                    "Universal Render Pipeline/2D/Sprite-Lit-Default",
                    "Sprites/Lit",
                    "Sprites/Default");

                if (_litMaterial == null && !_litAttempted)
                {
                    _litAttempted = true;
                    _litMaterial = TryCloneFromScene("LitMaterial");
                }
                return _litMaterial;
            }
        }

        /// <summary>
        /// URP 2D Sprite-Unlit material. Self-illuminated appearance.
        /// Used for hot/glowing shrapnel fragments and outlines.
        /// </summary>
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

        /// <summary>
        /// Trail material for TrailRenderer on physics shrapnel.
        /// </summary>
        public static Material TrailMaterial
        {
            get
            {
                if (_trailMaterial != null && _trailMaterial.shader != null)
                    return _trailMaterial;

                _trailMaterial = TryCreateMaterial("TrailMaterial",
                    "Sprites/Default",
                    "Legacy Shaders/Particles/Alpha Blended")
                    ?? TryCloneFromScene("TrailMaterial");

                return _trailMaterial;
            }
        }

        //  PARTICLE SYSTEM MATERIALS

        /// <summary>
        /// Additive particle material for sparks, embers, fire glow.
        /// Blend: SrcAlpha + One. NOT compatible with SpriteRenderer.
        /// </summary>
        public static Material AdditiveParticleMaterial
        {
            get
            {
                if (_additiveParticleMaterial != null
                    && _additiveParticleMaterial.shader != null)
                    return _additiveParticleMaterial;

                _additiveParticleMaterial = CreateParticleMaterial("AdditiveParticleMat",
                    additive: true);
                return _additiveParticleMaterial;
            }
        }

        /// <summary>
        /// Alpha-blended particle material for dirt, dust, smoke, debris.
        /// Blend: SrcAlpha + OneMinusSrcAlpha. NOT compatible with SpriteRenderer.
        /// </summary>
        public static Material DebrisParticleMaterial
        {
            get
            {
                if (_debrisParticleMaterial != null
                    && _debrisParticleMaterial.shader != null)
                    return _debrisParticleMaterial;

                _debrisParticleMaterial = CreateParticleMaterial("DebrisParticleMat",
                    additive: false);
                return _debrisParticleMaterial;
            }
        }

        /// <summary>
        /// Shared procedural white circle texture (16×16, soft falloff).
        /// Used by particle materials.
        /// </summary>
        public static Texture2D ParticleTexture
        {
            get
            {
                if (_particleTexture != null) return _particleTexture;
                _particleTexture = CreateParticleTexture();
                return _particleTexture;
            }
        }

        //  MATERIAL CREATION HELPERS

        private static Material CreateParticleMaterial(string label, bool additive)
        {
            if (!additive)
            {
                Material litClone = TryCreateLitParticleMaterial(label);
                if (litClone != null) return litClone;
            }

            string[] shaderNames = additive
                ? new[]
                {
                    "Universal Render Pipeline/Particles/Unlit",
                    "Particles/Standard Unlit",
                    "Legacy Shaders/Particles/Additive"
                }
                : new[]
                {
                    "Universal Render Pipeline/Particles/Unlit",
                    "Particles/Standard Unlit",
                    "Legacy Shaders/Particles/Alpha Blended"
                };

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
                    Console.Error(
                        $"[Visuals] {label} shader '{shaderNames[i]}' failed: {e.Message}");
                }
            }

            if (mat == null)
            {
                Console.Error($"[Visuals] {label}: all shaders failed");
                return null;
            }

            Console.Log($"[Visuals] {label} using shader: {usedShader}");
            mat.mainTexture = ParticleTexture;

            if (additive)
            {
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            }
            else
            {
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend",
                    (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            }

            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = additive ? 3100 : 3000;
            return mat;
        }

        /// <summary>
        /// Creates lit particle material by using Sprite-Lit shader with explicit _MainTex.
        /// URP 2D Sprite-Lit-Default reads _MainTex, which ParticleSystemRenderer CAN set.
        /// </summary>
        private static Material TryCreateLitParticleMaterial(string label)
        {
            // Strategy 1: Sprite-Lit shader directly
            try
            {
                Shader litShader = Shader.Find(
                    "Universal Render Pipeline/2D/Sprite-Lit-Default");
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

            // Strategy 2: Clone from scene SpriteRenderer
            try
            {
                SpriteRenderer[] renderers =
                    UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    Material srcMat = renderers[i].sharedMaterial;
                    if (srcMat == null || srcMat.shader == null) continue;
                    if (!srcMat.shader.name.Contains("Lit")) continue;

                    Material clone = new(srcMat.shader);
                    clone.CopyPropertiesFromMaterial(srcMat);
                    clone.SetTexture("_MainTex", ParticleTexture);
                    clone.renderQueue = 3000;
                    Console.Log(
                        $"[Visuals] {label}: cloned '{srcMat.shader.name}' from scene");
                    return clone;
                }
            }
            catch (Exception e)
            {
                Console.Error($"[Visuals] {label} scene clone: {e.Message}");
            }

            Console.Log($"[Visuals] {label}: no lit shader found, falling back to unlit");
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
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) / center;
                    float alpha = Mathf.Clamp01(1f - dist);
                    alpha = alpha * alpha; // Soft falloff
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
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
                    Console.Error(
                        $"[Visuals] {label} shader '{shaderNames[i]}' failed: {e.Message}");
                }
            }
            return null;
        }

        private static Material TryCloneFromScene(string label)
        {
            try
            {
                SpriteRenderer sr = UnityEngine.Object.FindObjectOfType<SpriteRenderer>();
                if (sr != null && sr.sharedMaterial != null
                    && sr.sharedMaterial.shader != null)
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

            Console.Error($"[Visuals] {label}: all init methods failed, will retry next call");
            return null;
        }

        /// <summary>Resets all cached materials. Call on scene unload.</summary>
        public static void ResetMaterials()
        {
            _litMaterial               = null;
            _unlitMaterial             = null;
            _trailMaterial             = null;
            _additiveParticleMaterial  = null;
            _debrisParticleMaterial    = null;
            _particleTexture           = null;
            _litAttempted              = false;
            _unlitAttempted            = false;
            Console.Log("[Visuals] Materials reset");
        }

        //  TRIANGLE SPRITES

        /// <summary>Shape variants for procedurally generated triangle sprites.</summary>
        public enum TriangleShape
        {
            Acute, Right, Obtuse, Shard, Needle, Chunk
        }

        /// <summary>
        /// Returns (or creates) a cached sprite for the given shape.
        /// Sprites are procedurally rasterized at 32×32.
        /// </summary>
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

        private static Vector2[] GetShapeVertices(TriangleShape shape)
        {
            return shape switch
            {
                TriangleShape.Acute => new[]
                                    {
                        new Vector2(0.45f, 1f),  new Vector2(0.7f, 0.6f),
                        new Vector2(0.9f, 0.15f), new Vector2(0.1f, 0f),
                        new Vector2(0.2f, 0.4f)
                    },
                TriangleShape.Right => new[]
                    {
                        new Vector2(0f, 0.95f),  new Vector2(0.15f, 0.5f),
                        new Vector2(0f, 0.05f),  new Vector2(0.75f, 0f),
                        new Vector2(0.6f, 0.3f)
                    },
                TriangleShape.Obtuse => new[]
                    {
                        new Vector2(0.25f, 0.85f), new Vector2(0.55f, 0.7f),
                        new Vector2(1f, 0.1f),     new Vector2(0.6f, 0f),
                        new Vector2(0f, 0.05f),    new Vector2(0.1f, 0.5f)
                    },
                TriangleShape.Shard => new[]
                    {
                        new Vector2(0.35f, 1f),  new Vector2(0.55f, 0.65f),
                        new Vector2(0.85f, 0.05f), new Vector2(0.4f, 0.15f),
                        new Vector2(0f, 0.2f),   new Vector2(0.15f, 0.55f)
                    },
                TriangleShape.Needle => new[]
                    {
                        new Vector2(0.5f, 1f),  new Vector2(0.6f, 0.5f),
                        new Vector2(0.55f, 0f), new Vector2(0.4f, 0.3f),
                        new Vector2(0.45f, 0.7f)
                    },
                TriangleShape.Chunk => new[]
                    {
                        new Vector2(0.1f, 0.9f), new Vector2(0.5f, 0.95f),
                        new Vector2(0.9f, 0.7f), new Vector2(0.95f, 0.15f),
                        new Vector2(0.4f, 0f),   new Vector2(0f, 0.1f),
                        new Vector2(0.15f, 0.5f)
                    },
                _ => new[]
                    {
                        new Vector2(0.5f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f)
                    },
            };
        }

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

        //  COLORS

        /// <summary>Cold/resting color for the given shrapnel material type.</summary>
        public static Color GetColdColor(ShrapnelProjectile.ShrapnelType type)
        {
            return type switch
            {
                ShrapnelProjectile.ShrapnelType.Metal => new Color(0.30f, 0.30f, 0.35f),
                ShrapnelProjectile.ShrapnelType.HeavyMetal => new Color(0.15f, 0.15f, 0.20f),
                ShrapnelProjectile.ShrapnelType.Stone => new Color(0.50f, 0.45f, 0.40f),
                ShrapnelProjectile.ShrapnelType.Wood => new Color(0.55f, 0.35f, 0.15f),
                ShrapnelProjectile.ShrapnelType.Electronic => new Color(0.10f, 0.60f, 0.30f),
                _ => Color.gray,
            };
        }

        /// <summary>Hot/glowing color shared by all shrapnel types (orange).</summary>
        public static Color GetHotColor() => new(1f, 0.55f, 0.1f);
    }

    //  WEIGHT ENUM

    /// <summary>
    /// Weight/size categories for shrapnel fragments.
    /// Values are serialized — DO NOT reorder.
    /// Micro added at END to preserve existing save compatibility.
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