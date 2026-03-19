using System;
using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Manages all ParticleSystem pools. Singleton lifetime tied to world.
    ///
    /// Pool types:
    ///   DebrisPool — alpha-blended: ground chunks, dust, smoke, ash, metal chips, steam
    ///   GlowPool — additive: sparks, embers, fire, muzzle flash, burning chunks
    ///   SparkPool — additive with shorter default lifetime: streak sparks, core sparks
    ///
    /// Pre-warmed at world load via Plugin.OnWorldLoad().
    /// Configures ParticleSystem modules to replicate AshParticle physics:
    ///   - Noise module = turbulence
    ///   - Gravity modifier = per-particle gravity (via EmitParams not available,
    ///     use main.gravityModifier as baseline)
    ///   - Velocity over lifetime = wind drift
    ///   - Color over lifetime = alpha fade (smoothstep approximation via gradient)
    ///   - Limit velocity over lifetime = drag
    /// </summary>
    public static class ParticlePoolManager
    {
        private static ParticlePool _debrisPool;
        private static ParticlePool _glowPool;
        private static ParticlePool _sparkPool;

        private static bool _initialized;

        /// <summary>Alpha-blended pool for inert debris (dust, smoke, chunks, ash).</summary>
        public static ParticlePool Debris => _debrisPool;

        /// <summary>Additive pool for glowing effects (embers, fire, burning chunks).</summary>
        public static ParticlePool Glow => _glowPool;

        /// <summary>Additive pool for fast sparks (streak sparks, core sparks).</summary>
        public static ParticlePool Spark => _sparkPool;

        public static bool Initialized => _initialized;

        /// <summary>
        /// Creates all pools using config-defined sizes.
        /// Call once per world load after materials are ready.
        /// Safe to call multiple times — destroys old pools first.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            Shutdown();

            try
            {
                Material debrisMat = ShrapnelVisuals.DebrisParticleMaterial;
                Material additiveMat = ShrapnelVisuals.AdditiveParticleMaterial;

                if (debrisMat == null || additiveMat == null)
                {
                    Console.Error("[ParticlePool] Materials not ready, skipping init");
                    return;
                }

                // WHY: Pool sizes from config — users can tune GPU load vs visual fidelity
                int debrisMax = ShrapnelConfig.PoolDebrisMaxParticles.Value;
                int glowMax = ShrapnelConfig.PoolGlowMaxParticles.Value;
                int sparkMax = ShrapnelConfig.PoolSparkMaxParticles.Value;

                _debrisPool = new ParticlePool("Debris", debrisMat, debrisMax, 7);
                ConfigureDebrisPool(_debrisPool.System);

                _glowPool = new ParticlePool("Glow", additiveMat, glowMax, 13);
                ConfigureGlowPool(_glowPool.System);

                _sparkPool = new ParticlePool("Spark", additiveMat, sparkMax, 14);
                ConfigureSparkPool(_sparkPool.System);

                _initialized = true;
                Console.Log($"[ParticlePool] Initialized: Debris={debrisMax}" +
                    $" Glow={glowMax} Spark={sparkMax}");
            }
            catch (Exception e)
            {
                Console.Error($"[ParticlePool] Init failed: {e.Message}");
                Shutdown();
            }
        }

        /// <summary>Destroys all pools. Call on scene unload or mod shutdown.</summary>
        public static void Shutdown()
        {
            _debrisPool?.Destroy();
            _glowPool?.Destroy();
            _sparkPool?.Destroy();
            _debrisPool = null;
            _glowPool = null;
            _sparkPool = null;
            _initialized = false;
        }

        /// <summary>Clears all particles without destroying pools.</summary>
        public static void ClearAll()
        {
            _debrisPool?.Clear();
            _glowPool?.Clear();
            _sparkPool?.Clear();
        }

        //  POOL CONFIGURATION — replicate AshParticle physics via modules

        /// <summary>
        /// Configures debris pool to approximate AshParticle behavior:
        /// moderate gravity, high drag, turbulence, wind, alpha fade.
        /// </summary>
        private static void ConfigureDebrisPool(ParticleSystem ps)
        {
            var main = ps.main;
            main.gravityModifier = 0.15f;  // Уменьшил — был 0.2f
            main.startLifetime = new ParticleSystem.MinMaxCurve(2f, 40f);

            // CRITICAL FIX: Velocity over lifetime can override EmitParams velocity!
            // Disable it to let our velocity work
            var velocityOverLifetime = ps.velocityOverLifetime;
            velocityOverLifetime.enabled = false;

            // Noise module — WORLD SPACE is critical!
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.4f;  // Уменьшил — был 0.6f
            noise.frequency = 0.5f;  // Уменьшил — был 0.8f
            noise.scrollSpeed = 0.2f;
            noise.damping = true;
            noise.octaveCount = 2;
            noise.quality = ParticleSystemNoiseQuality.Medium;
            // CRITICAL: Noise in world space, not local
            noise.positionAmount = 0.3f;  // Добавляем шум к позиции, не скорости

            // Color over lifetime — smoothstep alpha fade
            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 0.6f),
                new GradientAlphaKey(0.5f, 0.85f),
                new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);

            // Limit velocity — INCREASED limit so it doesn't kill initial velocity
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 0.05f;  // Уменьшил — был 0.15f (слишком агрессивно гасил)
            limit.limit = 50f;      // Увеличил — был 20f
            limit.space = ParticleSystemSimulationSpace.World;

            // Size over lifetime — slight shrink
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.7f, 0.9f),
                new Keyframe(1f, 0.3f));
            size.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            // CRITICAL: Inherit velocity must be OFF
            var inheritVelocity = ps.inheritVelocity;
            inheritVelocity.enabled = false;
        }

        /// <summary>
        /// Configures glow pool for embers, fire, burning chunks.
        /// Moderate gravity, less drag, moderate turbulence.
        /// </summary>
        private static void ConfigureGlowPool(ParticleSystem ps)
        {
            var main = ps.main;
            main.gravityModifier = 0.5f;  // Уменьшил — был 0.8f
            main.startLifetime = new ParticleSystem.MinMaxCurve(1f, 12f);

            var velocityOverLifetime = ps.velocityOverLifetime;
            velocityOverLifetime.enabled = false;

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.2f;  // Уменьшил
            noise.frequency = 0.8f;
            noise.scrollSpeed = 0.3f;
            noise.damping = true;
            noise.octaveCount = 1;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f),
                new GradientColorKey(new Color(1f, 0.6f, 0.3f), 0.5f),
                new GradientColorKey(new Color(0.5f, 0.2f, 0.05f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.9f, 0.4f),
                new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 0.03f;  // Очень мягкое затухание
            limit.limit = 60f;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.5f, 0.8f),
                new Keyframe(1f, 0.1f));
            size.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var inheritVelocity = ps.inheritVelocity;
            inheritVelocity.enabled = false;
        }

        /// <summary>
        /// Configures spark pool for fast, short-lived sparks.
        /// No gravity (handled by velocity), minimal turbulence, fast fade.
        /// </summary>
        private static void ConfigureSparkPool(ParticleSystem ps)
        {
            var main = ps.main;
            main.gravityModifier = 0.3f;  // Уменьшил — был 0.5f
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.05f, 0.5f);

            var velocityOverLifetime = ps.velocityOverLifetime;
            velocityOverLifetime.enabled = false;

            var noise = ps.noise;
            noise.enabled = false;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.8f, 0.3f),
                new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 3f;
            renderer.velocityScale = 0.08f;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(1f, 0.2f));
            size.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var inheritVelocity = ps.inheritVelocity;
            inheritVelocity.enabled = false;

            // CRITICAL for sparks: no velocity limit
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = false;
        }
    }
}