using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Effects;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Logic
{
    /// <summary>
    /// Explosion orchestration engine. Classifies explosion types, allocates budget,
    /// sequences spawn phases for deterministic multiplayer sync.
    ///
    /// EXPLOSION PROFILES:
    ///   Mine      — 225° upward cone, more metallic sparks, blocked bottom arc
    ///   Dynamite  — 360°, large blast, heavy ground debris, fewer sparks
    ///   Turret    — 360°, sparse particles overall
    ///   Gravbag   — battery overload pop, mostly micro sparks, few electronic fragments
    ///   Unknown   — adaptive based on sqrt(structuralDamage × range)
    ///
    /// TWO-PHASE SPAWN MODEL:
    ///   Phase 1 (PreExplosion): Before block destruction
    ///     • Physics shrapnel with collision detection
    ///     • Sparks, embers, ash clouds, biome effects
    ///     • Needs intact terrain for bottom-blocked detection
    ///
    ///   Phase 2 (PostExplosion): After block destruction
    ///     • Ground debris from newly-exposed crater surfaces
    ///     • Requires destroyed blocks to find exposed faces
    ///
    /// MULTIPLAYER SYNCHRONIZATION:
    ///   • Seeds derived from rounded world position (×10, RoundToInt)
    ///   • Absorbs ±0.05 unit host/client float drift
    ///   • Time.frameCount NEVER used for determinism
    /// </summary>
    public static class ShrapnelSpawnLogic
    {
        //  THROTTLING & DEBOUNCE

        private static Vector2 _lastSpawnPos = new(float.MinValue, float.MinValue);
        private static int _lastSpawnFrame = -999;

        /// <summary>Global maximum particle/shrapnel speed clamp.</summary>
        public static float GlobalMaxSpeed => ShrapnelConfig.GlobalMaxSpeed.Value;

        //  FRACTION CONSTANTS — control particle budget distribution
        //
        //  Total shrapnel count allocated as:
        //    HotFraction:         stratified angular sectors, guaranteed coverage
        //    MicroFraction:       visual-only spark shower, no physics GO (+20% from 0.10)
        //    PyrotechnicFraction: diverse spark types needle/medium/glob (+20% from 0.15)
        //    Remainder:           random weight physics shrapnel

        private const int AngularSectors = 8;
        private const float HotFraction = 0.25f;

        /// <summary>Micro fraction increased from 0.10 = 0.12 (+20%).</summary>
        private const float MicroFraction = 0.12f;

        /// <summary>Pyrotechnic fraction increased from 0.15 = 0.18 (+20%).</summary>
        private const float PyrotechnicFraction = 0.18f;

        private const int BottomConeStartSector = 5;
        private const int BottomConeSectorCount = 3;

        /// <summary>
        /// Minimum explosion range to spawn any shrapnel.
        /// Filters out gravbag ghost explosions (range=0) which exist only to disfigure player.
        /// </summary>
        private const float MinExplosionRange = 0.1f;

        //  EXPLOSION PROFILE ENUM

        public enum ExplosionProfile { Mine, Dynamite, Turret, Gravbag, Unknown }

        /// <summary>Multiplier factors per explosion profile.</summary>
        private struct ProfileMultipliers
        {
            public float SparkMult;
            public float GroundDebrisMult;
            public float ShrapnelMult;

            public static ProfileMultipliers ForProfile(ExplosionProfile p)
            {
                return p switch
                {
                    ExplosionProfile.Mine => new ProfileMultipliers
                    { SparkMult = 1.4f, GroundDebrisMult = 1f, ShrapnelMult = 1f },
                    ExplosionProfile.Dynamite => new ProfileMultipliers
                    { SparkMult = 0.7f, GroundDebrisMult = 2f, ShrapnelMult = 1.2f },
                    ExplosionProfile.Turret => new ProfileMultipliers
                    { SparkMult = 0.6f, GroundDebrisMult = 0.7f, ShrapnelMult = 0.8f },
                    ExplosionProfile.Gravbag => new ProfileMultipliers
                    { SparkMult = 0.5f, GroundDebrisMult = 0.3f, ShrapnelMult = 0.2f },
                    _ => new ProfileMultipliers
                    { SparkMult = 1f, GroundDebrisMult = 1f, ShrapnelMult = 1f },
                };
            }
        }

        //  SEED GENERATION (multiplayer-safe)

        /// <summary>
        /// Generates deterministic seed from world position.
        ///
        /// ROUNDING TRICK: Position ×10 then RoundToInt gives 1 decimal precision.
        /// Host at (15.300001) and client at (15.299999) both round to 153.
        /// Without rounding, these would produce different seeds due to float drift.
        /// </summary>
        private static int MakeSeed(Vector2 position)
        {
            int versionHash = Plugin.Version.GetHashCode();
            int rx = Mathf.RoundToInt(position.x * 10f);
            int ry = Mathf.RoundToInt(position.y * 10f);
            return unchecked(rx * 397 ^ ry ^ versionHash);
        }

        /// <summary>Generates per-shrapnel seed from explosion's RNG chain.</summary>
        internal static int MakeShrapnelSeed(System.Random rng) => rng.Next();

        //  THROTTLE CONTROL

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

        //  PRE-EXPLOSION PHASE (before block destruction)

        /// <summary>
        /// Spawns physics shrapnel, sparks, ash, smoke, and biome effects.
        /// Runs BEFORE CreateExplosion destroys blocks.
        ///
        /// PERF FIX: Console.Log gated behind DebugLogging to avoid
        /// string interpolation allocation on every explosion.
        /// </summary>
        public static void PreExplosion(ExplosionParams param)
        {
            // PERF: Only allocate log string when debug logging is enabled
            if (ShrapnelConfig.DebugLogging.Value)
                Console.Log($"PRE-Explosion: range={param.range:F1}" +
                    $" dmg={param.structuralDamage:F1} vel={param.velocity:F1}");

            try
            {
                if (param.range <= MinExplosionRange)
                {
                    if (ShrapnelConfig.DebugLogging.Value)
                        Console.Log($"Skipped ghost explosion:" +
                            $" range={param.range} dmg={param.structuralDamage}");
                    return;
                }

                if (!TryRegisterSpawn(param.position)) return;

                ExplosionLogger.Record(param);
                System.Random rng = new(MakeSeed(param.position));
                float spawnMult = ShrapnelConfig.SpawnCountMultiplier.Value;

                ClassifyExplosion(param, rng, out var type, out int count,
                    out float speed, out ExplosionProfile profile);

                ProfileMultipliers pm = ProfileMultipliers.ForProfile(profile);

                count = Mathf.Max(1, Mathf.RoundToInt(count * spawnMult * pm.ShrapnelMult));

                float ambientTemp = GetAmbientTemperature();
                if (ambientTemp < 5f) count = Mathf.CeilToInt(count * 0.9f);
                else if (ambientTemp > 25f) count = Mathf.CeilToInt(count * 1.1f);

                bool bottomBlocked = IsBottomBlocked(param.position);
                int blockedCount = bottomBlocked ? BottomConeSectorCount : 0;
                int activeSectors = AngularSectors - blockedCount;

                int microCount = Mathf.Max(1, Mathf.CeilToInt(count * MicroFraction));
                int hotCount = Mathf.Max(activeSectors, Mathf.CeilToInt(count * HotFraction));
                int pyroCount = Mathf.Max(1, Mathf.CeilToInt(count * PyrotechnicFraction));
                int randomCount = Mathf.Max(0, count - hotCount - pyroCount - microCount);

                int totalSpawned = 0;
                bool spawnPhysics = MultiplayerHelper.ShouldSpawnPhysicsShrapnel;

                // Phase 1: Hot shrapnel
                if (spawnPhysics)
                {
                    try
                    {
                        totalSpawned += SpawnHotShrapnel(param.position, speed, type, rng,
                            hotCount, activeSectors, bottomBlocked);
                    }
                    catch (Exception e) { Console.Error($"Hot: {e.Message}"); }
                }
                else
                {
                    AdvanceRng(rng, hotCount);
                }

                // Phase 2: Micro sparks
                try
                {
                    totalSpawned += SpawnMicroShrapnel(param.position, speed, type, rng,
                        microCount, bottomBlocked);
                }
                catch (Exception e) { Console.Error($"Micro: {e.Message}"); }

                // Phase 3: Diverse sparks
                try
                {
                    totalSpawned += SpawnDiverseSparks(param.position, speed, type, rng,
                        Mathf.RoundToInt(pyroCount * pm.SparkMult), bottomBlocked);
                }
                catch (Exception e) { Console.Error($"Sparks: {e.Message}"); }

                // Phase 4: Random weight shrapnel
                if (spawnPhysics)
                {
                    try
                    {
                        totalSpawned += SpawnRandomWeight(param.position, speed, type, rng,
                            randomCount, hotCount, bottomBlocked);
                    }
                    catch (Exception e) { Console.Error($"Random: {e.Message}"); }
                }
                else
                {
                    AdvanceRng(rng, randomCount);
                }

                // Phase 5: Secondary shrapnel
                if (spawnPhysics)
                {
                    try { totalSpawned += SpawnSecondaryFromBlocks(param, rng, type, speed); }
                    catch (Exception e) { Console.Error($"Secondary: {e.Message}"); }
                }

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

                // Phase 9: Advanced effects
                try
                {
                    ExplosionEffectsLogic.SpawnAllEffects(param.position, param.range,
                        param.structuralDamage, rng);
                }
                catch (Exception e) { Console.Error($"Effects: {e.Message}"); }

                if (ShrapnelConfig.DebugLogging.Value)
                    Console.Log($"PRE profile={profile} total={totalSpawned}" +
                        $" bottom={bottomBlocked}" +
                        $" physics={spawnPhysics}" +
                        $" MP={MultiplayerHelper.IsNetworkRunning}" +
                        $" client={MultiplayerHelper.IsClient}" +
                        $" Total:{DebrisTracker.TotalAliveParticles}");
            }
            catch (Exception e)
            {
                Console.Error($"PreExplosion: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Advances RNG without spawning, to maintain deterministic sequence.
        /// Used when client skips physics shrapnel — keeps RNG in sync with server.
        /// </summary>
        private static void AdvanceRng(System.Random rng, int count)
        {
            // Each shrapnel spawn consumes roughly 8-12 RNG calls.
            // We advance by a conservative estimate to keep approximate sync.
            for (int i = 0; i < count * 10; i++)
                rng.Next();
        }

        //  POST-EXPLOSION PHASE (after block destruction)

        /// <summary>
        /// Spawns ground debris from newly-exposed crater surfaces.
        /// Runs AFTER CreateExplosion destroys blocks.
        ///
        /// MODES:
        ///   preScan=false: Scan post-destruction crater (normal flow)
        ///   preScan=true: Scan current terrain state (effects-only preview)
        ///
        /// WHY TWO MODES: DestroyBackup fires before blocks are destroyed,
        /// so we scan existing exposed faces instead of waiting for crater.
        /// </summary>
        public static void PostExplosion(ExplosionParams param, bool preScan = false)
        {
            try
            {
                if (param.range <= MinExplosionRange) return;

                System.Random rng = new(MakeSeed(param.position) ^ 0x5A5A);

                ClassifyExplosion(param, rng, out _, out _,
                    out _, out ExplosionProfile profile);
                ProfileMultipliers pm = ProfileMultipliers.ForProfile(profile);

                float adjustedRange = param.range * pm.GroundDebrisMult;

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

        //  FULL WRAPPER (console command path)

        /// <summary>
        /// Combined wrapper for console commands where we control full sequence.
        /// Not used by real explosions (handled by Harmony Prefix+Postfix).
        /// </summary>
        public static void CustomCreateExplosion(ExplosionParams param)
        {
            Console.Log($"FULL Explosion: range={param.range:F1} dmg={param.structuralDamage:F1} vel={param.velocity:F1}");
            PreExplosion(param);
            WorldGeneration.CreateExplosion(param);
            PostExplosion(param, preScan: false);
        }

        //  CLASSIFICATION (detect explosion type)

        /// <summary>
        /// Classifies explosion by matching known parameter patterns.
        ///
        /// DETECTION ORDER:
        ///   1. Dynamite:  range≈18, structuralDamage≈2000
        ///   2. Turret:    range≈9, velocity≈15
        ///   3. Gravbag:   disfigureChance≈0.15 (battery pop, not bomb!)
        ///   4. Mine:      structuralDamage>100, range>3
        ///   5. Unknown:   adaptive fallback based on sqrt(damage×range)
        ///
        /// GRAVBAG SPECIAL CASE:
        ///   Vanilla gravbag creates ExplosionParams with only position and
        ///   disfigureChance=0.15 set. All other fields use class defaults:
        ///   range=12, damage=500, velocity=60. This looks like a mine but is
        ///   actually just a battery overload pop. We detect it by the unique
        ///   disfigureChance value (0.15 vs standard 0.34) and spawn mostly
        ///   micro sparks with very few electronic fragments.
        /// </summary>
       private static void ClassifyExplosion(ExplosionParams p, System.Random rng,
            out ShrapnelProjectile.ShrapnelType type, out int count,
            out float speed, out ExplosionProfile profile)
        {
            float eps = ShrapnelConfig.ClassifyEpsilon.Value;
            float speedBoost = ShrapnelConfig.GlobalSpeedBoost.Value;

            // Dynamite: blunt rock debris, not sharp metal
            if (Mathf.Abs(p.range - ShrapnelConfig.DynamiteRange.Value) < eps &&
                Mathf.Abs(p.structuralDamage - ShrapnelConfig.DynamiteStructuralDamage.Value) < eps)
            {
                profile = ExplosionProfile.Dynamite;
                type = ShrapnelProjectile.ShrapnelType.Stone; // WHY: Rock debris, reduced bleed
                count = rng.Range(ShrapnelConfig.DynamitePrimaryMin.Value,
                                  ShrapnelConfig.DynamitePrimaryMax.Value);
                speed = ShrapnelConfig.DynamiteSpeed.Value * speedBoost;
            }
            else if (Mathf.Abs(p.range - ShrapnelConfig.TurretRange.Value) < eps &&
                     Mathf.Abs(p.velocity - ShrapnelConfig.TurretVelocity.Value) < eps)
            {
                profile = ExplosionProfile.Turret;
                type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                count = rng.Range(ShrapnelConfig.TurretPrimaryMin.Value,
                                  ShrapnelConfig.TurretPrimaryMax.Value);
                speed = ShrapnelConfig.TurretSpeed.Value * speedBoost;
            }
            else if (Mathf.Abs(p.disfigureChance - 0.15f) < 0.02f &&
                     Mathf.Abs(p.range - 12f) < eps &&
                     Mathf.Abs(p.structuralDamage - 500f) < eps)
            {
                profile = ExplosionProfile.Gravbag;
                type = ShrapnelProjectile.ShrapnelType.Electronic;
                count = rng.Range(3, 6);
                speed = 15f * speedBoost;
            }
            else if (IsKnownMine(p))
            {
                profile = ExplosionProfile.Mine;
                type = ShrapnelProjectile.ShrapnelType.Metal;
                count = rng.Range(ShrapnelConfig.MinePrimaryMin.Value,
                                  ShrapnelConfig.MinePrimaryMax.Value);
                speed = ShrapnelConfig.MineSpeed.Value * speedBoost;
            }
            else
            {
                profile = ExplosionProfile.Unknown;
                type = ShrapnelProjectile.ShrapnelType.Metal;
                float adaptive = Mathf.Sqrt(p.structuralDamage) * Mathf.Sqrt(p.range);
                count = Mathf.Clamp(Mathf.RoundToInt(adaptive * 0.08f), 5, 100);
                speed = Mathf.Clamp(adaptive * 0.15f, 20f, 80f) * speedBoost;
            }
        }

        //  ENVIRONMENT HELPERS

        /// <summary>
        /// Gets current ambient temperature from world state.
        /// Falls back to 20°C if world not loaded or access fails.
        ///
        /// WHY THIS IS SAFE:
        ///   WorldGeneration.world can be null during early startup.
        ///   Catch block provides reasonable default for particle logic.
        /// </summary>
        private static float GetAmbientTemperature()
        {
            try
            {
                if (WorldGeneration.world != null)
                    return WorldGeneration.world.ambientTemperature;
            }
            catch { }
            return 20f; // Default room temperature fallback
        }

        private static bool IsKnownMine(ExplosionParams p)
            => p.structuralDamage > 100f && p.range > 3f;

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

        //  SPARK DIVERSITY (3 sub-types with different characteristics)

        /// <summary>
        /// Spawns diverse spark types with physical properties:
        ///   • 40% thin/fast needles — brightest, fastest, smallest, shortest life
        ///   • 35% medium trailing  — moderate speed, longer visible trail
        ///   • 25% thick/hot globs  — largest, slowest, most orange, longest life
        ///
        /// SIZE↔SPEED RELATIONSHIP: speed ∝ 1/√(size) enforced per particle.
        /// Smaller sparks travel faster (realistic ballistic scaling).
        /// </summary>
        private static int SpawnDiverseSparks(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng,
            int totalCount, bool bottomBlocked)
        {
            int needleCount = Mathf.RoundToInt(totalCount * 0.40f);
            int mediumCount = Mathf.RoundToInt(totalCount * 0.35f);
            int globCount = totalCount - needleCount - mediumCount;

            int spawned = 0;

            // NEEDLES: 0.015–0.03 size, 2.5–4× speed, 0.05–0.12s life
            for (int i = 0; i < needleCount; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);

                float size = rng.Range(0.015f, 0.03f);
                float speedMult = 1f / Mathf.Sqrt(size / 0.02f);
                float speed = MathHelper.ClampSpeed(baseSpeed * rng.Range(2.5f, 4f) * speedMult,
                    GlobalMaxSpeed);

                Color col = new(1f, 0.9f, 0.6f, 1f);
                var vis = new VisualParticleParams(size, col, 15,
                    ShrapnelVisuals.TriangleShape.Needle);
                var spark = new SparkParams(dir, speed, rng.Range(0.05f, 0.12f));
                ParticleHelper.SpawnSpark(epicenter + rng.InsideUnitCircle() * 0.15f, vis, spark);
                spawned++;
            }

            // MEDIUM: 0.03–0.06 size, 1.5–2.5× speed, 0.12–0.25s life
            for (int i = 0; i < mediumCount; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);

                float size = rng.Range(0.03f, 0.06f);
                float speedMult = 1f / Mathf.Sqrt(size / 0.04f);
                float speed = MathHelper.ClampSpeed(baseSpeed * rng.Range(1.5f, 2.5f) * speedMult,
                    GlobalMaxSpeed);

                Color col = new(1f, 0.65f, 0.2f, 0.95f);
                var vis = new VisualParticleParams(size, col, 14,
                    ShrapnelVisuals.TriangleShape.Shard);
                var spark = new SparkParams(dir, speed, rng.Range(0.12f, 0.25f));
                ParticleHelper.SpawnSpark(epicenter + rng.InsideUnitCircle() * 0.2f, vis, spark);
                spawned++;
            }

            // GLOBS: 0.06–0.12 size, 0.8–1.5× speed, 0.2–0.4s life
            for (int i = 0; i < globCount; i++)
            {
                int sector = GetRandomActiveSector(bottomBlocked, rng);
                float angle = rng.AngleInSector(sector, AngularSectors);
                Vector2 dir = MathHelper.AngleToDirection(angle);

                float size = rng.Range(0.06f, 0.12f);
                float speedMult = 1f / Mathf.Sqrt(size / 0.08f);
                float speed = MathHelper.ClampSpeed(baseSpeed * rng.Range(0.8f, 1.5f) * speedMult,
                    GlobalMaxSpeed);

                Color col = new(1f, 0.45f, 0.08f, 0.9f);
                var vis = new VisualParticleParams(size, col, 13,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var spark = new SparkParams(dir, speed, rng.Range(0.2f, 0.4f));
                ParticleHelper.SpawnSpark(epicenter + rng.InsideUnitCircle() * 0.25f, vis, spark);
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

        //  STRATIFIED HOT SHRAPNEL (sector coverage guarantee)

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

        //  RANDOM WEIGHT SHRAPNEL

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

        //  SECTOR HELPERS (blocked bottom cone for mines)

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

        //  WEIGHT ROLLING (probability distribution)

        private static ShrapnelWeight RollRandomWeight(
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            float roll = rng.NextFloat();
            ShrapnelWeight weight;

            if (roll < 0.10f) weight = ShrapnelWeight.Micro;
            else if (roll < 0.45f) weight = ShrapnelWeight.Medium;
            else if (roll < 0.80f) weight = ShrapnelWeight.Heavy;
            else weight = ShrapnelWeight.Massive;

            // Stone rarely spawns massive (too big for rock shards)
            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Massive)
                weight = ShrapnelWeight.Heavy;

            return weight;
        }

        //  ASH COUNT BY TYPE

        private static int GetAshCount(ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            return type switch
            {
                ShrapnelProjectile.ShrapnelType.Stone => rng.Range(50, 80),
                ShrapnelProjectile.ShrapnelType.HeavyMetal => rng.Range(20, 40),
                _ => rng.Range(35, 60),
            };
        }

        //  BLOCK DEBRIS SPAWNING (nearby affected blocks)

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

            ParticleHelper.SpawnLit(pos, vis, phy, rng.Range(0f, 100f));
        }

        //  SECONDARY SHRAPNEL FROM NEARBY BLOCKS

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
                    ShrapnelProjectile.ShrapnelType secType;

                    if (cat == MaterialCategory.Metal)
                        secType = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                    else if (info.health < 100f &&
                             cat != MaterialCategory.Rock &&
                             cat != MaterialCategory.Concrete)
                        secType = ShrapnelProjectile.ShrapnelType.Wood;
                    else
                        secType = ShrapnelProjectile.ShrapnelType.Stone;

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

            // First N items get Hot weight for visual variety
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

            // HeavyMetal never spawns too light (unrealistic)
            if (type == ShrapnelProjectile.ShrapnelType.HeavyMetal && weight == ShrapnelWeight.Hot)
                weight = ShrapnelWeight.Medium;

            // Stone rarely spawns massive
            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Massive)
                weight = ShrapnelWeight.Heavy;

            return weight;
        }

        //  BIOME EFFECTS (desert dust, cold steam)

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
                    if (BlockClassifier.Classify(info) == MaterialCategory.Sand)
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
                Vector2 pos = epicenter + rng.InsideUnitCircle() * range * 1.2f
                    + Vector2.down * rng.Range(0f, 1f);

                Color col = new(rng.Range(0.7f, 0.85f), rng.Range(0.6f, 0.72f),
                    rng.Range(0.35f, 0.48f), rng.Range(0.35f, 0.65f));

                Vector2 vel = new(rng.Range(-2.5f, 2.5f), rng.Range(0.5f, 4f));
                var vis = new VisualParticleParams(rng.Range(0.08f, 0.28f), col, 11,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.DesertDust(vel, rng.Range(8f, 25f), rng);

                ParticleHelper.SpawnLit(pos, vis, phy, rng.Range(0f, 100f));
            }
        }

        private static void SpawnColdSteam(Vector2 epicenter, float range,
            System.Random rng, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Vector2 pos = epicenter + rng.InsideUnitCircle() * range * 0.6f;

                float gray = rng.Range(0.82f, 0.98f);
                Color col = new(gray, gray, gray, rng.Range(0.25f, 0.55f));

                Vector2 vel = new(rng.Range(-0.6f, 0.6f), rng.Range(1.5f, 5f));
                var vis = new VisualParticleParams(rng.Range(0.06f, 0.18f), col, 12,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.ColdSteam(vel, rng.Range(4f, 10f), rng);

                ParticleHelper.SpawnLit(pos, vis, phy, rng.Range(0f, 100f));
            }
        }
    }
}