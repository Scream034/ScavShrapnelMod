using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Ground debris particles: explosion shockwave, muzzle blast, and bullet impact.
    ///
    /// Material classification uses <see cref="BlockClassifier"/>.
    ///
    /// Three emission modes:
    ///   <see cref="SpawnFromExplosion"/>     — explosion crater (large radius, wave delay)
    ///   <see cref="SpawnFromMuzzleBlast"/>   — gun muzzle gas (column-scan, instant, directional)
    ///   <see cref="SpawnFromBulletImpact(Vector2, float, Vector2)"/> — bullet hit (kinetic transfer, cone spray)
    ///
    /// All modes reuse SpawnChunk / SpawnDust / SpawnStreak for visual consistency.
    /// Particle lifecycle managed by AshParticlePoolManager (zero-GC).
    ///
    /// Surface scanning uses column-scan: for each X offset, scan downward to find
    /// the first air→solid transition. Much faster than brute-force grid and guarantees
    /// finding actual exposed surfaces.
    /// </summary>
    public static class GroundDebrisLogic
    {
        #region Face Normal Cache (pre-allocated, no allocation per call)

        private static readonly Vector2 NormalUp = Vector2.up;
        private static readonly Vector2 NormalDown = Vector2.down;
        private static readonly Vector2 NormalLeft = Vector2.left;
        private static readonly Vector2 NormalRight = Vector2.right;

        private static readonly Vector2[] _faceNormals = new Vector2[4];
        private static readonly Vector2[] _facePositions = new Vector2[4];

        #endregion

        #region Explosion Constants

        private const int ScanDownMax = 25;
        private const int ScanUpMax = 20;
        private const float WaveSpeed = 50f;
        private const int MaxTotal = 3000;
        private const int ChunksPerSurface = 3;
        private const int DustPerSurface = 6;
        private const int StreaksPerSurface = 3;
        private const float StreakThreshold = 0.35f;
        private const float ChunkScaleMin = 0.12f;
        private const float ChunkScaleMax = 0.28f;
        private const float ChunkSpeedMin = 2f;
        private const float ChunkSpeedMax = 5f;
        private const float ChunkLifeMin = 8f;
        private const float ChunkLifeMax = 20f;
        private const float ChunkAlpha = 0.9f;
        private const float DustScaleMin = 0.03f;
        private const float DustScaleMax = 0.2f;
        private const float DustSpeedMin = 0.8f;
        private const float DustSpeedMax = 3f;
        private const float DustLifeMin = 15f;
        private const float DustLifeMax = 40f;
        private const float DustAlpha = 0.55f;
        private const float StreakScaleMin = 0.06f;
        private const float StreakScaleMax = 0.14f;
        private const float StreakSpeedMin = 15f;
        private const float StreakSpeedMax = 30f;
        private const float StreakLifeMin = 0.5f;
        private const float StreakLifeMax = 1.8f;
        private const float StreakAlpha = 0.85f;
        private const int AirwavePerColumn = 2;
        private const float AirwaveScaleMin = 0.04f;
        private const float AirwaveScaleMax = 0.10f;
        private const float AirwaveSpeedMin = 5f;
        private const float AirwaveSpeedMax = 10f;
        private const float AirwaveLifeMin = 2f;
        private const float AirwaveLifeMax = 5f;
        private const float AirwaveAlpha = 0.25f;

        #endregion

        #region Directional Blast Constants

        /// <summary>Max vertical scan depth when searching for surface blocks.</summary>
        private const int ColumnScanDepth = 20;

        /// <summary>How much blast direction influences particle velocity (0=face only, 1=blast only).</summary>
        private const float BlastDirectionBlend = 0.4f;

        /// <summary>Kinetic impact: bullet direction dominates over face normal.</summary>
        private const float ImpactDirectionBlend = 0.65f;

        /// <summary>Speed boost for particles along bullet travel axis.</summary>
        private const float KineticSpeedBoost = 2.5f;

        /// <summary>Extra particles spawned along bullet travel direction.</summary>
        private const int KineticBonusParticles = 4;

        /// <summary>Scale multiplier for kinetic shockwave dust clouds.</summary>
        private const float KineticDustScaleMult = 1.8f;

        #endregion

        // WHY: Muzzle blast and bullet impact are called from ShotEffectRouter,
        // not from explosion logic. Using a dedicated RNG prevents any cross-
        // contamination with explosion deterministic RNG sequences.
        private static System.Random _blastRng = new(42);

        #region Public API — Explosion

        /// <summary>
        /// Spawns ground debris from explosion shockwave.
        ///
        /// <para><b>Modes:</b>
        ///   preScan=false: Scans post-destruction crater (normal flow).
        ///   preScan=true:  Scans current terrain state (effects-only preview).</para>
        /// </summary>
        /// <param name="epicenter">Explosion world position.</param>
        /// <param name="range">Scan radius.</param>
        /// <param name="rng">Deterministic RNG.</param>
        /// <param name="preScan">If true, scans current block layout without waiting for destruction.</param>
        public static void SpawnFromExplosion(Vector2 epicenter, float range,
            System.Random rng, bool preScan = false)
        {
            if (!AshParticlePoolManager.EnsureReady())
            {
                Console.Error("[GroundDebris] AshParticlePoolManager not ready");
                return;
            }

            try { PropagateWave(epicenter, range, rng, preScan); }
            catch (Exception e) { Console.Error($"GroundDebris: {e.Message}"); }
        }

        #endregion

        #region Public API — Muzzle Blast

        /// <summary>
        /// Spawns ground debris from muzzle gas blast using column-scan surface detection.
        ///
        /// WHY: Firing a gun creates a concussive gas blast that disturbs
        /// loose material on nearby surfaces. Uses column-scan algorithm to
        /// find actual surface blocks efficiently, then emits particles with
        /// directional bias away from barrel.
        ///
        /// <para><b>Algorithm (Column-Scan):</b>
        ///   1. For each X offset in [-radius, +radius]:
        ///      a. Start at barrel Y + ScanUpMax
        ///      b. Scan downward until hitting a solid block (surface found)
        ///      c. Check adjacent blocks for additional exposed faces
        ///   2. Emit particles blending face normal (60%) + blast direction (40%)
        ///   3. Particles near barrel get extra velocity in fire direction</para>
        ///
        /// <para><b>Configuration: [Effects.MuzzleBlast]</b>
        ///   • Radius (default 12): base scan radius for guns
        ///   • RadiusTurretMultiplier (default 1.5): turret = base × mult → 18 blocks
        ///   • CountMultiplier (default 3.0): particles per surface × √(powerRatio)
        ///   • MaxParticles / MaxParticlesTurret: particle caps
        ///   • MinEnergy (default 0.5): intensity floor at max range</para>
        /// </summary>
        /// <param name="barrelPos">Barrel world position.</param>
        /// <param name="powerRatio">bulletPower / BaselinePower (25f).</param>
        /// <param name="isTurret">True for turrets — applies RadiusTurretMultiplier.</param>
        public static void SpawnFromMuzzleBlast(Vector2 barrelPos, float powerRatio,
            bool isTurret = false)
        {
            if (!ShrapnelConfig.EnableMuzzleBlastDust.Value) return;
            if (!AshParticlePoolManager.EnsureReady()) return;

            try
            {
                _blastRng = new System.Random(unchecked(
                    Mathf.RoundToInt(barrelPos.x * 100f) * 397 ^
                    Mathf.RoundToInt(barrelPos.y * 100f) ^
                    Time.frameCount));

                PropagateMuzzleBlast(barrelPos, powerRatio, isTurret);
            }
            catch (Exception e) { Console.Error($"MuzzleBlast: {e.Message}"); }
        }

        #endregion

        #region Public API — Bullet Impact Block Blast

        /// <summary>
        /// Spawns dust from solid blocks near a bullet impact point with kinetic energy transfer.
        ///
        /// WHY: When a bullet hits a block, the kinetic energy radiates outward
        /// AND transfers momentum in the bullet's travel direction. Metal conducts
        /// this energy further than soft materials. The result is a directional
        /// dust plume — particles spray forward (bullet direction) with a cone
        /// of debris expanding from the impact point.
        ///
        /// <para><b>Algorithm (Kinetic Transfer):</b>
        ///   1. Determine material conductivity (metal=1.5×, rock=1.0×, soft=0.7×)
        ///   2. Effective radius = base × conductivity
        ///   3. For each exposed surface block in radius:
        ///      a. Blend face normal (35%) + bullet direction (65%) for particle velocity
        ///      b. Scale speed by kinetic transfer factor (energy ∝ 1/dist²)
        ///   4. Spawn bonus "shockwave dust" along bullet travel axis
        ///   5. Metal blocks: spawn additional bright sparks from energy conduction</para>
        ///
        /// <para><b>Configuration: [Effects.BulletImpactBlast]</b>
        ///   • Radius (default 4): scan radius
        ///   • CountMultiplier (default 2.0): particles per surface
        ///   • MaxParticles (default 120): total cap
        ///   • MinEnergy (default 0.15): floor at max range
        ///   • KineticTransfer (default 1.0): directional energy multiplier
        ///   • MetalConductivity (default 1.5): radius bonus for metal blocks</para>
        /// </summary>
        /// <param name="hitPoint">Bullet impact world position.</param>
        /// <param name="powerRatio">bulletPower / BaselinePower (25f).</param>
        /// <param name="bulletDir">Normalized bullet travel direction for kinetic transfer.</param>
        public static void SpawnFromBulletImpact(Vector2 hitPoint, float powerRatio,
            Vector2 bulletDir)
        {
            if (!ShrapnelConfig.EnableBulletImpactBlockBlast.Value) return;
            if (!AshParticlePoolManager.EnsureReady()) return;

            try
            {
                _blastRng = new System.Random(unchecked(
                    Mathf.RoundToInt(hitPoint.x * 100f) * 397 ^
                    Mathf.RoundToInt(hitPoint.y * 100f) ^
                    Time.frameCount));

                PropagateImpactBlast(hitPoint, powerRatio, bulletDir);
            }
            catch (Exception e) { Console.Error($"ImpactBlast: {e.Message}"); }
        }

        /// <summary>
        /// Legacy overload without bullet direction. Falls back to upward spray.
        /// </summary>
        public static void SpawnFromBulletImpact(Vector2 hitPoint, float powerRatio)
        {
            SpawnFromBulletImpact(hitPoint, powerRatio, Vector2.up);
        }

        /// <summary>
        /// Ensures blast/bullet direction doesn't push particles into solid blocks.
        /// If blast direction opposes face normal (dot &lt; 0), reflects it across
        /// the normal so particles always spray AWAY from the surface.
        ///
        /// WHY: When a bullet hits a wall from the open side, bulletDir points
        /// INTO the wall. Blending this with face normal creates a vector that
        /// sends particles through blocks. Reflection fixes the direction while
        /// preserving the angular spread of the blast.
        /// </summary>
        private static Vector2 SafeBlastDirection(Vector2 blastDir, Vector2 faceNormal)
        {
            float dot = Vector2.Dot(blastDir, faceNormal);
            // WHY: dot >= 0 means blast and normal point same hemisphere — safe.
            // dot < 0 means blast points INTO the block — must reflect.
            return dot >= 0f ? blastDir : Vector2.Reflect(blastDir, faceNormal);
        }

        #endregion

        #region Explosion Wave Propagation

        private static void PropagateWave(Vector2 epicenter, float range,
            System.Random rng, bool preScan)
        {
            Vector2Int epi = WorldGeneration.world.WorldToBlockPos(epicenter);

            float rangeMult = ShrapnelConfig.GroundDebrisRangeMultiplier.Value;
            float countMult = ShrapnelConfig.GroundDebrisCountMultiplier.Value;

            int scanRange = Mathf.CeilToInt(range * rangeMult);
            float maxDist = scanRange + 0.5f;

            int yMin = epi.y - ScanDownMax;
            int yMax = epi.y + ScanUpMax;

            int total = 0, surfaces = 0;

            for (int i = 0; i <= scanRange; i++)
            {
                if (total >= MaxTotal) break;

                int firstSide = (i % 2 == 0) ? 0 : 1;
                int secondSide = 1 - firstSide;

                for (int s = 0; s < 2; s++)
                {
                    int side = (s == 0) ? firstSide : secondSide;
                    if (i == 0 && side == 1) continue;
                    if (total >= MaxTotal) break;

                    int dx = (side == 0) ? i : -i;
                    int blockX = epi.x + dx;
                    bool columnHadSurface = false;

                    for (int y = yMax; y >= yMin; y--)
                    {
                        if (total >= MaxTotal) break;

                        ushort blockId;
                        try { blockId = WorldGeneration.world.GetBlock(new Vector2Int(blockX, y)); }
                        catch (IndexOutOfRangeException) { continue; }
                        if (blockId == 0) continue;

                        bool fUp = IsAir(blockX, y + 1);
                        bool fDown = IsAir(blockX, y - 1);
                        bool fLeft = IsAir(blockX - 1, y);
                        bool fRight = IsAir(blockX + 1, y);

                        if (!fUp && !fDown && !fLeft && !fRight) continue;

                        BlockInfo info = null;
                        try { info = WorldGeneration.world.GetBlockInfo(blockId); }
                        catch { }

                        columnHadSurface = true;
                        surfaces++;

                        MaterialCategory cat = BlockClassifier.Classify(info);

                        float distY = y - epi.y;
                        float dist = Mathf.Sqrt(dx * dx + distY * distY);
                        float energy = Mathf.Clamp01(1f - dist / (maxDist * 1.1f));
                        energy = Mathf.Max(energy, 0.15f);

                        float delay = dist / WaveSpeed;

                        Color blockColor = BlockClassifier.GetColor(cat, rng);
                        float matMult = BlockClassifier.GetDustMultiplier(cat);

                        if (ShrapnelConfig.DebugLogging.Value && surfaces <= 5)
                        {
                            string bName = info != null ? info.name : "NULL";
                            Debug.Log($"[{Plugin.Name}] Block: name='{bName}'" +
                                $" cat={cat} matMult={matMult:F2}");
                        }

                        Vector2 blockWorld = WorldGeneration.world.BlockToWorldPos(
                            new Vector2Int(blockX, y));
                        int faceCount = CollectFaces(blockWorld, fUp, fDown, fLeft, fRight);
                        if (faceCount == 0) continue;

                        int budget = MaxTotal - total;
                        total += EmitSurfaceParticles(
                            faceCount, energy, blockColor, matMult,
                            rng, delay, countMult, budget);
                    }

                    if (!columnHadSurface && Mathf.Abs(dx) >= 2 && total < MaxTotal)
                    {
                        float absDist = Mathf.Abs(dx);
                        float energy = Mathf.Clamp01(1f - absDist / (maxDist * 1.1f));
                        if (energy > 0.1f)
                        {
                            float dirSign = (dx > 0) ? 1f : -1f;
                            float delay = absDist / WaveSpeed;
                            Vector2 airPos = WorldGeneration.world.BlockToWorldPos(
                                new Vector2Int(blockX, epi.y));
                            int budget = MaxTotal - total;
                            total += SpawnAirwave(airPos, dirSign, energy, rng,
                                delay, countMult, budget);
                        }
                    }
                }
            }

            if (ShrapnelConfig.DebugLogging.Value)
            {
                string mode = preScan ? "PRE-SCAN" : "CRATER";
                Debug.Log($"[{Plugin.Name}] GroundDebris [{mode}]:" +
                    $" range={range:F0} scan={scanRange}" +
                    $" surfaces={surfaces} particles={total}");
            }
        }

        #endregion

        #region Muzzle Blast Propagation (Column-Scan)

        /// <summary>
        /// Column-scan surface detection: for each X offset, scan downward from
        /// barrel height to find actual surface blocks. Then scan upward from barrel
        /// for ceilings. Much more efficient than brute-force grid for finding
        /// the surfaces that actually matter.
        ///
        /// WHY Column-Scan beats Grid-Scan:
        ///   Grid (old): Check every block in radius² area. Most are buried → wasted.
        ///   Column (new): Per-column, find first surface in O(scanDepth). Guaranteed
        ///   to find ground under barrel, walls beside barrel, ceiling above.
        /// </summary>
        private static void PropagateMuzzleBlast(Vector2 barrelPos, float powerRatio,
            bool isTurret)
        {
            Vector2Int epi;
            try { epi = WorldGeneration.world.WorldToBlockPos(barrelPos); }
            catch { return; }

            int baseRadius = ShrapnelConfig.MuzzleBlastRadius.Value;
            float turretMult = ShrapnelConfig.MuzzleBlastRadiusTurretMult.Value;
            float baseCount = ShrapnelConfig.MuzzleBlastCountMult.Value;
            int maxGun = ShrapnelConfig.MuzzleBlastMaxParticles.Value;
            int maxTurret = ShrapnelConfig.MuzzleBlastMaxParticlesTurret.Value;
            float minEnergy = ShrapnelConfig.MuzzleBlastMinEnergy.Value;

            int scanRadius = isTurret
                ? Mathf.RoundToInt(baseRadius * turretMult)
                : baseRadius;
            float maxDist = scanRadius + 0.5f;

            // WHY: √ for diminishing returns — shotgun doesn't produce 20× pistol
            float countMult = baseCount * Mathf.Clamp(Mathf.Sqrt(powerRatio), 0.8f, 4f);
            int maxTotal = isTurret ? maxTurret : maxGun;
            int total = 0;

            // WHY: Blast direction = downward + slight outward. Muzzle gas pushes
            // air down onto nearby surfaces, kicking up dust.
            Vector2 blastDir = Vector2.down;

            for (int dx = -scanRadius; dx <= scanRadius; dx++)
            {
                if (total >= maxTotal) break;
                int blockX = epi.x + dx;

                // ── Downward scan: find ground surface ──
                total += ScanColumnForSurfaces(
                    blockX, epi.y, -1, ColumnScanDepth,
                    barrelPos, blastDir, maxDist, minEnergy,
                    countMult, maxTotal - total, BlastDirectionBlend);

                // ── Upward scan: find ceiling ──
                total += ScanColumnForSurfaces(
                    blockX, epi.y, +1, ColumnScanDepth / 2,
                    barrelPos, blastDir, maxDist, minEnergy,
                    countMult * 0.5f, maxTotal - total, BlastDirectionBlend);

                // ── Lateral: check left/right faces at barrel height ──
                for (int dy = -2; dy <= 2; dy++)
                {
                    if (total >= maxTotal) break;
                    int blockY = epi.y + dy;

                    ushort blockId;
                    try { blockId = WorldGeneration.world.GetBlock(new Vector2Int(blockX, blockY)); }
                    catch { continue; }
                    if (blockId == 0) continue;

                    // WHY: Only interested in lateral faces near barrel height
                    bool fLeft = IsAir(blockX - 1, blockY);
                    bool fRight = IsAir(blockX + 1, blockY);
                    if (!fLeft && !fRight) continue;

                    // Skip if already found by vertical scan (has up/down face)
                    bool fUp = IsAir(blockX, blockY + 1);
                    bool fDown = IsAir(blockX, blockY - 1);
                    if (fUp || fDown) continue;

                    total += EmitBlockDirectional(
                        blockX, blockY, false, false, fLeft, fRight,
                        barrelPos, blastDir, maxDist, minEnergy,
                        countMult * 0.7f, maxTotal - total, BlastDirectionBlend);
                }
            }
        }

        /// <summary>
        /// Scans a single column in the given Y direction to find surface blocks.
        /// Stops after finding the first surface (exposed face).
        /// </summary>
        /// <returns>Number of particles spawned.</returns>
        private static int ScanColumnForSurfaces(
            int blockX, int startY, int yStep, int maxSteps,
            Vector2 origin, Vector2 blastDir, float maxDist, float minEnergy,
            float countMult, int budget, float dirBlend)
        {
            if (budget <= 0) return 0;

            int total = 0;
            bool foundAir = false;
            int surfacesFound = 0;
            // WHY: Allow up to 3 surfaces per column (e.g., overhangs, caves)
            const int maxSurfacesPerColumn = 3;

            for (int step = 0; step < maxSteps; step++)
            {
                int blockY = startY + step * yStep;

                ushort blockId;
                try { blockId = WorldGeneration.world.GetBlock(new Vector2Int(blockX, blockY)); }
                catch { break; }

                if (blockId == 0)
                {
                    foundAir = true;
                    continue;
                }

                // WHY: We only care about air→solid transitions (actual surfaces).
                // A solid block after air = surface found.
                if (!foundAir && step > 0) continue;

                bool fUp = IsAir(blockX, blockY + 1);
                bool fDown = IsAir(blockX, blockY - 1);
                bool fLeft = IsAir(blockX - 1, blockY);
                bool fRight = IsAir(blockX + 1, blockY);
                if (!fUp && !fDown && !fLeft && !fRight) continue;

                int spawned = EmitBlockDirectional(
                    blockX, blockY, fUp, fDown, fLeft, fRight,
                    origin, blastDir, maxDist, minEnergy,
                    countMult, budget - total, dirBlend);

                total += spawned;
                surfacesFound++;

                if (surfacesFound >= maxSurfacesPerColumn) break;
                foundAir = false; // Reset to find next air→solid transition
            }

            return total;
        }

        #endregion

        #region Bullet Impact Blast Propagation (Kinetic Transfer)

        /// <summary>
        /// Kinetic energy transfer model for bullet impacts.
        ///
        /// The bullet's kinetic energy radiates through the block structure.
        /// Metal conducts energy further, creating a larger dust plume.
        /// Soft materials absorb energy, creating a localized but dense cloud.
        ///
        /// Three phases:
        ///   1. Grid scan — find exposed surfaces, emit directional dust
        ///   2. Shockwave plume — bonus particles along bullet travel axis
        ///   3. Conduction sparks — metal-only bright energy discharge
        /// </summary>
        private static void PropagateImpactBlast(Vector2 hitPoint, float powerRatio,
            Vector2 bulletDir)
        {
            Vector2Int epi;
            try { epi = WorldGeneration.world.WorldToBlockPos(hitPoint); }
            catch { return; }

            int baseRadius = ShrapnelConfig.BulletImpactBlastRadius.Value;
            float baseCount = ShrapnelConfig.BulletImpactBlastCountMult.Value;
            int maxTotal = ShrapnelConfig.BulletImpactBlastMaxParticles.Value;
            float minEnergy = ShrapnelConfig.BulletImpactBlastMinEnergy.Value;
            float kineticMul = ShrapnelConfig.BulletImpactKineticTransfer.Value;
            float metalCond = ShrapnelConfig.BulletImpactMetalConductivity.Value;

            // WHY: Detect material at impact point for conductivity bonus
            float conductivity = 1f;
            bool impactIsMetal = false;
            try
            {
                ushort hitBlockId = WorldGeneration.world.GetBlock(epi);
                if (hitBlockId != 0)
                {
                    BlockInfo hitInfo = WorldGeneration.world.GetBlockInfo(hitBlockId);
                    MaterialCategory hitCat = BlockClassifier.Classify(hitInfo);
                    if (hitCat == MaterialCategory.Metal || (hitInfo != null && hitInfo.metallic))
                    {
                        conductivity = metalCond;
                        impactIsMetal = true;
                    }
                    else if (hitCat == MaterialCategory.Sand || hitCat == MaterialCategory.Organic)
                    {
                        // WHY: Soft materials absorb energy — smaller radius but denser cloud
                        conductivity = 0.7f;
                        baseCount *= 1.4f; // More particles, less spread
                    }
                    else if (hitCat == MaterialCategory.Rock || hitCat == MaterialCategory.Concrete)
                    {
                        conductivity = 1.1f;
                    }
                }
            }
            catch { }

            int scanRadius = Mathf.CeilToInt(baseRadius * conductivity);
            float maxDist = scanRadius + 0.5f;

            // WHY: √ for diminishing returns, same as muzzle blast
            float countMult = baseCount * Mathf.Clamp(Mathf.Sqrt(powerRatio), 0.5f, 3f);
            int total = 0;

            // ── Phase 1: Grid scan for exposed surfaces with directional emission ──
            for (int dx = -scanRadius; dx <= scanRadius; dx++)
            {
                if (total >= maxTotal) break;
                int blockX = epi.x + dx;

                for (int dy = -scanRadius; dy <= scanRadius; dy++)
                {
                    if (total >= maxTotal) break;
                    int blockY = epi.y + dy;

                    ushort blockId;
                    try { blockId = WorldGeneration.world.GetBlock(new Vector2Int(blockX, blockY)); }
                    catch { continue; }
                    if (blockId == 0) continue;

                    bool fUp = IsAir(blockX, blockY + 1);
                    bool fDown = IsAir(blockX, blockY - 1);
                    bool fLeft = IsAir(blockX - 1, blockY);
                    bool fRight = IsAir(blockX + 1, blockY);
                    if (!fUp && !fDown && !fLeft && !fRight) continue;

                    Vector2 blockWorld;
                    try { blockWorld = WorldGeneration.world.BlockToWorldPos(new Vector2Int(blockX, blockY)); }
                    catch { continue; }

                    float dist = Vector2.Distance(blockWorld, hitPoint);
                    if (dist > maxDist) continue;

                    // WHY: Inverse-square falloff for kinetic energy — realistic
                    // energy dissipation through solid material.
                    float energy = Mathf.Clamp01(1f - (dist * dist) / (maxDist * maxDist));
                    energy = Mathf.Max(energy, minEnergy);

                    // WHY: Blocks along bullet travel axis get MORE energy
                    // (kinetic transfer is directional, not isotropic).
                    Vector2 toBlock = (blockWorld - hitPoint);
                    if (toBlock.sqrMagnitude > 0.01f)
                    {
                        float alignment = Vector2.Dot(toBlock.normalized, bulletDir);
                        // PERF: Remap [-1,1] → [0.5, 1.5] — forward blocks get 1.5× energy
                        float dirBonus = 0.5f + Mathf.Clamp01(alignment * 0.5f + 0.5f);
                        energy *= dirBonus;
                    }

                    total += EmitBlockDirectional(
                        blockX, blockY, fUp, fDown, fLeft, fRight,
                        hitPoint, bulletDir, maxDist, minEnergy,
                        countMult, maxTotal - total, ImpactDirectionBlend);
                }
            }

            // ── Phase 2: Shockwave dust plume along bullet axis ──
            // WHY: The bullet punches through and creates a forward-spray of
            // debris — like dust blown out the back of a sandbag.
            int plumeBudget = Mathf.Min(
                Mathf.RoundToInt(KineticBonusParticles * powerRatio * kineticMul),
                maxTotal - total);
            total += SpawnKineticPlume(hitPoint, bulletDir, powerRatio, plumeBudget);

            // ── Phase 3: Metal conduction sparks ──
            if (impactIsMetal && total < maxTotal)
            {
                int sparkBudget = Mathf.Min(
                    Mathf.RoundToInt(3 * Mathf.Sqrt(powerRatio)),
                    maxTotal - total);
                total += SpawnConductionSparks(hitPoint, bulletDir, sparkBudget);
            }
        }

               /// <summary>
        /// Spawns a backward-spray dust plume from bullet impact.
        ///
        /// WHY reversed direction: The bullet hits a surface and stops.
        /// Kinetic energy ejects material BACKWARD toward the shooter,
        /// like dust blowing out the entry hole of a sandbag.
        /// Using bulletDir would spray through the block — physically wrong.
        /// </summary>
        private static int SpawnKineticPlume(Vector2 hitPoint, Vector2 bulletDir,
            float powerRatio, int count)
        {
            if (count <= 0) return 0;

            // WHY: Reverse direction — plume sprays BACK from impact surface.
            // Add slight perpendicular spread for cone shape.
            Vector2 plumeBase = -bulletDir;

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                Vector2 pos = hitPoint + _blastRng.InsideUnitCircle() * 0.15f;

                float spread = _blastRng.Range(-45f, 45f) * Mathf.Deg2Rad;
                Vector2 dir = MathHelper.RotateDirection(plumeBase, spread);
                dir.y += _blastRng.Range(0.1f, 0.4f);
                dir.Normalize();

                float speed = _blastRng.Range(2f, 6f) * Mathf.Sqrt(powerRatio);
                float scale = _blastRng.Range(DustScaleMin, DustScaleMax) * KineticDustScaleMult;
                float gray = _blastRng.Range(0.35f, 0.55f);
                Color col = new(gray, gray * 0.95f, gray * 0.9f, _blastRng.Range(0.4f, 0.65f));

                var vis = new VisualParticleParams(scale, col, 11,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.Dust(
                    dir * speed, _blastRng.Range(1.5f, 4f), _blastRng);

                ParticleHelper.SpawnLit(pos, vis, phy, _blastRng.Range(0f, 100f));
                spawned++;
            }

            // WHY: Large slow-rising dust cloud at impact — the "mushroom" effect.
            if (powerRatio > 2f)
            {
                // WHY: Cloud spawns slightly behind impact (toward shooter)
                Vector2 cloudPos = hitPoint + plumeBase * 0.2f;
                float cloudScale = _blastRng.Range(0.12f, 0.22f) * Mathf.Sqrt(powerRatio);
                float g = _blastRng.Range(0.3f, 0.45f);
                Color cloudCol = new(g, g, g, _blastRng.Range(0.3f, 0.5f));

                var cloudVis = new VisualParticleParams(
                    Mathf.Min(cloudScale, 0.35f), cloudCol, 10,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var cloudPhy = AshPhysicsParams.Smoke(
                    Vector2.up * _blastRng.Range(0.3f, 0.8f)
                        + _blastRng.InsideUnitCircle() * 0.2f,
                    _blastRng.Range(2f, 5f),
                    gravity: -0.015f, drag: 0.3f,
                    turbulence: _blastRng.Range(0.3f, 0.6f),
                    wind: new Vector2(_blastRng.Range(-0.05f, 0.05f), 0.02f),
                    thermalLift: 0.08f);
                ParticleHelper.SpawnLit(cloudPos, cloudVis, cloudPhy, _blastRng.Range(0f, 100f));
                spawned++;
            }

            return spawned;
        }

        /// <summary>
        /// Metal conduction sparks — bright particles that travel along the block
        /// surface away from impact, simulating energy conducted through metal.
        /// </summary>
        private static int SpawnConductionSparks(Vector2 hitPoint, Vector2 bulletDir,
            int count)
        {
            if (count <= 0) return 0;

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                Vector2 pos = hitPoint + _blastRng.InsideUnitCircle() * 0.08f;

                // WHY: Conduction sparks travel perpendicular to bullet — along surface.
                // Creates a "splash" ring of bright sparks on metal.
                Vector2 perpendicular = new(-bulletDir.y, bulletDir.x);
                float side = _blastRng.NextFloat() < 0.5f ? 1f : -1f;
                float angle = _blastRng.Range(-40f, 40f) * Mathf.Deg2Rad;
                Vector2 dir = MathHelper.RotateDirection(
                    perpendicular * side, angle).normalized;

                float heat = _blastRng.Range(0.7f, 1f);
                Color col = new(1f, Mathf.Lerp(0.6f, 0.95f, heat),
                    Mathf.Lerp(0.15f, 0.5f, heat), 0.9f);

                var vis = new VisualParticleParams(
                    _blastRng.Range(0.01f, 0.025f), col, 14,
                    ShrapnelVisuals.TriangleShape.Needle);
                var spark = new SparkParams(dir,
                    _blastRng.Range(8f, 18f), _blastRng.Range(0.06f, 0.15f));
                ParticleHelper.SpawnSpark(pos, vis, spark);
                spawned++;
            }

            return spawned;
        }

        #endregion

        #region Directional Block Emission (shared by muzzle blast + impact)

        /// <summary>
        /// Emits particles from a single block's exposed faces with directional bias.
        /// Blends face normal with blast/bullet direction for realistic spray.
        /// </summary>
        /// <param name="blockX">Block grid X.</param>
        /// <param name="blockY">Block grid Y.</param>
        /// <param name="fUp">Top face exposed.</param>
        /// <param name="fDown">Bottom face exposed.</param>
        /// <param name="fLeft">Left face exposed.</param>
        /// <param name="fRight">Right face exposed.</param>
        /// <param name="origin">Blast/impact origin for distance calc.</param>
        /// <param name="blastDir">Blast direction for velocity blending.</param>
        /// <param name="maxDist">Maximum effective distance.</param>
        /// <param name="minEnergy">Energy floor at max range.</param>
        /// <param name="countMult">Particle count multiplier.</param>
        /// <param name="budget">Remaining particle budget.</param>
        /// <param name="dirBlend">How much blast direction influences velocity (0–1).</param>
        /// <returns>Number of particles spawned.</returns>
        private static int EmitBlockDirectional(
            int blockX, int blockY,
            bool fUp, bool fDown, bool fLeft, bool fRight,
            Vector2 origin, Vector2 blastDir, float maxDist, float minEnergy,
            float countMult, int budget, float dirBlend)
        {
            if (budget <= 0) return 0;

            BlockInfo info = null;
            try
            {
                ushort blockId = WorldGeneration.world.GetBlock(new Vector2Int(blockX, blockY));
                if (blockId == 0) return 0;
                info = WorldGeneration.world.GetBlockInfo(blockId);
            }
            catch { return 0; }

            MaterialCategory cat = BlockClassifier.Classify(info);

            Vector2 blockWorld;
            try { blockWorld = WorldGeneration.world.BlockToWorldPos(new Vector2Int(blockX, blockY)); }
            catch { return 0; }

            float dist = Vector2.Distance(blockWorld, origin);
            if (dist > maxDist) return 0;

            float energy = Mathf.Clamp01(1f - dist / (maxDist * 1.1f));
            energy = Mathf.Max(energy, minEnergy);

            Color blockColor = BlockClassifier.GetColor(cat, _blastRng);
            float matMult = BlockClassifier.GetDustMultiplier(cat);

            int faceCount = CollectFaces(blockWorld, fUp, fDown, fLeft, fRight);
            if (faceCount == 0) return 0;

            return EmitDirectionalParticles(
                faceCount, energy, blockColor, matMult,
                _blastRng, countMult, budget, blastDir, dirBlend);
        }

        /// <summary>
        /// Core emitter blending face normals with blast direction.
        /// Creates more realistic directional dust plumes.
        /// </summary>
        private static int EmitDirectionalParticles(
            int faceCount, float energy, Color blockColor, float materialMult,
            System.Random rng, float countMult, int budget,
            Vector2 blastDir, float dirBlend)
        {
            int spawned = 0;

            int chunks = Mathf.Max(1, Mathf.RoundToInt(ChunksPerSurface * energy * countMult));
            chunks = Mathf.Min(chunks, budget);
            for (int j = 0; j < chunks; j++)
            {
                int fi = j % faceCount;
                SpawnDirectionalChunk(_facePositions[fi], _faceNormals[fi],
                    energy, blockColor, rng, blastDir, dirBlend);
            }
            spawned += chunks; budget -= chunks;
            if (budget <= 0) return spawned;

            int dust = Mathf.Max(1, Mathf.RoundToInt(DustPerSurface * energy * countMult * materialMult));
            dust = Mathf.Min(dust, budget);
            for (int j = 0; j < dust; j++)
            {
                int fi = j % faceCount;
                SpawnDirectionalDust(_facePositions[fi], _faceNormals[fi],
                    energy, blockColor, rng, blastDir, dirBlend);
            }
            spawned += dust; budget -= dust;
            if (budget <= 0) return spawned;

            if (energy > StreakThreshold)
            {
                int streaks = Mathf.Max(1, Mathf.RoundToInt(StreaksPerSurface * energy * countMult));
                streaks = Mathf.Min(streaks, budget);
                for (int j = 0; j < streaks; j++)
                {
                    int fi = j % faceCount;
                    SpawnStreak(_facePositions[fi], _faceNormals[fi],
                        energy, blockColor, rng, 0f);
                }
                spawned += streaks;
            }

            return spawned;
        }

        #endregion

        #region Face Collection

        private static int CollectFaces(Vector2 blockWorld,
            bool up, bool down, bool left, bool right)
        {
            int n = 0;
            if (up) { _faceNormals[n] = NormalUp; _facePositions[n] = blockWorld + new Vector2(0f, 0.5f); n++; }
            if (down) { _faceNormals[n] = NormalDown; _facePositions[n] = blockWorld + new Vector2(0f, -0.5f); n++; }
            if (left) { _faceNormals[n] = NormalLeft; _facePositions[n] = blockWorld + new Vector2(-0.5f, 0f); n++; }
            if (right) { _faceNormals[n] = NormalRight; _facePositions[n] = blockWorld + new Vector2(0.5f, 0f); n++; }
            return n;
        }

        #endregion

        #region Surface Particle Emission (explosion — non-directional)

        private static int EmitSurfaceParticles(
            int faceCount, float energy, Color blockColor, float materialMult,
            System.Random rng, float delay, float countMult, int budget)
        {
            int spawned = 0;

            int chunks = Mathf.Max(1, Mathf.RoundToInt(ChunksPerSurface * energy * countMult));
            chunks = Mathf.Min(chunks, budget);
            for (int j = 0; j < chunks; j++)
            {
                int fi = j % faceCount;
                SpawnChunk(_facePositions[fi], _faceNormals[fi], energy, blockColor, rng, delay);
            }
            spawned += chunks; budget -= chunks;
            if (budget <= 0) return spawned;

            int dust = Mathf.Max(1, Mathf.RoundToInt(DustPerSurface * energy * countMult * materialMult));
            dust = Mathf.Min(dust, budget);
            for (int j = 0; j < dust; j++)
            {
                int fi = j % faceCount;
                SpawnDust(_facePositions[fi], _faceNormals[fi], energy, blockColor, rng, delay);
            }
            spawned += dust; budget -= dust;
            if (budget <= 0) return spawned;

            if (energy > StreakThreshold)
            {
                int streaks = Mathf.Max(1, Mathf.RoundToInt(StreaksPerSurface * energy * countMult));
                streaks = Mathf.Min(streaks, budget);
                for (int j = 0; j < streaks; j++)
                {
                    int fi = j % faceCount;
                    SpawnStreak(_facePositions[fi], _faceNormals[fi], energy, blockColor, rng, delay);
                }
                spawned += streaks;
            }

            return spawned;
        }

        #endregion

        #region Particle Spawners — Directional (Muzzle Blast + Impact)

        /// <summary>
        /// Chunk with directional bias. Uses <see cref="SafeBlastDirection"/>
        /// to prevent particles from flying through solid blocks.
        /// </summary>
        private static void SpawnDirectionalChunk(Vector2 pos, Vector2 normal,
            float energy, Color blockColor, System.Random rng,
            Vector2 blastDir, float dirBlend)
        {
            Vector2 position = pos + rng.InsideUnitCircle() * 0.4f;
            float scale = rng.Range(ChunkScaleMin, ChunkScaleMax) * (0.6f + energy * 0.8f);
            Color color = new(blockColor.r, blockColor.g, blockColor.b, ChunkAlpha);

            // WHY: SafeBlastDirection reflects blastDir when it opposes the face
            // normal, preventing chunks from flying through solid blocks.
            Vector2 safeBlast = SafeBlastDirection(blastDir, normal);
            Vector2 baseDir = Vector2.Lerp(normal, safeBlast, dirBlend).normalized;
            Vector2 tangent = new(-baseDir.y, baseDir.x);
            Vector2 velocity = baseDir * rng.Range(ChunkSpeedMin, ChunkSpeedMax) * energy * KineticSpeedBoost
                             + tangent * rng.Range(-2f, 2f);

            var visual = new VisualParticleParams(scale, color, 12,
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            var physics = AshPhysicsParams.Chunk(velocity,
                rng.Range(ChunkLifeMin, ChunkLifeMax), rng);

            ParticleHelper.SpawnLit(position, visual, physics, rng.Range(0f, 100f));
        }

               /// <summary>
        /// Dust with directional bias, upward thermal drift, and safe direction.
        /// </summary>
        private static void SpawnDirectionalDust(Vector2 pos, Vector2 normal,
            float energy, Color blockColor, System.Random rng,
            Vector2 blastDir, float dirBlend)
        {
            Vector2 position = pos + rng.InsideUnitCircle() * 0.9f;
            float scale = rng.Range(DustScaleMin, DustScaleMax) * (0.7f + energy * 0.6f);

            Color dustColor = Color.Lerp(blockColor, new Color(0.55f, 0.53f, 0.50f), 0.35f);
            dustColor.a = DustAlpha;

            Vector2 safeBlast = SafeBlastDirection(blastDir, normal);
            Vector2 baseDir = Vector2.Lerp(normal, safeBlast, dirBlend).normalized;
            // WHY: Add upward component — dust RISES in real life due to thermal
            // convection from the blast and simple air displacement.
            baseDir.y += rng.Range(0.15f, 0.5f);
            baseDir.Normalize();

            Vector2 tangent = new(-baseDir.y, baseDir.x);
            Vector2 velocity = baseDir  * rng.Range(DustSpeedMin, DustSpeedMax) * energy * 1.5f
                             + tangent * rng.Range(-1.5f, 1.5f);

            var visual  = new VisualParticleParams(scale, dustColor, 11,
                ShrapnelVisuals.TriangleShape.Chunk);
            var physics = AshPhysicsParams.Dust(velocity,
                rng.Range(DustLifeMin, DustLifeMax), rng);

            ParticleHelper.SpawnLit(position, visual, physics, rng.Range(0f, 100f));
        }

        #endregion

        #region Particle Spawners — Non-directional (Explosion)

        private static void SpawnChunk(Vector2 pos, Vector2 normal, float energy,
            Color blockColor, System.Random rng, float delay)
        {
            Vector2 position = pos + rng.InsideUnitCircle() * 0.4f;
            float scale = rng.Range(ChunkScaleMin, ChunkScaleMax) * (0.6f + energy * 0.8f);
            Color color = new(blockColor.r, blockColor.g, blockColor.b, ChunkAlpha);

            Vector2 tangent = new(-normal.y, normal.x);
            Vector2 velocity = normal * rng.Range(ChunkSpeedMin, ChunkSpeedMax) * energy
                             + tangent * rng.Range(-2f, 2f);

            var visual = new VisualParticleParams(scale, color, 12,
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            var physics = AshPhysicsParams.Chunk(velocity,
                rng.Range(ChunkLifeMin, ChunkLifeMax), rng);

            ParticleHelper.SpawnLit(position, visual, physics,
                rng.Range(0f, 100f), delay);
        }

        private static void SpawnDust(Vector2 pos, Vector2 normal, float energy,
            Color blockColor, System.Random rng, float delay)
        {
            Vector2 position = pos + rng.InsideUnitCircle() * 0.9f;
            float scale = rng.Range(DustScaleMin, DustScaleMax) * (0.7f + energy * 0.6f);

            Color dustColor = Color.Lerp(blockColor, new Color(0.55f, 0.53f, 0.50f), 0.35f);
            dustColor.a = DustAlpha;

            Vector2 tangent = new(-normal.y, normal.x);
            Vector2 velocity = normal * rng.Range(DustSpeedMin, DustSpeedMax) * energy
                             + tangent * rng.Range(-1.5f, 1.5f);

            var visual = new VisualParticleParams(scale, dustColor, 11,
                ShrapnelVisuals.TriangleShape.Chunk);
            var physics = AshPhysicsParams.Dust(velocity,
                rng.Range(DustLifeMin, DustLifeMax), rng);

            ParticleHelper.SpawnLit(position, visual, physics,
                rng.Range(0f, 100f), delay);
        }

        private static void SpawnStreak(Vector2 pos, Vector2 normal, float energy,
            Color blockColor, System.Random rng, float delay)
        {
            Vector2 position = pos + rng.InsideUnitCircle() * 0.2f;
            float scale = rng.Range(StreakScaleMin, StreakScaleMax);
            Color color = new(blockColor.r * 0.75f, blockColor.g * 0.75f,
                blockColor.b * 0.75f, StreakAlpha);

            Vector2 tangent = new(-normal.y, normal.x);
            Vector2 dir = (normal + tangent * rng.Range(-0.4f, 0.4f)).normalized;
            float speed = rng.Range(StreakSpeedMin, StreakSpeedMax) * energy;

            var visual = new VisualParticleParams(scale, color, 12,
                ShrapnelVisuals.TriangleShape.Needle);
            var physics = AshPhysicsParams.Streak(dir * speed,
                rng.Range(StreakLifeMin, StreakLifeMax));

            ParticleHelper.SpawnLit(position, visual, physics,
                rng.Range(0f, 100f), delay);
        }

        private static int SpawnAirwave(Vector2 pos, float dirSign, float energy,
            System.Random rng, float delay, float countMult, int budget)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(AirwavePerColumn * energy * countMult));
            count = Mathf.Min(count, budget);

            for (int j = 0; j < count; j++)
            {
                Vector2 spawnPos = pos + new Vector2(
                    rng.Range(-0.2f, 0.2f), rng.Range(-0.5f, 0.5f));
                float scale = rng.Range(AirwaveScaleMin, AirwaveScaleMax) * (0.5f + energy * 0.5f);
                float g = rng.Range(0.45f, 0.6f);
                Color color = new(g, g * 0.97f, g * 0.94f, AirwaveAlpha * energy);

                Vector2 velocity = new(
                    dirSign * rng.Range(AirwaveSpeedMin, AirwaveSpeedMax) * energy,
                    rng.Range(-0.5f, 1.5f));

                var visual = new VisualParticleParams(scale, color, 10,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var physics = AshPhysicsParams.Dust(velocity,
                    rng.Range(AirwaveLifeMin, AirwaveLifeMax), rng);

                ParticleHelper.SpawnLit(spawnPos, visual, physics,
                    rng.Range(0f, 100f), delay);
            }

            return count;
        }

        #endregion

        #region Helpers

        private static bool IsAir(int blockX, int blockY)
        {
            try { return WorldGeneration.world.GetBlock(new Vector2Int(blockX, blockY)) == 0; }
            catch (IndexOutOfRangeException) { return true; }
        }

        #endregion
    }
}