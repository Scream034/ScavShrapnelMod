using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Основной компонент осколка. Finite State Machine: Flying → Stuck/Debris.
    /// 
    /// Управляет:
    /// - Физикой (скорость, гравитация, коллизии)
    /// - Остыванием (Heat → cold color)
    /// - Уроном (при попадании в Limb/Body)
    /// - Визуальными эффектами (обводка, искры, пар)
    /// - Рикошетом от металла
    /// - Разрушением при столкновении
    /// - Интерактивностью (клик мышью для уничтожения)
    /// </summary>
    public class ShrapnelProjectile : MonoBehaviour
    {
        //  ENUMS

        public enum ShrapnelType { Metal, Stone, Wood, Electronic, HeavyMetal }
        private enum State { Flying, Stuck, Debris }

        //  CONSTANTS

        /// <summary>
        /// Unity layer index для Ground/блоков.
        /// Верифицировано из TurretScript.Shoot: LayerMask.GetMask("Ground").
        /// </summary>
        private const int GroundLayer = 6;

        /// <summary>Максимальная скорость (м/с). Предотвращает туннелирование.</summary>
        private const float MaxVelocity = 100f;

        /// <summary>Задержка коллайдера (сек). Предотвращает self-collision в эпицентре.</summary>
        private const float PhysicsDelaySeconds = 0.05f;

        /// <summary>Время жизни осколка в стене до удаления (сек).</summary>
        private const float StuckLifetimeSeconds = 15f;

        /// <summary>Макс блоков, которые осколок может разрушить.</summary>
        private const int MaxBlocksToDestroy = 3;

        /// <summary>Макс рикошетов от металлических поверхностей.</summary>
        private const int MaxRicochets = 3;

        /// <summary>Максимальный угол (градусы) для рикошета.</summary>
        private const float RicochetMaxAngleDeg = 30f;

        /// <summary>Сохранение скорости при рикошете.</summary>
        private const float RicochetSpeedRetention = 0.7f;

        /// <summary>Минимальная скорость для рикошета (м/с).</summary>
        private const float RicochetMinSpeed = 5f;

        /// <summary>Скорость остывания (Heat единиц / сек).</summary>
        private const float HeatCoolRate = 0.42f;

        /// <summary>Масштаб обводки относительно родителя.</summary>
        private const float OutlineScaleMultiplier = 1.4f;

        /// <summary>Базовая альфа обводки.</summary>
        private const float OutlineAlphaBase = 0.35f;

        /// <summary>Минимальная скорость полёта (sqr magnitude).</summary>
        private const float MinFlySqrSpeed = 0.5f;

        /// <summary>Минимальное время полёта перед debris (сек).</summary>
        private const float MinFlyTimeBeforeDebris = 0.3f;

        /// <summary>Минимальная скорость удара для обработки столкновения.</summary>
        private const float MinBlockImpactSpeed = 3f;

        /// <summary>Скорость удара для спавна искры.</summary>
        private const float SparkImpactSpeed = 9f;

        /// <summary>Максимальная дистанция для уничтожения кликом (тайлы).</summary>
        private const float MaxInteractDistance = 3f;

        //  PUBLIC FIELDS (устанавливаются ShrapnelFactory)

        public ShrapnelType Type;
        public ShrapnelWeight Weight;
        public float Damage;
        public float BleedAmount;
        public float Heat = 1f;
        public bool HasTrail;
        public bool CanBreak = true;

        //  PRIVATE FIELDS

        //  Кэш компонентов 
        private Rigidbody2D rb;
        private SpriteRenderer sr;
        private TrailRenderer trail;
        private Collider2D _col;

        //  FSM 
        private State state = State.Flying;
        private float lifeTimer;
        private float maxLifetime = 5f;
        private float _physicsDelay = PhysicsDelaySeconds;

        //  Stuck state 
        private float stuckTimer;

        //  Debris state 
        private float debrisTimer;
        private float debrisLifetime = 900f;

        //  Лимиты 
        private int _blocksDestroyed;
        private int _ricochetCount;

        //  Водяная деактивация 
        private bool _submerged;

        //  Heat 
        private Color coldColor;
        private bool cooledInWater;
        private float lastEmissionHeat = -1f;

        //  Frame-staggering 
        private int frameSlot;

        //  Outline (обводка вместо glow) 
        private GameObject _outlineObj;
        private SpriteRenderer _outlineSr;
        private bool _outlineApplied;

        //  UNITY LIFECYCLE

        private void Awake()
        {
            rb = GetComponent<Rigidbody2D>();
            sr = GetComponent<SpriteRenderer>();
            trail = GetComponent<TrailRenderer>();
            _col = GetComponent<Collider2D>();
            frameSlot = Mathf.Abs(GetInstanceID()) % 10;
        }

        private void Start()
        {
            coldColor = ShrapnelVisuals.GetColdColor(Type);
        }

        private void Update()
        {
            int frame = Time.frameCount;
            switch (state)
            {
                case State.Flying: UpdateFlying(frame); break;
                case State.Stuck: UpdateStuck(frame); break;
                case State.Debris: UpdateDebris(frame); break;
            }
        }

        //  STATE: FLYING

        private void UpdateFlying(int frame)
        {
            // Micro-delay для коллайдера
            if (_physicsDelay > 0f)
            {
                _physicsDelay -= Time.deltaTime;
                if (_physicsDelay <= 0f && _col != null)
                    _col.enabled = true;
            }

            lifeTimer += Time.deltaTime;
            if (lifeTimer > maxLifetime) { BecomeDebris(); return; }

            // Кламп скорости
            float sqrSpeed = rb.velocity.sqrMagnitude;
            if (sqrSpeed > MaxVelocity * MaxVelocity)
                rb.velocity = rb.velocity.normalized * MaxVelocity;

            // Почти остановился → debris
            if (sqrSpeed < MinFlySqrSpeed && lifeTimer > MinFlyTimeBeforeDebris)
            { BecomeDebris(); return; }

            // Поворот по вектору скорости
            if (sqrSpeed > 4f)
            {
                float angle = Mathf.Atan2(rb.velocity.y, rb.velocity.x) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
            }

            // Остывание (frame-staggered)
            if (frame % 3 == frameSlot % 3)
                TickHeat(Time.deltaTime * 3f);
        }

        //  STATE: STUCK

        private void UpdateStuck(int frame)
        {
            stuckTimer += Time.deltaTime;
            if (stuckTimer > StuckLifetimeSeconds) { Destroy(gameObject); return; }

            if (!_outlineApplied)
            {
                CreateOutline();
                _outlineApplied = true;
            }

            if (frame % 5 == frameSlot % 5)
                PulseOutline();

            if (frame % 10 == frameSlot)
                CheckSupportAndFall();
        }

        //  STATE: DEBRIS

        private void UpdateDebris(int frame)
        {
            debrisTimer += Time.deltaTime;
            if (debrisTimer > debrisLifetime) { Destroy(gameObject); return; }

            if (!_outlineApplied)
            {
                CreateOutline();
                _outlineApplied = true;
            }

            if (frame % 5 == frameSlot % 5)
                PulseOutline();

            if (frame % 30 == frameSlot)
                CheckSubmerged();

            if (frame % 10 == frameSlot)
                CheckSupportAndFall();
        }

        //  OUTLINE — красная обводка (дочерний спрайт)

        /// <summary>
        /// Создаёт красную обводку: дочерний спрайт = тот же спрайт,
        /// масштаб ×1.5, позади родителя, Unlit-материал.
        /// Не создаётся если осколок в воде (безопасен).
        /// </summary>
        private void CreateOutline()
        {
            if (_outlineObj != null) return;
            if (_submerged) return;

            _outlineObj = new GameObject("Outline");
            _outlineObj.transform.SetParent(transform, false);
            _outlineObj.transform.localPosition = Vector3.zero;
            _outlineObj.transform.localRotation = Quaternion.identity;
            _outlineObj.transform.localScale = Vector3.one * OutlineScaleMultiplier;

            _outlineSr = _outlineObj.AddComponent<SpriteRenderer>();
            _outlineSr.sprite = sr.sprite;
            _outlineSr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
            _outlineSr.sortingOrder = sr.sortingOrder - 1;
            _outlineSr.color = new Color(0.9f, 0.1f, 0.05f, OutlineAlphaBase);
        }

        /// <summary>Удаляет обводку.</summary>
        private void DestroyOutline()
        {
            if (_outlineObj != null)
            {
                Destroy(_outlineObj);
                _outlineObj = null;
                _outlineSr = null;
            }
        }

        /// <summary>
        /// Пульсация обводки: альфа 40-70%.
        /// Скрывает обводку если осколок в воде.
        /// </summary>
        private void PulseOutline()
        {
            if (_outlineSr == null) return;

            if (_submerged)
            {
                DestroyOutline();
                _outlineApplied = false;
                return;
            }

            float phase = Time.time * 3.14f + frameSlot * 0.628f;
            float sinVal = Mathf.Sin(phase);
            float alpha = OutlineAlphaBase + sinVal * 0.15f;
            _outlineSr.color = new Color(0.9f, 0.1f, 0.05f, alpha);
        }

        //  ВОДЯНАЯ ДЕАКТИВАЦИЯ

        /// <summary>
        /// Проверяет наличие жидкости (вода, масло, сок и т.д.).
        /// Если есть — debris безопасен для наступания.
        /// </summary>
        private void CheckSubmerged()
        {
            try
            {
                Vector2Int wPos = WorldGeneration.world.WorldToBlockPos(transform.position);
                float liquidLevel = FluidManager.main.WaterInfo(wPos).Item1;
                _submerged = liquidLevel > 0f;
            }
            catch
            {
                _submerged = false;
            }
        }

        //  SUPPORT CHECK

        /// <summary>
        /// Проверяет, есть ли опора под осколком.
        /// Если нет — возвращает в состояние Flying.
        /// </summary>
        private void CheckSupportAndFall()
        {
            try
            {
                Vector2Int currentPos = WorldGeneration.world.WorldToBlockPos((Vector2)transform.position);
                Vector2Int belowPos = currentPos + Vector2Int.down;

                if (WorldGeneration.world.GetBlock(currentPos) == 0 &&
                    WorldGeneration.world.GetBlock(belowPos) == 0)
                {
                    RestorePhysicsAndFly();
                }
            }
            catch (IndexOutOfRangeException)
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// Переводит осколок обратно в Flying (блок разрушен).
        /// </summary>
        private void RestorePhysicsAndFly()
        {
            state = State.Flying;
            _outlineApplied = false;
            lifeTimer = 0f;
            maxLifetime = 3f;
            rb.isKinematic = false;

            switch (Weight)
            {
                case ShrapnelWeight.Hot: rb.gravityScale = 0.3f; break;
                case ShrapnelWeight.Medium: rb.gravityScale = 0.15f; break;
                case ShrapnelWeight.Heavy: rb.gravityScale = 0.35f; break;
                case ShrapnelWeight.Massive: rb.gravityScale = 0.5f; break;
                default: rb.gravityScale = 0.5f; break;
            }

            if (_col != null) _col.isTrigger = false;
            DestroyOutline();
        }

        //  COLLISION HANDLING

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (state == State.Debris || state == State.Stuck)
            {
                if (collision.relativeVelocity.magnitude > 5f)
                    BreakShard();
                return;
            }

            if (state != State.Flying) return;

            if (collision.collider.TryGetComponent(out Limb limb))
            {
                HitLimb(limb);
                return;
            }

            if (collision.collider.TryGetComponent(out Body body) &&
                body.limbs != null && body.limbs.Length > 0)
            {
                Limb target = FindClosestLimb(body, collision);
                HitLimb(target);
                return;
            }

            if (collision.gameObject.layer == GroundLayer)
                HitBlock(collision);
        }

        /// <summary>
        /// Ближайшая конечность к точке попадания.
        /// </summary>
        private Limb FindClosestLimb(Body body, Collision2D collision)
        {
            Vector2 hitPos = collision.contactCount > 0
                ? collision.GetContact(0).point
                : (Vector2)transform.position;

            Limb closest = body.limbs[0];
            float closestDist = float.MaxValue;

            for (int i = 0; i < body.limbs.Length; i++)
            {
                Limb l = body.limbs[i];
                if (l == null || l.dismembered) continue;

                float dist = ((Vector2)l.transform.position - hitPos).sqrMagnitude;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = l;
                }
            }

            return closest;
        }

        private void HitBlock(Collision2D collision)
        {
            if (collision.contactCount == 0) return;
            float impactSpeed = collision.relativeVelocity.magnitude;
            if (impactSpeed < MinBlockImpactSpeed) { BecomeDebris(); return; }

            Vector2 hitPoint = collision.GetContact(0).point;
            Vector2 hitNormal = collision.GetContact(0).normal;

            // Рикошет
            if (TryRicochet(impactSpeed, hitPoint, hitNormal))
                return;

            // Разрушение осколка
            if (CanBreak && TryBreak(impactSpeed, hitPoint, hitNormal))
                return;

            try
            {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(hitPoint - hitNormal * 0.1f);
                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                if (blockId == 0) { BecomeDebris(); return; }

                BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                if (info == null) { BecomeDebris(); return; }

                float kineticDamage = impactSpeed * rb.mass * 10f;

                // Лимит разрушения мягких блоков
                if (kineticDamage > info.health && _blocksDestroyed < MaxBlocksToDestroy)
                {
                    if (ShrapnelFactory.TryDamageSlot())
                    {
                        WorldGeneration.world.DamageBlock(hitPoint - hitNormal * 0.1f, kineticDamage, true, false);
                        _blocksDestroyed++;
                    }
                    rb.velocity = -hitNormal * impactSpeed * 0.4f;
                    return;
                }

                if (_blocksDestroyed >= MaxBlocksToDestroy)
                {
                    BecomeStuck(blockPos, hitPoint);
                    return;
                }

                bool isSoft = !info.metallic && info.health <= 300f;
                if ((isSoft || impactSpeed > 30f) && impactSpeed > 5f)
                {
                    BecomeStuck(blockPos, hitPoint);
                    return;
                }

                if (info.metallic && impactSpeed > SparkImpactSpeed)
                    SpawnSpark(hitPoint);

                rb.velocity *= 0.4f;
            }
            catch { BecomeDebris(); }
        }

        //  РИКОШЕТ

        /// <summary>
        /// Рикошет от металлического блока под скользящим углом.
        /// </summary>
        private bool TryRicochet(float impactSpeed, Vector2 hitPoint, Vector2 hitNormal)
        {
            if (_ricochetCount >= MaxRicochets) return false;
            if (impactSpeed < RicochetMinSpeed) return false;

            try
            {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(hitPoint - hitNormal * 0.1f);
                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                if (blockId == 0) return false;

                BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                if (info == null || !info.metallic) return false;
            }
            catch { return false; }

            Vector2 velDir = rb.velocity.normalized;
            float dotNormal = Mathf.Abs(Vector2.Dot(velDir, hitNormal));
            float angleFromSurface = Mathf.Asin(dotNormal) * Mathf.Rad2Deg;

            if (angleFromSurface > RicochetMaxAngleDeg) return false;

            Vector2 reflected = Vector2.Reflect(rb.velocity, hitNormal);
            rb.velocity = reflected * RicochetSpeedRetention;
            _ricochetCount++;

            SpawnSpark(hitPoint);
            return true;
        }

        private bool TryBreak(float impactSpeed, Vector2 hitPoint, Vector2 hitNormal)
        {
            float breakThreshold, breakChance;
            switch (Weight)
            {
                case ShrapnelWeight.Massive: breakThreshold = 8f; breakChance = 0.6f; break;
                case ShrapnelWeight.Heavy: breakThreshold = 15f; breakChance = 0.35f; break;
                case ShrapnelWeight.Medium: breakThreshold = 20f; breakChance = 0.2f; break;
                default: return false;
            }

            if (impactSpeed < breakThreshold) return false;

            float roll = DeterministicRoll(hitPoint);
            if (roll > breakChance) return false;

            ShrapnelFactory.SpawnBreakFragments(
                hitPoint, hitNormal, transform.localScale.x,
                Type, Weight, impactSpeed);
            BreakShard();
            return true;
        }

        /// <summary>
        /// Детерминированный псевдослучай [0, 1) из позиции.
        /// </summary>
        private float DeterministicRoll(Vector2 pos)
        {
            int hash = unchecked(
                (int)(pos.x * 73856093f) ^
                (int)(pos.y * 19349663f) ^
                Time.frameCount * 83492791);
            return (float)((uint)hash % 10000) / 10000f;
        }

        private void SpawnSpark(Vector2 pos)
        {
            GameObject spark = new GameObject("Spark");
            spark.transform.position = pos;
            spark.transform.localScale = Vector3.one * 0.04f;

            SpriteRenderer ssr = spark.AddComponent<SpriteRenderer>();
            ssr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
            ssr.sharedMaterial = ShrapnelVisuals.LitMaterial;
            ssr.color = new Color(1f, 0.8f, 0.3f);
            ssr.sortingOrder = 11;

            ShrapnelFactory.MPB.Clear();
            ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId, new Color(2f, 1.5f, 0.5f));
            ssr.SetPropertyBlock(ShrapnelFactory.MPB);

            float sparkAngle = DeterministicRoll(pos) * Mathf.PI * 2f;
            Vector2 dir = new Vector2(Mathf.Cos(sparkAngle), Mathf.Abs(Mathf.Sin(sparkAngle)));
            float sparkForce = 1f + DeterministicRoll(pos + Vector2.one) * 2f;

            var visual = spark.AddComponent<VisualShrapnel>();
            visual.Initialize(dir, sparkForce * 3f, 0.1f + DeterministicRoll(pos + Vector2.up) * 0.2f);
        }

        //  LIMB DAMAGE

        /// <summary>
        /// Наносит урон конечности с учётом брони.
        /// </summary>
        private void HitLimb(Limb limb)
        {
            if (limb.dismembered) { Destroy(gameObject); return; }

            float armor = limb.GetArmorReduction();
            float dmg = Damage / armor;
            float bleed = BleedAmount / armor;

            limb.skinHealth -= dmg * 0.7f;
            limb.muscleHealth -= dmg;
            limb.bleedAmount += bleed;

            // Износ брони
            float armorWearAmount;
            switch (Weight)
            {
                case ShrapnelWeight.Hot: armorWearAmount = 0.005f; break;
                case ShrapnelWeight.Medium: armorWearAmount = 0.01f; break;
                case ShrapnelWeight.Heavy: armorWearAmount = 0.02f; break;
                case ShrapnelWeight.Massive: armorWearAmount = 0.05f; break;
                default: armorWearAmount = 0.01f; break;
            }
            limb.DamageWearables(armorWearAmount);

            // Шанс застревания — armor² вместо armor
            float embedChance;
            switch (Weight)
            {
                case ShrapnelWeight.Hot: embedChance = 0.15f; break;
                case ShrapnelWeight.Medium: embedChance = 0.4f; break;
                case ShrapnelWeight.Heavy: embedChance = 0.7f; break;
                case ShrapnelWeight.Massive: embedChance = 0.9f; break;
                default: embedChance = 0.3f; break;
            }

            Vector2 limbPos = (Vector2)limb.transform.position;
            float roll1 = DeterministicRoll(limbPos);
            float roll2 = DeterministicRoll(limbPos + Vector2.right);
            if (roll1 < embedChance / (armor * armor) && roll2 > 0.2f)
                limb.shrapnel++;

            // Переломы
            if (Weight == ShrapnelWeight.Massive)
            {
                limb.BreakBone();
            }
            else if (Weight == ShrapnelWeight.Heavy)
            {
                float boneChance = Type == ShrapnelType.HeavyMetal ? 0.15f : 0.08f;
                if (DeterministicRoll(limbPos + Vector2.left) < boneChance / armor)
                    limb.BreakBone();
            }

            // Внутреннее кровотечение
            if (limb.isVital && Weight != ShrapnelWeight.Hot)
            {
                float intChance = Weight == ShrapnelWeight.Massive ? 0.6f
                                : (Weight == ShrapnelWeight.Heavy ? 0.3f : 0.15f);
                if (DeterministicRoll(limbPos + Vector2.down) < intChance / armor)
                    limb.body.internalBleeding += dmg * 0.3f;
            }

            // Голова
            if (limb.isHead)
            {
                limb.body.consciousness -= dmg * 2f;

                if ((Weight == ShrapnelWeight.Heavy || Weight == ShrapnelWeight.Massive) &&
                    DeterministicRoll(limbPos * 2f) < 0.2f / armor)
                    limb.body.brainHealth -= dmg * 0.5f;

                if (Weight == ShrapnelWeight.Massive &&
                    DeterministicRoll(limbPos * 3f) < 0.3f / armor)
                    limb.body.Disfigure();
            }

            limb.body.shock = Mathf.Max(limb.body.shock, Damage * 2f);
            limb.body.adrenaline = Mathf.Max(limb.body.adrenaline, 20f + Damage);
            limb.body.DoGoreSound();
            limb.body.talker.Talk(Locale.GetCharacter("loud"), null, false, false);

            // Рэгдолл
            if (Weight == ShrapnelWeight.Heavy || Weight == ShrapnelWeight.Massive || Damage > 15f)
            {
                limb.body.lastTimeStepVelocity = rb.velocity.normalized *
                    (Weight == ShrapnelWeight.Massive ? 10f : 5f);
                limb.body.Ragdoll();
            }

            ApplyWoundVisuals(limb);
            Destroy(gameObject);
        }

        private void ApplyWoundVisuals(Limb limb)
        {
            try
            {
                if (ShrapnelFactory.WoundSprite != null)
                    limb.CreateTemporarySprite(ShrapnelFactory.WoundSprite, 0f, null, false, 600f,
                        (Limb x) => !x.hasShrapnel);
                if (ShrapnelFactory.WoundPanel != null)
                    WoundView.view.AddImageToLimb(limb, ShrapnelFactory.WoundPanel, false,
                        (Limb x) => !x.hasShrapnel || x.dismembered);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Shrapnel] Wound: {e.Message}");
            }
        }

        //  STATE TRANSITIONS

        private void BecomeStuck(Vector2Int blockPos, Vector2 hitPoint)
        {
            state = State.Stuck;
            stuckTimer = 0f;
            _outlineApplied = false;
            rb.velocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.isKinematic = true;
            transform.position = (Vector2)hitPoint;
            if (trail != null) trail.enabled = false;
            ClearHeatAndEmission();
        }

        private void BecomeDebris()
        {
            state = State.Debris;
            _outlineApplied = false;
            rb.velocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.isKinematic = true;
            if (trail != null) trail.enabled = false;
            if (_col != null) _col.isTrigger = true;
            ClearHeatAndEmission();

            debrisTimer = 0f;

            // Время жизни debris (уменьшено в 2 раза)
            switch (Type)
            {
                case ShrapnelType.Metal: debrisLifetime = 600f; break;
                case ShrapnelType.HeavyMetal: debrisLifetime = 750f; break;
                case ShrapnelType.Stone: debrisLifetime = 360f; break;
                case ShrapnelType.Wood: debrisLifetime = 240f; break;
                case ShrapnelType.Electronic: debrisLifetime = 450f; break;
                default: debrisLifetime = 300f; break;
            }
        }

        //  TRIGGER — наступание на debris

        /// <summary>
        /// Умная система наступания:
        /// 1. В воде → безопасно
        /// 2. Обувь → износ ботинка, осколок ломается
        /// 3. Ближайшая конечность
        /// 4. Броня снижает урон
        /// </summary>
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (state != State.Debris && state != State.Stuck) return;

            if (!other.TryGetComponent(out Body body))
            {
                BreakShard();
                return;
            }

            if (state != State.Debris) return;

            // Debris в жидкости безопасен
            if (_submerged) return;

            // Обувь
            bool isStanding = body.transform.position.y > transform.position.y;
            if (isStanding)
            {
                Item footwear = body.GetWearableBySlotID("feet");
                if (footwear != null)
                {
                    footwear.SetCondition(footwear.condition -
                        0.05f * footwear.Stats.wearableHitDurabilityLossMultiplier);
                    BreakShard();
                    return;
                }
            }

            // Ближайшая конечность
            Limb target = FindClosestUndamagedLimb(body);
            if (target == null) { BreakShard(); return; }

            float armor = target.GetArmorReduction();

            System.Random localRng = new System.Random(unchecked(
                (int)(transform.position.x * 10000f) ^
                (int)(transform.position.y * 10000f)));

            target.skinHealth -= localRng.Range(15f, 35f) / armor;
            target.muscleHealth -= localRng.Range(5f, 15f) / armor;
            target.bleedAmount += localRng.Range(3f, 12f) / armor;
            target.pain += 50f / armor;
            target.shrapnel++;

            target.DamageWearables(0.01f);

            body.adrenaline = Mathf.Max(body.adrenaline, 40f);
            body.DoGoreSound();
            body.talker.Talk(Locale.GetCharacter("steponglass"), null, false, false);
            ApplyWoundVisuals(target);
            BreakShard();
        }

        /// <summary>Ближайшая неотрублённая конечность.</summary>
        private Limb FindClosestUndamagedLimb(Body body)
        {
            if (body.limbs == null || body.limbs.Length == 0) return null;

            Vector2 myPos = (Vector2)transform.position;
            Limb closest = null;
            float closestDist = float.MaxValue;

            for (int i = 0; i < body.limbs.Length; i++)
            {
                Limb l = body.limbs[i];
                if (l == null || l.dismembered) continue;

                float dist = ((Vector2)l.transform.position - myPos).sqrMagnitude;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = l;
                }
            }

            return closest;
        }

        //  MOUSE INTERACTION — уничтожение кликом

        /// <summary>
        /// Позволяет игроку уничтожить debris/stuck кликом мыши.
        /// Проверяет дистанцию ≤ MaxInteractDistance тайлов.
        /// </summary>
        private void OnMouseDown()
        {
            if (state != State.Debris && state != State.Stuck) return;
            if (PlayerCamera.main == null || PlayerCamera.main.body == null) return;

            float dist = Vector2.Distance(
                (Vector2)transform.position,
                (Vector2)PlayerCamera.main.body.transform.position);

            if (dist <= MaxInteractDistance)
            {
                BreakShard();
            }
        }

        private void BreakShard()
        {
            try
            {
                Sound.Play("glassshard", transform.position, false, true, null, 1f, 1f, false, false);
            }
            catch { }
            Destroy(gameObject);
        }

        //  HEAT SYSTEM

        private void TickHeat(float dt)
        {
            if (Heat <= 0f) return;

            float cool = HeatCoolRate * dt;
            if (WorldGeneration.world != null && WorldGeneration.world.ambientTemperature < 5f)
                cool *= 2f;

            Heat = Mathf.Max(Heat - cool, 0f);
            sr.color = Color.Lerp(coldColor, ShrapnelVisuals.GetHotColor(), Heat);

            if (Mathf.Abs(Heat - lastEmissionHeat) > 0.05f)
            {
                lastEmissionHeat = Heat;
                UpdateEmission();
            }

            if (Heat <= 0f) ClearHeatAndEmission();

            // Остывание в воде + пар
            if (!cooledInWater && Heat > 0.1f)
            {
                try
                {
                    Vector2Int wPos = WorldGeneration.world.WorldToBlockPos(transform.position);
                    if (FluidManager.main.WaterInfo(wPos).Item1 > 0f)
                    {
                        cooledInWater = true;
                        _submerged = true;
                        ClearHeatAndEmission();
                        Sound.Play("fizz", transform.position, false, true, null, 1f, 1f, false, false);
                        SpawnSteamPuff();
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Пуф пара при попадании горячего осколка в воду.
        /// </summary>
        private void SpawnSteamPuff()
        {
            System.Random rng = new System.Random(unchecked(
                (int)(transform.position.x * 10000f) ^
                (int)(transform.position.y * 10000f)));

            int count = rng.Range(3, 6);
            for (int i = 0; i < count; i++)
            {
                GameObject steam = new GameObject("Steam");
                steam.transform.position = transform.position;
                steam.transform.localScale = Vector3.one * rng.Range(0.03f, 0.07f);

                SpriteRenderer ssr = steam.AddComponent<SpriteRenderer>();
                ssr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
                ssr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
                ssr.sortingOrder = 12;

                float gray = rng.Range(0.8f, 1f);
                Color steamColor = new Color(gray, gray, gray, 0.5f);

                Vector2 velocity = new Vector2(rng.Range(-0.3f, 0.3f), rng.Range(1f, 2.5f));

                AshParticle ash = steam.AddComponent<AshParticle>();
                ash.Initialize(velocity, rng.Range(0.5f, 1.2f), steamColor, rng.Range(0f, 6.28f));
            }
        }

        private void UpdateEmission()
        {
            if (Heat > 0.01f)
            {
                ShrapnelFactory.MPB.Clear();
                // Уменьшен на 35%: было Heat * 2f
                ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId,
                    ShrapnelVisuals.GetHotColor() * Heat * 1.3f);
                sr.SetPropertyBlock(ShrapnelFactory.MPB);
            }
            else
            {
                ClearEmission();
            }
        }

        private void ClearHeatAndEmission()
        {
            Heat = 0f;
            sr.color = coldColor;
            ClearEmission();
        }

        private void ClearEmission()
        {
            sr.SetPropertyBlock(null);
            lastEmissionHeat = 0f;
        }
    }
}