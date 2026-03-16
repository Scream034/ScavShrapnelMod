using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Advanced explosion visual effects: smoke columns, fire embers, crater dust.
    /// Called from ShrapnelSpawnLogic after primary shrapnel spawning.
    ///
    /// Material rules:
    ///   Smoke/Dust → SpawnLit (inert, dark in shadows)
    ///   Embers     → SpawnUnlit + emission (self-luminous, always visible)
    ///
    /// All spawning routed through <see cref="ParticleHelper"/> for
    /// consistent material selection and future pooling support.
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
            float countMult = ShrapnelConfig.SmokeColumnCountMultiplier.Value;
            int baseCount = Mathf.RoundToInt(Mathf.Lerp(40, 120, power) * countMult);

            // Spawn in waves for more natural column formation
            int waves = 3;
            int perWave = baseCount / waves;

            for (int wave = 0; wave < waves; wave++)
            {
                float waveHeight = wave * 0.5f;

                for (int i = 0; i < perWave; i++)
                    SpawnSmokeParticle(epicenter, power, waveHeight, rng);
            }

            // Core dense smoke at center
            int coreCount = Mathf.RoundToInt(20 * countMult);
            for (int i = 0; i < coreCount; i++)
                SpawnCoreSmokeParticle(epicenter, power, rng);
        }

        private static void SpawnSmokeParticle(Vector2 epicenter, float power,
            float waveHeight, System.Random rng)
        {
            float spawnRadius = rng.Range(0.3f, 1.5f) * (1f + power);
            Vector2 offset = rng.InsideUnitCircle() * spawnRadius;
            offset.y = Mathf.Abs(offset.y) * 0.3f + waveHeight;

            Vector2 pos = epicenter + offset;

            float darkness = rng.Range(0.08f, 0.25f);
            float alpha = rng.Range(0.4f, 0.7f);
            Color smokeColor = new Color(darkness, darkness * 0.95f, darkness * 0.9f, alpha);

            float upSpeed = rng.Range(1.5f, 4f) * (1f + power * 0.5f);
            float sideSpeed = rng.Range(-0.8f, 0.8f);
            Vector2 velocity = new Vector2(sideSpeed, upSpeed);

            float lifetime = rng.Range(12f, 25f) * ShrapnelConfig.SmokeColumnLifetimeMultiplier.Value;
            float scale = rng.Range(0.15f, 0.45f) * (1f + power * 0.5f);

            var visual = new VisualParticleParams(
                scale, smokeColor, 7,
                ShrapnelVisuals.TriangleShape.Chunk);

            var physics = AshPhysicsParams.Smoke(
                velocity, lifetime,
                gravity: rng.Range(-0.08f, -0.02f),
                drag: rng.Range(0.3f, 0.6f),
                turbulence: rng.Range(0.6f, 1.2f),
                wind: new Vector2(rng.Range(-0.3f, 0.3f), 0.1f),
                thermalLift: rng.Range(0.3f, 0.8f));

            // WHY: Smoke is inert — dark in dark areas
            ParticleHelper.SpawnLit("Smoke", pos, visual, physics,
                rng.Range(0f, 100f));
        }

        private static void SpawnCoreSmokeParticle(Vector2 epicenter, float power,
            System.Random rng)
        {
            Vector2 offset = rng.InsideUnitCircle() * 0.5f;
            Vector2 pos = epicenter + offset;

            float darkness = rng.Range(0.05f, 0.15f);
            Color smokeColor = new Color(darkness, darkness, darkness, rng.Range(0.6f, 0.85f));

            Vector2 velocity = new Vector2(rng.Range(-0.3f, 0.3f), rng.Range(2f, 5f));
            float lifetime = rng.Range(8f, 18f) * ShrapnelConfig.SmokeColumnLifetimeMultiplier.Value;
            float scale = rng.Range(0.25f, 0.6f) * (1f + power);

            var visual = new VisualParticleParams(
                scale, smokeColor, 6,
                ShrapnelVisuals.TriangleShape.Chunk);

            var physics = AshPhysicsParams.Smoke(
                velocity, lifetime,
                gravity: -0.05f,
                drag: 0.4f,
                turbulence: 0.8f,
                wind: new Vector2(rng.Range(-0.2f, 0.2f), 0.15f),
                thermalLift: 0.6f);

            ParticleHelper.SpawnLit("CoreSmoke", pos, visual, physics,
                rng.Range(0f, 100f));
        }

        // ══════════════════════════════════════════════════════════════════
        //  FIRE EMBERS — Glowing particles that land and briefly burn
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns glowing fire embers that arc outward, land, and fade.
        /// Uses UnlitMaterial + emission — self-luminous, always visible.
        /// </summary>
        public static void SpawnFireEmbers(Vector2 epicenter, float power, System.Random rng)
        {
            float countMult = ShrapnelConfig.FireEmbersCountMultiplier.Value;
            int count = Mathf.RoundToInt(Mathf.Lerp(25, 70, power) * countMult);

            for (int i = 0; i < count; i++)
                SpawnEmberParticle(epicenter, power, rng);

            // Spawn some larger "burning chunks"
            int chunkCount = Mathf.RoundToInt(Mathf.Lerp(5, 15, power) * countMult);
            for (int i = 0; i < chunkCount; i++)
                SpawnBurningChunk(epicenter, power, rng);
        }

        private static void SpawnEmberParticle(Vector2 epicenter, float power,
            System.Random rng)
        {
            Vector2 offset = rng.InsideUnitCircle() * 0.4f;
            Vector2 pos = epicenter + offset;

            float heat = rng.Range(0.6f, 1f);
            Color emberColor = new Color(
                1f,
                Mathf.Lerp(0.3f, 0.7f, heat),
                Mathf.Lerp(0f, 0.2f, heat * 0.5f),
                0.95f);

            var visual = new VisualParticleParams(
                rng.Range(0.03f, 0.08f),
                emberColor, 13,
                ShrapnelVisuals.TriangleShape.Needle);

            var emission = new EmissionParams(
                new Color(2f, Mathf.Lerp(0.5f, 1.2f, heat), 0.1f));

            // Arc outward and upward, then fall
            float angle = rng.NextAngle();
            float speed = rng.Range(4f, 12f) * (0.7f + power * 0.5f);
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle) * 0.5f + 0.5f);
            Vector2 velocity = dir * speed;

            var physics = AshPhysicsParams.Ember(
                velocity,
                rng.Range(2f, 5f),
                gravity: rng.Range(1.5f, 3f),
                drag: 0.2f,
                turbulence: 0.3f,
                wind: new Vector2(rng.Range(-0.1f, 0.1f), 0f),
                thermalLift: rng.Range(-0.1f, 0.2f));

            // WHY: Embers glow — self-luminous, always visible in dark
            ParticleHelper.SpawnUnlit("Ember", pos, visual, physics,
                rng.Range(0f, 100f), emission);
        }

        private static void SpawnBurningChunk(Vector2 epicenter, float power,
            System.Random rng)
        {
            Vector2 offset = rng.InsideUnitCircle() * 0.3f;
            Vector2 pos = epicenter + offset;

            Color chunkColor = new Color(
                rng.Range(0.8f, 1f),
                rng.Range(0.4f, 0.6f),
                rng.Range(0.1f, 0.25f),
                0.9f);

            var visual = new VisualParticleParams(
                rng.Range(0.08f, 0.18f),
                chunkColor, 12,
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));

            var emission = new EmissionParams(new Color(1.5f, 0.6f, 0.1f));

            // Slower, heavier arc
            float angle = rng.NextAngle();
            float speed = rng.Range(3f, 8f) * (0.5f + power * 0.5f);
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Abs(Mathf.Sin(angle)) * 0.7f + 0.3f);
            Vector2 velocity = dir * speed;

            var physics = AshPhysicsParams.Ember(
                velocity,
                rng.Range(4f, 10f),
                gravity: rng.Range(2f, 4f),
                drag: 0.15f,
                turbulence: 0.2f,
                wind: Vector2.zero,
                thermalLift: 0f);

            // WHY: Burning chunks glow — self-luminous
            ParticleHelper.SpawnUnlit("BurningChunk", pos, visual, physics,
                rng.Range(0f, 100f), emission);
        }

        // ══════════════════════════════════════════════════════════════════
        //  CRATER DUST — Lingering low particles at epicenter
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns lingering dust cloud at ground level around crater.
        /// Very slow movement, high turbulence, long lifetime.
        /// Uses LitMaterial (inert dust, dark in shadows).
        /// </summary>
        public static void SpawnCraterDust(Vector2 epicenter, float range, float power, System.Random rng)
        {
            float countMult = ShrapnelConfig.CraterDustCountMultiplier.Value;
            int count = Mathf.RoundToInt(Mathf.Lerp(30, 80, power) * countMult);

            Color baseColor = GetCraterDustColor(epicenter, rng);

            for (int i = 0; i < count; i++)
                SpawnCraterDustParticle(epicenter, range, baseColor, rng);

            // Dense center dust
            int centerCount = Mathf.RoundToInt(15 * countMult);
            for (int i = 0; i < centerCount; i++)
                SpawnCenterDustParticle(epicenter, baseColor, rng);
        }

        private static void SpawnCraterDustParticle(Vector2 epicenter, float range,
            Color baseColor, System.Random rng)
        {
            float spawnRadius = range * rng.Range(0.3f, 1.2f);
            Vector2 offset = rng.InsideUnitCircle() * spawnRadius;
            offset.y = Mathf.Abs(offset.y) * -0.3f;

            Vector2 pos = epicenter + offset;

            Color dustColor = new Color(
                baseColor.r * rng.Range(0.85f, 1.15f),
                baseColor.g * rng.Range(0.85f, 1.15f),
                baseColor.b * rng.Range(0.85f, 1.15f),
                rng.Range(0.25f, 0.5f));

            Vector2 velocity = new Vector2(
                rng.Range(-0.5f, 0.5f),
                rng.Range(0.1f, 0.6f));

            float lifetime = rng.Range(15f, 35f) * ShrapnelConfig.CraterDustLifetimeMultiplier.Value;

            var visual = new VisualParticleParams(
                rng.Range(0.08f, 0.25f),
                dustColor, 5,
                ShrapnelVisuals.TriangleShape.Chunk);

            var physics = AshPhysicsParams.Smoke(
                velocity, lifetime,
                gravity: rng.Range(0.02f, 0.08f),
                drag: 0.7f,
                turbulence: rng.Range(0.8f, 1.5f),
                wind: new Vector2(rng.Range(-0.15f, 0.15f), 0.05f),
                thermalLift: rng.Range(0.05f, 0.2f));

            // WHY: Dust is inert — dark in dark areas
            ParticleHelper.SpawnLit("CraterDust", pos, visual, physics,
                rng.Range(0f, 100f));
        }

        private static void SpawnCenterDustParticle(Vector2 epicenter,
            Color baseColor, System.Random rng)
        {
            Vector2 offset = rng.InsideUnitCircle() * 0.8f;
            Vector2 pos = epicenter + offset;

            Color dustColor = new Color(
                baseColor.r * 0.7f,
                baseColor.g * 0.7f,
                baseColor.b * 0.7f,
                rng.Range(0.4f, 0.65f));

            Vector2 velocity = new Vector2(
                rng.Range(-0.3f, 0.3f),
                rng.Range(0.2f, 0.8f));

            float lifetime = rng.Range(20f, 40f) * ShrapnelConfig.CraterDustLifetimeMultiplier.Value;

            var visual = new VisualParticleParams(
                rng.Range(0.12f, 0.35f),
                dustColor, 5,
                ShrapnelVisuals.TriangleShape.Chunk);

            var physics = AshPhysicsParams.Smoke(
                velocity, lifetime,
                gravity: 0.03f,
                drag: 0.8f,
                turbulence: 1.2f,
                wind: new Vector2(rng.Range(-0.1f, 0.1f), 0.08f),
                thermalLift: 0.15f);

            ParticleHelper.SpawnLit("CenterDust", pos, visual, physics,
                rng.Range(0f, 100f));
        }

        private static Color GetCraterDustColor(Vector2 epicenter, System.Random rng)
        {
            // Try to sample ground block for color via BlockClassifier
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
                        // WHY: Use BlockClassifier for consistent color across all systems
                        MaterialCategory cat = BlockClassifier.Classify(info);
                        return BlockClassifier.GetColor(cat, rng);
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