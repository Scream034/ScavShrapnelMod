using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Визуальные частицы грунта от ударной волны.
    /// </summary>
    public static class GroundDebrisLogic
    {
        //  SCAN

        /// <summary>Множитель радиуса сканирования.</summary>
        private const float RangeMultiplier = 2.0f;

        /// <summary>Шаг по X. Каждый блок = максимум частиц.</summary>
        private const int ScanStepX = 1;

        /// <summary>Блоков вниз от эпицентра.</summary>
        private const int ScanDownMax = 15;

        /// <summary>Блоков вверх от эпицентра.</summary>
        private const int ScanUpMax = 5;

        //  CHUNKS — крупные комки, НИЗКО летящие

        /// <summary>Комков на каждую поверхность.</summary>
        private const int ChunksPerSurface = 6;

        private const float ChunkScaleMin = 0.12f;
        private const float ChunkScaleMax = 0.26f;

        /// <summary>Скорость ВВЕРХ: низкая! Прижаты к земле.</summary>
        private const float ChunkSpeedUpMin = 1.75f;
        private const float ChunkSpeedUpMax = 4f;

        /// <summary>Скорость В СТОРОНЫ: сильная! Разлетаются горизонтально.</summary>
        private const float ChunkSpeedSideMin = 0.25f;
        private const float ChunkSpeedSideMax = 1.5f;

        /// <summary>Время жизни: ДОЛГОЕ — висят у земли.</summary>
        private const float ChunkLifeMin = 3f;
        private const float ChunkLifeMax = 10f;

        /// <summary>Гравитация: очень слабая — медленно оседают.</summary>
        private const float ChunkGravity = 0.3f;

        private const float ChunkAlpha = 0.85f;

        //  DUST — пылевые облака, ОЧЕНЬ долго висят

        private const int DustPerSurface = 13;

        private const float DustScaleMin = 0.025f;
        private const float DustScaleMax = 0.18f;

        /// <summary>Пыль поднимается ЧУТЬ-ЧУТЬ.</summary>
        private const float DustSpeedUpMin = 0.5f;
        private const float DustSpeedUpMax = 2f;

        private const float DustSpeedSideMin = 0.6f;
        private const float DustSpeedSideMax = 1.8f;

        /// <summary>Пыль висит ОЧЕНЬ долго.</summary>
        private const float DustLifeMin = 5f;
        private const float DustLifeMax = 18f;

        /// <summary>Почти нулевая гравитация — "парит".</summary>
        private const float DustGravity = 0.15f;

        private const float DustAlpha = 0.5f;

        //  STREAKS — быстрые полосы от центра

        private const int StreaksPerSurface = 7;

        private const float StreakScaleMin = 0.06f;
        private const float StreakScaleMax = 0.13f;

        private const float StreakSpeedMin = 12f;
        private const float StreakSpeedMax = 24f;

        /// <summary>Полосы исчезают быстро — "выстрел" грязи.</summary>
        private const float StreakLifeMin = 0.6f;
        private const float StreakLifeMax = 1.5f;

        private const float StreakGravity = 0.8f;

        private const float StreakAlpha = 0.8f;

        //  PUBLIC API

        /// <summary>
        /// Спавнит ground debris. Горизонтальная развёртка по блокам,
        /// поиск поверхности, три типа частиц с разной гравитацией.
        /// </summary>
        public static void SpawnFromExplosion(Vector2 epicenter, float range, System.Random rng)
        {
            Material unlitMat = ShrapnelVisuals.UnlitMaterial;
            if (unlitMat == null) return;

            int totalSpawned = 0;

            try
            {
                Vector2Int epicenterBlock = WorldGeneration.world.WorldToBlockPos(epicenter);
                int scanRange = Mathf.CeilToInt(range * RangeMultiplier);

                for (int dx = -scanRange; dx <= scanRange; dx += ScanStepX)
                {
                    int blockX = epicenterBlock.x + dx;

                    if (!FindSurface(blockX, epicenterBlock.y, out Vector2Int surfaceBlockPos, out BlockInfo surfaceInfo))
                        continue;

                    // Мировая позиция: верх блока-поверхности
                    Vector2 worldPos = WorldGeneration.world.BlockToWorldPos(
                        new Vector2Int(surfaceBlockPos.x, surfaceBlockPos.y + 1));

                    Color blockColor = GetBlockColor(surfaceInfo, rng);

                    float dist = Mathf.Abs(dx);
                    float intensity = Mathf.Clamp01(1f - dist / (scanRange * 1.1f));
                    intensity = Mathf.Max(intensity, 0.2f);

                    float radialX = (dx == 0) ? 0f : Mathf.Sign(dx);

                    //  Комки 
                    int chunks = Mathf.Max(1, Mathf.RoundToInt(ChunksPerSurface * intensity));
                    for (int j = 0; j < chunks; j++)
                        SpawnChunk(worldPos, radialX, intensity, blockColor, unlitMat, rng);

                    //  Пыль 
                    int dust = Mathf.Max(1, Mathf.RoundToInt(DustPerSurface * intensity));
                    for (int j = 0; j < dust; j++)
                        SpawnDust(worldPos, radialX, intensity, blockColor, unlitMat, rng);

                    //  Полосы (только рядом с центром) 
                    if (intensity > 0.4f)
                    {
                        int streaks = Mathf.Max(1, Mathf.RoundToInt(StreaksPerSurface * intensity));
                        for (int j = 0; j < streaks; j++)
                            SpawnStreak(worldPos, radialX, intensity, blockColor, unlitMat, rng);
                    }

                    totalSpawned++;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ShrapnelMod] GroundDebris: {e.Message}");
            }
        }

        //  SURFACE DETECTION

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

        //  COLOR

        private static Color GetBlockColor(BlockInfo info, System.Random rng)
        {
            if (info.metallic)
            {
                float gray = rng.Range(0.25f, 0.4f);
                return new Color(gray, gray, gray);
            }

            string name = info.name != null ? info.name.ToLower() : "";

            if (name.Contains("stone") || name.Contains("rock") ||
                name.Contains("granite") || name.Contains("slate"))
            {
                float g = rng.Range(0.35f, 0.55f);
                return new Color(g, g * 0.95f, g * 0.9f);
            }

            if (name.Contains("sand"))
                return new Color(rng.Range(0.7f, 0.85f), rng.Range(0.6f, 0.75f), rng.Range(0.3f, 0.45f));

            if (name.Contains("clay"))
                return new Color(rng.Range(0.55f, 0.7f), rng.Range(0.3f, 0.45f), rng.Range(0.2f, 0.3f));

            if (name.Contains("wood") || name.Contains("plank") || name.Contains("log"))
                return new Color(rng.Range(0.4f, 0.55f), rng.Range(0.25f, 0.35f), rng.Range(0.1f, 0.2f));

            if (name.Contains("snow") || name.Contains("ice"))
            {
                float w = rng.Range(0.8f, 0.95f);
                return new Color(w, w, w);
            }

            if (info.health < 200f)
                return new Color(rng.Range(0.3f, 0.45f), rng.Range(0.2f, 0.3f), rng.Range(0.1f, 0.18f));

            float gg = rng.Range(0.3f, 0.5f);
            return new Color(gg, gg, gg);
        }

        //  PARTICLES

        /// <summary>
        /// Крупный комок — летит НИЗКО и В СТОРОНЫ.
        /// Gravity = 0.4 → медленно оседает.
        /// </summary>
        private static void SpawnChunk(Vector2 worldPos, float radialX, float intensity,
            Color blockColor, Material mat, System.Random rng)
        {
            GameObject obj = new GameObject("GndChunk");
            obj.transform.position = worldPos + rng.InsideUnitCircle() * 0.3f;

            float scale = rng.Range(ChunkScaleMin, ChunkScaleMax) * (0.6f + intensity * 0.8f);
            obj.transform.localScale = Vector3.one * scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            sr.sharedMaterial = mat;
            sr.sortingOrder = 12;

            Color c = new Color(blockColor.r, blockColor.g, blockColor.b, ChunkAlpha);

            // НИЗКО вверх, СИЛЬНО в стороны
            float speedUp = rng.Range(ChunkSpeedUpMin, ChunkSpeedUpMax) * intensity;
            float speedSide = radialX * rng.Range(ChunkSpeedSideMin, ChunkSpeedSideMax)
                            + rng.Range(-1.5f, 1.5f);

            AshParticle ash = obj.AddComponent<AshParticle>();
            ash.Initialize(new Vector2(speedSide, speedUp),
                rng.Range(ChunkLifeMin, ChunkLifeMax), c, rng.Range(0f, 6.28f),
                ChunkGravity);
        }

        /// <summary>
        /// Пыль — едва поднимается, ОЧЕНЬ долго висит.
        /// Gravity = 0.15 → почти "парит" в воздухе.
        /// </summary>
        private static void SpawnDust(Vector2 worldPos, float radialX, float intensity,
            Color blockColor, Material mat, System.Random rng)
        {
            GameObject obj = new GameObject("GndDust");
            obj.transform.position = worldPos + rng.InsideUnitCircle() * 0.7f;

            float scale = rng.Range(DustScaleMin, DustScaleMax) * (0.7f + intensity * 0.5f);
            obj.transform.localScale = Vector3.one * scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 11;

            Color dustColor = Color.Lerp(blockColor, new Color(0.65f, 0.6f, 0.55f), 0.4f);
            dustColor.a = DustAlpha;

            float speedUp = rng.Range(DustSpeedUpMin, DustSpeedUpMax) * intensity;
            float speedSide = radialX * rng.Range(DustSpeedSideMin, DustSpeedSideMax)
                            + rng.Range(-1f, 1f);

            AshParticle ash = obj.AddComponent<AshParticle>();
            ash.Initialize(new Vector2(speedSide, speedUp),
                rng.Range(DustLifeMin, DustLifeMax), dustColor, rng.Range(0f, 6.28f),
                DustGravity);
        }

        /// <summary>
        /// Быстрая полоса — "выстрел" грязи от центра.
        /// </summary>
        private static void SpawnStreak(Vector2 worldPos, float radialX, float intensity,
            Color blockColor, Material mat, System.Random rng)
        {
            GameObject obj = new GameObject("GndStreak");
            obj.transform.position = worldPos + rng.InsideUnitCircle() * 0.15f;

            float scale = rng.Range(StreakScaleMin, StreakScaleMax);
            obj.transform.localScale = Vector3.one * scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
            sr.sharedMaterial = mat;
            sr.sortingOrder = 12;

            Color c = new Color(blockColor.r * 0.8f, blockColor.g * 0.8f, blockColor.b * 0.8f, StreakAlpha);

            float speed = rng.Range(StreakSpeedMin, StreakSpeedMax) * intensity;
            Vector2 dir = new Vector2(
                radialX * rng.Range(0.8f, 2f) + rng.Range(-0.3f, 0.3f),
                rng.Range(0.3f, 1f)); // Почти горизонтально!
            dir.Normalize();

            AshParticle ash = obj.AddComponent<AshParticle>();
            ash.Initialize(dir * speed, rng.Range(StreakLifeMin, StreakLifeMax), c,
                rng.Range(0f, 6.28f), StreakGravity);
        }
    }
}