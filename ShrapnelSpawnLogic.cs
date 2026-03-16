using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Single entry point for explosion shrapnel spawning.
    ///
    /// Key features:
    /// - Stratified angular distribution (8 sectors, no clustering)
    /// - Weight distribution: 25% Hot, 15% pyrotechnic, 60% random
    /// - Biome-aware effects (desert dust, cold steam)
    /// - Block debris from solid blocks in blast radius
    /// - Bottom sector blocked for ground-placed mines
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
        private const int BottomSectorIndex = 5;
        private const float HotFraction = 0.25f;
        private const float PyrotechnicFraction = 0.15f;

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
        /// WHY: Prevents stale position/frame from previous session
        /// blocking first explosion in new session.
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
                int activeSectors = bottomBlocked ? (AngularSectors - 1) : AngularSectors;

                int hotCount = Mathf.Max(activeSectors, Mathf.CeilToInt(count * HotFraction));
                int pyroCount = Mathf.Max(1, Mathf.CeilToInt(count * PyrotechnicFraction));
                int randomCount = Mathf.Max(0, count - hotCount - pyroCount);

                // Phase 1: Hot shrapnel (stratified across sectors)
                int hotSpawned = 0;
                try
                {
                    hotSpawned = SpawnHotShrapnel(param.position, speed, type, rng,
                        hotCount, activeSectors, bottomBlocked);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase1 Hot: {e.Message}"); }

                // Phase 2: Pyrotechnic visual shrapnel
                try
                {
                    SpawnPyrotechnic(param.position, speed, type, rng, pyroCount, bottomBlocked);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase2 Pyro: {e.Message}"); }

                // Phase 3: Random weight shrapnel
                try
                {
                    SpawnRandomWeight(param.position, speed, type, rng, randomCount,
                        hotCount, bottomBlocked);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase3 Random: {e.Message}"); }

                // Phase 4: Secondary shrapnel from destroyed blocks
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
                try
                {
                    for (int i = 0; i < visualCount; i++)
                        ShrapnelFactory.SpawnVisual(param.position, speed, type, rng);
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

                // Phase 10: Advanced effects (smoke column, fire embers, crater dust)
                try
                {
                    ExplosionEffectsLogic.SpawnAllEffects(param.position, param.range,
                        param.structuralDamage, rng);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[ShrapnelMod] Phase10 AdvancedEffects: {e.Message}"); }

                // Log spawn counts for diagnostics
                Debug.Log($"[ShrapnelMod] SPAWNED Hot:{hotSpawned} Pyro:{pyroCount} Rnd:{randomCount}" +
                          $" V:{visualCount} S:{secondarySpawned} BlkD:{blockDebrisCount}" +
                          $" A:{ashCount} Total:{hotSpawned + pyroCount + randomCount + secondarySpawned}" +
                          $" Temp:{ambientTemp:F0}" +
                          $" Phys:{DebrisTracker.PhysicalCount} Vis:{DebrisTracker.VisualCount}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelMod] Logic Error: {e.Message}\n{e.StackTrace}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  BLOCK DEBRIS — particles from solid blocks destroyed by explosion
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns debris particles from solid blocks within blast radius.
        /// Scales with explosion power (structuralDamage).
        /// </summary>
        private static int SpawnBlockDebris(ExplosionParams param, System.Random rng)
        {
            Material unlitMat = ShrapnelVisuals.UnlitMaterial;
            if (unlitMat == null) return 0;

            if (param.structuralDamage < 500f) return 0;

            float powerFactor = Mathf.Clamp01(param.structuralDamage / 2000f);
            int maxSamples = Mathf.RoundToInt(20 + powerFactor * 40);
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
                        SpawnSingleBlockDebris(samplePos, param.position, info, rng, unlitMat);
                    }
                    spawned += debrisPerBlock;
                }
                catch (IndexOutOfRangeException) { }
            }

            return spawned;
        }

        private static void SpawnSingleBlockDebris(Vector2 blockWorldPos, Vector2 epicenter,
            BlockInfo info, System.Random rng, Material mat)
        {
            GameObject debris = new GameObject("BlkDebris");
            debris.transform.position = blockWorldPos + rng.InsideUnitCircle() * 0.3f;
            debris.transform.localScale = Vector3.one * rng.Range(0.05f, 0.18f);

            SpriteRenderer sr = debris.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            sr.sharedMaterial = mat;
            sr.sortingOrder = 11;

            Color debrisColor = GetBlockDebrisColor(info, rng);

            Vector2 awayDir = (blockWorldPos - epicenter).normalized;
            if (awayDir.sqrMagnitude < 0.01f)
                awayDir = rng.OnUnitCircle();

            float spreadAngle = rng.Range(-45f, 45f) * Mathf.Deg2Rad;
            Vector2 finalDir = MathHelper.RotateDirection(awayDir, spreadAngle);

            finalDir.y = Mathf.Max(finalDir.y, 0.2f);
            finalDir.Normalize();

            float speed = rng.Range(4f, 12f);
            Vector2 velocity = finalDir * speed;

            AshParticle ash = debris.AddComponent<AshParticle>();
            ash.InitializeFull(
                velocity,
                rng.Range(1.5f, 4f),
                debrisColor,
                gravity: rng.Range(0.8f, 1.5f),
                drag: 0.3f,
                turbulenceStrength: 0.4f,
                turbulenceScale: 1.5f,
                wind: new Vector2(rng.Range(-0.2f, 0.2f), 0f),
                thermalLift: 0.2f,
                perlinSeed: rng.Range(0f, 100f));

            DebrisTracker.RegisterVisual(debris);
        }

        /// <summary>Zero-alloc string contains check.</summary>
        private static bool ContainsIgnoreCase(string source, string value)
        {
            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Color GetBlockDebrisColor(BlockInfo info, System.Random rng)
        {
            if (info.metallic)
            {
                float gray = rng.Range(0.3f, 0.5f);
                return new Color(gray, gray, gray, 0.9f);
            }

            string name = info.name ?? string.Empty;

            if (ContainsIgnoreCase(name, "stone") || ContainsIgnoreCase(name, "rock"))
            {
                float g = rng.Range(0.4f, 0.6f);
                return new Color(g, g * 0.95f, g * 0.9f, 0.85f);
            }

            if (ContainsIgnoreCase(name, "wood"))
            {
                return new Color(
                    rng.Range(0.35f, 0.5f),
                    rng.Range(0.2f, 0.35f),
                    rng.Range(0.1f, 0.2f),
                    0.9f);
            }

            if (ContainsIgnoreCase(name, "sand"))
            {
                return new Color(
                    rng.Range(0.7f, 0.85f),
                    rng.Range(0.6f, 0.7f),
                    rng.Range(0.35f, 0.45f),
                    0.8f);
            }

            float gray2 = rng.Range(0.35f, 0.55f);
            return new Color(gray2, gray2, gray2, 0.85f);
        }

        // ══════════════════════════════════════════════════════════════════
        //  STRATIFIED SPAWNING
        // ══════════════════════════════════════════════════════════════════

        private static int SpawnHotShrapnel(Vector2 epicenter, float speed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int hotCount, int activeSectors, bool bottomBlocked)
        {
            int hotPerSector = Mathf.Max(1, hotCount / activeSectors);
            int hotSpawned = 0;

            for (int sector = 0; sector < AngularSectors; sector++)
            {
                if (bottomBlocked && sector == BottomSectorIndex) continue;

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

        private static void SpawnPyrotechnic(Vector2 epicenter, float speed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int count, bool bottomBlocked)
        {
            for (int i = 0; i < count; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                ShrapnelFactory.SpawnVisual(epicenter, speed, type, rng, angle);
            }
        }

        private static void SpawnRandomWeight(Vector2 epicenter, float speed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int count, int startIndex, bool bottomBlocked)
        {
            for (int i = 0; i < count; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                ShrapnelWeight weight = RollRandomWeight(type, rng);
                Vector2 dir = MathHelper.AngleToDirection(angle);
                ShrapnelFactory.SpawnDirectional(epicenter, speed, type,
                    weight, startIndex + i, rng, dir);
            }
        }

        private static int GetRandomActiveSector(bool bottomBlocked, System.Random rng)
        {
            if (!bottomBlocked)
                return rng.Next(0, AngularSectors);

            int sector = rng.Next(0, AngularSectors - 1);
            if (sector >= BottomSectorIndex) sector++;
            return sector;
        }

        private static bool IsBottomBlocked(Vector2 position)
        {
            try
            {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(
                    position + Vector2.down * 0.5f);
                return WorldGeneration.world.GetBlock(blockPos) != 0;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════════
        //  BIOME EFFECTS
        // ══════════════════════════════════════════════════════════════════

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
                    if (info?.name != null && ContainsIgnoreCase(info.name, "sand"))
                        sandCount++;
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

                ParticleHelper.SpawnAshParticle("DesertDust", position, visual, physics,
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

                ParticleHelper.SpawnAshParticle("ColdSteam", position, visual, physics,
                    rng.Range(0f, 100f));
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════════════════

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

                    string blockName = info.name ?? string.Empty;

                    var secType = info.metallic
                        ? ShrapnelProjectile.ShrapnelType.HeavyMetal
                        : (info.health < 100f && !ContainsIgnoreCase(blockName, "stone")
                            ? ShrapnelProjectile.ShrapnelType.Wood
                            : ShrapnelProjectile.ShrapnelType.Stone);

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