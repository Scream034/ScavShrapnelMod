using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Bullet impact visual effects with power-based scaling,
    /// ricochet torch light, and enhanced muzzle flash.
    /// </summary>
    public static class BulletImpactEffects
    {
        #region Public API

        /// <summary>
        /// Spawns all bullet impact effects for a metal surface hit.
        /// </summary>
        /// <param name="hitPoint">World position of impact.</param>
        /// <param name="hitNormal">Surface normal at impact point.</param>
        /// <param name="rng">Deterministic random generator.</param>
        /// <param name="isRicochet">True if bullet is ricocheting.</param>
        /// <param name="damageScale">Power-based multiplier for counts.</param>
        public static void SpawnFullImpact(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, bool isRicochet = false, float damageScale = 1f)
        {
            try { SpawnImpactFlash(hitPoint, rng); }
            catch (Exception e) { Console.Error($"[BulletImpact] Flash: {e.Message}"); }

            try { SpawnSparkShower(hitPoint, hitNormal, rng, damageScale, isRicochet); }
            catch (Exception e) { Console.Error($"[BulletImpact] Sparks: {e.Message}"); }

            try { SpawnMetalChips(hitPoint, hitNormal, rng, damageScale); }
            catch (Exception e) { Console.Error($"[BulletImpact] Chips: {e.Message}"); }

            // WHY: Ricochet produces a bright torch-like flash from metal-on-metal.
            // Visible in dark areas, simulates real-world ricochet spark bloom.
            if (isRicochet)
            {
                try { SpawnRicochetTorchLight(hitPoint, hitNormal, rng); }
                catch (Exception e) { Console.Error($"[BulletImpact] Torch: {e.Message}"); }
            }
        }

        /// <summary>
        /// Spawns spark shower with all sub-types.
        /// </summary>
        /// <param name="hitPoint">World position of impact.</param>
        /// <param name="hitNormal">Surface normal at impact point.</param>
        /// <param name="rng">Deterministic random generator.</param>
        /// <param name="damageScale">Power-based multiplier for counts.</param>
        /// <param name="isRicochet">True if bullet is ricocheting.</param>
        public static void SpawnSparkShower(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, float damageScale = 1f, bool isRicochet = false)
        {
            int streakMin, streakMax;
            if (isRicochet)
            {
                streakMin = ShrapnelConfig.RicochetStreakSparksMin.Value;
                streakMax = ShrapnelConfig.RicochetStreakSparksMax.Value;
            }
            else
            {
                streakMin = ShrapnelConfig.ImpactStreakSparksMin.Value;
                streakMax = ShrapnelConfig.ImpactStreakSparksMax.Value;
            }

            int streakCount = Mathf.RoundToInt(rng.Range(streakMin, streakMax) * damageScale);
            for (int i = 0; i < streakCount; i++)
                SpawnStreakSpark(hitPoint, hitNormal, rng, isRicochet);

            int microCount = Mathf.RoundToInt(streakCount * 1.5f);
            for (int i = 0; i < microCount; i++)
                SpawnMicroSpark(hitPoint, hitNormal, rng);

            int floatCount = Mathf.RoundToInt(rng.Range(
                ShrapnelConfig.ImpactFloatSparksMin.Value,
                ShrapnelConfig.ImpactFloatSparksMax.Value) * damageScale);
            for (int i = 0; i < floatCount; i++)
                SpawnFloatingSpark(hitPoint, hitNormal, rng);

            if (rng.NextFloat() < 0.7f)
            {
                int coreCount = rng.Range(2, 5);
                for (int i = 0; i < coreCount; i++)
                    SpawnCoreSpark(hitPoint, hitNormal, rng);
            }
        }

        /// <summary>
        /// Spawns enhanced muzzle flash with torch glow at barrel.
        /// </summary>
        /// <param name="firePos">Barrel world position.</param>
        /// <param name="fireDir">Fire direction (normalized).</param>
        public static void SpawnMuzzleFlash(Vector2 firePos, Vector2 fireDir)
        {
            var rng = new System.Random(
                Mathf.RoundToInt(firePos.x * 100f) * 397 ^
                Mathf.RoundToInt(firePos.y * 100f) ^
                Time.frameCount);

            // Primary flash — bright core
            var flashVis = new VisualParticleParams(
                rng.Range(0.1f, 0.2f),
                new Color(1f, 0.95f, 0.7f, 1f), 16,
                ShrapnelVisuals.TriangleShape.Chunk);
            var flashPhy = new AshPhysicsParams(
                Vector2.zero, rng.Range(0.03f, 0.06f),
                gravity: 0f, drag: 0f,
                turbulenceStrength: 0f, turbulenceScale: 0f);
            ParticleHelper.SpawnUnlit(firePos, flashVis, flashPhy, rng.Range(0f, 100f));

            // WHY: Torch glow — large soft unlit particle that simulates
            // the bright flash illuminating the barrel area.
            // Visible in dark caves, fades quickly (0.06-0.1s).
            var torchVis = new VisualParticleParams(
                rng.Range(0.5f, 0.8f),
                new Color(1f, 0.8f, 0.4f, 0.35f), 5,
                ShrapnelVisuals.TriangleShape.Chunk);
            var torchPhy = new AshPhysicsParams(
                Vector2.zero, rng.Range(0.06f, 0.1f),
                gravity: 0f, drag: 0f,
                turbulenceStrength: 0f, turbulenceScale: 0f);
            ParticleHelper.SpawnUnlit(firePos, torchVis, torchPhy, rng.Range(0f, 100f));

            // Barrel sparks
            Vector2 dir = fireDir.sqrMagnitude > 0.01f ? fireDir.normalized : Vector2.right;
            int sparkCount = rng.Range(3, 7);

            for (int i = 0; i < sparkCount; i++)
            {
                Vector2 sparkDir = ComputeSparkDirection(dir, rng, 40f);
                float heat = rng.Range(0.6f, 1f);
                Color col = new(1f, Mathf.Lerp(0.5f, 0.9f, heat),
                    Mathf.Lerp(0.1f, 0.3f, heat));

                var vis = new VisualParticleParams(
                    rng.Range(0.01f, 0.03f), col, 15,
                    ShrapnelVisuals.TriangleShape.Needle);
                var spark = new SparkParams(sparkDir,
                    rng.Range(5f, 15f), rng.Range(0.04f, 0.1f));

                ParticleHelper.SpawnSpark(
                    firePos + rng.InsideUnitCircle() * 0.05f, vis, spark);
            }
        }

        #endregion

        #region Private Spark Spawners

        /// <summary>Computes randomized spark direction from normal.</summary>
        private static Vector2 ComputeSparkDirection(Vector2 normal,
            System.Random rng, float spreadDeg = 80f)
        {
            float angle = rng.Range(-spreadDeg, spreadDeg) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(normal, angle);
            dir.y = Mathf.Max(dir.y, rng.Range(-0.2f, 0.4f));
            return dir.normalized;
        }

        private static void SpawnImpactFlash(Vector2 hitPoint, System.Random rng)
        {
            var visual = new VisualParticleParams(
                rng.Range(0.12f, 0.22f),
                new Color(1f, 0.95f, 0.8f, 1f), 15,
                ShrapnelVisuals.TriangleShape.Chunk);
            var physics = new AshPhysicsParams(
                Vector2.zero, rng.Range(0.04f, 0.08f),
                gravity: 0f, drag: 0f,
                turbulenceStrength: 0f, turbulenceScale: 0f);
            ParticleHelper.SpawnLit(hitPoint, visual, physics, rng.Range(0f, 100f));
        }

        /// <summary>Fast streak spark with glow halo.</summary>
        private static void SpawnStreakSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, bool isRicochet)
        {
            Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.05f;

            float heat = rng.Range(0.7f, 1f);
            Color col = new(1f, Mathf.Lerp(0.5f, 0.9f, heat),
                Mathf.Lerp(0.1f, 0.4f, heat), 0.95f);

            Vector2 dir = ComputeSparkDirection(hitNormal, rng);
            float speed = rng.Range(8f, 20f) * (isRicochet ? 1.3f : 1f);

            var visual = new VisualParticleParams(
                rng.Range(0.015f, 0.04f), col, 14,
                ShrapnelVisuals.TriangleShape.Needle);
            var spark = new SparkParams(dir, speed, rng.Range(0.05f, 0.18f));
            ParticleHelper.SpawnSpark(pos, visual, spark);

            // Glow halo — soft bloom around bright spark
            Color glowCol = new(1f, Mathf.Lerp(0.4f, 0.8f, heat), 0.15f, 0.4f);
            var glowVis = new VisualParticleParams(
                rng.Range(0.04f, 0.09f), glowCol, 13,
                ShrapnelVisuals.TriangleShape.Chunk);
            var glowPhy = new AshPhysicsParams(
                dir * speed * 0.3f, rng.Range(0.08f, 0.2f),
                gravity: 0.5f, drag: 1f,
                turbulenceStrength: 0f, turbulenceScale: 0f);
            ParticleHelper.SpawnUnlit(pos, glowVis, glowPhy, rng.Range(0f, 100f));
        }

        /// <summary>Very tiny, extremely fast micro spark.</summary>
        private static void SpawnMicroSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng)
        {
            Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.03f;

            float heat = rng.Range(0.8f, 1f);
            Color col = new(1f, Mathf.Lerp(0.7f, 1f, heat),
                Mathf.Lerp(0.3f, 0.7f, heat), 0.9f);

            var visual = new VisualParticleParams(
                rng.Range(0.008f, 0.02f), col, 15,
                ShrapnelVisuals.TriangleShape.Needle);

            Vector2 dir = ComputeSparkDirection(hitNormal, rng, 90f);
            var spark = new SparkParams(dir, rng.Range(12f, 30f), rng.Range(0.03f, 0.1f));
            ParticleHelper.SpawnSpark(pos, visual, spark);
        }

        /// <summary>Slow floating ember spark.</summary>
        private static void SpawnFloatingSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng)
        {
            Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.08f;
            Color col = new(1f, rng.Range(0.6f, 0.85f), rng.Range(0.2f, 0.4f), 0.85f);

            var visual = new VisualParticleParams(
                rng.Range(0.02f, 0.05f), col, 13,
                ShrapnelVisuals.TriangleShape.Chunk);

            Vector2 dir = ComputeSparkDirection(hitNormal, rng, 70f);
            dir.y = Mathf.Abs(dir.y) * 0.6f + 0.3f;
            dir.Normalize();

            var physics = AshPhysicsParams.Ember(
                dir * rng.Range(2f, 6f), rng.Range(0.3f, 0.8f),
                gravity: rng.Range(2f, 5f), drag: 0.3f,
                turbulence: 0.4f, wind: Vector2.zero, thermalLift: 0f);

            ParticleHelper.SpawnUnlit(pos, visual, physics, rng.Range(0f, 100f));
        }

        /// <summary>Bright core flash spark.</summary>
        private static void SpawnCoreSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng)
        {
            Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.03f;

            var visual = new VisualParticleParams(
                rng.Range(0.04f, 0.08f),
                new Color(1f, 0.95f, 0.85f, 1f), 15,
                ShrapnelVisuals.TriangleShape.Chunk);

            Vector2 dir = ComputeSparkDirection(hitNormal, rng, 50f);
            var spark = new SparkParams(dir, rng.Range(5f, 12f), rng.Range(0.08f, 0.15f));
            ParticleHelper.SpawnSpark(pos, visual, spark);
        }

        /// <summary>
        /// Large bright torch glow at ricochet point.
        /// WHY: Metal-on-metal ricochet produces a visible flash in real life.
        /// Large soft unlit particle simulates area illumination without Light2D.
        /// Multiple layers: bright core (0.3) + large soft bloom (0.8).
        /// </summary>
        private static void SpawnRicochetTorchLight(Vector2 hitPoint,
            Vector2 hitNormal, System.Random rng)
        {
            // Bright core flash
            var coreVis = new VisualParticleParams(
                rng.Range(0.25f, 0.4f),
                new Color(1f, 0.9f, 0.6f, 0.9f), 16,
                ShrapnelVisuals.TriangleShape.Chunk);
            var corePhy = new AshPhysicsParams(
                hitNormal * 0.5f, rng.Range(0.06f, 0.1f),
                gravity: 0f, drag: 2f,
                turbulenceStrength: 0f, turbulenceScale: 0f);
            ParticleHelper.SpawnUnlit(hitPoint, coreVis, corePhy, rng.Range(0f, 100f));

            // Large soft bloom — simulates light cast on nearby surfaces
            var bloomVis = new VisualParticleParams(
                rng.Range(0.7f, 1.2f),
                new Color(1f, 0.75f, 0.35f, 0.25f), 4,
                ShrapnelVisuals.TriangleShape.Chunk);
            var bloomPhy = new AshPhysicsParams(
                Vector2.zero, rng.Range(0.08f, 0.15f),
                gravity: 0f, drag: 0f,
                turbulenceStrength: 0f, turbulenceScale: 0f);
            ParticleHelper.SpawnUnlit(hitPoint, bloomVis, bloomPhy, rng.Range(0f, 100f));

            // Secondary warm glow — slightly offset, longer fade
            var warmVis = new VisualParticleParams(
                rng.Range(0.4f, 0.6f),
                new Color(1f, 0.6f, 0.2f, 0.15f), 3,
                ShrapnelVisuals.TriangleShape.Chunk);
            var warmPhy = new AshPhysicsParams(
                hitNormal * 0.3f, rng.Range(0.12f, 0.2f),
                gravity: 0f, drag: 1f,
                turbulenceStrength: 0f, turbulenceScale: 0f);
            ParticleHelper.SpawnUnlit(
                hitPoint + rng.InsideUnitCircle() * 0.1f,
                warmVis, warmPhy, rng.Range(0f, 100f));
        }

        /// <summary>Metal chip debris with damage scaling.</summary>
        private static void SpawnMetalChips(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, float damageScale)
        {
            int min = Mathf.RoundToInt(ShrapnelConfig.ImpactMetalChipsMin.Value * damageScale);
            int max = Mathf.RoundToInt(ShrapnelConfig.ImpactMetalChipsMax.Value * damageScale);
            int count = rng.Range(min, max);

            for (int i = 0; i < count; i++)
            {
                Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.06f;
                float gray = rng.Range(0.25f, 0.45f);
                Color col = new(gray, gray, gray * 1.05f, 0.9f);

                var visual = new VisualParticleParams(
                    rng.Range(0.03f, 0.09f), col, 11,
                    (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));

                Vector2 dir = ComputeSparkDirection(hitNormal, rng, 75f);
                var physics = AshPhysicsParams.Ember(
                    dir * rng.Range(2f, 7f), rng.Range(1f, 3f),
                    gravity: rng.Range(3f, 6f), drag: 0.25f,
                    turbulence: 0.2f, wind: Vector2.zero, thermalLift: 0f);

                ParticleHelper.SpawnLit(pos, visual, physics, rng.Range(0f, 100f));
            }
        }

        #endregion
    }
}