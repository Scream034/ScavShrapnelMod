using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Helpers
{
    /// <summary>
    /// Immutable parameter structure for particle visual appearance.
    /// Zero heap allocation — value type passed by ref.
    /// </summary>
    public readonly struct VisualParticleParams
    {
        public readonly float Scale;
        public readonly Color Color;
        public readonly int SortingOrder;
        public readonly ShrapnelVisuals.TriangleShape Shape;

        public VisualParticleParams(float scale, Color color, int sortingOrder,
            ShrapnelVisuals.TriangleShape shape = ShrapnelVisuals.TriangleShape.Chunk)
        {
            Scale = scale;
            Color = color;
            SortingOrder = sortingOrder;
            Shape = shape;
        }
    }

    /// <summary>
    /// Immutable parameter structure for AshParticle physics simulation.
    /// Zero heap allocation — value type passed by ref.
    ///
    /// Physics model implemented in AshParticlePooled.Update():
    ///   • Gravity: constant downward acceleration (negative = rises)
    ///   • Drag: quadratic air resistance F ∝ v²
    ///   • Turbulence: Perlin noise displacement (frame-staggered every 3 frames)
    ///   • Wind: constant drift vector, influence grows with age²
    ///   • ThermalLift: upward force, decays with lifetime t²
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

        /// <summary>Ground chunks: low gravity (0.3), moderate drag (0.4).</summary>
        public static AshPhysicsParams Chunk(Vector2 velocity, float lifetime, System.Random rng)
            => new AshPhysicsParams(velocity, lifetime, 0.3f, 0.4f, 0.3f, 1.5f,
                new Vector2(rng.Range(-0.1f, 0.1f), 0f), 0f);

        /// <summary>Dust cloud: very low gravity (0.15), high drag (0.7), strong turbulence.</summary>
        public static AshPhysicsParams Dust(Vector2 velocity, float lifetime, System.Random rng)
            => new AshPhysicsParams(velocity, lifetime, 0.15f, 0.7f, 0.9f, 2.0f,
                new Vector2(rng.Range(-0.15f, 0.15f), 0f), 0.1f);

        /// <summary>Fast streaks: higher gravity (0.8), minimal drag (0.2), little turbulence.</summary>
        public static AshPhysicsParams Streak(Vector2 velocity, float lifetime)
            => new AshPhysicsParams(velocity, lifetime, 0.8f, 0.2f, 0.15f, 1.0f,
                Vector2.zero, 0f);

        /// <summary>Desert dust: ultra-low gravity (0.08), long-lived, windy drift.</summary>
        public static AshPhysicsParams DesertDust(Vector2 velocity, float lifetime, System.Random rng)
            => new AshPhysicsParams(velocity, lifetime, 0.08f, 0.6f, 0.8f, 1.5f,
                new Vector2(rng.Range(-0.3f, 0.3f), 0f), 0.3f);

        /// <summary>Cold steam: rises (-0.15 gravity), high turbulence, slight vertical wind.</summary>
        public static AshPhysicsParams ColdSteam(Vector2 velocity, float lifetime, System.Random rng)
            => new AshPhysicsParams(velocity, lifetime, -0.15f, 0.5f, 1.0f, 2.5f,
                new Vector2(rng.Range(-0.2f, 0.2f), 0.1f), 0.5f);

        /// <summary>Standard ash: auto-derives drag/turbulence from gravity magnitude.</summary>
        public static AshPhysicsParams Ash(Vector2 velocity, float lifetime,
            float gravity, System.Random rng)
        {
            float normalizedGravity = Mathf.Clamp01(Mathf.Abs(gravity) / 2f);
            return new AshPhysicsParams(velocity, lifetime, gravity,
                Mathf.Lerp(0.7f, 0.3f, normalizedGravity),
                Mathf.Lerp(0.9f, 0.3f, normalizedGravity), 2f,
                new Vector2(rng.Range(-0.2f, 0.2f), 0f), rng.Range(0f, 0.3f));
        }

        /// <summary>Smoke: configurable rising behavior.</summary>
        public static AshPhysicsParams Smoke(Vector2 velocity, float lifetime,
            float gravity, float drag, float turbulence, Vector2 wind, float thermalLift)
            => new AshPhysicsParams(velocity, lifetime, gravity, drag, turbulence, 2f,
                wind, thermalLift);

        /// <summary>Embers: configurable glowing debris.</summary>
        public static AshPhysicsParams Ember(Vector2 velocity, float lifetime,
            float gravity, float drag, float turbulence, Vector2 wind, float thermalLift)
            => new AshPhysicsParams(velocity, lifetime, gravity, drag, turbulence, 1f,
                wind, thermalLift);
    }

    /// <summary>
    /// Immutable parameter structure for spark emission.
    /// Used by GPU-batched ParticleSystem spark pool.
    /// </summary>
    public readonly struct SparkParams
    {
        public readonly Vector2 Direction;
        public readonly float Speed;
        public readonly float Lifetime;

        public SparkParams(Vector2 dir, float speed, float life)
        {
            Direction = dir;
            Speed = speed;
            Lifetime = life;
        }
    }

    /// <summary>
    /// Emission hint structure retained for call-site compatibility.
    /// No longer affects pooled particle rendering — material color is used instead.
    /// Call sites may still pass this without breaking.
    /// </summary>
    public readonly struct EmissionParams
    {
        public readonly Color Color;
        public readonly bool Enabled;
        public EmissionParams(Color color) { Color = color; Enabled = true; }
        public static readonly EmissionParams None = default;
    }

    /// <summary>
    /// Central particle spawning router. Routes to appropriate pool system:
    ///
    ///   • SpawnLit/SpawnUnlit → AshParticlePoolManager (complex physics, zero-GC)
    ///     Material: Sprite-Lit/Unlit-Default, reacts to URP 2D lighting.
    ///
    ///   • SpawnSpark* → ParticlePoolManager.Spark (GPU-batched via Unity ParticleSystem)
    ///     Material: Particles/Unlit additive, stretch billboard for trails.
    ///
    /// BACKWARD COMPATIBILITY:
    ///   All old method signatures with (string name, ...) and EmissionParams still compile.
    ///   These parameters are silently ignored — they existed only for legacy GameObject paths.
    ///   This avoids breaking 60+ call sites across the codebase.
    ///
    /// PERFORMANCE GUARANTEES:
    ///   • Zero GC allocations in steady state (object pooling)
    ///   • O(1) amortized emit operations
    ///   • Lazy initialization on first use via EnsureReady()
    /// </summary>
    public static class ParticleHelper
    {
        // ──────────────────────────────────────────────────────
        //  MATERIAL PROPERTY BLOCK CACHE (for hot shrapnel glow)
        // ──────────────────────────────────────────────────────

        private static MaterialPropertyBlock _mpb;
        private static int _emissionId = -1;

        private static int EmissionColorId =>
            _emissionId == -1
                ? (_emissionId = Shader.PropertyToID("_EmissionColor"))
                : _emissionId;

        private static MaterialPropertyBlock MPB =>
            _mpb ?? (_mpb = new MaterialPropertyBlock());

        // ──────────────────────────────────────────────────────
        //  SPARK RENDERING CONFIGURATIONS
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Minimum visible spark size in world units.
        /// Below this threshold particles render as sub-pixel and become invisible.
        /// </summary>
        private const float MinSparkSize = 0.08f;

        /// <summary>
        /// Size multiplier for ParticleSystem sparks.
        /// Original Scale values target SpriteRenderer pixel sizes (0.01–0.12 range).
        /// ParticleSystem Stretch mode uses world units for width, requiring larger base sizes.
        /// Formula: finalSize = max(scale × multiplier, minSize)
        /// </summary>
        private const float SparkSizeMultiplier = 3f;

        // ──────────────────────────────────────────────────────
        //  LIT PARTICLES (debris, dust, smoke, ash, steam)
        //  Material: Sprite-Lit-Default — reacts to URP 2D lighting
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Core lit emitter. Initializes pools lazily via EnsureReady().
        /// All other lit overloads forward to this implementation.
        /// </summary>
        public static GameObject SpawnLit(Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, float startDelay = 0f)
        {
            if (!AshParticlePoolManager.EnsureReady()) return null;

            EmitPooled(pos, visual, physics, perlinSeed, startDelay,
                AshParticlePoolManager.Lit);
            return null;
        }

        /// <summary>Backward compat: accepts unused string name parameter.</summary>
        public static GameObject SpawnLit(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, float startDelay = 0f)
            => SpawnLit(pos, visual, physics, perlinSeed, startDelay);

        /// <summary>Backward compat: accepts unused name and EmissionParams.</summary>
        public static GameObject SpawnLit(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, in EmissionParams emission, float startDelay = 0f)
            => SpawnLit(pos, visual, physics, perlinSeed, startDelay);

        // ──────────────────────────────────────────────────────
        //  UNLIT PARTICLES (embers, fire, glow, burning chunks)
        //  Material: Sprite-Unlit-Default — self-illuminated appearance
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Core unlit emitter. Initializes pools lazily via EnsureReady().
        /// All other unlit overloads forward to this implementation.
        /// </summary>
        public static GameObject SpawnUnlit(Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, float startDelay = 0f)
        {
            if (!AshParticlePoolManager.EnsureReady()) return null;

            EmitPooled(pos, visual, physics, perlinSeed, startDelay,
                AshParticlePoolManager.Unlit);
            return null;
        }

        /// <summary>Backward compat: accepts unused string name parameter.</summary>
        public static GameObject SpawnUnlit(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, float startDelay = 0f)
            => SpawnUnlit(pos, visual, physics, perlinSeed, startDelay);

        /// <summary>Backward compat: accepts unused name and EmissionParams.</summary>
        public static GameObject SpawnUnlit(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, in EmissionParams emission, float startDelay = 0f)
            => SpawnUnlit(pos, visual, physics, perlinSeed, startDelay);

        // ──────────────────────────────────────────────────────
        //  SPARKS (GPU-batched via Unity ParticleSystem)
        //  RenderMode.Stretch for trail-like appearance
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Core spark emitter. Scales up small sprite sizes to world-unit visibility.
        /// All other spark overloads forward to this implementation.
        /// </summary>
        public static GameObject SpawnSpark(Vector2 pos,
            in VisualParticleParams visual, in SparkParams spark)
        {
            if (!ParticlePoolManager.EnsureReady() || ParticlePoolManager.Spark == null)
                return null;

            Vector2 velocity = spark.Direction * spark.Speed;
            float size = Mathf.Max(visual.Scale * SparkSizeMultiplier, MinSparkSize);
            ParticlePoolManager.Spark.Emit(pos, velocity, size, spark.Lifetime, visual.Color);

            return null;
        }

        /// <summary>Backward compat: SpawnSparkUnlit with name + EmissionParams.</summary>
        public static GameObject SpawnSparkUnlit(string name, Vector2 pos,
            in VisualParticleParams visual, in SparkParams spark,
            in EmissionParams emission = default)
            => SpawnSpark(pos, visual, spark);

        /// <summary>Backward compat: SpawnSparkLit with name + EmissionParams.</summary>
        public static GameObject SpawnSparkLit(string name, Vector2 pos,
            in VisualParticleParams visual, in SparkParams spark,
            in EmissionParams emission = default)
            => SpawnSpark(pos, visual, spark);

        // ──────────────────────────────────────────────────────
        //  LEGACY API — renamed method forwards
        // ──────────────────────────────────────────────────────

        public static GameObject SpawnAshParticle(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics, float seed)
            => SpawnLit(pos, visual, physics, seed);

        public static GameObject SpawnAshParticle(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float seed, float delay)
            => SpawnLit(pos, visual, physics, seed, delay);

        public static GameObject SpawnAshParticle(string name, Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float seed, float delay, Material overrideMaterial)
            => SpawnLit(pos, visual, physics, seed, delay);

        // ──────────────────────────────────────────────────────
        //  BURST EMISSION (circle of particles around center)
        // ──────────────────────────────────────────────────────

        /// <summary>Spawns a burst of particles in a circle around center.</summary>
        public static void SpawnBurst(string prefix, Vector2 center, int count,
            float radius,
            System.Func<int, System.Random, VisualParticleParams> vf,
            System.Func<int, System.Random, AshPhysicsParams> pf,
            System.Random rng, bool useLit = false)
        {
            for (int i = 0; i < count; i++)
            {
                Vector2 pos = center + rng.InsideUnitCircle() * radius;
                var vis = vf(i, rng);
                var phy = pf(i, rng);
                float seed = rng.Range(0f, 100f);
                if (useLit) SpawnLit(pos, vis, phy, seed);
                else SpawnUnlit(pos, vis, phy, seed);
            }
        }

        /// <summary>Backward compat: SpawnBurst without useLit flag.</summary>
        public static void SpawnBurst(string prefix, Vector2 center, int count,
            float radius,
            System.Func<int, System.Random, VisualParticleParams> vf,
            System.Func<int, System.Random, AshPhysicsParams> pf,
            System.Random rng)
            => SpawnBurst(prefix, center, count, radius, vf, pf, rng, false);

        // ──────────────────────────────────────────────────────
        //  CORE POOL EMISSION (private helper)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Emits a single particle from the specified AshParticlePool.
        /// O(1) amortized operation, zero-GC in steady state after warmup.
        /// Returns immediately if pool unavailable or exhausted.
        /// </summary>
        private static void EmitPooled(Vector2 pos,
            in VisualParticleParams visual, in AshPhysicsParams physics,
            float perlinSeed, float startDelay, AshParticlePool pool)
        {
            if (pool == null) return;

            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(visual.Shape);
            if (sprite == null) return;

            AshParticlePooled particle = pool.Get();
            if (particle == null) return;

            particle.Initialize(
                pos, physics.Velocity, physics.Lifetime, visual.Color,
                visual.Scale, physics.Gravity, physics.Drag,
                physics.TurbulenceStrength, physics.TurbulenceScale,
                physics.Wind, physics.ThermalLift,
                perlinSeed, startDelay,
                sprite, visual.SortingOrder
            );
        }

        // ──────────────────────────────────────────────────────
        //  EMISSION GLOW FOR PHYSICS SHRAPNEL (SpriteRenderer)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Applies emission glow to a SpriteRenderer via MaterialPropertyBlock.
        /// Used for hot physics shrapnel — zero material allocation, instant apply/clear.
        /// </summary>
        internal static void ApplyEmission(SpriteRenderer sr, Color emissionColor)
        {
            MPB.Clear();
            MPB.SetColor(EmissionColorId, emissionColor);
            sr.SetPropertyBlock(MPB);
        }

        /// <summary>Clears emission property block from SpriteRenderer.</summary>
        internal static void ClearEmission(SpriteRenderer sr)
        {
            sr.SetPropertyBlock(null);
        }
    }
}