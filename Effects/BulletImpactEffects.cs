using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Enhanced bullet impact effects for visceral gunfire feedback.
    ///
    /// Features:
    /// - Impact flash (brief bright point) — Lit + emission
    /// - Shower of sparks with trails — Unlit (self-luminous)
    /// - Metal chips that scatter and land — Lit (inert debris)
    /// - Ricochet spark lines — Unlit (self-luminous)
    ///
    /// All spawning routed through <see cref="ParticleHelper"/> for
    /// consistent material selection and future pooling support.
    /// </summary>
    public static class BulletImpactEffects
    {
        //  IMPACT FLASH — Brief bright point at hit location

        /// <summary>
        /// Spawns a brief bright flash at bullet impact point.
        /// Uses LitMaterial + strong emission for bloom effect.
        /// </summary>
        public static void SpawnImpactFlash(Vector2 hitPoint, System.Random rng)
        {
            var visual = new VisualParticleParams(
                rng.Range(0.12f, 0.22f),
                new Color(1f, 0.95f, 0.8f, 1f),
                15,
                ShrapnelVisuals.TriangleShape.Chunk);

            // WHY: Very brief lifetime, zero velocity — pure flash effect
            var physics = new AshPhysicsParams(
                Vector2.zero,
                rng.Range(0.04f, 0.08f),
                gravity: 0f,
                drag: 0f,
                turbulenceStrength: 0f,
                turbulenceScale: 0f);

            var emission = new EmissionParams(new Color(5f, 4f, 2f));

            ParticleHelper.SpawnLit("ImpactFlash", hitPoint, visual, physics,
                rng.Range(0f, 100f), emission);
        }

        //  SPARK SHOWER — Many sparks with varied behavior

        /// <summary>
        /// Spawns a shower of sparks from bullet impact.
        /// Mix of fast streak sparks (Unlit), slow floating sparks (Unlit),
        /// and bright core sparks (Unlit). All self-luminous.
        /// </summary>
        public static void SpawnSparkShower(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, bool isRicochet = false)
        {
            // Fast streak sparks
            int streakCount = isRicochet
                ? rng.Range(ShrapnelConfig.RicochetStreakSparksMin.Value,
                            ShrapnelConfig.RicochetStreakSparksMax.Value)
                : rng.Range(ShrapnelConfig.ImpactStreakSparksMin.Value,
                            ShrapnelConfig.ImpactStreakSparksMax.Value);

            for (int i = 0; i < streakCount; i++)
                SpawnStreakSpark(hitPoint, hitNormal, rng, isRicochet);

            // Slower floating sparks
            int floatCount = rng.Range(
                ShrapnelConfig.ImpactFloatSparksMin.Value,
                ShrapnelConfig.ImpactFloatSparksMax.Value);

            for (int i = 0; i < floatCount; i++)
                SpawnFloatingSpark(hitPoint, hitNormal, rng);

            // Occasional bright "core" sparks
            if (rng.NextFloat() < 0.7f)
            {
                int coreCount = rng.Range(2, 5);
                for (int i = 0; i < coreCount; i++)
                    SpawnCoreSpark(hitPoint, hitNormal, rng);
            }
        }

        private static void SpawnStreakSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, bool isRicochet)
        {
            Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.05f;

            float heat = rng.Range(0.7f, 1f);
            Color sparkColor = new(
                1f,
                Mathf.Lerp(0.5f, 0.9f, heat),
                Mathf.Lerp(0.1f, 0.4f, heat),
                0.95f);

            var visual = new VisualParticleParams(
                rng.Range(0.015f, 0.04f),
                sparkColor,
                14,
                ShrapnelVisuals.TriangleShape.Needle);

            var emission = new EmissionParams(
                new Color(3f, Mathf.Lerp(1f, 2f, heat), 0.3f));

            // Fast outward direction with spread
            float spreadAngle = rng.Range(-80f, 80f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(hitNormal, spreadAngle);
            dir.y = Mathf.Max(dir.y, rng.Range(-0.2f, 0.4f));
            dir.Normalize();

            float speed = rng.Range(8f, 20f);
            if (isRicochet) speed *= 1.3f;

            var spark = new SparkParams(dir, speed, rng.Range(0.05f, 0.18f));

            // WHY: Sparks are self-luminous — always visible in dark
            ParticleHelper.SpawnSparkUnlit("StreakSpark", pos, visual, spark, emission);
        }

        private static void SpawnFloatingSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng)
        {
            Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.08f;

            Color sparkColor = new(
                1f,
                rng.Range(0.6f, 0.85f),
                rng.Range(0.2f, 0.4f),
                0.85f);

            var visual = new VisualParticleParams(
                rng.Range(0.02f, 0.05f),
                sparkColor,
                13,
                ShrapnelVisuals.TriangleShape.Chunk);

            var emission = new EmissionParams(new Color(2f, 1.2f, 0.3f));

            // Slower, more arc-like movement
            float angle = rng.Range(-70f, 70f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(hitNormal, angle);
            dir.y = Mathf.Abs(dir.y) * 0.6f + 0.3f;
            dir.Normalize();

            Vector2 velocity = dir * rng.Range(2f, 6f);

            var physics = AshPhysicsParams.Ember(
                velocity,
                rng.Range(0.3f, 0.8f),
                gravity: rng.Range(2f, 5f),
                drag: 0.3f,
                turbulence: 0.4f,
                wind: Vector2.zero,
                thermalLift: 0f);

            // WHY: Floating sparks glow — self-luminous
            ParticleHelper.SpawnUnlit("FloatSpark", pos, visual, physics,
                rng.Range(0f, 100f), emission);
        }

        private static void SpawnCoreSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng)
        {
            Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.03f;

            var visual = new VisualParticleParams(
                rng.Range(0.04f, 0.08f),
                new Color(1f, 0.95f, 0.85f, 1f),
                15,
                ShrapnelVisuals.TriangleShape.Chunk);

            var emission = new EmissionParams(new Color(4f, 3f, 1.5f));

            float angle = rng.Range(-50f, 50f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(hitNormal, angle);
            dir.y = Mathf.Max(dir.y, 0.2f);
            dir.Normalize();

            var spark = new SparkParams(dir, rng.Range(5f, 12f), rng.Range(0.08f, 0.15f));

            ParticleHelper.SpawnSparkUnlit("CoreSpark", pos, visual, spark, emission);
        }

        //  METAL CHIPS — Larger debris that scatters and lands

        /// <summary>
        /// Spawns metal chip debris that scatters from impact.
        /// Larger, slower than sparks. Uses LitMaterial (inert, dark in shadows).
        /// </summary>
        public static void SpawnMetalChips(Vector2 hitPoint, Vector2 hitNormal, System.Random rng)
        {
            int count = rng.Range(
                ShrapnelConfig.ImpactMetalChipsMin.Value,
                ShrapnelConfig.ImpactMetalChipsMax.Value);

            for (int i = 0; i < count; i++)
                SpawnMetalChip(hitPoint, hitNormal, rng);
        }

        private static void SpawnMetalChip(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng)
        {
            Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.06f;

            float gray = rng.Range(0.25f, 0.45f);
            Color chipColor = new(gray, gray, gray * 1.05f, 0.9f);

            var visual = new VisualParticleParams(
                rng.Range(0.03f, 0.09f),
                chipColor,
                11,
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));

            // Scatter direction
            float angle = rng.Range(-75f, 75f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(hitNormal, angle);
            dir.y = Mathf.Max(dir.y, rng.Range(0f, 0.3f));
            dir.Normalize();

            Vector2 velocity = dir * rng.Range(2f, 7f);

            var physics = AshPhysicsParams.Ember(
                velocity,
                rng.Range(1f, 3f),
                gravity: rng.Range(3f, 6f),
                drag: 0.25f,
                turbulence: 0.2f,
                wind: Vector2.zero,
                thermalLift: 0f);

            // WHY: Metal chips are inert debris — dark in dark areas
            ParticleHelper.SpawnLit("MetalChip", pos, visual, physics,
                rng.Range(0f, 100f));
        }

        //  MAIN ENTRY POINT

        /// <summary>
        /// Spawns all bullet impact effects for metal hit.
        /// Call this from BulletShrapnelLogic when bullet hits metallic block.
        /// </summary>
        public static void SpawnFullImpact(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, bool isRicochet = false)
        {
            try
            {
                SpawnImpactFlash(hitPoint, rng);
            }
            catch (Exception e)
            {
                Console.Error($"[BulletImpact] Flash: {e.Message}");
            }

            try
            {
                SpawnSparkShower(hitPoint, hitNormal, rng, isRicochet);
            }
            catch (Exception e)
            {
                Console.Error($"[BulletImpact] Sparks: {e.Message}");
            }

            try
            {
                SpawnMetalChips(hitPoint, hitNormal, rng);
            }
            catch (Exception e)
            {
                Console.Error($"[BulletImpact] Chips: {e.Message}");
            }
        }
    }
}