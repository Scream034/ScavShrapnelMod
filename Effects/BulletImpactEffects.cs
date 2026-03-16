using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Enhanced bullet impact effects for visceral gunfire feedback.
    /// 
    /// Features:
    /// - Impact flash (brief bright point)
    /// - Shower of sparks with trails
    /// - Metal chips that scatter and land
    /// - Ricochet spark lines
    /// 
    /// Designed to make every shot "feel" impactful on metal surfaces.
    /// </summary>
    public static class BulletImpactEffects
    {
        // ══════════════════════════════════════════════════════════════════
        //  IMPACT FLASH — Brief bright point at hit location
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns a brief bright flash at bullet impact point.
        /// Creates immediate visual feedback for the hit.
        /// </summary>
        public static void SpawnImpactFlash(Vector2 hitPoint, System.Random rng)
        {
            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            GameObject flash = new GameObject("ImpactFlash");
            flash.transform.position = hitPoint;
            flash.transform.localScale = Vector3.one * rng.Range(0.12f, 0.22f);

            SpriteRenderer sr = flash.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
            sr.sharedMaterial = litMat;
            sr.sortingOrder = 15;

            // Bright white-yellow flash
            sr.color = new Color(1f, 0.95f, 0.8f, 1f);

            // Strong emission for glow
            ShrapnelFactory.MPB.Clear();
            ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId, 
                new Color(5f, 4f, 2f));
            sr.SetPropertyBlock(ShrapnelFactory.MPB);

            // Very short lifetime, no movement
            AshParticle ash = flash.AddComponent<AshParticle>();
            ash.InitializeFull(
                Vector2.zero,
                rng.Range(0.04f, 0.08f), // Very brief
                sr.color,
                gravity: 0f,
                drag: 0f,
                turbulenceStrength: 0f,
                turbulenceScale: 0f,
                wind: Vector2.zero,
                thermalLift: 0f,
                perlinSeed: 0f);

            DebrisTracker.RegisterVisual(flash);
        }

        // ══════════════════════════════════════════════════════════════════
        //  SPARK SHOWER — Many sparks with varied behavior
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns a shower of sparks from bullet impact.
        /// Mix of fast streaks and slower floating sparks.
        /// </summary>
        public static void SpawnSparkShower(Vector2 hitPoint, Vector2 hitNormal, 
            System.Random rng, bool isRicochet = false)
        {
            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            // Fast streak sparks
            int streakCount = isRicochet 
                ? rng.Range(ShrapnelConfig.RicochetStreakSparksMin.Value, 
                            ShrapnelConfig.RicochetStreakSparksMax.Value)
                : rng.Range(ShrapnelConfig.ImpactStreakSparksMin.Value,
                            ShrapnelConfig.ImpactStreakSparksMax.Value);

            for (int i = 0; i < streakCount; i++)
            {
                SpawnStreakSpark(hitPoint, hitNormal, rng, litMat, isRicochet);
            }

            // Slower floating sparks
            int floatCount = rng.Range(
                ShrapnelConfig.ImpactFloatSparksMin.Value,
                ShrapnelConfig.ImpactFloatSparksMax.Value);

            for (int i = 0; i < floatCount; i++)
            {
                SpawnFloatingSpark(hitPoint, hitNormal, rng, litMat);
            }

            // Occasional bright "core" sparks
            if (rng.NextFloat() < 0.7f)
            {
                int coreCount = rng.Range(2, 5);
                for (int i = 0; i < coreCount; i++)
                {
                    SpawnCoreSpark(hitPoint, hitNormal, rng, litMat);
                }
            }
        }

        private static void SpawnStreakSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, Material mat, bool isRicochet)
        {
            GameObject spark = new GameObject("StreakSpark");
            spark.transform.position = hitPoint + rng.InsideUnitCircle() * 0.05f;
            spark.transform.localScale = Vector3.one * rng.Range(0.015f, 0.04f);

            SpriteRenderer sr = spark.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 14;

            // Hot spark colors
            float heat = rng.Range(0.7f, 1f);
            Color sparkColor = new Color(
                1f,
                Mathf.Lerp(0.5f, 0.9f, heat),
                Mathf.Lerp(0.1f, 0.4f, heat),
                0.95f);
            sr.color = sparkColor;

            ShrapnelFactory.MPB.Clear();
            ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId,
                new Color(3f, Mathf.Lerp(1f, 2f, heat), 0.3f));
            sr.SetPropertyBlock(ShrapnelFactory.MPB);

            // Fast outward direction with spread
            float spreadAngle = rng.Range(-80f, 80f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(hitNormal, spreadAngle);
            
            // Add upward bias
            dir.y = Mathf.Max(dir.y, rng.Range(-0.2f, 0.4f));
            dir.Normalize();

            float speed = rng.Range(8f, 20f);
            if (isRicochet) speed *= 1.3f;

            VisualShrapnel visual = spark.AddComponent<VisualShrapnel>();
            visual.Initialize(dir, speed, rng.Range(0.05f, 0.18f));

            DebrisTracker.RegisterVisual(spark);
        }

        private static void SpawnFloatingSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, Material mat)
        {
            GameObject spark = new GameObject("FloatSpark");
            spark.transform.position = hitPoint + rng.InsideUnitCircle() * 0.08f;
            spark.transform.localScale = Vector3.one * rng.Range(0.02f, 0.05f);

            SpriteRenderer sr = spark.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 13;

            // Warm orange-yellow
            Color sparkColor = new Color(
                1f,
                rng.Range(0.6f, 0.85f),
                rng.Range(0.2f, 0.4f),
                0.85f);
            sr.color = sparkColor;

            ShrapnelFactory.MPB.Clear();
            ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId,
                new Color(2f, 1.2f, 0.3f));
            sr.SetPropertyBlock(ShrapnelFactory.MPB);

            // Slower, more arc-like movement
            float angle = rng.Range(-70f, 70f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(hitNormal, angle);
            dir.y = Mathf.Abs(dir.y) * 0.6f + 0.3f;
            dir.Normalize();

            Vector2 velocity = dir * rng.Range(2f, 6f);

            AshParticle ash = spark.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                rng.Range(0.3f, 0.8f),
                sparkColor,
                gravity: rng.Range(2f, 5f),
                drag: 0.3f,
                turbulenceStrength: 0.4f,
                turbulenceScale: 1f,
                wind: Vector2.zero,
                thermalLift: 0f,
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(spark);
        }

        private static void SpawnCoreSpark(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, Material mat)
        {
            GameObject spark = new GameObject("CoreSpark");
            spark.transform.position = hitPoint + rng.InsideUnitCircle() * 0.03f;
            spark.transform.localScale = Vector3.one * rng.Range(0.04f, 0.08f);

            SpriteRenderer sr = spark.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 15;

            // Very bright white-hot
            sr.color = new Color(1f, 0.95f, 0.85f, 1f);

            ShrapnelFactory.MPB.Clear();
            ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId,
                new Color(4f, 3f, 1.5f));
            sr.SetPropertyBlock(ShrapnelFactory.MPB);

            float angle = rng.Range(-50f, 50f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(hitNormal, angle);
            dir.y = Mathf.Max(dir.y, 0.2f);
            dir.Normalize();

            VisualShrapnel visual = spark.AddComponent<VisualShrapnel>();
            visual.Initialize(dir, rng.Range(5f, 12f), rng.Range(0.08f, 0.15f));

            DebrisTracker.RegisterVisual(spark);
        }

        // ══════════════════════════════════════════════════════════════════
        //  METAL CHIPS — Larger debris that scatters and lands
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns metal chip debris that scatters from impact.
        /// Larger, slower than sparks. Lands on ground.
        /// </summary>
        public static void SpawnMetalChips(Vector2 hitPoint, Vector2 hitNormal, System.Random rng)
        {
            Material unlitMat = ShrapnelVisuals.UnlitMaterial;
            if (unlitMat == null) return;

            int count = rng.Range(
                ShrapnelConfig.ImpactMetalChipsMin.Value,
                ShrapnelConfig.ImpactMetalChipsMax.Value);

            for (int i = 0; i < count; i++)
            {
                SpawnMetalChip(hitPoint, hitNormal, rng, unlitMat);
            }
        }

        private static void SpawnMetalChip(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, Material mat)
        {
            GameObject chip = new GameObject("MetalChip");
            chip.transform.position = hitPoint + rng.InsideUnitCircle() * 0.06f;
            chip.transform.localScale = Vector3.one * rng.Range(0.03f, 0.09f);

            SpriteRenderer sr = chip.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            sr.sharedMaterial = mat;
            sr.sortingOrder = 11;

            // Dark metal color
            float gray = rng.Range(0.25f, 0.45f);
            Color chipColor = new Color(gray, gray, gray * 1.05f, 0.9f);
            sr.color = chipColor;

            // Scatter direction
            float angle = rng.Range(-75f, 75f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(hitNormal, angle);
            dir.y = Mathf.Max(dir.y, rng.Range(0f, 0.3f));
            dir.Normalize();

            Vector2 velocity = dir * rng.Range(2f, 7f);

            AshParticle ash = chip.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                rng.Range(1f, 3f),
                chipColor,
                gravity: rng.Range(3f, 6f),
                drag: 0.25f,
                turbulenceStrength: 0.2f,
                turbulenceScale: 1f,
                wind: Vector2.zero,
                thermalLift: 0f,
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(chip);
        }

        // ══════════════════════════════════════════════════════════════════
        //  MAIN ENTRY POINT
        // ══════════════════════════════════════════════════════════════════

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
                Plugin.Log.LogWarning($"[BulletImpact] Flash: {e.Message}");
            }

            try
            {
                SpawnSparkShower(hitPoint, hitNormal, rng, isRicochet);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[BulletImpact] Sparks: {e.Message}");
            }

            try
            {
                SpawnMetalChips(hitPoint, hitNormal, rng);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[BulletImpact] Chips: {e.Message}");
            }
        }
    }
}