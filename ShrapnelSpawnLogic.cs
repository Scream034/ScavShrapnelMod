using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Единственная точка входа для спавна осколков при взрывах.
    /// 
    /// Параметры классификации, скорости и количества читаются из
    /// <see cref="ShrapnelConfig"/>.
    /// 
    /// Детерминированность: System.Random с координатным сидом + версия мода.
    /// </summary>
    public static class ShrapnelSpawnLogic
    {
        private static Vector2 _lastSpawnPos;
        private static int _lastSpawnFrame;

        /// <summary>
        /// Максимальная скорость осколка. Читается из конфига.
        /// Свойство для обратной совместимости (используется в ShrapnelFactory, VisualShrapnel).
        /// </summary>
        public static float GlobalMaxSpeed => ShrapnelConfig.GlobalMaxSpeed.Value;

        /// <summary>
        /// Генерирует детерминированный seed из позиции взрыва и версии мода.
        /// Версия мода в seed гарантирует что при обновлении мода
        /// паттерн осколков изменится (не будет "заученных" взрывов).
        /// </summary>
        private static int MakeSeed(Vector2 position)
        {
            int versionHash = Plugin.Version.GetHashCode();
            return unchecked(
                (int)(position.x * 1000f) * 397 ^
                (int)(position.y * 1000f) ^
                versionHash);
        }

        /// <summary>
        /// Throttle: не более одного спавна в кадре на позицию.
        /// Дистанция берётся из конфига.
        /// </summary>
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

        /// <summary>Обёртка для Transpiler. Спавн + оригинальный вызов.</summary>
        public static void CustomCreateExplosion(ExplosionParams param)
        {
            TrySpawnFromExplosion(param);
            WorldGeneration.CreateExplosion(param);
        }

        /// <summary>
        /// Главная точка спавна.
        /// primary → secondary → visual → ash → ground debris.
        /// Все объекты регистрируются в <see cref="DebrisTracker"/>.
        /// </summary>
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

                // Применяем глобальный множитель
                count = Mathf.Max(1, Mathf.RoundToInt(count * spawnMult));
                visualCount = Mathf.Max(1, Mathf.RoundToInt(visualCount * spawnMult));

                // Температурный модификатор для визуальных
                float ambientTemp = GetAmbientTemperature();
                if (ambientTemp < 5f)
                    visualCount = (int)(visualCount * 0.7f);
                else if (ambientTemp > 25f)
                    visualCount = (int)(visualCount * 1.3f);

                int secondarySpawned = SpawnSecondaryFromBlocks(param, rng, type, speed);

                for (int i = 0; i < count; i++)
                    SpawnSingle(param.position, type, speed, rng, i, count);

                for (int i = 0; i < visualCount; i++)
                    ShrapnelFactory.SpawnVisual(param.position, speed, type, rng);

                int ashCount = Mathf.Max(1, Mathf.RoundToInt(GetAshCount(type, rng) * spawnMult));
                ShrapnelFactory.SpawnAshCloud(param.position, ashCount, type, rng);

                GroundDebrisLogic.SpawnFromExplosion(param.position, param.range, rng);

                // Conditional logging — только если включено в конфиге
                if (ShrapnelConfig.DebugLogging.Value)
                {
                    Debug.Log($"[ShrapnelMod] V:{visualCount} P:{count} S:{secondarySpawned} A:{ashCount}" +
                              $" at {param.position} (Seed:{seed} Temp:{ambientTemp:F0}" +
                              $" Debris:{DebrisTracker.Count})");
                }
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

        /// <summary>Количество пепла по типу. Базовые значения до множителя.</summary>
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

                    string blockName = info.name ?? string.Empty;

                    var secType = info.metallic
                        ? ShrapnelProjectile.ShrapnelType.HeavyMetal
                        : (info.health < 100f &&
                           blockName.IndexOf("stone", System.StringComparison.OrdinalIgnoreCase) < 0
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
        /// Классификация взрыва по параметрам.
        /// 
        /// Все пороговые значения (range, structuralDamage, velocity)
        /// и выходные параметры (count, speed, visualCount) читаются из
        /// <see cref="ShrapnelConfig"/>.
        /// 
        /// Если параметры взрыва не совпадают ни с динамитом, ни с турелью —
        /// используется fallback "мина".
        /// </summary>
        private static void ClassifyExplosion(ExplosionParams p, System.Random rng,
            out ShrapnelProjectile.ShrapnelType type, out int count,
            out float speed, out int visualCount)
        {
            float eps = ShrapnelConfig.ClassifyEpsilon.Value;
            float speedBoost = ShrapnelConfig.GlobalSpeedBoost.Value;

            // Динамит
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
            // Турель
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
            // Мина (fallback)
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