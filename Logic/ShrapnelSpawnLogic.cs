using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Effects;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Logic
{
    /// <summary>
    /// Explosion profile classification and shrapnel orchestration.
    ///
    /// Profiles:
    ///   Mine     — ~225° upward cone, more metallic sparks, blocked bottom
    ///   Dynamite — 360°, larger blast, more ground debris, fewer sparks
    ///   Turret   — 360°, fewer particles overall
    ///   Unknown  — adaptive: sqrt(structuralDamage) × sqrt(range)
    ///
    /// Two-phase spawning:
    ///   PRE-explosion: physics shrapnel, sparks, visual shrapnel, ash
    ///   POST-explosion: ground debris (needs crater surfaces exposed)
    ///
    /// Spark diversity (3 sub-types per emission):
    ///   40% thin/fast needles — brightest, fastest, shortest life
    ///   35% medium trailing — moderate speed, longer trail
    ///   25% thick/hot globs — largest, slowest, most orange, longest life
    ///
    /// Size↔Speed: speed ∝ 1/√(size) enforced in visual spark emission.
    ///   Position rounded to 1 decimal to absorb host/client float drift.
    /// </summary>
    public static class ShrapnelSpawnLogic
    {
        private static Vector2 _lastSpawnPos = new Vector2(float.MinValue, float.MinValue);
        private static int _lastSpawnFrame = -999;

        public static float GlobalMaxSpeed => ShrapnelConfig.GlobalMaxSpeed.Value;

        private const int AngularSectors = 8;
        private const float HotFraction = 0.25f;
        private const float PyrotechnicFraction = 0.15f;
        private const float MicroFraction = 0.10f;
        private const int BottomConeStartSector = 5;
        private const int BottomConeSectorCount = 3;

        /// <summary>
        /// Minimum explosion range to spawn shrapnel.
        /// WHY: Vanilla gravbag battery explosion uses range=0, structuralDamage=0
        /// purely to disfigure the player. Spawning shrapnel at range 0 places
        /// fragments directly inside the player's torso, causing instant lethal
        /// internal bleeding that testers mistook for "fall damage."
        /// </summary>
        private const float MinExplosionRange = 0.1f;

        //  EXPLOSION PROFILE 

        public enum ExplosionProfile { Mine, Dynamite, Turret, Unknown }

        private struct ProfileMultipliers
        {
            public float SparkMult;
            public float GroundDebrisMult;
            public float ShrapnelMult;
            public float VisualMult;

            public static ProfileMultipliers ForProfile(ExplosionProfile p)
            {
                switch (p)
                {
                    case ExplosionProfile.Mine:
                        return new ProfileMultipliers
                        { SparkMult = 1.4f, GroundDebrisMult = 1f, ShrapnelMult = 1f, VisualMult = 1f };
                    case ExplosionProfile.Dynamite:
                        return new ProfileMultipliers
                        { SparkMult = 0.7f, GroundDebrisMult = 2f, ShrapnelMult = 1.2f, VisualMult = 0.8f };
                    case ExplosionProfile.Turret:
                        return new ProfileMultipliers
                        { SparkMult = 0.6f, GroundDebrisMult = 0.7f, ShrapnelMult = 0.8f, VisualMult = 0.6f };
                    default:
                        return new ProfileMultipliers
                        { SparkMult = 1f, GroundDebrisMult = 1f, ShrapnelMult = 1f, VisualMult = 1f };
                }
            }
        }

        //  SEED / THROTTLE 

        /// <summary>
        /// Generates deterministic seed from world position.
        ///
        /// MULTIPLAYER FIX: Time.frameCount removed — frames are NOT synchronized
        /// between host and client. Using frameCount caused different RNG sequences
        /// for the same explosion, producing phantom shards visible to only one peer.
        ///
        /// Position is rounded to 1 decimal place (×10, RoundToInt) to absorb
        /// floating-point drift between host and client. Two machines may compute
        /// slightly different explosion positions (e.g., 15.300001 vs 15.299999).
        /// Rounding to 0.1 units ensures both get the same seed.
        /// </summary>
        private static int MakeSeed(Vector2 position)
        {
            int versionHash = Plugin.Version.GetHashCode();
            // WHY: RoundToInt(x * 10) = 1 decimal place precision.
            // Absorbs ±0.05 world-unit drift between host/client.
            int rx = Mathf.RoundToInt(position.x * 10f);
            int ry = Mathf.RoundToInt(position.y * 10f);
            return unchecked(rx * 397 ^ ry ^ versionHash);
        }

        /// <summary>
        /// Generates a per-shrapnel seed for ShrapnelProjectile._rng.
        ///
        /// MULTIPLAYER FIX: Previously ShrapnelProjectile used Time.frameCount
        /// in its _rng seed (Awake), causing desync in DeterministicRoll results.
        /// Now the seed is generated here from the explosion's deterministic RNG
        /// and passed to each projectile, ensuring identical sequences on all peers.
        /// </summary>
        internal static int MakeShrapnelSeed(System.Random rng)
        {
            return rng.Next();
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

        public static void ResetThrottle()
        {
            _lastSpawnPos = new Vector2(float.MinValue, float.MinValue);
            _lastSpawnFrame = -999;
        }

        //  TWO-PHASE WRAPPER — called from Transpiler

        /// <summary>
        /// PRE-explosion: shrapnel, sparks, visual effects, ash.
        /// Must run BEFORE CreateExplosion destroys blocks.
        ///
        /// BUG FIX: Filters out gravbag ghost explosions (range ≤ 0.1).
        /// These are vanilla disfigure-only explosions with zero blast radius.
        /// Spawning shrapnel at range 0 places fragments inside the player.
        /// </summary>
        public static void PreExplosion(ExplosionParams param)
        {
            Console.Log($"PRE-Explosion: range={param.range:F1} dmg={param.structuralDamage:F1} vel={param.velocity:F1}");
            try
            {
                // BUG FIX: Gravbag ghost explosion filter.
                // Vanilla creates explosions with range=0, structuralDamage=0
                // when a gravbag battery breaks. These exist only to call
                // body.Disfigure(). Spawning shrapnel here is lethal and wrong.
                if (param.range <= MinExplosionRange)
                {
                    if (ShrapnelConfig.DebugLogging.Value)
                        Console.Log($"Skipped ghost explosion:" +
                            $" range={param.range} dmg={param.structuralDamage}");
                    return;
                }

                if (!TryRegisterSpawn(param.position)) return;

                ExplosionLogger.Record(param);
                System.Random rng = new System.Random(MakeSeed(param.position));
                float spawnMult = ShrapnelConfig.SpawnCountMultiplier.Value;

                ClassifyExplosion(param, rng, out var type, out int count,
                    out float speed, out int visualCount, out ExplosionProfile profile);

                ProfileMultipliers pm = ProfileMultipliers.ForProfile(profile);

                count = Mathf.Max(1, Mathf.RoundToInt(count * spawnMult * pm.ShrapnelMult));
                visualCount = Mathf.Max(1, Mathf.RoundToInt(visualCount * spawnMult * pm.VisualMult));

                float ambientTemp = GetAmbientTemperature();
                if (ambientTemp < 5f) visualCount = (int)(visualCount * 0.7f);
                else if (ambientTemp > 25f) visualCount = (int)(visualCount * 1.3f);

                bool bottomBlocked = IsBottomBlocked(param.position);
                int blockedCount = bottomBlocked ? BottomConeSectorCount : 0;
                int activeSectors = AngularSectors - blockedCount;

                int microCount = Mathf.Max(1, Mathf.CeilToInt(count * MicroFraction));
                int hotCount = Mathf.Max(activeSectors, Mathf.CeilToInt(count * HotFraction));
                int pyroCount = Mathf.Max(1, Mathf.CeilToInt(count * PyrotechnicFraction));
                int randomCount = Mathf.Max(0, count - hotCount - pyroCount - microCount);

                int totalSpawned = 0;

                // Phase 1: Hot shrapnel (stratified)
                try
                {
                    totalSpawned += SpawnHotShrapnel(param.position, speed, type, rng,
                    hotCount, activeSectors, bottomBlocked);
                }
                catch (Exception e) { Console.Error($"Hot: {e.Message}"); }

                // Phase 2: Micro shrapnel (visual sparks, no physics)
                try
                {
                    totalSpawned += SpawnMicroShrapnel(param.position, speed, type, rng,
                    microCount, bottomBlocked);
                }
                catch (Exception e) { Console.Error($"Micro: {e.Message}"); }

                // Phase 3: Pyrotechnic with spark diversity
                try
                {
                    totalSpawned += SpawnDiverseSparks(param.position, speed, type, rng,
                    Mathf.RoundToInt(pyroCount * pm.SparkMult), bottomBlocked);
                }
                catch (Exception e) { Console.Error($"Sparks: {e.Message}"); }

                // Phase 4: Random weight shrapnel
                try
                {
                    totalSpawned += SpawnRandomWeight(param.position, speed, type, rng,
                    randomCount, hotCount, bottomBlocked);
                }
                catch (Exception e) { Console.Error($"Random: {e.Message}"); }

                // Phase 5: Secondary from blocks (before destruction)
                try { totalSpawned += SpawnSecondaryFromBlocks(param, rng, type, speed); }
                catch (Exception e) { Console.Error($"Secondary: {e.Message}"); }

                // Phase 6: Block debris particles
                try { SpawnBlockDebris(param, rng); }
                catch (Exception e) { Console.Error($"BlockDebris: {e.Message}"); }

                // Phase 7: Ash cloud
                try
                {
                    int ashCount = Mathf.Max(1, Mathf.RoundToInt(GetAshCount(type, rng) * spawnMult));
                    ShrapnelFactory.SpawnAshCloud(param.position, ashCount, type, rng);
                }
                catch (Exception e) { Console.Error($"Ash: {e.Message}"); }

                // Phase 8: Biome effects
                try { SpawnBiomeEffects(param.position, param.range, ambientTemp, rng, spawnMult); }
                catch (Exception e) { Console.Error($"Biome: {e.Message}"); }

                // Phase 9: Advanced effects (smoke, embers, crater dust)
                try
                {
                    ExplosionEffectsLogic.SpawnAllEffects(param.position, param.range,
                    param.structuralDamage, rng);
                }
                catch (Exception e) { Console.Error($"Effects: {e.Message}"); }

                if (ShrapnelConfig.DebugLogging.Value)
                    Console.Log($"PRE profile={profile} total={totalSpawned}" +
                        $" bottom={bottomBlocked} Phys:{DebrisTracker.PhysicalCount}" +
                        $" Vis:{DebrisTracker.VisualCount}");
            }
            catch (Exception e)
            {
                Console.Error($"PreExplosion: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// POST-explosion: ground debris from newly exposed crater surfaces.
        /// Must run AFTER CreateExplosion destroys blocks.
        ///
        /// Supports pre-scan mode for effects-only explosions.
        ///   preScan=true: Scans current terrain (called from console -e flag).
        ///   preScan=false: Scans crater after destruction (normal flow).
        /// </summary>
        /// <param name="param">Explosion parameters</param>
        /// <param name="preScan">
        /// If true, scans existing block layout without waiting for destruction.
        /// Used by effects-only mode to show ground debris preview.
        /// </param>
        public static void PostExplosion(ExplosionParams param, bool preScan = false)
        {
            try
            {
                // Same filter as PreExplosion — ghost explosions produce no debris
                if (param.range <= MinExplosionRange) return;

                System.Random rng = new System.Random(MakeSeed(param.position) ^ 0x5A5A);

                ClassifyExplosion(param, rng, out _, out _, out _,
                    out _, out ExplosionProfile profile);
                ProfileMultipliers pm = ProfileMultipliers.ForProfile(profile);

                float adjustedRange = param.range * pm.GroundDebrisMult;

                // NEW: Pass preScan flag to GroundDebrisLogic
                GroundDebrisLogic.SpawnFromExplosion(param.position, adjustedRange,
                    rng, preScan);

                if (ShrapnelConfig.DebugLogging.Value)
                {
                    string mode = preScan ? "PRE-SCAN" : "CRATER";
                    Console.Log($"POST [{mode}] ground debris range={adjustedRange:F1}");
                }
            }
            catch (Exception e)
            {
                Console.Error($"PostExplosion: {e.Message}");
            }
        }

        /// <summary>
        /// Combined wrapper for Transpiler: pre = CreateExplosion = post.
        /// </summary>
        public static void CustomCreateExplosion(ExplosionParams param)
        {
            Console.Log($"FULL Explosion: range={param.range:F1} dmg={param.structuralDamage:F1} vel={param.velocity:F1}");
            PreExplosion(param);
            WorldGeneration.CreateExplosion(param);
            PostExplosion(param, preScan: false);  // Scan crater after destruction
        }

        /// <summary>
        /// Legacy entry point — routes to PreExplosion only.
        /// PostExplosion called separately after CreateExplosion by patch.
        /// 
        /// DEPRECATED: Use PreExplosion + PostExplosion explicitly instead.
        /// </summary>
        public static void TrySpawnFromExplosion(ExplosionParams param)
        {
            PreExplosion(param);
        }

        //  SPARK DIVERSITY — 3 sub-types

        /// <summary>
        /// Spawns sparks with 3 sub-types:
        ///   40% thin/fast needles — brightest, fastest, smallest
        ///   35% medium trailing — moderate speed, longer visible trail
        ///   25% thick/hot globs — largest, slowest, most orange, longest life
        ///
        /// Size↔Speed: speed ∝ 1/√(size)
        /// </summary>
        private static int SpawnDiverseSparks(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int totalCount, bool bottomBlocked)
        {
            int needleCount = Mathf.RoundToInt(totalCount * 0.40f);
            int mediumCount = Mathf.RoundToInt(totalCount * 0.35f);
            int globCount = totalCount - needleCount - mediumCount;

            int spawned = 0;

            for (int i = 0; i < needleCount; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);

                float size = rng.Range(0.015f, 0.03f);
                float speedMult = 1f / Mathf.Sqrt(size / 0.02f);
                float speed = MathHelper.ClampSpeed(baseSpeed * rng.Range(2.5f, 4f) * speedMult,
                    GlobalMaxSpeed);

                Color col = new Color(1f, 0.9f, 0.6f, 1f);
                var vis = new VisualParticleParams(size, col, 15,
                    ShrapnelVisuals.TriangleShape.Needle);
                var spark = new SparkParams(dir, speed, rng.Range(0.05f, 0.12f));
                ParticleHelper.SpawnSparkUnlit("SpkNeedle",
                    epicenter + rng.InsideUnitCircle() * 0.15f, vis, spark,
                    new EmissionParams(new Color(4f, 3f, 1f)));
                spawned++;
            }

            for (int i = 0; i < mediumCount; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);

                float size = rng.Range(0.03f, 0.06f);
                float speedMult = 1f / Mathf.Sqrt(size / 0.04f);
                float speed = MathHelper.ClampSpeed(baseSpeed * rng.Range(1.5f, 2.5f) * speedMult,
                    GlobalMaxSpeed);

                Color col = new Color(1f, 0.65f, 0.2f, 0.95f);
                var vis = new VisualParticleParams(size, col, 14,
                    ShrapnelVisuals.TriangleShape.Shard);
                var spark = new SparkParams(dir, speed, rng.Range(0.12f, 0.25f));
                ParticleHelper.SpawnSparkUnlit("SpkMedium",
                    epicenter + rng.InsideUnitCircle() * 0.2f, vis, spark,
                    new EmissionParams(new Color(2.5f, 1.5f, 0.3f)));
                spawned++;
            }

            for (int i = 0; i < globCount; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);

                float size = rng.Range(0.06f, 0.12f);
                float speedMult = 1f / Mathf.Sqrt(size / 0.08f);
                float speed = MathHelper.ClampSpeed(baseSpeed * rng.Range(0.8f, 1.5f) * speedMult,
                    GlobalMaxSpeed);

                Color col = new Color(1f, 0.45f, 0.08f, 0.9f);
                var vis = new VisualParticleParams(size, col, 13,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var spark = new SparkParams(dir, speed, rng.Range(0.2f, 0.4f));
                ParticleHelper.SpawnSparkUnlit("SpkGlob",
                    epicenter + rng.InsideUnitCircle() * 0.25f, vis, spark,
                    new EmissionParams(new Color(1.5f, 0.6f, 0.1f)));
                spawned++;
            }

            return spawned;
        }

        //  MICRO SHRAPNEL SPAWNING

        private static int SpawnMicroShrapnel(Vector2 epicenter, float speed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int count, bool bottomBlocked)
        {
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);
                ShrapnelFactory.SpawnDirectional(epicenter, speed, type,
                    ShrapnelWeight.Micro, i, rng, dir);
                spawned++;
            }
            return spawned;
        }

        //  EXISTING SPAWN METHODS (unchanged logic)

        private static int SpawnHotShrapnel(Vector2 epicenter, float speed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int hotCount, int activeSectors, bool bottomBlocked)
        {
            int hotPerSector = Mathf.Max(1, hotCount / activeSectors);
            int spawned = 0;
            for (int sector = 0; sector < AngularSectors; sector++)
            {
                if (IsSectorBlocked(sector, bottomBlocked)) continue;
                int toSpawn = Mathf.Min(hotPerSector, hotCount - spawned);
                for (int j = 0; j < toSpawn; j++)
                {
                    float angle = rng.AngleInSector(sector, AngularSectors);
                    Vector2 dir = MathHelper.AngleToDirection(angle);
                    ShrapnelFactory.SpawnDirectional(epicenter, speed, type,
                        ShrapnelWeight.Hot, spawned, rng, dir);
                    spawned++;
                }
            }
            while (spawned < hotCount)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);
                ShrapnelFactory.SpawnDirectional(epicenter, speed, type,
                    ShrapnelWeight.Hot, spawned, rng, dir);
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

        //  CLASSIFICATION — with Unknown adaptive fallback

        private static void ClassifyExplosion(ExplosionParams p, System.Random rng,
            out ShrapnelProjectile.ShrapnelType type, out int count,
            out float speed, out int visualCount, out ExplosionProfile profile)
        {
            float eps = ShrapnelConfig.ClassifyEpsilon.Value;
            float speedBoost = ShrapnelConfig.GlobalSpeedBoost.Value;

            if (Mathf.Abs(p.range - ShrapnelConfig.DynamiteRange.Value) < eps &&
                Mathf.Abs(p.structuralDamage - ShrapnelConfig.DynamiteStructuralDamage.Value) < eps)
            {
                profile = ExplosionProfile.Dynamite;
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
                profile = ExplosionProfile.Turret;
                type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                count = rng.Range(ShrapnelConfig.TurretPrimaryMin.Value,
                                  ShrapnelConfig.TurretPrimaryMax.Value);
                speed = ShrapnelConfig.TurretSpeed.Value * speedBoost;
                visualCount = rng.Range(ShrapnelConfig.TurretVisualMin.Value,
                                        ShrapnelConfig.TurretVisualMax.Value);
            }
            else if (IsKnownMine(p))
            {
                profile = ExplosionProfile.Mine;
                type = ShrapnelProjectile.ShrapnelType.Metal;
                count = rng.Range(ShrapnelConfig.MinePrimaryMin.Value,
                                  ShrapnelConfig.MinePrimaryMax.Value);
                speed = ShrapnelConfig.MineSpeed.Value * speedBoost;
                visualCount = rng.Range(ShrapnelConfig.MineVisualMin.Value,
                                        ShrapnelConfig.MineVisualMax.Value);
            }
            else
            {
                profile = ExplosionProfile.Unknown;
                type = ShrapnelProjectile.ShrapnelType.Metal;
                float adaptive = Mathf.Sqrt(p.structuralDamage) * Mathf.Sqrt(p.range);
                count = Mathf.Clamp(Mathf.RoundToInt(adaptive * 0.08f), 5, 100);
                speed = Mathf.Clamp(adaptive * 0.15f, 20f, 80f) * speedBoost;
                visualCount = Mathf.Clamp(Mathf.RoundToInt(adaptive * 0.5f), 20, 400);

                if (ShrapnelConfig.DebugLogging.Value)
                    Console.Log($"Unknown explosion: range={p.range}" +
                        $" dmg={p.structuralDamage} vel={p.velocity}" +
                        $" adaptive={adaptive:F1} count={count} visual={visualCount}");
            }
        }

        private static bool IsKnownMine(ExplosionParams p)
        {
            return p.structuralDamage > 100f && p.range > 3f;
        }

        //  HELPERS (unchanged)

        private static bool IsSectorBlocked(int sector, bool bottomBlocked)
        {
            if (!bottomBlocked) return false;
            return sector >= BottomConeStartSector
                && sector < BottomConeStartSector + BottomConeSectorCount;
        }

        private static int GetRandomActiveSector(bool bottomBlocked, System.Random rng)
        {
            if (!bottomBlocked) return rng.Next(0, AngularSectors);
            int active = AngularSectors - BottomConeSectorCount;
            int slot = rng.Next(0, active);
            int found = 0;
            for (int s = 0; s < AngularSectors; s++)
            {
                if (IsSectorBlocked(s, true)) continue;
                if (found == slot) return s;
                found++;
            }
            return 0;
        }

        private static bool IsBottomBlocked(Vector2 position)
        {
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    Vector2Int bp = WorldGeneration.world.WorldToBlockPos(
                        position + Vector2.down * (0.3f + i));
                    if (WorldGeneration.world.GetBlock(bp) != 0) return true;
                }
            }
            catch { }
            return false;
        }

        private static ShrapnelWeight RollRandomWeight(
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            float roll = rng.NextFloat();
            ShrapnelWeight weight;
            if (roll < 0.10f) weight = ShrapnelWeight.Micro;
            else if (roll < 0.45f) weight = ShrapnelWeight.Medium;
            else if (roll < 0.80f) weight = ShrapnelWeight.Heavy;
            else weight = ShrapnelWeight.Massive;

            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Massive)
                weight = ShrapnelWeight.Heavy;
            return weight;
        }

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

        private static int SpawnBlockDebris(ExplosionParams param, System.Random rng)
        {
            if (param.structuralDamage < 500f) return 0;
            float powerFactor = Mathf.Clamp01(param.structuralDamage / 2000f);
            float mult = ShrapnelConfig.BlockDebrisCountMultiplier.Value;
            int maxSamples = Mathf.RoundToInt((20 + powerFactor * 40) * mult);
            int spawned = 0;
            for (int i = 0; i < maxSamples; i++)
            {
                Vector2 offset = rng.InsideUnitCircle() * param.range;
                Vector2 samplePos = param.position + offset;
                try
                {
                    Vector2Int bp = WorldGeneration.world.WorldToBlockPos(samplePos);
                    ushort bid = WorldGeneration.world.GetBlock(bp);
                    if (bid == 0) continue;
                    BlockInfo info = WorldGeneration.world.GetBlockInfo(bid);
                    if (info == null) continue;
                    int perBlock = rng.Range(3, 8);
                    for (int j = 0; j < perBlock; j++)
                    {
                        SpawnSingleBlockDebris(samplePos, param.position, info, rng);
                        spawned++;
                    }
                }
                catch (IndexOutOfRangeException) { }
            }
            return spawned;
        }

        private static void SpawnSingleBlockDebris(Vector2 blockPos, Vector2 epicenter,
            BlockInfo info, System.Random rng)
        {
            Vector2 pos = blockPos + rng.InsideUnitCircle() * 0.3f;
            MaterialCategory cat = BlockClassifier.Classify(info);
            Color col = BlockClassifier.GetColorWithAlpha(cat, rng, 0.85f);

            Vector2 away = (blockPos - epicenter).normalized;
            if (away.sqrMagnitude < 0.01f) away = rng.OnUnitCircle();
            float spread = rng.Range(-45f, 45f) * Mathf.Deg2Rad;
            Vector2 dir = MathHelper.RotateDirection(away, spread);
            dir.y = Mathf.Max(dir.y, 0.2f);
            dir.Normalize();

            Vector2 vel = dir * rng.Range(4f, 12f);
            var vis = new VisualParticleParams(rng.Range(0.05f, 0.18f), col, 11,
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            var phy = new AshPhysicsParams(vel, rng.Range(1.5f, 4f),
                rng.Range(0.8f, 1.5f), 0.3f, 0.4f, 1.5f,
                new Vector2(rng.Range(-0.2f, 0.2f), 0f), 0.2f);

            ParticleHelper.SpawnLit("BlkDebris", pos, vis, phy, rng.Range(0f, 100f));
        }

        private static int SpawnSecondaryFromBlocks(ExplosionParams param, System.Random rng,
            ShrapnelProjectile.ShrapnelType primaryType, float primarySpeed)
        {
            int spawned = 0;
            const int maxSamples = 20, maxSecondary = 20;
            for (int i = 0; i < maxSamples && spawned < maxSecondary; i++)
            {
                Vector2 offset = rng.InsideUnitCircle() * param.range;
                Vector2 samplePos = param.position + offset;
                try
                {
                    Vector2Int bp = WorldGeneration.world.WorldToBlockPos(samplePos);
                    ushort bid = WorldGeneration.world.GetBlock(bp);
                    if (bid == 0) continue;
                    BlockInfo info = WorldGeneration.world.GetBlockInfo(bid);
                    if (info == null) continue;

                    MaterialCategory cat = BlockClassifier.Classify(info);
                    var secType = (cat == MaterialCategory.Metal)
                        ? ShrapnelProjectile.ShrapnelType.HeavyMetal
                        : (info.health < 100f && cat != MaterialCategory.Rock
                            && cat != MaterialCategory.Concrete)
                            ? ShrapnelProjectile.ShrapnelType.Wood
                            : ShrapnelProjectile.ShrapnelType.Stone;

                    ShrapnelWeight weight = RollWeightForSecondary(secType, i, maxSamples, rng);
                    ShrapnelFactory.Spawn(samplePos, primarySpeed * 0.4f,
                        secType, weight, i, rng);
                    spawned++;
                }
                catch (IndexOutOfRangeException) { }
            }
            return spawned;
        }

        private static ShrapnelWeight RollWeightForSecondary(
            ShrapnelProjectile.ShrapnelType type, int index, int total, System.Random rng)
        {
            ShrapnelWeight weight;
            if (index < Mathf.CeilToInt(total * HotFraction))
                weight = ShrapnelWeight.Hot;
            else
            {
                float roll = rng.NextFloat();
                if (roll < 0.10f) weight = ShrapnelWeight.Micro;
                else if (roll < 0.25f) weight = ShrapnelWeight.Hot;
                else if (roll < 0.55f) weight = ShrapnelWeight.Medium;
                else if (roll < 0.90f) weight = ShrapnelWeight.Heavy;
                else weight = ShrapnelWeight.Massive;
            }
            if (type == ShrapnelProjectile.ShrapnelType.HeavyMetal && weight == ShrapnelWeight.Hot)
                weight = ShrapnelWeight.Medium;
            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Massive)
                weight = ShrapnelWeight.Heavy;
            return weight;
        }

        private static void SpawnBiomeEffects(Vector2 epicenter, float range,
            float temperature, System.Random rng, float spawnMult)
        {
            try
            {
                if (IsDesertBiome(epicenter))
                {
                    int extra = Mathf.RoundToInt(rng.Range(40, 80) * spawnMult);
                    SpawnDesertDust(epicenter, range, rng, extra);
                }
                if (temperature < 5f)
                {
                    float tf = Mathf.InverseLerp(5f, -20f, temperature);
                    int steam = Mathf.RoundToInt(rng.Range(20, 45) * spawnMult * (1f + tf));
                    SpawnColdSteam(epicenter, range, rng, steam);
                }
            }
            catch (Exception e) { Console.Error($"Biome: {e.Message}"); }
        }

        private static bool IsDesertBiome(Vector2 position)
        {
            int sandCount = 0;
            try
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    Vector2Int bp = WorldGeneration.world.WorldToBlockPos(
                        position + new Vector2(dx, -1f));
                    ushort bid = WorldGeneration.world.GetBlock(bp);
                    if (bid == 0) continue;
                    BlockInfo info = WorldGeneration.world.GetBlockInfo(bid);
                    if (BlockClassifier.Classify(info) == MaterialCategory.Sand) sandCount++;
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
                Vector2 pos = epicenter + rng.InsideUnitCircle() * range * 1.2f
                    + Vector2.down * rng.Range(0f, 1f);
                Color col = new Color(rng.Range(0.7f, 0.85f), rng.Range(0.6f, 0.72f),
                    rng.Range(0.35f, 0.48f), rng.Range(0.35f, 0.65f));
                Vector2 vel = new Vector2(rng.Range(-2.5f, 2.5f), rng.Range(0.5f, 4f));
                var vis = new VisualParticleParams(rng.Range(0.08f, 0.28f), col, 11,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.DesertDust(vel, rng.Range(8f, 25f), rng);
                ParticleHelper.SpawnLit("DesertDust", pos, vis, phy, rng.Range(0f, 100f));
            }
        }

        private static void SpawnColdSteam(Vector2 epicenter, float range,
            System.Random rng, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Vector2 pos = epicenter + rng.InsideUnitCircle() * range * 0.6f;
                float gray = rng.Range(0.82f, 0.98f);
                Color col = new Color(gray, gray, gray, rng.Range(0.25f, 0.55f));
                Vector2 vel = new Vector2(rng.Range(-0.6f, 0.6f), rng.Range(1.5f, 5f));
                var vis = new VisualParticleParams(rng.Range(0.06f, 0.18f), col, 12,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.ColdSteam(vel, rng.Range(4f, 10f), rng);
                ParticleHelper.SpawnLit("ColdSteam", pos, vis, phy, rng.Range(0f, 100f));
            }
        }
    }
}