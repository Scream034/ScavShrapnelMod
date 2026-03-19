using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Ground debris particles from explosion shockwave.
    ///
    /// Material classification uses <see cref="BlockClassifier"/> — single source of truth.
    ///
    /// Lighting:
    ///   All debris uses LitMaterial (dark in dark areas).
    ///   Ground debris is inert matter, not self-luminous.
    ///
    /// Particle lifecycle managed by AshParticlePoolManager (zero-GC).
    /// </summary>
    public static class GroundDebrisLogic
    {
        //
        //  FACE NORMALS
        //

        private static readonly Vector2 NormalUp = Vector2.up;
        private static readonly Vector2 NormalDown = Vector2.down;
        private static readonly Vector2 NormalLeft = Vector2.left;
        private static readonly Vector2 NormalRight = Vector2.right;

        //
        //  PRE-ALLOCATED FACE BUFFERS
        //

        private static readonly Vector2[] _faceNormals = new Vector2[4];
        private static readonly Vector2[] _facePositions = new Vector2[4];

        //
        //  SCAN / WAVE / BUDGET
        //

        private const int ScanDownMax = 25;
        private const int ScanUpMax = 20;
        private const float WaveSpeed = 50f;
        private const int MaxTotal = 3000;

        //
        //  PER-SURFACE BASE PARTICLE COUNTS
        //

        private const int ChunksPerSurface = 3;
        private const int DustPerSurface = 6;
        private const int StreaksPerSurface = 3;
        private const float StreakThreshold = 0.35f;

        //
        //  CHUNK PARAMETERS
        //

        private const float ChunkScaleMin = 0.12f;
        private const float ChunkScaleMax = 0.28f;
        private const float ChunkSpeedMin = 2f;
        private const float ChunkSpeedMax = 5f;
        private const float ChunkLifeMin = 8f;
        private const float ChunkLifeMax = 20f;
        private const float ChunkAlpha = 0.9f;

        //
        //  DUST PARAMETERS
        //

        private const float DustScaleMin = 0.03f;
        private const float DustScaleMax = 0.2f;
        private const float DustSpeedMin = 0.8f;
        private const float DustSpeedMax = 3f;
        private const float DustLifeMin = 15f;
        private const float DustLifeMax = 40f;
        private const float DustAlpha = 0.55f;

        //
        //  STREAK PARAMETERS
        //

        private const float StreakScaleMin = 0.06f;
        private const float StreakScaleMax = 0.14f;
        private const float StreakSpeedMin = 15f;
        private const float StreakSpeedMax = 30f;
        private const float StreakLifeMin = 0.5f;
        private const float StreakLifeMax = 1.8f;
        private const float StreakAlpha = 0.85f;

        //
        //  AIRWAVE PARAMETERS
        //

        private const int AirwavePerColumn = 2;
        private const float AirwaveScaleMin = 0.04f;
        private const float AirwaveScaleMax = 0.10f;
        private const float AirwaveSpeedMin = 5f;
        private const float AirwaveSpeedMax = 10f;
        private const float AirwaveLifeMin = 2f;
        private const float AirwaveLifeMax = 5f;
        private const float AirwaveAlpha = 0.25f;

        //  PUBLIC API

        /// <summary>
        /// Spawns ground debris from explosion shockwave.
        ///
        /// MODES:
        ///   preScan=false (default): Scans for newly-exposed crater surfaces.
        ///     Called from PostExplosion after blocks destroyed.
        ///   preScan=true: Scans existing exposed block faces (no crater needed).
        ///     Called from effects-only mode (-e flag) to show ground debris
        ///     without terrain destruction. Particles may clip into intact blocks.
        /// </summary>
        /// <param name="epicenter">Explosion world position.</param>
        /// <param name="range">Scan radius.</param>
        /// <param name="rng">Deterministic RNG.</param>
        /// <param name="preScan">
        /// If true, scans current block layout (effects-only preview).
        /// If false, scans post-destruction crater (normal mode).
        /// </param>
        public static void SpawnFromExplosion(Vector2 epicenter, float range,
            System.Random rng, bool preScan = false)
        {
            // CRITICAL: Ensure pools are ready before spawning.
            // This is the lazy-init safety net — if somehow pools aren't
            // ready by PostExplosion time, initialize them now.
            if (!AshParticlePoolManager.EnsureReady())
            {
                Console.Error("[GroundDebris] AshParticlePoolManager failed to initialize");
                return;
            }

            try
            {
                PropagateWave(epicenter, range, rng, preScan);
            }
            catch (Exception e)
            {
                Console.Error($"GroundDebris: {e.Message}");
            }
        }

        //  WAVE PROPAGATION

        /// <summary>
        /// Propagates debris wave from epicenter.
        ///
        /// WHY preScan parameter: In effects-only mode, blocks haven't been destroyed
        /// yet, so we scan current terrain for exposed faces. In normal mode (after
        /// CreateExplosion), we scan the newly-formed crater surfaces.
        ///
        /// Same algorithm, different timing — preScan=true just means "use current
        /// block state" instead of "use post-destruction state".
        /// </summary>
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

            int total = 0;
            int surfaces = 0;

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
                        try
                        {
                            blockId = WorldGeneration.world.GetBlock(
                                new Vector2Int(blockX, y));
                        }
                        catch (IndexOutOfRangeException) { continue; }

                        if (blockId == 0) continue;

                        bool fUp = IsAir(blockX, y + 1);
                        bool fDown = IsAir(blockX, y - 1);
                        bool fLeft = IsAir(blockX - 1, y);
                        bool fRight = IsAir(blockX + 1, y);

                        if (!fUp && !fDown && !fLeft && !fRight) continue;

                        BlockInfo info = null;
                        try { info = WorldGeneration.world.GetBlockInfo(blockId); }
                        catch { /* null safe */ }

                        columnHadSurface = true;
                        surfaces++;

                        MaterialCategory cat = BlockClassifier.Classify(info);

                        float distY = y - epi.y;
                        float dist = Mathf.Sqrt((float)(dx * dx) + distY * distY);
                        float energy = Mathf.Clamp01(1f - dist / (maxDist * 1.1f));
                        energy = Mathf.Max(energy, 0.15f);

                        float delay = dist / WaveSpeed;

                        Color blockColor = BlockClassifier.GetColor(cat, rng);
                        float matMult = BlockClassifier.GetDustMultiplier(cat);

                        if (ShrapnelConfig.DebugLogging.Value && surfaces <= 5)
                        {
                            string bName = info != null ? info.name : "NULL";
                            string bHit = info != null ? (info.hitsound ?? "?") : "?";
                            string bStep = info != null ? (info.stepsound ?? "?") : "?";
                            float bHp = info != null ? info.health : -1f;
                            Debug.Log($"[{Plugin.Name}] Block: name='{bName}'" +
                                      $" hp={bHp:F0} hit={bHit} step={bStep}" +
                                      $" cat={cat} matMult={matMult:F2}");
                        }

                        Vector2 blockWorld = WorldGeneration.world.BlockToWorldPos(
                            new Vector2Int(blockX, y));
                        int faceCount = CollectFaces(
                            blockWorld, fUp, fDown, fLeft, fRight);
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
                            total += SpawnAirwave(
                                airPos, dirSign, energy,
                                rng, delay, countMult, budget);
                        }
                    }
                }
            }

            if (ShrapnelConfig.DebugLogging.Value)
            {
                string mode = preScan ? "PRE-SCAN" : "CRATER";
                Debug.Log($"[{Plugin.Name}] GroundDebris [{mode}]:" +
                          $" range={range:F0} scan={scanRange}" +
                          $" surfaces={surfaces} particles={total}" +
                          $" poolActive={AshParticlePoolManager.TotalActive}");
            }
        }

        //  FACE COLLECTION

        private static int CollectFaces(
            Vector2 blockWorld,
            bool up, bool down, bool left, bool right)
        {
            int n = 0;
            if (up) { _faceNormals[n] = NormalUp; _facePositions[n] = blockWorld + new Vector2(0f, 0.5f); n++; }
            if (down) { _faceNormals[n] = NormalDown; _facePositions[n] = blockWorld + new Vector2(0f, -0.5f); n++; }
            if (left) { _faceNormals[n] = NormalLeft; _facePositions[n] = blockWorld + new Vector2(-0.5f, 0f); n++; }
            if (right) { _faceNormals[n] = NormalRight; _facePositions[n] = blockWorld + new Vector2(0.5f, 0f); n++; }
            return n;
        }

        //  PARTICLE EMISSION

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

        //  PARTICLE SPAWNERS — all use ParticleHelper.SpawnLit

        private static void SpawnChunk(
            Vector2 pos, Vector2 normal, float energy,
            Color blockColor, System.Random rng, float delay)
        {
            Vector2 position = pos + rng.InsideUnitCircle() * 0.4f;
            float scale = rng.Range(ChunkScaleMin, ChunkScaleMax) * (0.6f + energy * 0.8f);
            Color color = new Color(blockColor.r, blockColor.g, blockColor.b, ChunkAlpha);

            Vector2 tangent = new Vector2(-normal.y, normal.x);
            Vector2 velocity = normal * rng.Range(ChunkSpeedMin, ChunkSpeedMax) * energy
                             + tangent * rng.Range(-2f, 2f);

            var visual = new VisualParticleParams(scale, color, 12,
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            var physics = AshPhysicsParams.Chunk(velocity, rng.Range(ChunkLifeMin, ChunkLifeMax), rng);

            ParticleHelper.SpawnLit("GndChunk", position, visual, physics,
                rng.Range(0f, 100f), delay);
        }

        private static void SpawnDust(
            Vector2 pos, Vector2 normal, float energy,
            Color blockColor, System.Random rng, float delay)
        {
            Vector2 position = pos + rng.InsideUnitCircle() * 0.9f;
            float scale = rng.Range(DustScaleMin, DustScaleMax) * (0.7f + energy * 0.6f);

            Color dustColor = Color.Lerp(blockColor, new Color(0.55f, 0.53f, 0.50f), 0.35f);
            dustColor.a = DustAlpha;

            Vector2 tangent = new Vector2(-normal.y, normal.x);
            Vector2 velocity = normal * rng.Range(DustSpeedMin, DustSpeedMax) * energy
                             + tangent * rng.Range(-1.5f, 1.5f);

            var visual = new VisualParticleParams(scale, dustColor, 11,
                ShrapnelVisuals.TriangleShape.Chunk);
            var physics = AshPhysicsParams.Dust(velocity, rng.Range(DustLifeMin, DustLifeMax), rng);

            ParticleHelper.SpawnLit("GndDust", position, visual, physics,
                rng.Range(0f, 100f), delay);
        }

        private static void SpawnStreak(
            Vector2 pos, Vector2 normal, float energy,
            Color blockColor, System.Random rng, float delay)
        {
            Vector2 position = pos + rng.InsideUnitCircle() * 0.2f;
            float scale = rng.Range(StreakScaleMin, StreakScaleMax);
            Color color = new Color(blockColor.r * 0.75f, blockColor.g * 0.75f,
                blockColor.b * 0.75f, StreakAlpha);

            Vector2 tangent = new Vector2(-normal.y, normal.x);
            Vector2 dir = (normal + tangent * rng.Range(-0.4f, 0.4f)).normalized;
            float speed = rng.Range(StreakSpeedMin, StreakSpeedMax) * energy;

            var visual = new VisualParticleParams(scale, color, 12,
                ShrapnelVisuals.TriangleShape.Needle);
            var physics = AshPhysicsParams.Streak(dir * speed,
                rng.Range(StreakLifeMin, StreakLifeMax));

            ParticleHelper.SpawnLit("GndStreak", position, visual, physics,
                rng.Range(0f, 100f), delay);
        }

        private static int SpawnAirwave(
            Vector2 pos, float dirSign, float energy,
            System.Random rng, float delay, float countMult, int budget)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(AirwavePerColumn * energy * countMult));
            count = Mathf.Min(count, budget);

            for (int j = 0; j < count; j++)
            {
                Vector2 spawnPos = pos + new Vector2(rng.Range(-0.2f, 0.2f), rng.Range(-0.5f, 0.5f));
                float scale = rng.Range(AirwaveScaleMin, AirwaveScaleMax) * (0.5f + energy * 0.5f);

                float g = rng.Range(0.45f, 0.6f);
                Color color = new Color(g, g * 0.97f, g * 0.94f, AirwaveAlpha * energy);

                Vector2 velocity = new Vector2(
                    dirSign * rng.Range(AirwaveSpeedMin, AirwaveSpeedMax) * energy,
                    rng.Range(-0.5f, 1.5f));

                var visual = new VisualParticleParams(scale, color, 10,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var physics = AshPhysicsParams.Dust(velocity,
                    rng.Range(AirwaveLifeMin, AirwaveLifeMax), rng);

                ParticleHelper.SpawnLit("GndWave", spawnPos, visual, physics,
                    rng.Range(0f, 100f), delay);
            }

            return count;
        }

        //  HELPERS

        private static bool IsAir(int blockX, int blockY)
        {
            try { return WorldGeneration.world.GetBlock(new Vector2Int(blockX, blockY)) == 0; }
            catch (IndexOutOfRangeException) { return true; }
        }
    }
}