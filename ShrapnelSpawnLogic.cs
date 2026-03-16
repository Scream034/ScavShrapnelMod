using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Единственная точка входа для спавна осколков при взрывах.
    /// Полностью детерминированно: System.Random с координатным сидом + версия мода.
    /// </summary>
    public static class ShrapnelSpawnLogic
    {
        private static Vector2 _lastSpawnPos;
        private static int _lastSpawnFrame;

        /// <summary>Максимальная скорость любого осколка (м/с).</summary>
        public const float GlobalMaxSpeed = 128f;

        private const float MinDistanceBetweenSpawns = 1.5f;

        /// <summary>Множитель скорости для всех осколков.</summary>
        private const float GlobalSpeedBoost = 1.5f;

        /// <summary>
        /// Генерирует детерминированный seed из позиции взрыва и версии мода.
        /// Версия мода в seed гарантирует что при обновлении мода
        /// паттерн осколков изменится (не будет "заученных" взрывов).
        /// </summary>
        private static int MakeSeed(Vector2 position)
        {
            // Хеш версии: "0.5.2" → стабильное число
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
                Vector2.Distance(pos, _lastSpawnPos) < MinDistanceBetweenSpawns)
                return false;

            _lastSpawnPos = pos;
            _lastSpawnFrame = frame;
            return true;
        }

        /// <summary>Обёртка для Transpiler.</summary>
        public static void CustomCreateExplosion(ExplosionParams param)
        {
            TrySpawnFromExplosion(param);
            WorldGeneration.CreateExplosion(param);
        }

        /// <summary>
        /// Главная точка спавна.
        /// primary → secondary → visual → ash → ground debris.
        /// </summary>
        public static void TrySpawnFromExplosion(ExplosionParams param)
        {
            try
            {
                if (!TryRegisterSpawn(param.position)) return;

                ExplosionLogger.Record(param);

                int seed = MakeSeed(param.position);
                System.Random rng = new System.Random(seed);

                ClassifyExplosion(param, rng, out var type, out int count,
                    out float speed, out int visualCount);

                // Температурный модификатор
                float ambientTemp = GetAmbientTemperature();
                if (ambientTemp < 5f)
                    visualCount = (int)(visualCount * 0.7f);
                else if (ambientTemp > 25f)
                    visualCount = (int)(visualCount * 1.3f);

                //  Secondary 
                int secondarySpawned = SpawnSecondaryFromBlocks(param, rng, type, speed);

                //  Primary 
                for (int i = 0; i < count; i++)
                    SpawnSingle(param.position, type, speed, rng, i, count);

                //  Visual (+50% от классификации) 
                for (int i = 0; i < visualCount; i++)
                    ShrapnelFactory.SpawnVisual(param.position, speed, type, rng);

                //  Ash 
                int ashCount = GetAshCount(type, rng);
                ShrapnelFactory.SpawnAshCloud(param.position, ashCount, type, rng);

                //  Ground Debris 
                GroundDebrisLogic.SpawnFromExplosion(param.position, param.range, rng);

                Debug.Log($"[ShrapnelMod] V:{visualCount} P:{count} S:{secondarySpawned} A:{ashCount}" +
                          $" at {param.position} (Seed:{seed} Temp:{ambientTemp:F0})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelMod] Logic Error: {e.Message}");
            }
        }

        private static float GetAmbientTemperature()
        {
            try { return WorldGeneration.world.ambientTemperature; }
            catch { return 20f; }
        }

        /// <summary>Пепел: +20%.</summary>
        private static int GetAshCount(ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            switch (type)
            {
                case ShrapnelProjectile.ShrapnelType.Stone:      return rng.Range(30, 50);
                case ShrapnelProjectile.ShrapnelType.HeavyMetal: return rng.Range(10, 20);
                default:                                          return rng.Range(18, 32);
            }
        }

        private static int SpawnSecondaryFromBlocks(ExplosionParams param, System.Random rng,
            ShrapnelProjectile.ShrapnelType primaryType, float primarySpeed)
        {
            int spawned = 0;
            const int maxSamples = 15;
            const int maxSecondary = 15;

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

                    var secType = info.metallic
                        ? ShrapnelProjectile.ShrapnelType.HeavyMetal
                        : (info.health < 100f && !info.name.ToLower().Contains("stone")
                            ? ShrapnelProjectile.ShrapnelType.Wood
                            : ShrapnelProjectile.ShrapnelType.Stone);

                    SpawnSingle(samplePos, secType, primarySpeed * 0.4f, rng, i, maxSamples);
                    spawned++;
                }
                catch (IndexOutOfRangeException) { }
            }

            return spawned;
        }

        private static void SpawnSingle(Vector2 epicenter, ShrapnelProjectile.ShrapnelType type,
            float baseSpeed, System.Random rng, int index, int total)
        {
            try
            {
                ShrapnelWeight weight = RollWeight(type, index, total, rng);
                ShrapnelFactory.Spawn(epicenter, baseSpeed, type, weight, index, rng);
            }
            catch { }
        }

        /// <summary>
        /// Классификация взрыва.
        /// 
        /// v0.5.2:
        /// - Все visual +50%
        /// - Мина: 25–37 primary, 75–121 visual
        /// - Динамит: 150–226 visual (МАКСИМУМ мусора)
        /// - Турель: 45–76 visual
        /// </summary>
        private static void ClassifyExplosion(ExplosionParams p, System.Random rng,
            out ShrapnelProjectile.ShrapnelType type, out int count,
            out float speed, out int visualCount)
        {
            const float eps = 0.5f;

            // Динамит
            if (Mathf.Abs(p.range - 18f) < eps && Mathf.Abs(p.structuralDamage - 2000f) < eps)
            {
                type = ShrapnelProjectile.ShrapnelType.Stone;
                count = rng.Range(12, 21);
                speed = 35f * GlobalSpeedBoost;
                visualCount = rng.Range(150, 226); // +50%
            }
            // Турель
            else if (Mathf.Abs(p.range - 9f) < eps && Mathf.Abs(p.velocity - 15f) < eps)
            {
                type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                count = rng.Range(6, 13);
                speed = 40f * GlobalSpeedBoost;
                visualCount = rng.Range(45, 76); // +50%
            }
            // Мина
            else
            {
                type = ShrapnelProjectile.ShrapnelType.Metal;
                count = rng.Range(25, 37);
                speed = 45f * GlobalSpeedBoost;
                visualCount = rng.Range(75, 121); // +50%
            }
        }

        private static ShrapnelWeight RollWeight(ShrapnelProjectile.ShrapnelType type,
            int index, int total, System.Random rng)
        {
            ShrapnelWeight weight;

            if (index < Mathf.CeilToInt(total * 0.35f))
            {
                weight = ShrapnelWeight.Hot;
            }
            else
            {
                float roll = rng.NextFloat();
                if (roll < 0.15f)       weight = ShrapnelWeight.Hot;
                else if (roll < 0.45f)  weight = ShrapnelWeight.Medium;
                else if (roll < 0.85f)  weight = ShrapnelWeight.Heavy;
                else                    weight = ShrapnelWeight.Massive;
            }

            if (type == ShrapnelProjectile.ShrapnelType.HeavyMetal && weight == ShrapnelWeight.Hot)
                weight = ShrapnelWeight.Medium;
            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Heavy)
                weight = ShrapnelWeight.Medium;
            if (type == ShrapnelProjectile.ShrapnelType.Stone && weight == ShrapnelWeight.Massive)
                weight = ShrapnelWeight.Heavy;

            return weight;
        }
    }
}