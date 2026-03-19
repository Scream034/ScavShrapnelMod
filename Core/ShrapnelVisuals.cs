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
    ///   LitMaterial / UnlitMaterial — for SpriteRenderers on physics shrapnel GameObjects.
    ///   AdditiveParticleMaterial — for ParticleSystemRenderer (sparks, embers, fire glow).
    ///   DebrisParticleMaterial — for ParticleSystemRenderer (dirt, dust, smoke, debris).
    ///   TrailMaterial — for TrailRenderer on physics shrapnel.
    ///
    /// CRITICAL: ParticleSystemRenderer is INCOMPATIBLE with sprite shaders
    /// (Sprite-Lit-Default, Sprite-Unlit-Default). Those shaders read texture from
    /// SpriteRenderer.sprite, not from material _MainTex. Particle materials MUST use
    /// particle-compatible shaders.
    /// </summary>
    public static class ShrapnelVisuals
    {
        private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();

        //  SPRITE RENDERER MATERIALS (for physics shrapnel GameObjects) 
        private static Material _litMaterial;
        private static Material _unlitMaterial;
        private static Material _trailMaterial;
        private static bool _litAttempted;
        private static bool _unlitAttempted;

        //  PARTICLE SYSTEM MATERIALS (for GPU-batched visual effects) 
        private static Material _additiveParticleMaterial;
        private static Material _debrisParticleMaterial;

        //  SHARED PARTICLE TEXTURE 
        private static Texture2D _particleTexture;

        /// <summary>
        /// Pre-warms all materials, particle materials, and sprite cache.
        /// </summary>
        public static void PreWarm()
        {
            try
            {
                var lit = LitMaterial;
                var unlit = UnlitMaterial;
                var trail = TrailMaterial;
                var additive = AdditiveParticleMaterial;
                var debris = DebrisParticleMaterial;

                for (int i = 0; i < 6; i++)
                {
                    try { GetTriangleSprite((TriangleShape)i); }
                    catch (Exception e)
                    {
                        Console.Error($"[Visuals] Sprite {i} failed: {e.Message}");
                    }
                }

                Console.Log($"[Visuals] PreWarm complete. Lit:{lit != null}" +
                    $" Unlit:{unlit != null} Trail:{trail != null}" +
                    $" AdditivePart:{additive != null} DebrisPart:{debris != null}");
            }
            catch (Exception e)
            {
                Console.Error($"[Visuals] PreWarm exception: {e.Message}");
            }
        }

        //  SPRITE RENDERER MATERIALS 

        public static Material LitMaterial
        {
            get
            {
                // WHY: Unity AssetBundle unloads can destroy the underlying shader without making the Material null.
                if (_litMaterial != null && _litMaterial.shader != null) return _litMaterial;

                _litAttempted = false; // Reset attempt flag to allow retry on corruption
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

        public static Material UnlitMaterial
        {
            get
            {
                if (_unlitMaterial != null && _unlitMaterial.shader != null) return _unlitMaterial;

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

        public static Material TrailMaterial
        {
            get
            {
                if (_trailMaterial != null && _trailMaterial.shader != null) return _trailMaterial;

                _trailMaterial = TryCreateMaterial("TrailMaterial",
                    "Sprites/Default",
                    "Legacy Shaders/Particles/Alpha Blended");
                if (_trailMaterial == null)
                    _trailMaterial = TryCloneFromScene("TrailMaterial");
                return _trailMaterial;
            }
        }

        //  PARTICLE SYSTEM MATERIALS 

        /// <summary>
        /// Additive particle material for sparks, embers, fire glow.
        /// Blend: SrcAlpha + One (additive glow).
        /// Uses particle-compatible shader, NOT sprite shaders.
        /// </summary>
        public static Material AdditiveParticleMaterial
        {
            get
            {
                if (_additiveParticleMaterial != null && _additiveParticleMaterial.shader != null) return _additiveParticleMaterial;

                _additiveParticleMaterial = CreateParticleMaterial(
                    "AdditiveParticleMat", true);
                return _additiveParticleMaterial;
            }
        }

        /// <summary>
        /// Alpha-blended particle material for dirt, dust, smoke, debris.
        /// Blend: SrcAlpha + OneMinusSrcAlpha (standard transparency, no glow).
        /// Uses particle-compatible shader, NOT sprite shaders.
        /// </summary>
        public static Material DebrisParticleMaterial
        {
            get
            {
                if (_debrisParticleMaterial != null && _debrisParticleMaterial.shader != null) return _debrisParticleMaterial;

                _debrisParticleMaterial = CreateParticleMaterial(
                    "DebrisParticleMat", false);
                return _debrisParticleMaterial;
            }
        }

        /// <summary>
        /// Shared white circle texture for particle systems.
        /// Procedurally generated, 16x16, soft circle with falloff.
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

        private static Material CreateParticleMaterial(string label, bool additive)
        {
            if (!additive)
            {
                // DEBRIS: Try to clone a working lit material from scene,
                // then override its texture. This gives us a material that
                // actually responds to URP 2D lighting AND works with 
                // ParticleSystemRenderer.
                Material litClone = TryCreateLitParticleMaterial(label);
                if (litClone != null) return litClone;
            }

            // Additive (sparks/glow) or lit fallback failed
            string[] shaderNames = additive
                ? new[] {
            "Universal Render Pipeline/Particles/Unlit",
            "Particles/Standard Unlit",
            "Legacy Shaders/Particles/Additive"
                  }
                : new[] {
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

            if (additive)
            {
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            }
            else
            {
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            }

            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = additive ? 3100 : 3000;

            return mat;
        }

        /// <summary>
        /// Creates a lit particle material by cloning a working Sprite-Lit material
        /// from the scene and replacing its texture with particle circle.
        /// 
        /// WHY: URP 2D Sprite-Lit-Default reads texture via _MainTex property,
        /// which ParticleSystemRenderer CAN set (unlike SpriteRenderer.sprite).
        /// The key insight: Sprite-Lit DOES read _MainTex — the sprite path 
        /// is just an optimization. Setting _MainTex explicitly works.
        /// </summary>
        private static Material TryCreateLitParticleMaterial(string label)
        {
            // Strategy 1: Find Sprite-Lit shader directly
            try
            {
                Shader litShader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
                if (litShader != null && litShader.isSupported)
                {
                    Material mat = new Material(litShader);
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
                SpriteRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    Material srcMat = renderers[i].sharedMaterial;
                    if (srcMat == null || srcMat.shader == null) continue;
                    if (!srcMat.shader.name.Contains("Lit")) continue;

                    Material clone = new Material(srcMat.shader);
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

            Console.Log($"[Visuals] {label}: no lit shader found, falling back to unlit");
            return null;
        }

        private static Texture2D CreateParticleTexture()
        {
            const int size = 16;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] pixels = new Color[size * size];
            float center = (size - 1) * 0.5f;
            float maxDist = center;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) / maxDist;
                    float alpha = Mathf.Clamp01(1f - dist);
                    alpha = alpha * alpha; // Soft falloff
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        //  EXISTING MATERIAL HELPERS 

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
                    Material cloned = new Material(sr.sharedMaterial.shader);
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

        public static void ResetMaterials()
        {
            _litMaterial = null;
            _unlitMaterial = null;
            _trailMaterial = null;
            _additiveParticleMaterial = null;
            _debrisParticleMaterial = null;
            _particleTexture = null;
            _litAttempted = false;
            _unlitAttempted = false;
            Console.Log("[Visuals] Materials reset");
        }

        //  TRIANGLE SPRITES (unchanged) 

        public enum TriangleShape { Acute, Right, Obtuse, Shard, Needle, Chunk }

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
                for (int x = 0; x < size; x++)
                    if (PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), scaled))
                        pixels[y * size + x] = Color.white;

            tex.SetPixels(pixels);
            tex.Apply();

            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 32f);
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
                        new Vector2(0.45f, 1f), new Vector2(0.7f, 0.6f),
                        new Vector2(0.9f, 0.15f), new Vector2(0.1f, 0f),
                        new Vector2(0.2f, 0.4f) };
                case TriangleShape.Right:
                    return new[] {
                        new Vector2(0f, 0.95f), new Vector2(0.15f, 0.5f),
                        new Vector2(0f, 0.05f), new Vector2(0.75f, 0f),
                        new Vector2(0.6f, 0.3f) };
                case TriangleShape.Obtuse:
                    return new[] {
                        new Vector2(0.25f, 0.85f), new Vector2(0.55f, 0.7f),
                        new Vector2(1f, 0.1f), new Vector2(0.6f, 0f),
                        new Vector2(0f, 0.05f), new Vector2(0.1f, 0.5f) };
                case TriangleShape.Shard:
                    return new[] {
                        new Vector2(0.35f, 1f), new Vector2(0.55f, 0.65f),
                        new Vector2(0.85f, 0.05f), new Vector2(0.4f, 0.15f),
                        new Vector2(0f, 0.2f), new Vector2(0.15f, 0.55f) };
                case TriangleShape.Needle:
                    return new[] {
                        new Vector2(0.5f, 1f), new Vector2(0.6f, 0.5f),
                        new Vector2(0.55f, 0f), new Vector2(0.4f, 0.3f),
                        new Vector2(0.45f, 0.7f) };
                case TriangleShape.Chunk:
                    return new[] {
                        new Vector2(0.1f, 0.9f), new Vector2(0.5f, 0.95f),
                        new Vector2(0.9f, 0.7f), new Vector2(0.95f, 0.15f),
                        new Vector2(0.4f, 0f), new Vector2(0f, 0.1f),
                        new Vector2(0.15f, 0.5f) };
                default:
                    return new[] {
                        new Vector2(0.5f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) };
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
                    inside = !inside;
            }
            return inside;
        }

        //  COLORS 

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

        public static Color GetHotColor() => new Color(1f, 0.55f, 0.1f);
    }

    /// <summary>
    /// Weight categories for shrapnel fragments.
    /// CRITICAL: Micro added at END to preserve existing serialized enum values.
    /// </summary>
    public enum ShrapnelWeight
    {
        Hot = 0,
        Medium = 1,
        Heavy = 2,
        Massive = 3,
        Micro = 4
    }
}