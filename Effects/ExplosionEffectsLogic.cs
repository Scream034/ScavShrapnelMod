using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Advanced explosion visual effects: smoke columns, fire embers, crater dust.
    /// Called from ShrapnelSpawnLogic after primary shrapnel spawning.
    /// 
    /// All effects use AshParticle for unified physics and lifecycle management.
    /// Registered in DebrisTracker visual pool.
    /// </summary>
    public static class ExplosionEffectsLogic
    {
        // ══════════════════════════════════════════════════════════════════
        //  SMOKE COLUMN — Rising dark smoke pillar, persists 10-25 seconds
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns a rising smoke column from explosion epicenter.
        /// Creates dramatic aftermath effect that lingers.
        /// </summary>
        /// <param name="epicenter">Explosion center position.</param>
        /// <param name="power">Explosion power (0-1), affects column size and duration.</param>
        /// <param name="rng">Deterministic random generator.</param>
        public static void SpawnSmokeColumn(Vector2 epicenter, float power, System.Random rng)
        {
            Material unlitMat = ShrapnelVisuals.UnlitMaterial;
            if (unlitMat == null) return;

            float countMult = ShrapnelConfig.SmokeColumnCountMultiplier.Value;
            int baseCount = Mathf.RoundToInt(Mathf.Lerp(40, 120, power) * countMult);
            
            // Spawn in waves for more natural column formation
            int waves = 3;
            int perWave = baseCount / waves;

            for (int wave = 0; wave < waves; wave++)
            {
                float waveDelay = wave * 0.15f; // Stagger wave timing
                float waveHeight = wave * 0.5f; // Each wave starts slightly higher
                
                for (int i = 0; i < perWave; i++)
                {
                    SpawnSmokeParticle(epicenter, power, waveDelay, waveHeight, rng, unlitMat);
                }
            }

            // Core dense smoke at center
            int coreCount = Mathf.RoundToInt(20 * countMult);
            for (int i = 0; i < coreCount; i++)
            {
                SpawnCoreSmokeParticle(epicenter, power, rng, unlitMat);
            }
        }

        private static void SpawnSmokeParticle(Vector2 epicenter, float power, 
            float waveDelay, float waveHeight, System.Random rng, Material mat)
        {
            GameObject smoke = new GameObject("Smoke");
            
            // Spawn in column formation with slight randomness
            float spawnRadius = rng.Range(0.3f, 1.5f) * (1f + power);
            Vector2 offset = rng.InsideUnitCircle() * spawnRadius;
            offset.y = Mathf.Abs(offset.y) * 0.3f + waveHeight; // Bias upward
            
            smoke.transform.position = epicenter + offset;
            smoke.transform.localScale = Vector3.one * rng.Range(0.15f, 0.45f) * (1f + power * 0.5f);

            SpriteRenderer sr = smoke.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 7; // Behind shrapnel but visible

            // Dark smoke colors with slight variation
            float darkness = rng.Range(0.08f, 0.25f);
            float alpha = rng.Range(0.4f, 0.7f);
            Color smokeColor = new Color(darkness, darkness * 0.95f, darkness * 0.9f, alpha);

            // Upward velocity with slight spread
            float upSpeed = rng.Range(1.5f, 4f) * (1f + power * 0.5f);
            float sideSpeed = rng.Range(-0.8f, 0.8f);
            Vector2 velocity = new Vector2(sideSpeed, upSpeed);

            float lifetime = rng.Range(12f, 25f) * ShrapnelConfig.SmokeColumnLifetimeMultiplier.Value;

            AshParticle ash = smoke.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                lifetime,
                smokeColor,
                gravity: rng.Range(-0.08f, -0.02f), // Negative = rises
                drag: rng.Range(0.3f, 0.6f),
                turbulenceStrength: rng.Range(0.6f, 1.2f),
                turbulenceScale: rng.Range(1.5f, 3f),
                wind: new Vector2(rng.Range(-0.3f, 0.3f), 0.1f),
                thermalLift: rng.Range(0.3f, 0.8f), // Hot air rises
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(smoke);
        }

        private static void SpawnCoreSmokeParticle(Vector2 epicenter, float power, 
            System.Random rng, Material mat)
        {
            GameObject smoke = new GameObject("CoreSmoke");
            
            Vector2 offset = rng.InsideUnitCircle() * 0.5f;
            smoke.transform.position = epicenter + offset;
            smoke.transform.localScale = Vector3.one * rng.Range(0.25f, 0.6f) * (1f + power);

            SpriteRenderer sr = smoke.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 6;

            // Very dark, dense core smoke
            float darkness = rng.Range(0.05f, 0.15f);
            Color smokeColor = new Color(darkness, darkness, darkness, rng.Range(0.6f, 0.85f));

            Vector2 velocity = new Vector2(rng.Range(-0.3f, 0.3f), rng.Range(2f, 5f));
            float lifetime = rng.Range(8f, 18f) * ShrapnelConfig.SmokeColumnLifetimeMultiplier.Value;

            AshParticle ash = smoke.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                lifetime,
                smokeColor,
                gravity: -0.05f,
                drag: 0.4f,
                turbulenceStrength: 0.8f,
                turbulenceScale: 2f,
                wind: new Vector2(rng.Range(-0.2f, 0.2f), 0.15f),
                thermalLift: 0.6f,
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(smoke);
        }

        // ══════════════════════════════════════════════════════════════════
        //  FIRE EMBERS — Glowing particles that land and briefly burn
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns glowing fire embers that arc outward, land, and fade.
        /// Creates dangerous, hot aftermath feeling.
        /// </summary>
        public static void SpawnFireEmbers(Vector2 epicenter, float power, System.Random rng)
        {
            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            float countMult = ShrapnelConfig.FireEmbersCountMultiplier.Value;
            int count = Mathf.RoundToInt(Mathf.Lerp(25, 70, power) * countMult);

            for (int i = 0; i < count; i++)
            {
                SpawnEmberParticle(epicenter, power, i, rng, litMat);
            }

            // Spawn some larger "burning chunks"
            int chunkCount = Mathf.RoundToInt(Mathf.Lerp(5, 15, power) * countMult);
            for (int i = 0; i < chunkCount; i++)
            {
                SpawnBurningChunk(epicenter, power, rng, litMat);
            }
        }

        private static void SpawnEmberParticle(Vector2 epicenter, float power, 
            int index, System.Random rng, Material mat)
        {
            GameObject ember = new GameObject("Ember");
            
            Vector2 offset = rng.InsideUnitCircle() * 0.4f;
            ember.transform.position = epicenter + offset;
            ember.transform.localScale = Vector3.one * rng.Range(0.03f, 0.08f);

            SpriteRenderer sr = ember.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 13;

            // Hot ember colors: orange to red-orange
            float heat = rng.Range(0.6f, 1f);
            Color emberColor = new Color(
                1f,
                Mathf.Lerp(0.3f, 0.7f, heat),
                Mathf.Lerp(0f, 0.2f, heat * 0.5f),
                0.95f);
            sr.color = emberColor;

            // Emission for glow
            ShrapnelFactory.MPB.Clear();
            ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId, 
                new Color(2f, Mathf.Lerp(0.5f, 1.2f, heat), 0.1f));
            sr.SetPropertyBlock(ShrapnelFactory.MPB);

            // Arc outward and upward, then fall
            float angle = rng.NextAngle();
            float speed = rng.Range(4f, 12f) * (0.7f + power * 0.5f);
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle) * 0.5f + 0.5f);
            Vector2 velocity = dir * speed;

            float lifetime = rng.Range(2f, 5f);

            AshParticle ash = ember.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                lifetime,
                emberColor,
                gravity: rng.Range(1.5f, 3f), // Falls like real embers
                drag: 0.2f,
                turbulenceStrength: 0.3f,
                turbulenceScale: 1f,
                wind: new Vector2(rng.Range(-0.1f, 0.1f), 0f),
                thermalLift: rng.Range(-0.1f, 0.2f),
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(ember);
        }

        private static void SpawnBurningChunk(Vector2 epicenter, float power, 
            System.Random rng, Material mat)
        {
            GameObject chunk = new GameObject("BurningChunk");
            
            Vector2 offset = rng.InsideUnitCircle() * 0.3f;
            chunk.transform.position = epicenter + offset;
            chunk.transform.localScale = Vector3.one * rng.Range(0.08f, 0.18f);

            SpriteRenderer sr = chunk.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            sr.sharedMaterial = mat;
            sr.sortingOrder = 12;

            // Burning chunk: dark core with orange edges
            Color chunkColor = new Color(
                rng.Range(0.8f, 1f),
                rng.Range(0.4f, 0.6f),
                rng.Range(0.1f, 0.25f),
                0.9f);
            sr.color = chunkColor;

            ShrapnelFactory.MPB.Clear();
            ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId, 
                new Color(1.5f, 0.6f, 0.1f));
            sr.SetPropertyBlock(ShrapnelFactory.MPB);

            // Slower, heavier arc
            float angle = rng.NextAngle();
            float speed = rng.Range(3f, 8f) * (0.5f + power * 0.5f);
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Abs(Mathf.Sin(angle)) * 0.7f + 0.3f);
            Vector2 velocity = dir * speed;

            float lifetime = rng.Range(4f, 10f);

            AshParticle ash = chunk.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                lifetime,
                chunkColor,
                gravity: rng.Range(2f, 4f),
                drag: 0.15f,
                turbulenceStrength: 0.2f,
                turbulenceScale: 1.5f,
                wind: Vector2.zero,
                thermalLift: 0f,
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(chunk);
        }

        // ══════════════════════════════════════════════════════════════════
        //  CRATER DUST — Lingering low particles at epicenter
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns lingering dust cloud at ground level around crater.
        /// Very slow movement, high turbulence, long lifetime.
        /// </summary>
        public static void SpawnCraterDust(Vector2 epicenter, float range, float power, System.Random rng)
        {
            Material unlitMat = ShrapnelVisuals.UnlitMaterial;
            if (unlitMat == null) return;

            float countMult = ShrapnelConfig.CraterDustCountMultiplier.Value;
            int count = Mathf.RoundToInt(Mathf.Lerp(30, 80, power) * countMult);

            // Get ground color if possible
            Color baseColor = GetCraterDustColor(epicenter, rng);

            for (int i = 0; i < count; i++)
            {
                SpawnCraterDustParticle(epicenter, range, power, baseColor, rng, unlitMat);
            }

            // Dense center dust
            int centerCount = Mathf.RoundToInt(15 * countMult);
            for (int i = 0; i < centerCount; i++)
            {
                SpawnCenterDustParticle(epicenter, power, baseColor, rng, unlitMat);
            }
        }

        private static void SpawnCraterDustParticle(Vector2 epicenter, float range, float power,
            Color baseColor, System.Random rng, Material mat)
        {
            GameObject dust = new GameObject("CraterDust");
            
            // Spread around crater area
            float spawnRadius = range * rng.Range(0.3f, 1.2f);
            Vector2 offset = rng.InsideUnitCircle() * spawnRadius;
            offset.y = Mathf.Abs(offset.y) * -0.3f; // Bias toward ground
            
            dust.transform.position = epicenter + offset;
            dust.transform.localScale = Vector3.one * rng.Range(0.08f, 0.25f);

            SpriteRenderer sr = dust.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 5; // Behind most effects

            // Vary from base color
            Color dustColor = new Color(
                baseColor.r * rng.Range(0.85f, 1.15f),
                baseColor.g * rng.Range(0.85f, 1.15f),
                baseColor.b * rng.Range(0.85f, 1.15f),
                rng.Range(0.25f, 0.5f));

            // Very slow, drifting movement
            Vector2 velocity = new Vector2(
                rng.Range(-0.5f, 0.5f),
                rng.Range(0.1f, 0.6f));

            float lifetime = rng.Range(15f, 35f) * ShrapnelConfig.CraterDustLifetimeMultiplier.Value;

            AshParticle ash = dust.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                lifetime,
                dustColor,
                gravity: rng.Range(0.02f, 0.08f), // Almost hovers
                drag: 0.7f,
                turbulenceStrength: rng.Range(0.8f, 1.5f), // High turbulence
                turbulenceScale: rng.Range(2f, 4f),
                wind: new Vector2(rng.Range(-0.15f, 0.15f), 0.05f),
                thermalLift: rng.Range(0.05f, 0.2f),
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(dust);
        }

        private static void SpawnCenterDustParticle(Vector2 epicenter, float power,
            Color baseColor, System.Random rng, Material mat)
        {
            GameObject dust = new GameObject("CenterDust");
            
            Vector2 offset = rng.InsideUnitCircle() * 0.8f;
            dust.transform.position = epicenter + offset;
            dust.transform.localScale = Vector3.one * rng.Range(0.12f, 0.35f);

            SpriteRenderer sr = dust.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 5;

            // Denser, darker dust at center
            Color dustColor = new Color(
                baseColor.r * 0.7f,
                baseColor.g * 0.7f,
                baseColor.b * 0.7f,
                rng.Range(0.4f, 0.65f));

            Vector2 velocity = new Vector2(
                rng.Range(-0.3f, 0.3f),
                rng.Range(0.2f, 0.8f));

            float lifetime = rng.Range(20f, 40f) * ShrapnelConfig.CraterDustLifetimeMultiplier.Value;

            AshParticle ash = dust.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                lifetime,
                dustColor,
                gravity: 0.03f,
                drag: 0.8f,
                turbulenceStrength: 1.2f,
                turbulenceScale: 3f,
                wind: new Vector2(rng.Range(-0.1f, 0.1f), 0.08f),
                thermalLift: 0.15f,
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(dust);
        }

        private static Color GetCraterDustColor(Vector2 epicenter, System.Random rng)
        {
            // Try to sample ground block for color
            try
            {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(
                    epicenter + Vector2.down * 0.5f);
                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                
                if (blockId != 0)
                {
                    BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                    if (info != null)
                    {
                        string name = info.name ?? string.Empty;
                        
                        if (name.IndexOf("sand", StringComparison.OrdinalIgnoreCase) >= 0)
                            return new Color(0.76f, 0.65f, 0.42f);
                        if (name.IndexOf("stone", StringComparison.OrdinalIgnoreCase) >= 0)
                            return new Color(0.5f, 0.48f, 0.45f);
                        if (name.IndexOf("dirt", StringComparison.OrdinalIgnoreCase) >= 0)
                            return new Color(0.45f, 0.35f, 0.25f);
                        if (info.metallic)
                            return new Color(0.4f, 0.4f, 0.42f);
                    }
                }
            }
            catch { }
            
            // Default brownish dust
            return new Color(
                rng.Range(0.4f, 0.55f),
                rng.Range(0.35f, 0.45f),
                rng.Range(0.25f, 0.35f));
        }

        // ══════════════════════════════════════════════════════════════════
        //  MAIN ENTRY POINT
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns all advanced explosion effects.
        /// Called from ShrapnelSpawnLogic after primary effects.
        /// </summary>
        /// <param name="epicenter">Explosion center.</param>
        /// <param name="range">Explosion radius.</param>
        /// <param name="structuralDamage">Explosion power for scaling.</param>
        /// <param name="rng">Deterministic random generator.</param>
        public static void SpawnAllEffects(Vector2 epicenter, float range, 
            float structuralDamage, System.Random rng)
        {
            // Normalize power to 0-1 range
            float power = Mathf.Clamp01(structuralDamage / 2000f);

            try
            {
                if (ShrapnelConfig.EnableSmokeColumn.Value)
                    SpawnSmokeColumn(epicenter, power, rng);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Effects] SmokeColumn: {e.Message}");
            }

            try
            {
                if (ShrapnelConfig.EnableFireEmbers.Value)
                    SpawnFireEmbers(epicenter, power, rng);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Effects] FireEmbers: {e.Message}");
            }

            try
            {
                if (ShrapnelConfig.EnableCraterDust.Value)
                    SpawnCraterDust(epicenter, range, power, rng);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Effects] CraterDust: {e.Message}");
            }
        }
    }
}