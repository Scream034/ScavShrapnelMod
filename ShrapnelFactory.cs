using UnityEngine;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Фабрика осколков: создаёт GameObject'ы для primary, visual, break-фрагментов и пепла.
    /// 
    /// Все методы принимают System.Random для детерминированности в мультиплеере.
    /// Использует пулинг материалов и спрайтов через ShrapnelVisuals.
    /// </summary>
    public static class ShrapnelFactory
    {
        //  Физический материал (общий для всех осколков) 

        private static PhysicsMaterial2D _physMat;
        private static PhysicsMaterial2D PhysMat =>
            _physMat ?? (_physMat = new PhysicsMaterial2D("ShrapnelMat")
            {
                bounciness = 0.15f,
                friction = 0.6f
            });

        //  Кэш ресурсов ран 

        private static bool _woundCached;
        private static Sprite _woundSprite;
        private static Sprite _woundPanel;

        //  Throttle для DamageBlock 

        private static int _dmgFrame;
        private static int _dmgCount;
        private const int MaxDmgPerFrame = 5;

        //  MaterialPropertyBlock (переиспользуется) 

        private static MaterialPropertyBlock _mpb;
        internal static MaterialPropertyBlock MPB => _mpb ?? (_mpb = new MaterialPropertyBlock());

        private static int _emissionId = -1;
        internal static int EmissionColorId =>
            _emissionId == -1
                ? (_emissionId = Shader.PropertyToID("_EmissionColor"))
                : _emissionId;

        //  WOUND SPRITES

        /// <summary>
        /// Загружает спрайты ран из ресурсов игры (однократно).
        /// </summary>
        internal static void EnsureWoundSprites()
        {
            if (_woundCached) return;
            _woundCached = true;
            try
            {
                _woundSprite = Resources.Load<Sprite>("Special/footglass");
                _woundPanel = Resources.Load<Sprite>("Special/footglasshealthpanel");
            }
            catch { }
        }

        internal static Sprite WoundSprite => _woundSprite;
        internal static Sprite WoundPanel => _woundPanel;

        //  THROTTLE

        /// <summary>
        /// Ограничивает количество DamageBlock-вызовов за кадр.
        /// Предотвращает лаги при массовом попадании осколков в стены.
        /// </summary>
        internal static bool TryDamageSlot()
        {
            int f = Time.frameCount;
            if (f != _dmgFrame) { _dmgFrame = f; _dmgCount = 0; }
            if (_dmgCount >= MaxDmgPerFrame) return false;
            _dmgCount++;
            return true;
        }

        //  PRIMARY SPAWN — реальный осколок с физикой и уроном

        /// <summary>
        /// Создаёт полноценный осколок с Rigidbody2D, коллайдером и ShrapnelProjectile.
        /// 
        /// Параметры:
        /// - epicenter: точка спавна (центр взрыва или позиция разрушенного блока)
        /// - baseSpeed: базовая скорость (модифицируется весом)
        /// - type: тип материала (влияет на урон и цвет)
        /// - weight: весовая категория (влияет на физику и урон)
        /// - index: порядковый номер (для имени GameObject)
        /// - rng: детерминированный генератор
        /// </summary>
        public static void Spawn(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng)
        {
            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            var shape = (ShrapnelVisuals.TriangleShape)rng.Next(0, 6);
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return;

            EnsureWoundSprites();

            //  GameObject 
            GameObject obj = new GameObject($"Shr_{type}_{index}");
            obj.transform.position = epicenter + rng.InsideUnitCircle() * 0.3f;
            obj.layer = 0;

            float scale = ScaleForWeight(weight, rng);
            obj.transform.localScale = Vector3.one * scale;

            //  SpriteRenderer 
            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = litMat;

            float heat = HeatForWeight(weight);
            sr.color = Color.Lerp(ShrapnelVisuals.GetColdColor(type), ShrapnelVisuals.GetHotColor(), heat);

            // Emission (уменьшен на 35%: было heat * 2f)
            if (heat > 0.3f)
            {
                MPB.Clear();
                MPB.SetColor(EmissionColorId, ShrapnelVisuals.GetHotColor() * heat * 1.3f);
                sr.SetPropertyBlock(MPB);
            }

            //  Rigidbody2D 
            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.sharedMaterial = PhysMat;

            // ФИКС: Hot теперь падают нормально (gravityScale 0.3 вместо 0.05)
            switch (weight)
            {
                case ShrapnelWeight.Hot:     rb.mass = 0.02f; rb.gravityScale = 0.3f;  break;
                case ShrapnelWeight.Medium:  rb.mass = 0.08f; rb.gravityScale = 0.15f; break;
                case ShrapnelWeight.Heavy:   rb.mass = 0.25f; rb.gravityScale = 0.35f; break;
                case ShrapnelWeight.Massive: rb.mass = 0.8f;  rb.gravityScale = 0.5f;  break;
            }

            // ФИКС: Hot получает повышенный drag чтобы не "парить"
            rb.drag = weight == ShrapnelWeight.Hot ? 0.4f 
                    : (weight == ShrapnelWeight.Massive ? 0.1f : 0.2f);

            //  Collider (с micro-delay для предотвращения self-collision) 
            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = weight == ShrapnelWeight.Massive ? 0.5f : 0.3f;
            col.sharedMaterial = PhysMat;
            col.enabled = false; // Включится через ShrapnelProjectile._physicsDelay

            //  ShrapnelProjectile 
            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = type;
            proj.Weight = weight;
            proj.Heat = heat;
            proj.CanBreak = weight != ShrapnelWeight.Hot;
            SetDamage(proj, type, weight, rng);

            //  Запуск 
            LaunchFragment(rb, weight, baseSpeed, type, rng);
            TryAddTrail(obj, proj, weight, rng);
        }

        //  VISUAL SPAWN — fake-осколок без физики (только трансформ)

        /// <summary>
        /// Создаёт визуальный осколок без Rigidbody/коллайдера.
        /// 
        /// Направление зависит от типа:
        /// - Metal (мина): конус 240° вверх (осколки не уходят в землю)
        /// - Остальные: 360°
        /// 
        /// Скорость клампится к GlobalMaxSpeed.
        /// </summary>
        public static void SpawnVisual(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            GameObject obj = new GameObject("VisualShr");
            obj.transform.position = epicenter + rng.InsideUnitCircle() * 0.2f;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
            sr.sharedMaterial = ShrapnelVisuals.LitMaterial;
            sr.sortingOrder = 9;

            Color hotCol = ShrapnelVisuals.GetHotColor();
            sr.color = hotCol;

            // Emission (уменьшен)
            MPB.Clear();
            MPB.SetColor(EmissionColorId, hotCol * rng.Range(1f, 2f));
            sr.SetPropertyBlock(MPB);

            float scale = rng.Range(0.04f, 0.1f);
            obj.transform.localScale = Vector3.one * scale;

            Vector2 dir = GetDirectionForType(type, rng);

            float rawSpeed = baseSpeed * rng.Range(2f, 3.5f);
            float speed = Mathf.Min(rawSpeed, ShrapnelSpawnLogic.GlobalMaxSpeed);

            float lifetime = rng.Range(0.15f, 0.3f);

            var visual = obj.AddComponent<VisualShrapnel>();
            visual.Initialize(dir, speed, lifetime);
        }

        /// <summary>
        /// Определяет направление осколка в зависимости от типа взрыва.
        /// 
        /// Metal (мина): конус 240° вверх — от -30° до 210° (центр = 90° = вверх).
        /// Нижние 120° (в землю) исключены — реалистично для наземной мины.
        /// 
        /// Остальные типы: полные 360°.
        /// </summary>
        private static Vector2 GetDirectionForType(ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            if (type == ShrapnelProjectile.ShrapnelType.Metal)
            {
                // Конус 240° вверх: от -30° до 210°
                float angleDeg = rng.Range(-30f, 210f);
                float angleRad = angleDeg * Mathf.Deg2Rad;
                return new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad));
            }

            return rng.InsideUnitCircleNormalized();
        }

        //  ASH CLOUD — облако пепла/углей после взрыва

        /// <summary>
        /// Спавнит облако пепла/тлеющих углей после взрыва.
        /// 
        /// Визуальный эффект без физики и урона.
        /// Частицы медленно оседают, покачиваются и затухают.
        /// 
        /// Температура влияет на визуал:
        /// - cold (&lt;5°C): больше "пара" (светлые частицы)
        /// - hot (&gt;25°C): больше тлеющих углей (оранжевые частицы)
        /// </summary>
        public static void SpawnAshCloud(Vector2 epicenter, int count,
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            Material unlitMat = ShrapnelVisuals.UnlitMaterial;
            if (unlitMat == null) return;

            // Температурный модификатор
            float ambientTemp = 20f;
            try { ambientTemp = WorldGeneration.world.ambientTemperature; } catch { }

            bool isCold = ambientTemp < 5f;
            bool isHot = ambientTemp > 25f;

            for (int i = 0; i < count; i++)
            {
                GameObject obj = new GameObject("Ash");
                obj.transform.position = epicenter + rng.InsideUnitCircle() * 0.5f;

                float scale = rng.Range(0.02f, 0.06f);
                obj.transform.localScale = Vector3.one * scale;

                SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
                sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
                sr.sharedMaterial = unlitMat;
                sr.sortingOrder = 8;

                // Цвет зависит от температуры
                Color ashColor = GetAshColor(isCold, isHot, rng);

                Vector2 velocity = new Vector2(
                    rng.Range(-1.5f, 1.5f),
                    rng.Range(1f, 4f));

                float lifetime = rng.Range(1.5f, 4f);
                float wobblePhase = rng.Range(0f, Mathf.PI * 2f);

                AshParticle ash = obj.AddComponent<AshParticle>();
                ash.Initialize(velocity, lifetime, ashColor, wobblePhase);
            }
        }

        /// <summary>
        /// Определяет цвет частицы пепла в зависимости от температуры.
        /// </summary>
        private static Color GetAshColor(bool isCold, bool isHot, System.Random rng)
        {
            if (isCold && rng.NextFloat() < 0.4f)
            {
                // Холод → светлый пар/дым
                float gray = rng.Range(0.7f, 0.9f);
                return new Color(gray, gray, gray, 0.6f);
            }
            
            if (isHot || rng.NextFloat() < 0.3f)
            {
                // Жарко или шанс → тлеющий уголёк
                return new Color(
                    rng.Range(0.8f, 1f),
                    rng.Range(0.2f, 0.4f),
                    rng.Range(0f, 0.1f),
                    0.7f);
            }

            // Обычный тёмный пепел
            float g = rng.Range(0.15f, 0.35f);
            return new Color(g, g, g, 0.5f);
        }

        //  BREAK FRAGMENTS — осколки от разрушения осколка

        /// <summary>
        /// Спавнит дочерние фрагменты при разрушении тяжёлого осколка.
        /// При отсутствии rng — создаёт локальный детерминированный.
        /// </summary>
        internal static void SpawnBreakFragments(Vector2 position, Vector2 impactNormal,
            float parentScale, ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight parentWeight, float impactSpeed, System.Random rng = null)
        {
            // Детерминированный fallback для вызовов из OnCollision
            if (rng == null)
            {
                int seed = unchecked(
                    (int)(position.x * 10000f) * 397 ^
                    (int)(position.y * 10000f) ^
                    (int)(impactSpeed * 100f));
                rng = new System.Random(seed);
            }

            int count = rng.Range(2, 4);

            ShrapnelWeight childWeight;
            switch (parentWeight)
            {
                case ShrapnelWeight.Massive: childWeight = ShrapnelWeight.Heavy;  break;
                case ShrapnelWeight.Heavy:   childWeight = ShrapnelWeight.Medium; break;
                default:                     childWeight = ShrapnelWeight.Hot;    break;
            }

            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            for (int i = 0; i < count; i++)
            {
                GameObject obj = new GameObject($"ShrBrk_{i}");
                float childScale = Mathf.Max(parentScale * rng.Range(0.4f, 0.6f), 0.05f);

                obj.transform.position = position + rng.InsideUnitCircle() * 0.15f;
                obj.transform.localScale = Vector3.one * childScale;
                obj.layer = 0;

                SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
                sr.sprite = ShrapnelVisuals.GetTriangleSprite(
                    (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
                sr.sortingOrder = 10;
                sr.sharedMaterial = litMat;
                sr.color = ShrapnelVisuals.GetColdColor(type);

                Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
                rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                rb.sharedMaterial = PhysMat;
                rb.drag = 0.3f;

                switch (childWeight)
                {
                    case ShrapnelWeight.Hot:    rb.mass = 0.01f; rb.gravityScale = 0.3f;  break;
                    case ShrapnelWeight.Medium: rb.mass = 0.05f; rb.gravityScale = 0.2f;  break;
                    case ShrapnelWeight.Heavy:  rb.mass = 0.15f; rb.gravityScale = 0.4f;  break;
                }

                CircleCollider2D colChild = obj.AddComponent<CircleCollider2D>();
                colChild.radius = 0.25f;
                colChild.sharedMaterial = PhysMat;

                ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
                proj.Type = type;
                proj.Weight = childWeight;
                proj.Heat = 0.1f;
                proj.CanBreak = false;
                proj.Damage = rng.Range(2f, 6f);
                proj.BleedAmount = rng.Range(0.3f, 1.5f);

                Vector2 spread = rng.InsideUnitCircle() * 0.8f;
                Vector2 dir = (impactNormal + spread).normalized;
                dir.y = Mathf.Abs(dir.y) * 0.5f + 0.1f;

                float childSpeed = Mathf.Min(
                    impactSpeed * rng.Range(0.2f, 0.5f),
                    ShrapnelSpawnLogic.GlobalMaxSpeed);

                rb.AddForce(dir * childSpeed * rb.mass * 5f, ForceMode2D.Impulse);
                rb.AddTorque(rng.Range(-300f, 300f));
            }
        }

        //  UTILITIES

        /// <summary>
        /// Масштаб спрайта по весу. Massive крупнее — видны на полу.
        /// </summary>
        internal static float ScaleForWeight(ShrapnelWeight w, System.Random rng)
        {
            switch (w)
            {
                case ShrapnelWeight.Hot:     return rng.Range(0.08f, 0.14f);
                case ShrapnelWeight.Medium:  return rng.Range(0.14f, 0.25f);
                case ShrapnelWeight.Heavy:   return rng.Range(0.22f, 0.45f);
                case ShrapnelWeight.Massive: return rng.Range(0.5f, 0.8f);
                default:                     return 0.18f;
            }
        }

        /// <summary>
        /// Начальный нагрев: Hot = раскалённый, Massive = почти холодный.
        /// </summary>
        internal static float HeatForWeight(ShrapnelWeight w)
        {
            switch (w)
            {
                case ShrapnelWeight.Hot:     return 1.0f;
                case ShrapnelWeight.Medium:  return 0.4f;
                case ShrapnelWeight.Heavy:   return 0.15f;
                case ShrapnelWeight.Massive: return 0.08f;
                default:                     return 0f;
            }
        }

        /// <summary>
        /// Устанавливает урон и кровотечение. Детерминированно.
        /// </summary>
        internal static void SetDamage(ShrapnelProjectile proj,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight, System.Random rng)
        {
            switch (weight)
            {
                case ShrapnelWeight.Hot:
                    proj.Damage = rng.Range(3f, 8f);
                    proj.BleedAmount = rng.Range(0.5f, 2f);
                    break;
                case ShrapnelWeight.Medium:
                    proj.Damage = rng.Range(6f, 15f);
                    proj.BleedAmount = rng.Range(1f, 4f);
                    break;
                case ShrapnelWeight.Heavy:
                    proj.Damage = rng.Range(12f, 25f);
                    proj.BleedAmount = rng.Range(2f, 6f);
                    break;
                case ShrapnelWeight.Massive:
                    proj.Damage = rng.Range(25f, 50f);
                    proj.BleedAmount = rng.Range(5f, 12f);
                    break;
            }

            if (type == ShrapnelProjectile.ShrapnelType.HeavyMetal)
                proj.Damage *= 1.3f;
        }

        /// <summary>
        /// Запускает осколок. Мина = 240° вверх, остальные = 360°.
        /// Результирующая скорость клампится к GlobalMaxSpeed.
        /// </summary>
        internal static void LaunchFragment(Rigidbody2D rb, ShrapnelWeight weight,
            float baseSpeed, ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            Vector2 dir = GetDirectionForType(type, rng);

            // ФИКС: Hot speedMult снижен (было 1.2-1.8)
            float speedMult;
            switch (weight)
            {
                case ShrapnelWeight.Hot:     speedMult = rng.Range(0.8f, 1.3f);  break;
                case ShrapnelWeight.Medium:  speedMult = rng.Range(0.8f, 1.2f);  break;
                case ShrapnelWeight.Heavy:   speedMult = rng.Range(0.4f, 0.8f);  break;
                case ShrapnelWeight.Massive: speedMult = rng.Range(0.2f, 0.4f);  break;
                default:                     speedMult = 1f; break;
            }

            float targetSpeed = Mathf.Min(baseSpeed * speedMult, ShrapnelSpawnLogic.GlobalMaxSpeed);
            rb.AddForce(dir * targetSpeed * rb.mass, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-500f, 500f));
        }

        /// <summary>
        /// Добавляет TrailRenderer горячим/тяжёлым осколкам.
        /// </summary>
        internal static void TryAddTrail(GameObject obj, ShrapnelProjectile proj,
            ShrapnelWeight weight, System.Random rng)
        {
            bool give = weight == ShrapnelWeight.Hot
                     || weight == ShrapnelWeight.Massive
                     || (weight == ShrapnelWeight.Medium && rng.NextDouble() < 0.25);
            if (!give) return;

            Material mat = ShrapnelVisuals.TrailMaterial;
            if (mat == null) return;

            TrailRenderer tr = obj.AddComponent<TrailRenderer>();
            tr.sharedMaterial = mat;
            tr.sortingOrder = 9;
            tr.numCapVertices = 1;
            tr.autodestruct = false;

            float scale = obj.transform.localScale.x;

            if (weight == ShrapnelWeight.Massive)
            {
                tr.time = 0.4f;
                tr.startWidth = 0.12f * scale * 5f;
                tr.endWidth = 0f;
                tr.startColor = new Color(0.3f, 0.25f, 0.2f, 0.8f);
                tr.endColor = new Color(0.2f, 0.2f, 0.2f, 0f);
            }
            else if (weight == ShrapnelWeight.Hot)
            {
                tr.time = 0.25f;
                tr.startWidth = 0.06f * scale * 10f;
                tr.endWidth = 0f;
                tr.startColor = new Color(1f, 0.5f, 0.1f, 0.9f);
                tr.endColor = new Color(1f, 0.2f, 0f, 0f);
            }
            else
            {
                tr.time = 0.15f;
                tr.startWidth = 0.04f * scale * 10f;
                tr.endWidth = 0f;
                tr.startColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
                tr.endColor = new Color(0.4f, 0.4f, 0.4f, 0f);
            }

            proj.HasTrail = true;
        }
    }
}