using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Effects;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Logic
{
    /// <summary>
    /// Single entry point for explosion shrapnel spawning.
    ///
    /// Key features:
    /// - Stratified angular distribution (8 sectors, no clustering)
    /// - Weight distribution: 25% Hot, 15% pyrotechnic, 60% random
    /// - Biome-aware effects (desert dust, cold steam)
    /// - Block debris from destroyed blocks (material-aware colors)
    /// - Bottom cone blocked for ground-placed mines (sectors 5,6,7)
    /// - Advanced effects: smoke column, fire embers, crater dust
    ///
    /// Deterministic via System.Random with coordinate seed + mod version.
    /// </summary>
    public static class ShrapnelSpawnLogic
    {
        private static Vector2 _lastSpawnPos = new Vector2(float.MinValue, float.MinValue);
        private static int _lastSpawnFrame = -999;

        /// <summary>Maximum shrapnel speed from config.</summary>
        public static float GlobalMaxSpeed => ShrapnelConfig.GlobalMaxSpeed.Value;

        private const int AngularSectors = 8;
        private const float HotFraction = 0.25f;
        private const float PyrotechnicFraction = 0.15f;

        // WHY: Mines on flat ground block 3 bottom sectors (5,6,7 = 225°-360°)
        // giving a symmetric ~180° upward cone.
        private const int BottomConeStartSector = 5;
        private const int BottomConeSectorCount = 3; // sectors 5,6,7

        // ── CONSTANTS ──
        private const float BlockDebrisSpread = 0.3f;
        private const float AshCloudSpread = 0.5f;

        private static int MakeSeed(Vector2 position)
        {
            int versionHash = Plugin.Version.GetHashCode();
            return unchecked(
                (int)(position.x * 1000f) * 397 ^
                (int)(position.y * 1000f) ^
                versionHash);
        }

        private static bool TryRegisterSpawn(Vector2 pos)
        {
            int frame = Time.frameCount;
            if (frame == _lastSpawnFrame &&
                Vector2.Distance(pos, _lastSpawnPos) < ShrapnelConfig.MinDistanceBetweenSpawns.Value)
                return false;

            _lastSpawnPos = pos;
            _lastSpawnFrame = frame;
            return true;
        }

        /// <summary>Wrapper for Transpiler — creates shrapnel then real explosion.</summary>
        public static void CustomCreateExplosion(ExplosionParams param)
        {
            TrySpawnFromExplosion(param);
            WorldGeneration.CreateExplosion(param);
        }

        /// <summary>
        /// Resets spawn throttle state. Call on scene/world load.
        /// </summary>
        public static void ResetThrottle()
        {
            _lastSpawnPos = new Vector2(float.MinValue, float.MinValue);
            _lastSpawnFrame = -999;
        }

        /// <summary>Main spawn entry point.</summary>
        public static void TrySpawnFromExplosion(ExplosionParams param)
        {
            try
            {
                if (!TryRegisterSpawn(param.position)) return;

                ExplosionLogger.Record(param);

                int seed = MakeSeed(param.position);
                System.Random rng = new System.Random(seed);

                float spawnMult = ShrapnelConfig.SpawnCountMultiplier.Value;

                ClassifyExplosion(param, rng, out var type, out int count,
                    out float speed, out int visualCount);

                count = Mathf.Max(1, Mathf.RoundToInt(count * spawnMult));
                visualCount = Mathf.Max(1, Mathf.RoundToInt(visualCount * spawnMult));

                float ambientTemp = GetAmbientTemperature();

                if (ambientTemp < 5f)
                    visualCount = (int)(visualCount * 0.7f);
                else if (ambientTemp > 25f)
                    visualCount = (int)(visualCount * 1.3f);

                bool bottomBlocked = IsBottomBlocked(param.position);

                int blockedCount = bottomBlocked ? BottomConeSectorCount : 0;
                int activeSectors = AngularSectors - blockedCount;

                int hotCount = Mathf.Max(activeSectors, Mathf.CeilToInt(count * HotFraction));
                int pyroCount = Mathf.Max(1, Mathf.CeilToInt(count * PyrotechnicFraction));
                int randomCount = Mathf.Max(0, count - hotCount - pyroCount);

                // Phase 1: Hot shrapnel (stratified)
                int hotSpawned = 0;
                try
                {
                    hotSpawned = SpawnHotShrapnel(param.position, speed, type, rng,
                        hotCount, activeSectors, bottomBlocked);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase1 Hot: {e.Message}"); }

                // Phase 2: Pyrotechnic visual shrapnel
                int pyroSpawned = 0;
                try
                {
                    pyroSpawned = SpawnPyrotechnic(param.position, speed, type, rng, pyroCount, bottomBlocked);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase2 Pyro: {e.Message}"); }

                // Phase 3: Random weight shrapnel
                int randomSpawned = 0;
                try
                {
                    randomSpawned = SpawnRandomWeight(param.position, speed, type, rng, randomCount,
                        hotCount, bottomBlocked);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase3 Random: {e.Message}"); }

                // Phase 4: Secondary from blocks
                int secondarySpawned = 0;
                try
                {
                    secondarySpawned = SpawnSecondaryFromBlocks(param, rng, type, speed);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase4 Secondary: {e.Message}"); }

                // Phase 5: Block debris particles
                int blockDebrisCount = 0;
                try
                {
                    blockDebrisCount = SpawnBlockDebris(param, rng);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase5 BlockDebris: {e.Message}"); }

                // Phase 6: Visual shrapnel (non-physics)
                int visualSpawned = 0;
                try
                {
                    for (int i = 0; i < visualCount; i++)
                    {
                        ShrapnelFactory.SpawnVisual(param.position, speed, type, rng);
                        visualSpawned++;
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase6 Visual: {e.Message}"); }

                // Phase 7: Ash cloud
                int ashCount = 0;
                try
                {
                    ashCount = Mathf.Max(1, Mathf.RoundToInt(GetAshCount(type, rng) * spawnMult));
                    ShrapnelFactory.SpawnAshCloud(param.position, ashCount, type, rng);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase7 Ash: {e.Message}"); }

                // Phase 8: Ground surface debris
                try
                {
                    GroundDebrisLogic.SpawnFromExplosion(param.position, param.range, rng);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase8 Ground: {e.Message}"); }

                // Phase 9: Biome-specific effects
                try
                {
                    SpawnBiomeEffects(param.position, param.range, ambientTemp, rng, spawnMult);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase9 Biome: {e.Message}"); }

                // Phase 10: Advanced effects (smoke, embers, crater dust)
                try
                {
                    ExplosionEffectsLogic.SpawnAllEffects(param.position, param.range,
                        param.structuralDamage, rng);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase10 Effects: {e.Message}"); }

                Debug.Log($"[ShrapnelMod] SPAWNED Hot:{hotSpawned} Pyro:{pyroSpawned} Rnd:{randomSpawned}" +
                          $" V:{visualSpawned} S:{secondarySpawned} BlkD:{blockDebrisCount}" +
                          $" A:{ashCount} Total:{hotSpawned + pyroSpawned + randomSpawned + secondarySpawned}" +
                          $" Temp:{ambientTemp:F0} Bottom:{bottomBlocked}" +
                          $" Phys:{DebrisTracker.PhysicalCount} Vis:{DebrisTracker.VisualCount}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelMod] Logic Error: {e.Message}\n{e.StackTrace}");
            }
        }

        //  BLOCK DEBRIS — inert particles from destroyed blocks

        /// <summary>
        /// Spawns debris particles from solid blocks within blast radius.
        /// Uses material-aware colors via BlockClassifier.
        /// LitMaterial — debris is inert, should be dark in dark areas.
        /// </summary>
        private static int SpawnBlockDebris(ExplosionParams param, System.Random rng)
        {
            if (param.structuralDamage < 500f) return 0;

            float powerFactor = Mathf.Clamp01(param.structuralDamage / 2000f);
            float blockDebrisMult = ShrapnelConfig.BlockDebrisCountMultiplier.Value;

            int maxSamples = Mathf.RoundToInt((20 + powerFactor * 40) * blockDebrisMult);
            int spawned = 0;

            for (int i = 0; i < maxSamples; i++)
            {
                Vector2 offset = rng.InsideUnitCircle() * param.range;
                Vector2 samplePos = param.position + offset;

                try
                {
                    Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(samplePos);
                    ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                    if (blockId == 0) continue;

                    BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                    if (info == null) continue;

                    int debrisPerBlock = rng.Range(3, 8);
                    for (int j = 0; j < debrisPerBlock; j++)
                    {
                        SpawnSingleBlockDebris(samplePos, param.position, info, rng);
                        spawned++;
                    }
                }
                catch (IndexOutOfRangeException) { }
            }

            return spawned;
        }

        private static void SpawnSingleBlockDebris(Vector2 blockWorldPos, Vector2 epicenter,
            BlockInfo info, System.Random rng)
        {
            Vector2 position = blockWorldPos + rng.InsideUnitCircle() * BlockDebrisSpread;

            MaterialCategory cat = BlockClassifier.Classify(info);
            Color debrisColor = BlockClassifier.GetColorWithAlpha(cat, rng, 0.85f);

            Vector2 awayDir = (blockWorldPos - epicenter).normalized;
            if (awayDir.sqrMagnitude < 0.01f)
                awayDir = rng.OnUnitCircle();

            float spreadAngle = rng.Range(-45f, 45f) * Mathf.Deg2Rad;
            Vector2 finalDir = MathHelper.RotateDirection(awayDir, spreadAngle);

            // WHY: Upward bias — debris doesn't fly straight down into ground
            finalDir.y = Mathf.Max(finalDir.y, 0.2f);
            finalDir.Normalize();

            float speed = rng.Range(4f, 12f);
            Vector2 velocity = finalDir * speed;

            var visual = new VisualParticleParams(
                rng.Range(0.05f, 0.18f),
                debrisColor,
                11,
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));

            var physics = new AshPhysicsParams(
                velocity,
                lifetime: rng.Range(1.5f, 4f),
                gravity: rng.Range(0.8f, 1.5f),
                drag: 0.3f,
                turbulenceStrength: 0.4f,
                turbulenceScale: 1.5f,
                wind: new Vector2(rng.Range(-0.2f, 0.2f), 0f),
                thermalLift: 0.2f);

            // WHY: LitMaterial — inert debris is dark in dark areas
            ParticleHelper.SpawnLit("BlkDebris", position, visual, physics,
                rng.Range(0f, 100f));
        }

        //  SECTOR BLOCKING

        /// <summary>
        /// Checks if a sector index is blocked when bottom is blocked.
        /// Blocked sectors: 5,6,7 (225°-360°).
        /// Active sectors: 0,1,2,3,4 (0°-225°).
        /// </summary>
        private static bool IsSectorBlocked(int sector, bool bottomBlocked)
        {
            if (!bottomBlocked) return false;
            return sector >= BottomConeStartSector
                && sector < BottomConeStartSector + BottomConeSectorCount;
        }

        //  STRATIFIED SPAWNING

        private static int SpawnHotShrapnel(Vector2 epicenter, float speed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int hotCount, int activeSectors, bool bottomBlocked)
        {
            int hotPerSector = Mathf.Max(1, hotCount / activeSectors);
            int hotSpawned = 0;

            for (int sector = 0; sector < AngularSectors; sector++)
            {
                if (IsSectorBlocked(sector, bottomBlocked)) continue;

                int toSpawn = Mathf.Min(hotPerSector, hotCount - hotSpawned);
                for (int j = 0; j < toSpawn; j++)
                {
                    float angle = rng.AngleInSector(sector, AngularSectors);
                    Vector2 dir = MathHelper.AngleToDirection(angle);
                    ShrapnelFactory.SpawnDirectional(epicenter, speed, type,
                        ShrapnelWeight.Hot, hotSpawned, rng, dir);
                    hotSpawned++;
                }
            }

            while (hotSpawned < hotCount)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);
                ShrapnelFactory.SpawnDirectional(epicenter, speed, type,
                    ShrapnelWeight.Hot, hotSpawned, rng, dir);
                hotSpawned++;
            }

            return hotSpawned;
        }

        private static int SpawnPyrotechnic(Vector2 epicenter, float speed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int count, bool bottomBlocked)
        {
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                ShrapnelFactory.SpawnVisual(epicenter, speed, type, rng, angle);
                spawned++;
            }
            return spawned;
        }

        private static int SpawnRandomWeight(Vector2 epicenter, float speed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int count, int startIndex, bool bottomBlocked)
        {
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                ShrapnelWeight weight = RollRandomWeight(type, rng);
                Vector2 dir = MathHelper.AngleToDirection(angle);
                ShrapnelFactory.SpawnDirectional(epicenter, speed, type,
                    weight, startIndex + i, rng, dir);
                spawned++;
            }
            return spawned;
        }

        /// <summary>
        /// Returns a random non-blocked sector index.
        /// Active sectors when bottom blocked: 0,1,2,3,4.
        /// </summary>
        private static int GetRandomActiveSector(bool bottomBlocked, System.Random rng)
        {
            if (!bottomBlocked)
                return rng.Next(0, AngularSectors);

            // WHY: With 3 blocked sectors (5,6,7), active sectors are 0,1,2,3,4.
            int activeSectors = AngularSectors - BottomConeSectorCount;
            int slot = rng.Next(0, activeSectors);

            int found = 0;
            for (int s = 0; s < AngularSectors; s++)
            {
                if (IsSectorBlocked(s, true)) continue;
                if (found == slot) return s;
                found++;
            }

            return 0; // fallback
        }

        /// <summary>
        /// Checks if there is solid ground below explosion epicenter.
        /// Scans 4 positions downward to handle mines placed 0.5-3 units above blocks.
        /// </summary>
        private static bool IsBottomBlocked(Vector2 position)
        {
            try
            {
                // WHY: Mines explode 0.5-3 units above the block they're placed on.
                // Explosion position.y might be above block boundary, so we check
                // 4 positions below at 0.3, 1.3, 2.3, 3.3 block offsets to reliably
                // detect ground even if epicenter is floating above it.
                for (int i = 0; i < 4; i++)
                {
                    Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(
                        position + Vector2.down * (0.3f + i));
                    if (WorldGeneration.world.GetBlock(blockPos) != 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        //  BIOME EFFECTS

        private static void SpawnBiomeEffects(Vector2 epicenter, float range,
            float temperature, System.Random rng, float spawnMult)
        {
            try
            {
                if (IsDesertBiome(epicenter))
                {
                    int extraDust = Mathf.RoundToInt(rng.Range(40, 80) * spawnMult);
                    SpawnDesertDust(epicenter, range, rng, extraDust);
                }

                if (temperature < 5f)
                {
                    float tempFactor = Mathf.InverseLerp(5f, -20f, temperature);
                    int steamCount = Mathf.RoundToInt(rng.Range(20, 45) * spawnMult * (1f + tempFactor));
                    SpawnColdSteam(epicenter, range, rng, steamCount);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ShrapnelMod] BiomeEffects: {e.Message}");
            }
        }

        private static bool IsDesertBiome(Vector2 position)
        {
            int sandCount = 0;
            try
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(
                        position + new Vector2(dx, -1f));
                    ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                    if (blockId == 0) continue;

                    BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                    MaterialCategory cat = BlockClassifier.Classify(info);
                    if (cat == MaterialCategory.Sand) sandCount++;
                }
            }
            catch { }
            return sandCount >= 2;
        }

        private static void SpawnDesertDust(Vector2 epicenter, float range,
            System.Random rng, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Vector2 offset = rng.InsideUnitCircle() * range * 1.2f;
                Vector2 position = epicenter + offset + Vector2.down * rng.Range(0f, 1f);

                Color color = new Color(
                    rng.Range(0.7f, 0.85f),
                    rng.Range(0.6f, 0.72f),
                    rng.Range(0.35f, 0.48f),
                    rng.Range(0.35f, 0.65f));

                Vector2 vel = new Vector2(rng.Range(-2.5f, 2.5f), rng.Range(0.5f, 4f));

                var visual = new VisualParticleParams(rng.Range(0.08f, 0.28f), color, 11,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var physics = AshPhysicsParams.DesertDust(vel, rng.Range(8f, 25f), rng);

                // WHY: LitMaterial — sand dust is inert, should be dark in dark areas
                ParticleHelper.SpawnLit("DesertDust", position, visual, physics,
                    rng.Range(0f, 100f));
            }
        }

        private static void SpawnColdSteam(Vector2 epicenter, float range,
            System.Random rng, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Vector2 position = epicenter + rng.InsideUnitCircle() * range * 0.6f;

                float gray = rng.Range(0.82f, 0.98f);
                Color color = new Color(gray, gray, gray, rng.Range(0.25f, 0.55f));

                Vector2 vel = new Vector2(rng.Range(-0.6f, 0.6f), rng.Range(1.5f, 5f));

                var visual = new VisualParticleParams(rng.Range(0.06f, 0.18f), color, 12,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var physics = AshPhysicsParams.ColdSteam(vel, rng.Range(4f, 10f), rng);

                // WHY: LitMaterial — water vapor is not self-luminous
                ParticleHelper.SpawnLit("ColdSteam", position, visual, physics,
                    rng.Range(0f, 100f));
            }
        }

        //  HELPERS

        private static float GetAmbientTemperature()
        {
            try { return WorldGeneration.world.ambientTemperature; }
            catch { return 20f; }
        }

        private static int GetAshCount(ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            switch (type)
            {
                case ShrapnelProjectile.ShrapnelType.Stone: return rng.Range(50, 80);
                case ShrapnelProjectile.ShrapnelType.HeavyMetal: return rng.Range(20, 40);
                default: return rng.Range(35, 60);
            }
        }

        private static int SpawnSecondaryFromBlocks(ExplosionParams param, System.Random rng,
            ShrapnelProjectile.ShrapnelType primaryType, float primarySpeed)
        {
            int spawned = 0;
            const int maxSamples = 20;
            const int maxSecondary = 20;

            for (int i = 0; i < maxSamples && spawned < maxSecondary; i++)
            {
                Vector2 offset = rng.InsideUnitCircle() * param.range;
                Vector2 samplePos = param.position + offset;

                try
                {
                    Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(samplePos);
                    ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                    if (blockId == 0) continue;

                    BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                    if (info == null) continue;

                    MaterialCategory cat = BlockClassifier.Classify(info);

                    var secType = (cat == MaterialCategory.Metal)
                        ? ShrapnelProjectile.ShrapnelType.HeavyMetal
                        : (info.health < 100f && cat != MaterialCategory.Rock && cat != MaterialCategory.Concrete)
                            ? ShrapnelProjectile.ShrapnelType.Wood
                            : ShrapnelProjectile.ShrapnelType.Stone;

                    ShrapnelWeight weight = RollWeight(secType, i, maxSamples, rng);
                    ShrapnelFactory.Spawn(samplePos, primarySpeed * 0.4f, secType, weight, i, rng);
                    spawned++;
                }
                catch (IndexOutOfRangeException) { }
            }

            return spawned;
        }

        private static ShrapnelWeight RollRandomWeight(
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            float roll = rng.NextFloat();
            ShrapnelWeight weight;

            if (roll < 0.40f) weight = ShrapnelWeight.Medium;
            else if (roll < 0.80f) weight = ShrapnelWeight.Heavy;
            else weight = ShrapnelWeight.Massive;

            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Massive)
                weight = ShrapnelWeight.Heavy;

            return weight;
        }

        private static ShrapnelWeight RollWeight(ShrapnelProjectile.ShrapnelType type,
            int index, int total, System.Random rng)
        {
            ShrapnelWeight weight;

            if (index < Mathf.CeilToInt(total * HotFraction))
            {
                weight = ShrapnelWeight.Hot;
            }
            else
            {
                float roll = rng.NextFloat();
                if (roll < 0.15f) weight = ShrapnelWeight.Hot;
                else if (roll < 0.45f) weight = ShrapnelWeight.Medium;
                else if (roll < 0.85f) weight = ShrapnelWeight.Heavy;
                else weight = ShrapnelWeight.Massive;
            }

            if (type == ShrapnelProjectile.ShrapnelType.HeavyMetal && weight == ShrapnelWeight.Hot)
                weight = ShrapnelWeight.Medium;
            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Heavy)
                weight = ShrapnelWeight.Medium;
            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Massive)
                weight = ShrapnelWeight.Heavy;

            return weight;
        }

        private static void ClassifyExplosion(ExplosionParams p, System.Random rng,
            out ShrapnelProjectile.ShrapnelType type, out int count,
            out float speed, out int visualCount)
        {
            float eps = ShrapnelConfig.ClassifyEpsilon.Value;
            float speedBoost = ShrapnelConfig.GlobalSpeedBoost.Value;

            if (Mathf.Abs(p.range - ShrapnelConfig.DynamiteRange.Value) < eps &&
                Mathf.Abs(p.structuralDamage - ShrapnelConfig.DynamiteStructuralDamage.Value) < eps)
            {
                type = ShrapnelProjectile.ShrapnelType.Stone;
                count = rng.Range(ShrapnelConfig.DynamitePrimaryMin.Value,
                                  ShrapnelConfig.DynamitePrimaryMax.Value);
                speed = ShrapnelConfig.DynamiteSpeed.Value * speedBoost;
                visualCount = rng.Range(ShrapnelConfig.DynamiteVisualMin.Value,
                                        ShrapnelConfig.DynamiteVisualMax.Value);
            }
            else if (Mathf.Abs(p.range - ShrapnelConfig.TurretRange.Value) < eps &&
                     Mathf.Abs(p.velocity - ShrapnelConfig.TurretVelocity.Value) < eps)
            {
                type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                count = rng.Range(ShrapnelConfig.TurretPrimaryMin.Value,
                                  ShrapnelConfig.TurretPrimaryMax.Value);
                speed = ShrapnelConfig.TurretSpeed.Value * speedBoost;
                visualCount = rng.Range(ShrapnelConfig.TurretVisualMin.Value,
                                        ShrapnelConfig.TurretVisualMax.Value);
            }
            else
            {
                type = ShrapnelProjectile.ShrapnelType.Metal;
                count = rng.Range(ShrapnelConfig.MinePrimaryMin.Value,
                                  ShrapnelConfig.MinePrimaryMax.Value);
                speed = ShrapnelConfig.MineSpeed.Value * speedBoost;
                visualCount = rng.Range(ShrapnelConfig.MineVisualMin.Value,
                                        ShrapnelConfig.MineVisualMax.Value);
            }
        }
    }
}