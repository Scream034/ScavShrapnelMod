using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Helpers
{
    //  PARAMETER STRUCTS — at namespace level for global access

    public readonly struct VisualParticleParams
    {
        public readonly float Scale;
        public readonly Color Color;
        public readonly int SortingOrder;
        public readonly ShrapnelVisuals.TriangleShape Shape;

        public VisualParticleParams(float scale, Color color, int sortingOrder,
            ShrapnelVisuals.TriangleShape shape = ShrapnelVisuals.TriangleShape.Chunk)
        { Scale = scale; Color = color; SortingOrder = sortingOrder; Shape = shape; }
    }

    /// <summary>
    /// Parameters for AshParticle physics initialization.
    /// Immutable struct for clean parameter passing.
    /// All factory methods preserve original parameter names for
    /// backward compatibility with named-argument call sites.
    /// </summary>
    public readonly struct AshPhysicsParams
    {
        public readonly Vector2 Velocity;
        public readonly float Lifetime;
        public readonly float Gravity;
        public readonly float Drag;
        public readonly float TurbulenceStrength;
        public readonly float TurbulenceScale;
        public readonly Vector2 Wind;
        public readonly float ThermalLift;

        public AshPhysicsParams(Vector2 velocity, float lifetime,
            float gravity = 1.5f, float drag = 0.5f,
            float turbulenceStrength = 0.5f, float turbulenceScale = 2f,
            Vector2 wind = default, float thermalLift = 0f)
        {
            Velocity = velocity;
            Lifetime = lifetime;
            Gravity = gravity;
            Drag = drag;
            TurbulenceStrength = turbulenceStrength;
            TurbulenceScale = turbulenceScale;
            Wind = wind;
            ThermalLift = thermalLift;
        }

        /// <summary>Creates params for ground chunks: low gravity, moderate drag.</summary>
        public static AshPhysicsParams Chunk(Vector2 velocity, float lifetime, System.Random rng)
        {
            return new AshPhysicsParams(velocity, lifetime,
                gravity: 0.3f,
                drag: 0.4f,
                turbulenceStrength: 0.3f,
                turbulenceScale: 1.5f,
                wind: new Vector2(rng.Range(-0.1f, 0.1f), 0f),
                thermalLift: 0f);
        }

        /// <summary>Creates params for dust: very low gravity, high turbulence, lingers.</summary>
        public static AshPhysicsParams Dust(Vector2 velocity, float lifetime, System.Random rng)
        {
            return new AshPhysicsParams(velocity, lifetime,
                gravity: 0.15f,
                drag: 0.7f,
                turbulenceStrength: 0.9f,
                turbulenceScale: 2.0f,
                wind: new Vector2(rng.Range(-0.15f, 0.15f), 0f),
                thermalLift: 0.1f);
        }

        /// <summary>Creates params for streaks: fast, low drag, minimal turbulence.</summary>
        public static AshPhysicsParams Streak(Vector2 velocity, float lifetime)
        {
            return new AshPhysicsParams(velocity, lifetime,
                gravity: 0.8f,
                drag: 0.2f,
                turbulenceStrength: 0.15f,
                turbulenceScale: 1.0f,
                wind: Vector2.zero,
                thermalLift: 0f);
        }

        /// <summary>Creates params for desert dust: very low gravity, long-lasting.</summary>
        public static AshPhysicsParams DesertDust(Vector2 velocity, float lifetime, System.Random rng)
        {
            return new AshPhysicsParams(velocity, lifetime,
                gravity: 0.08f,
                drag: 0.6f,
                turbulenceStrength: 0.8f,
                turbulenceScale: 1.5f,
                wind: new Vector2(rng.Range(-0.3f, 0.3f), 0f),
                thermalLift: 0.3f);
        }

        /// <summary>Creates params for cold steam: negative gravity (rises), high turbulence.</summary>
        public static AshPhysicsParams ColdSteam(Vector2 velocity, float lifetime, System.Random rng)
        {
            return new AshPhysicsParams(velocity, lifetime,
                gravity: -0.15f,
                drag: 0.5f,
                turbulenceStrength: 1.0f,
                turbulenceScale: 2.5f,
                wind: new Vector2(rng.Range(-0.2f, 0.2f), 0.1f),
                thermalLift: 0.5f);
        }

        /// <summary>Creates params for standard ash with automatic derivation from gravity.</summary>
        public static AshPhysicsParams Ash(Vector2 velocity, float lifetime,
            float gravity, System.Random rng)
        {
            float normalizedGravity = Mathf.Clamp01(Mathf.Abs(gravity) / 2f);
            return new AshPhysicsParams(velocity, lifetime,
                gravity: gravity,
                drag: Mathf.Lerp(0.7f, 0.3f, normalizedGravity),
                turbulenceStrength: Mathf.Lerp(0.9f, 0.3f, normalizedGravity),
                turbulenceScale: 2f,
                wind: new Vector2(rng.Range(-0.2f, 0.2f), 0f),
                thermalLift: rng.Range(0f, 0.3f));
        }

        /// <summary>Creates params for smoke: negative gravity (rises), high drag.</summary>
        public static AshPhysicsParams Smoke(Vector2 velocity, float lifetime,
            float gravity, float drag, float turbulence, Vector2 wind,
            float thermalLift)
        {
            return new AshPhysicsParams(velocity, lifetime,
                gravity: gravity,
                drag: drag,
                turbulenceStrength: turbulence,
                turbulenceScale: 2f,
                wind: wind,
                thermalLift: thermalLift);
        }

        /// <summary>Creates params for embers: moderate gravity, low drag.</summary>
        public static AshPhysicsParams Ember(Vector2 velocity, float lifetime,
            float gravity, float drag, float turbulence, Vector2 wind,
            float thermalLift)
        {
            return new AshPhysicsParams(velocity, lifetime,
                gravity: gravity,
                drag: drag,
                turbulenceStrength: turbulence,
                turbulenceScale: 1f,
                wind: wind,
                thermalLift: thermalLift);
        }
    }

    public readonly struct SparkParams
    {
        public readonly Vector2 Direction;
        public readonly float Speed;
        public readonly float Lifetime;
        public SparkParams(Vector2 dir, float speed, float life)
        { Direction = dir; Speed = speed; Lifetime = life; }
    }

    public readonly struct EmissionParams
    {
        public readonly Color Color;
        public readonly bool Enabled;
        public EmissionParams(Color color) { Color = color; Enabled = true; }
        public static readonly EmissionParams None = default;
    }

    //  PARTICLE HELPER — routes to ParticlePool or fallback GO

    public static class ParticleHelper
    {
        private static MaterialPropertyBlock _mpb;
        private static int _emissionId = -1;
        private static int EmissionColorId =>
            _emissionId == -1 ? (_emissionId = Shader.PropertyToID("_EmissionColor")) : _emissionId;
        private static MaterialPropertyBlock MPB => _mpb ?? (_mpb = new MaterialPropertyBlock());

        /// <summary>Scale factor: sprite polygon = soft circle particle compensation.</summary>
        private const float SizeScale = 1.5f;

        //  LIT (DebrisPool: alpha blend) 

        public static GameObject SpawnLit(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, float startDelay = 0f)
        {
            // WHY: Just-In-Time initialization. This is more robust than relying on
            //      Unity lifecycle hooks (Awake, Start) which can have unpredictable
            //      timing in a heavily modded environment.
            if (!ParticlePoolManager.Initialized)
            {
                ParticlePoolManager.Initialize();
            }

            if (ParticlePoolManager.Initialized)
            {
                float size = visual.Scale * SizeScale;
                if (startDelay > 0f)
                    ParticlePoolManager.Debris.EmitDelayed(pos, physics.Velocity,
                        size, physics.Lifetime, visual.Color, startDelay, null);
                else
                    ParticlePoolManager.Debris.Emit(pos, physics.Velocity,
                        size, physics.Lifetime, visual.Color);
                return null;
            }
            return SpawnFallbackAsh(name, pos, visual, physics, perlinSeed, startDelay, true);
        }

        public static GameObject SpawnLit(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, in EmissionParams emission, float startDelay = 0f)
        {
            return SpawnLit(name, pos, visual, physics, perlinSeed, startDelay);
        }

        //  UNLIT (GlowPool: additive) 

        public static GameObject SpawnUnlit(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, float startDelay = 0f)
        {
            if (ParticlePoolManager.Initialized)
            {
                float size = visual.Scale * SizeScale;
                if (startDelay > 0f)
                    ParticlePoolManager.Glow.EmitDelayed(pos, physics.Velocity,
                        size, physics.Lifetime, visual.Color, startDelay, null);
                else
                    ParticlePoolManager.Glow.Emit(pos, physics.Velocity,
                        size, physics.Lifetime, visual.Color);
                return null;
            }
            return SpawnFallbackAsh(name, pos, visual, physics, perlinSeed, startDelay, false);
        }

        public static GameObject SpawnUnlit(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, in EmissionParams emission, float startDelay = 0f)
        {
            return SpawnUnlit(name, pos, visual, physics, perlinSeed, startDelay);
        }

        //  SPARKS (SparkPool: additive + stretch) 

        public static GameObject SpawnSparkLit(string name, Vector2 pos,
            in VisualParticleParams visual, in SparkParams spark,
            in EmissionParams emission = default)
        {
            return EmitSpark(pos, visual, spark);
        }

        public static GameObject SpawnSparkUnlit(string name, Vector2 pos,
            in VisualParticleParams visual, in SparkParams spark,
            in EmissionParams emission = default)
        {
            return EmitSpark(pos, visual, spark);
        }

        private static GameObject EmitSpark(Vector2 pos,
            in VisualParticleParams visual, in SparkParams spark)
        {
            if (ParticlePoolManager.Initialized)
            {
                float size = visual.Scale * SizeScale;
                Vector2 vel = spark.Direction * spark.Speed;
                ParticlePoolManager.Spark.Emit(pos, vel, size, spark.Lifetime, visual.Color);
                return null;
            }
            return SpawnFallbackSpark(pos, visual, spark);
        }

        //  LEGACY API 

        public static GameObject SpawnAshParticle(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics, float seed)
            => SpawnLit(name, pos, visual, physics, seed);

        public static GameObject SpawnAshParticle(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float seed, float delay)
            => SpawnLit(name, pos, visual, physics, seed, delay);

        public static GameObject SpawnAshParticle(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float seed, float delay, Material overrideMaterial)
            => SpawnLit(name, pos, visual, physics, seed, delay);

        public static void SpawnBurst(string prefix, Vector2 center, int count,
            float radius,
            System.Func<int, System.Random, VisualParticleParams> vf,
            System.Func<int, System.Random, AshPhysicsParams> pf,
            System.Random rng) => SpawnBurst(prefix, center, count, radius, vf, pf, rng, false);

        public static void SpawnBurst(string prefix, Vector2 center, int count,
            float radius,
            System.Func<int, System.Random, VisualParticleParams> vf,
            System.Func<int, System.Random, AshPhysicsParams> pf,
            System.Random rng, bool useLit)
        {
            for (int i = 0; i < count; i++)
            {
                Vector2 pos = center + rng.InsideUnitCircle() * radius;
                var vis = vf(i, rng); var phy = pf(i, rng);
                float seed = rng.Range(0f, 100f);
                if (useLit) SpawnLit(prefix, pos, vis, phy, seed);
                else SpawnUnlit(prefix, pos, vis, phy, seed);
            }
        }

        //  FALLBACKS (GameObject-based if pools not ready) 

        private static GameObject SpawnFallbackAsh(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, float startDelay, bool useLit)
        {
            Plugin.Log.LogWarning($"[ParticleHelper] ParticlePoolManager not initialized. Spawning fallback for {name}");

            Material mat = useLit
                ? (ShrapnelVisuals.LitMaterial ?? ShrapnelVisuals.UnlitMaterial)
                : ShrapnelVisuals.UnlitMaterial;
            if (mat == null) return null;

            GameObject obj = new GameObject(name);
            obj.transform.position = pos;
            obj.transform.localScale = Vector3.one * visual.Scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(visual.Shape);
            sr.sharedMaterial = mat;
            sr.sortingOrder = visual.SortingOrder;
            sr.color = visual.Color;

            AshParticle ash = obj.AddComponent<AshParticle>();
            ash.InitializeFullDelayed(physics.Velocity, physics.Lifetime, visual.Color,
                physics.Gravity, physics.Drag, physics.TurbulenceStrength,
                physics.TurbulenceScale, physics.Wind, physics.ThermalLift,
                perlinSeed, startDelay);

            DebrisTracker.RegisterVisual(obj);
            return obj;
        }

        private static GameObject SpawnFallbackSpark(Vector2 pos,
            in VisualParticleParams visual, in SparkParams spark)
        {
            Material mat = ShrapnelVisuals.UnlitMaterial;
            if (mat == null) return null;

            GameObject obj = new GameObject("FallbackSpark");
            obj.transform.position = pos;
            obj.transform.localScale = Vector3.one * visual.Scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
            sr.sharedMaterial = mat;
            sr.sortingOrder = visual.SortingOrder;
            sr.color = visual.Color;

            VisualShrapnel vs = obj.AddComponent<VisualShrapnel>();
            vs.Initialize(spark.Direction, spark.Speed, spark.Lifetime);

            DebrisTracker.RegisterVisual(obj);
            return obj;
        }

        //  EMISSION (for physics shrapnel SpriteRenderers) 

        internal static void ApplyEmission(SpriteRenderer sr, Color emissionColor)
        {
            MPB.Clear();
            MPB.SetColor(EmissionColorId, emissionColor);
            sr.SetPropertyBlock(MPB);
        }

        internal static void ClearEmission(SpriteRenderer sr)
        {
            sr.SetPropertyBlock(null);
        }
    }
}