using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Visual ground debris particles from explosion shockwave.
    ///
    /// Spawns three particle types with distinct physics:
    /// - Chunks: large, fly low and sideways, slow settling
    /// - Dust: fine, almost hovers in air, very long lifetime
    /// - Streaks: fast "shots" of dirt from epicenter
    ///
    /// Scan radius and particle counts configurable via ShrapnelConfig.
    /// All objects registered in <see cref="DebrisTracker"/>.
    /// </summary>
    public static class GroundDebrisLogic
    {
        // ── SCAN PARAMETERS ──
        private const int ScanStepX = 1;

        /// <summary>Blocks DOWN from epicenter for surface search.</summary>
        private const int ScanDownMax = 25;

        /// <summary>Blocks UP from epicenter for surface search.</summary>
        private const int ScanUpMax = 20;

        // ── CHUNK PARAMETERS ──
        private const int ChunksPerSurface = 8;
        private const float ChunkScaleMin = 0.12f;
        private const float ChunkScaleMax = 0.28f;
        private const float ChunkSpeedUpMin = 2f;
        private const float ChunkSpeedUpMax = 5f;
        private const float ChunkSpeedSideMin = 0.5f;
        private const float ChunkSpeedSideMax = 2f;
        private const float ChunkLifeMin = 4f;
        private const float ChunkLifeMax = 12f;
        private const float ChunkAlpha = 0.9f;

        // ── DUST PARAMETERS ──
        private const int DustPerSurface = 18;
        private const float DustScaleMin = 0.03f;
        private const float DustScaleMax = 0.2f;
        private const float DustSpeedUpMin = 0.8f;
        private const float DustSpeedUpMax = 3f;
        private const float DustSpeedSideMin = 0.8f;
        private const float DustSpeedSideMax = 2.5f;
        private const float DustLifeMin = 6f;
        private const float DustLifeMax = 25f;
        private const float DustAlpha = 0.55f;

        // ── STREAK PARAMETERS ──
        private const int StreaksPerSurface = 10;
        private const float StreakScaleMin = 0.06f;
        private const float StreakScaleMax = 0.14f;
        private const float StreakSpeedMin = 15f;
        private const float StreakSpeedMax = 30f;
        private const float StreakLifeMin = 0.5f;
        private const float StreakLifeMax = 1.8f;
        private const float StreakAlpha = 0.85f;
        private const float StreakIntensityThreshold = 0.35f;

        /// <summary>
        /// Spawns ground debris around explosion epicenter.
        /// Uses wide scan range for surface detection.
        ///
        /// WHY: Range multiplier from config (default 3.5) ensures debris
        /// covers a wide area. CountMultiplier scales particle density.
        /// </summary>
        public static void SpawnFromExplosion(Vector2 epicenter, float range, System.Random rng)
        {
            if (ShrapnelVisuals.UnlitMaterial == null) return;

            try
            {
                Vector2Int epicenterBlock = WorldGeneration.world.WorldToBlockPos(epicenter);
                float rangeMultiplier = ShrapnelConfig.GroundDebrisRangeMultiplier.Value;
                float countMultiplier = ShrapnelConfig.GroundDebrisCountMultiplier.Value;
                int scanRange = Mathf.CeilToInt(range * rangeMultiplier);

                int surfacesFound = 0;

                for (int dx = -scanRange; dx <= scanRange; dx += ScanStepX)
                {
                    int blockX = epicenterBlock.x + dx;

                    if (!FindSurface(blockX, epicenterBlock.y, out Vector2Int surfacePos, out BlockInfo surfaceInfo))
                        continue;

                    surfacesFound++;

                    Vector2 worldPos = WorldGeneration.world.BlockToWorldPos(
                        new Vector2Int(surfacePos.x, surfacePos.y + 1));

                    Color blockColor = GetBlockColor(surfaceInfo, rng);

                    float dist = Mathf.Abs(dx);
                    float intensity = Mathf.Clamp01(1f - dist / (scanRange * 1.1f));
                    intensity = Mathf.Max(intensity, 0.15f);

                    float radialX = (dx == 0) ? 0f : Mathf.Sign(dx);

                    SpawnSurfaceDebris(worldPos, radialX, intensity, blockColor, rng, countMultiplier);
                }

                if (ShrapnelConfig.DebugLogging.Value)
                {
                    Debug.Log($"[ShrapnelMod] GroundDebris: scanRange={scanRange}" +
                              $" surfaces={surfacesFound} epicY={epicenterBlock.y}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ShrapnelMod] GroundDebris: {e.Message}");
            }
        }

        /// <summary>
        /// Spawns all debris types for a single surface point.
        /// </summary>
        private static void SpawnSurfaceDebris(Vector2 worldPos, float radialX,
            float intensity, Color blockColor, System.Random rng, float countMultiplier)
        {
            // Chunks
            int chunks = Mathf.Max(1, Mathf.RoundToInt(ChunksPerSurface * intensity * countMultiplier));
            for (int j = 0; j < chunks; j++)
                SpawnChunk(worldPos, radialX, intensity, blockColor, rng);

            // Dust
            int dust = Mathf.Max(1, Mathf.RoundToInt(DustPerSurface * intensity * countMultiplier));
            for (int j = 0; j < dust; j++)
                SpawnDust(worldPos, radialX, intensity, blockColor, rng);

            // Streaks (only near center)
            if (intensity > StreakIntensityThreshold)
            {
                int streaks = Mathf.Max(1, Mathf.RoundToInt(StreaksPerSurface * intensity * countMultiplier));
                for (int j = 0; j < streaks; j++)
                    SpawnStreak(worldPos, radialX, intensity, blockColor, rng);
            }
        }

        // ── PARTICLE SPAWNERS ──

        private static void SpawnChunk(Vector2 worldPos, float radialX, float intensity,
            Color blockColor, System.Random rng)
        {
            Vector2 position = worldPos + rng.InsideUnitCircle() * 0.4f;

            float scale = rng.Range(ChunkScaleMin, ChunkScaleMax) * (0.6f + intensity * 0.8f);
            Color color = new Color(blockColor.r, blockColor.g, blockColor.b, ChunkAlpha);

            float speedUp = rng.Range(ChunkSpeedUpMin, ChunkSpeedUpMax) * intensity;
            float speedSide = radialX * rng.Range(ChunkSpeedSideMin, ChunkSpeedSideMax)
                            + rng.Range(-2f, 2f);
            Vector2 velocity = new Vector2(speedSide, speedUp);

            var visual = new VisualParticleParams(scale, color, 12,
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            var physics = AshPhysicsParams.Chunk(velocity, rng.Range(ChunkLifeMin, ChunkLifeMax), rng);

            ParticleHelper.SpawnAshParticle("GndChunk", position, visual, physics, rng.Range(0f, 100f));
        }

        private static void SpawnDust(Vector2 worldPos, float radialX, float intensity,
            Color blockColor, System.Random rng)
        {
            Vector2 position = worldPos + rng.InsideUnitCircle() * 0.9f;

            float scale = rng.Range(DustScaleMin, DustScaleMax) * (0.7f + intensity * 0.6f);
            Color dustColor = Color.Lerp(blockColor, new Color(0.65f, 0.6f, 0.55f), 0.35f);
            dustColor.a = DustAlpha;

            float speedUp = rng.Range(DustSpeedUpMin, DustSpeedUpMax) * intensity;
            float speedSide = radialX * rng.Range(DustSpeedSideMin, DustSpeedSideMax)
                            + rng.Range(-1.5f, 1.5f);
            Vector2 velocity = new Vector2(speedSide, speedUp);

            var visual = new VisualParticleParams(scale, dustColor, 11,
                ShrapnelVisuals.TriangleShape.Chunk);
            var physics = AshPhysicsParams.Dust(velocity, rng.Range(DustLifeMin, DustLifeMax), rng);

            ParticleHelper.SpawnAshParticle("GndDust", position, visual, physics, rng.Range(0f, 100f));
        }

        private static void SpawnStreak(Vector2 worldPos, float radialX, float intensity,
            Color blockColor, System.Random rng)
        {
            Vector2 position = worldPos + rng.InsideUnitCircle() * 0.2f;

            float scale = rng.Range(StreakScaleMin, StreakScaleMax);
            Color color = new Color(blockColor.r * 0.75f, blockColor.g * 0.75f, blockColor.b * 0.75f, StreakAlpha);

            float speed = rng.Range(StreakSpeedMin, StreakSpeedMax) * intensity;
            Vector2 dir = new Vector2(
                radialX * rng.Range(1f, 2.5f) + rng.Range(-0.4f, 0.4f),
                rng.Range(0.2f, 0.8f)).normalized;

            var visual = new VisualParticleParams(scale, color, 12,
                ShrapnelVisuals.TriangleShape.Needle);
            var physics = AshPhysicsParams.Streak(dir * speed, rng.Range(StreakLifeMin, StreakLifeMax));

            ParticleHelper.SpawnAshParticle("GndStreak", position, visual, physics, rng.Range(0f, 100f));
        }

        // ── SURFACE DETECTION ──

        /// <summary>
        /// Finds surface (air-to-solid transition) in column blockX.
        /// Scans from top to bottom looking for air->block transition.
        ///
        /// WHY: Previous version only scanned 5 blocks up from epicenter,
        /// missing surfaces when explosion was below ground or in a cave.
        /// Now scans 20 blocks up and 25 blocks down for full coverage.
        /// Also finds surfaces BELOW the epicenter (ceiling of cave below).
        /// </summary>
        private static bool FindSurface(int blockX, int epicenterBlockY,
            out Vector2Int surfacePos, out BlockInfo surfaceInfo)
        {
            surfacePos = Vector2Int.zero;
            surfaceInfo = null;

            int startY = epicenterBlockY + ScanUpMax;
            int endY = epicenterBlockY - ScanDownMax;
            bool wasAir = false;

            for (int y = startY; y >= endY; y--)
            {
                try
                {
                    Vector2Int pos = new Vector2Int(blockX, y);
                    ushort blockId = WorldGeneration.world.GetBlock(pos);

                    if (blockId == 0)
                    {
                        wasAir = true;
                    }
                    else if (wasAir)
                    {
                        BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                        if (info != null)
                        {
                            surfacePos = pos;
                            surfaceInfo = info;
                            return true;
                        }
                    }
                }
                catch (IndexOutOfRangeException) { continue; }
            }

            return false;
        }

        // ── BLOCK COLOR ──

        private static Color GetBlockColor(BlockInfo info, System.Random rng)
        {
            if (info.metallic)
            {
                float gray = rng.Range(0.25f, 0.4f);
                return new Color(gray, gray, gray);
            }

            string name = info.name ?? string.Empty;

            if (ContainsAny(name, "stone", "rock", "granite", "slate"))
            {
                float g = rng.Range(0.35f, 0.55f);
                return new Color(g, g * 0.95f, g * 0.9f);
            }

            if (Contains(name, "sand"))
                return new Color(rng.Range(0.7f, 0.85f), rng.Range(0.6f, 0.75f), rng.Range(0.3f, 0.45f));

            if (Contains(name, "clay"))
                return new Color(rng.Range(0.55f, 0.7f), rng.Range(0.3f, 0.45f), rng.Range(0.2f, 0.3f));

            if (ContainsAny(name, "wood", "plank", "log"))
                return new Color(rng.Range(0.4f, 0.55f), rng.Range(0.25f, 0.35f), rng.Range(0.1f, 0.2f));

            if (ContainsAny(name, "snow", "ice"))
            {
                float w = rng.Range(0.8f, 0.95f);
                return new Color(w, w, w);
            }

            if (info.health < 200f)
                return new Color(rng.Range(0.3f, 0.45f), rng.Range(0.2f, 0.3f), rng.Range(0.1f, 0.18f));

            float gg = rng.Range(0.3f, 0.5f);
            return new Color(gg, gg, gg);
        }

        private static bool Contains(string source, string value)
            => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool ContainsAny(string source, params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (source.IndexOf(values[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}