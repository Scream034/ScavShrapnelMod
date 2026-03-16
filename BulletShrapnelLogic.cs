using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Осколки от пуль при попадании в металлический блок.
    /// 
    /// Механика:
    /// 1. Пуля попадает в блок (TurretScript.Shoot → Postfix)
    /// 2. Делаем raycast по направлению выстрела
    /// 3. Если попали в металлический блок → 1-3 мелких осколка
    /// 4. Осколки летят от точки попадания + рандомный разброс
    /// 
    /// Ограничения производительности:
    /// - Макс 1 спавн за MinFramesBetweenSpawns кадров
    /// - Макс 3 осколка за спавн
    /// - Только metallic блоки
    /// - Raycast макс RaycastDistance единиц
    /// </summary>
    public static class BulletShrapnelLogic
    {
        //  Константы 

        /// <summary>Дистанция raycast. Совпадает с TurretScript.Shoot (200f).</summary>
        private const float RaycastDistance = 200f;

        /// <summary>Имя слоя Ground в Unity.</summary>
        private const string GroundLayerName = "Ground";

        /// <summary>Минимальный интервал между спавнами (кадры).</summary>
        private const int MinFramesBetweenSpawns = 3;

        /// <summary>Базовая скорость осколков от пуль (м/с).</summary>
        private const float BaseSpeed = 25;

        /// <summary>Количество осколков: min inclusive.</summary>
        private const int MinFragments = 1;

        /// <summary>Количество осколков: max exclusive.</summary>
        private const int MaxFragmentsExclusive = 4;

        /// <summary>Искры: min inclusive.</summary>
        private const int MinSparks = 4;

        /// <summary>Искры: max exclusive.</summary>
        private const int MaxSparksExclusive = 8;

        /// <summary>Множитель масштаба (мельче чем от взрыва).</summary>
        private const float ScaleMultiplier = 0.72f;

        /// <summary>Множитель нагрева (менее горячие).</summary>
        private const float HeatMultiplier = 0.5f;

        /// <summary>Шанс что осколок Hot vs Medium.</summary>
        private const float HotWeightChance = 0.6f;

        /// <summary>Кэшированная маска слоя Ground.</summary>
        private static int _groundMask = -1;
        private static int GroundMask
        {
            get
            {
                if (_groundMask == -1)
                    _groundMask = LayerMask.GetMask(GroundLayerName);
                return _groundMask;
            }
        }

        private static int _lastSpawnFrame;

        /// <summary>
        /// Точка входа. Вызывается из Postfix патча TurretScript.Shoot.
        /// </summary>
        public static void TrySpawnFromBullet(FireInfo info)
        {
            try
            {
                // Throttle
                int frame = Time.frameCount;
                if (frame - _lastSpawnFrame < MinFramesBetweenSpawns) return;

                Vector2 origin = info.pos;
                Vector2 direction = info.dir;

                // Raycast по Ground
                RaycastHit2D hit = Physics2D.Raycast(origin, direction, RaycastDistance, GroundMask);
                if (!hit.collider) return;

                // Проверка металлического блока
                Vector2 blockSamplePos = hit.point + direction * 0.1f;
                Vector2Int blockPos;
                try
                {
                    blockPos = WorldGeneration.world.WorldToBlockPos(blockSamplePos);
                }
                catch (IndexOutOfRangeException) { return; }

                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                if (blockId == 0) return;

                BlockInfo blockInfo = WorldGeneration.world.GetBlockInfo(blockId);
                if (blockInfo == null || !blockInfo.metallic) return;

                _lastSpawnFrame = frame;

                // Детерминированный RNG
                int seed = unchecked(
                    (int)(hit.point.x * 10000f) * 397 ^
                    (int)(hit.point.y * 10000f) ^
                    frame);
                System.Random rng = new System.Random(seed);

                int fragmentCount = rng.Range(MinFragments, MaxFragmentsExclusive);
                for (int i = 0; i < fragmentCount; i++)
                {
                    SpawnBulletFragment(hit.point, hit.normal, rng, i);
                }

                SpawnBulletSparks(hit.point, hit.normal, rng);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ShrapnelMod] BulletShrapnel: {e.Message}");
            }
        }

        /// <summary>
        /// Спавнит один мелкий осколок от попадания пули.
        /// </summary>
        private static void SpawnBulletFragment(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, int index)
        {
            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            ShrapnelWeight weight = rng.NextFloat() < HotWeightChance
                ? ShrapnelWeight.Hot
                : ShrapnelWeight.Medium;

            var shape = (ShrapnelVisuals.TriangleShape)rng.Next(0, 6);
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return;

            ShrapnelFactory.EnsureWoundSprites();

            GameObject obj = new GameObject($"BulletShr_{index}");
            obj.transform.position = hitPoint + rng.InsideUnitCircle() * 0.1f;
            obj.layer = 0;

            float scale = ShrapnelFactory.ScaleForWeight(weight, rng) * ScaleMultiplier;
            obj.transform.localScale = Vector3.one * scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = litMat;

            float heat = ShrapnelFactory.HeatForWeight(weight) * HeatMultiplier;
            sr.color = Color.Lerp(
                ShrapnelVisuals.GetColdColor(ShrapnelProjectile.ShrapnelType.Metal),
                ShrapnelVisuals.GetHotColor(), heat);

            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.mass = weight == ShrapnelWeight.Hot ? 0.01f : 0.04f;
            rb.gravityScale = 0.15f;
            rb.drag = 0.3f;

            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = 0.2f;

            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = ShrapnelProjectile.ShrapnelType.Metal;
            proj.Weight = weight;
            proj.Heat = heat;
            proj.CanBreak = false;
            proj.Damage = rng.Range(1f, 5f);
            proj.BleedAmount = rng.Range(0.3f, 1.5f);

            Vector2 spread = rng.InsideUnitCircle() * 0.6f;
            Vector2 dir = (hitNormal + spread).normalized;
            dir.y = Mathf.Max(dir.y, 0.1f);
            dir.Normalize();

            float speed = Mathf.Min(
                BaseSpeed * rng.Range(0.5f, 1.5f),
                ShrapnelSpawnLogic.GlobalMaxSpeed);

            rb.AddForce(dir * speed * rb.mass, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-200f, 200f));
        }

        /// <summary>
        /// Спавнит визуальные искры при попадании пули в металл.
        /// </summary>
        private static void SpawnBulletSparks(Vector2 hitPoint, Vector2 hitNormal, System.Random rng)
        {
            int sparkCount = rng.Range(MinSparks, MaxSparksExclusive);

            for (int i = 0; i < sparkCount; i++)
            {
                GameObject spark = new GameObject("BulletSpark");
                spark.transform.position = hitPoint;
                spark.transform.localScale = Vector3.one * 0.03f;

                SpriteRenderer ssr = spark.AddComponent<SpriteRenderer>();
                ssr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
                ssr.sharedMaterial = ShrapnelVisuals.LitMaterial;
                ssr.color = new Color(1f, 0.9f, 0.4f);
                ssr.sortingOrder = 11;

                ShrapnelFactory.MPB.Clear();
                ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId, new Color(3f, 2f, 0.5f));
                ssr.SetPropertyBlock(ShrapnelFactory.MPB);

                Vector2 sparkDir = (hitNormal + rng.InsideUnitCircle() * 0.8f).normalized;
                sparkDir.y = Mathf.Abs(sparkDir.y) * 0.5f + 0.3f;
                float sparkSpeed = rng.Range(2f, 6f);

                var visual = spark.AddComponent<VisualShrapnel>();
                visual.Initialize(sparkDir, sparkSpeed, rng.Range(0.05f, 0.15f));
            }
        }
    }
}