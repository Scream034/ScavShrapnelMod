using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Projectiles
{
    /// <summary>
    /// Main shrapnel component. FSM: Flying → Stuck / Debris.
    ///
    /// MULTIPLAYER: This component exists ONLY on the server (host).
    /// Clients receive visual mirrors (ClientMirrorShrapnel) via ShrapnelNetSync.
    /// NetSyncId = 0 in singleplayer; assigned by ShrapnelNetSync.ServerRegister in MP.
    /// </summary>
    public sealed class ShrapnelProjectile : MonoBehaviour
    {
        public enum ShrapnelType { Metal, Stone, Wood, Electronic, HeavyMetal }
        private enum State { Flying, Stuck, Debris }

        //  CONSTANTS

        private const int GroundLayer = 6;
        private const float MaxVelocity = 100f;
        private const float PhysicsDelaySeconds = 0.05f;
        private const int MaxBlocksToDestroy = 3;
        private const int MaxRicochets = 3;
        private const float RicochetMaxAngleDeg = 30f;
        private const float RicochetSpeedRetention = 0.7f;
        private const float RicochetMinSpeed = 5f;
        private const float MinFlySqrSpeed = 0.5f;
        private const float MinFlyTimeBeforeDebris = 0.3f;
        private const float MinBlockImpactSpeed = 3f;
        private const float SparkImpactSpeed = 9f;

        // Visual constants sourced from ShrapnelVisuals (single source of truth).
        // See ShrapnelVisuals for documentation on each value.
        private const float HeatCoolRate = ShrapnelVisuals.HeatCoolRate;
        private const float HotThreshold = ShrapnelVisuals.HotThreshold;
        private const float OutlineScaleMultiplier = ShrapnelVisuals.OutlineScale;
        private const float OutlineAlphaBase = ShrapnelVisuals.OutlineAlphaBase;

        //  PUBLIC FIELDS

        public ShrapnelType Type;
        public ShrapnelWeight Weight;
        public float Damage;
        public float BleedAmount;
        public float Heat = 1f;
        public bool HasTrail;
        public bool CanBreak = true;

        /// <summary>
        /// Deterministic seed for per-projectile RNG.
        /// Set by ShrapnelFactory from the explosion's RNG chain.
        /// Ensures identical DeterministicRoll sequences on host and client
        /// (previously used GetInstanceID() ^ Time.frameCount which diverges).
        /// Set BEFORE Start() runs — factory sets field immediately after AddComponent.
        /// </summary>
        public int Seed;

        /// <summary>
        /// Network sync ID assigned by ShrapnelNetSync.ServerRegister.
        /// 0 = singleplayer (no sync). Used by OnDestroy to unregister.
        /// </summary>
        [NonSerialized] public ushort NetSyncId;

        //  PRIVATE FIELDS

        private Rigidbody2D rb;
        private SpriteRenderer sr;
        private TrailRenderer trail;
        private Collider2D _col;
        private Transform _transform;

        private State state = State.Flying;
        private float lifeTimer;
        private float maxLifetime = 5f;
        private float _physicsDelay = PhysicsDelaySeconds;
        private float stuckTimer;
        private float debrisTimer;
        private float debrisLifetime;
        private int _blocksDestroyed;
        private int _ricochetCount;
        private bool _submerged;
        private Color coldColor;
        private bool cooledInWater;
        private float lastEmissionHeat = -1f;
        private int frameSlot;

        private GameObject _outlineObj;
        private SpriteRenderer _outlineSr;
        private bool _outlineApplied;
        private Vector3 _originalScale;

        /// <summary>
        /// Per-instance deterministic RNG. Initialized from Seed in Start().
        /// Using Seed (set by factory) instead of GetInstanceID() ^ frameCount
        /// ensures identical random sequences host↔client.
        /// </summary>
        private System.Random _rng;

        //  PROPERTIES

        private float NormalizedLifetime
        {
            get
            {
                if (state == State.Stuck)
                    return 1f - Mathf.Clamp01(
                        stuckTimer / ShrapnelConfig.StuckLifetime.Value);
                if (state == State.Debris)
                    return 1f - Mathf.Clamp01(debrisTimer / debrisLifetime);
                return 1f;
            }
        }

        private float DamageDecayMultiplier
        {
            get
            {
                float t = NormalizedLifetime;
                const float e = 2.71828f;
                return Mathf.Log(1f + t * e) / Mathf.Log(1f + e);
            }
        }

        //  UNITY LIFECYCLE

        private void Awake()
        {
            rb = GetComponent<Rigidbody2D>();
            sr = GetComponent<SpriteRenderer>();
            trail = GetComponent<TrailRenderer>();
            _col = GetComponent<Collider2D>();
            _transform = transform;

            frameSlot = Mathf.Abs(GetInstanceID()) % 10;
        }

        private void Start()
        {
            coldColor = ShrapnelVisuals.GetColdColor(Type);
            _originalScale = _transform.localScale;
            _rng = new System.Random(Seed != 0 ? Seed : GetInstanceID());
        }

        private void Update()
        {
            // Guard: sr can be null during destruction
            if (sr == null) return;

            // Self-heal corrupted material (AssetBundle unload can null the shader)
            if (sr.sharedMaterial != null && sr.sharedMaterial.shader == null)
            {
                sr.sharedMaterial = Heat > HotThreshold
                    ? ShrapnelVisuals.UnlitMaterial
                    : (ShrapnelVisuals.LitMaterial ?? ShrapnelVisuals.UnlitMaterial);

                if (Heat > 0.01f) UpdateEmission();
            }

            if (trail != null
                && trail.sharedMaterial != null
                && trail.sharedMaterial.shader == null)
            {
                trail.sharedMaterial = ShrapnelVisuals.TrailMaterial;
            }

            int frame = Time.frameCount;
            switch (state)
            {
                case State.Flying: UpdateFlying(frame); break;
                case State.Stuck: UpdateStuck(frame); break;
                case State.Debris: UpdateDebris(frame); break;
            }
        }

        private void OnDestroy()
        {
            if (NetSyncId != 0)
                Net.ShrapnelNetSync.ServerUnregister(NetSyncId);
        }

        //  FSM UPDATES

        private void UpdateFlying(int frame)
        {
            if (_physicsDelay > 0f)
            {
                _physicsDelay -= Time.deltaTime;
                if (_physicsDelay <= 0f && _col != null)
                    _col.enabled = true;
            }

            lifeTimer += Time.deltaTime;
            if (lifeTimer > maxLifetime) { BecomeDebris(); return; }

            float sqrSpeed = rb.velocity.sqrMagnitude;
            if (sqrSpeed > MaxVelocity * MaxVelocity)
                rb.velocity = rb.velocity.normalized * MaxVelocity;

            if (sqrSpeed < MinFlySqrSpeed && lifeTimer > MinFlyTimeBeforeDebris)
            { BecomeDebris(); return; }

            if (sqrSpeed > 4f)
            {
                float angle = Mathf.Atan2(rb.velocity.y, rb.velocity.x) * Mathf.Rad2Deg;
                _transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
            }

            if (frame % 3 == frameSlot % 3)
                TickHeat(Time.deltaTime * 3f);
        }

        private void UpdateStuck(int frame)
        {
            stuckTimer += Time.deltaTime;
            if (stuckTimer > ShrapnelConfig.StuckLifetime.Value)
            { Destroy(gameObject); return; }

            if (!_outlineApplied) { CreateOutline(); _outlineApplied = true; }
            // WHY: ApplyVisualDecay now returns true if shard was destroyed
            // (visually depleted). Must exit immediately — subsequent calls
            // to PulseOutline/CheckSupportAndFall would access destroyed GO.
            if (ApplyVisualDecay()) return;

            if (frame % 5 == frameSlot % 5) PulseOutline();
            if (frame % 10 == frameSlot) CheckSupportAndFall();
        }

        private void UpdateDebris(int frame)
        {
            debrisTimer += Time.deltaTime;
            if (debrisTimer > debrisLifetime)
            { Destroy(gameObject); return; }

            if (!_outlineApplied) { CreateOutline(); _outlineApplied = true; }
            // WHY: ApplyVisualDecay now returns true if shard was destroyed
            // (visually depleted). Must exit immediately to avoid accessing
            // components on a destroyed GameObject.
            if (ApplyVisualDecay()) return;

            if (frame % 5 == frameSlot % 5) PulseOutline();
            if (frame % 30 == frameSlot) CheckSubmerged();
            if (frame % 10 == frameSlot) CheckSupportAndFall();
        }

        //  VISUAL DECAY

        /// <summary>
        /// Applies scale and alpha decay during the final 30% of shard lifetime.
        /// Returns true if the shard was destroyed (caller must return immediately).
        ///
        /// WHY EARLY DESTROY: Without this, the collider remains active while
        /// the shard is visually invisible (~21% scale, ~11% alpha). Stuck shards
        /// block physics; Debris shards deal step-on damage via OnTriggerEnter2D.
        /// Players take damage from shards they cannot see — reported in both
        /// singleplayer and multiplayer.
        ///
        /// WHY COLLIDER DISABLE: Safety margin before destroy threshold.
        /// At decayFactor &lt; 0.15 (~32% scale, ~24% alpha) the shard provides
        /// no visual feedback for the damage it deals. Disabling the collider
        /// earlier prevents the invisible-damage window entirely.
        /// </summary>
        /// <returns>True if the shard was destroyed and caller should return.</returns>
        private bool ApplyVisualDecay()
        {
            float t = NormalizedLifetime;
            if (t > 0.3f) return false;

            // WHY: Shard is visually depleted — destroy to prevent invisible collider
            // dealing damage or blocking physics. At NormalizedLifetime=0.02:
            // scale ≈ 21%, alpha ≈ 11% — effectively invisible to the player.
            // For 300s debris lifetime, this triggers at ~294s (6s early — imperceptible).
            if (t <= 0.02f)
            {
                Destroy(gameObject);
                return true;
            }

            float decayT = t / 0.3f;
            float decayFactor = decayT * decayT * (3f - 2f * decayT);

            // WHY: Disable collider before shard is fully invisible to prevent
            // damage from nearly-invisible fragments. At decayFactor=0.15:
            // scale ≈ 32%, alpha ≈ 24% — barely visible, no gameplay value
            // in dealing damage. Prevents the invisible-damage window between
            // "too small to see" and "early destroy threshold".
            // PERF: Only checks once — _col.enabled becomes false permanently.
            if (decayFactor < 0.15f && _col != null && _col.enabled)
                _col.enabled = false;

            _transform.localScale = _originalScale * (0.2f + 0.8f * decayFactor);
            _transform.Rotate(0f, 0f, (1f - decayFactor) * 360f * Time.deltaTime);

            if (sr != null)
            {
                Color c = sr.color;
                c.a = Mathf.Lerp(0.1f, 1f, decayFactor);
                sr.color = c;
            }

            return false;
        }

        //  OUTLINE

        private void CreateOutline()
        {
            if (_outlineObj != null || _submerged) return;

            _outlineObj = new GameObject("Outline");
            _outlineObj.transform.SetParent(_transform, false);
            _outlineObj.transform.localScale = Vector3.one * OutlineScaleMultiplier;

            _outlineSr = _outlineObj.AddComponent<SpriteRenderer>();
            _outlineSr.sprite = sr.sprite;
            _outlineSr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
            _outlineSr.sortingOrder = sr.sortingOrder - 1;
            _outlineSr.color = ShrapnelVisuals.GetOutlineBaseColor();
        }

        private void DestroyOutline()
        {
            if (_outlineObj != null)
            {
                Destroy(_outlineObj);
                _outlineObj = null;
                _outlineSr = null;
            }
        }

        private void PulseOutline()
        {
            if (_outlineSr == null) return;

            // Self-heal outline material if corrupted
            if (_outlineSr.sharedMaterial != null
                && _outlineSr.sharedMaterial.shader == null)
                _outlineSr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;

            if (_submerged) { DestroyOutline(); _outlineApplied = false; return; }

            _outlineSr.color = ShrapnelVisuals.GetOutlineColor(
                Time.time, frameSlot * 0.628f);
        }

        //  ENVIRONMENT CHECKS

        private void CheckSubmerged()
        {
            try
            {
                Vector2Int wPos = WorldGeneration.world.WorldToBlockPos(_transform.position);
                _submerged = FluidManager.main.WaterInfo(wPos).Item1 > 0f;
            }
            catch { _submerged = false; }
        }

        private void CheckSupportAndFall()
        {
            if (state == State.Flying) return;
            try
            {
                Vector2Int cur = WorldGeneration.world.WorldToBlockPos(
                    (Vector2)_transform.position);
                Vector2Int below = cur + Vector2Int.down;
                if (WorldGeneration.world.GetBlock(cur) == 0 &&
                    WorldGeneration.world.GetBlock(below) == 0)
                    RestorePhysicsAndFly();
            }
            catch (IndexOutOfRangeException)
            {
                if (state == State.Stuck || state == State.Debris)
                    Destroy(gameObject);
            }
        }

        private void RestorePhysicsAndFly()
        {
            state = State.Flying;
            _outlineApplied = false;
            lifeTimer = 0f;
            maxLifetime = 3f;
            _originalScale = _transform.localScale;
            rb.isKinematic = false;

            switch (Weight)
            {
                case ShrapnelWeight.Micro: rb.gravityScale = 0.1f; break;
                case ShrapnelWeight.Hot: rb.gravityScale = 0.3f; break;
                case ShrapnelWeight.Medium: rb.gravityScale = 0.15f; break;
                case ShrapnelWeight.Heavy: rb.gravityScale = 0.35f; break;
                case ShrapnelWeight.Massive: rb.gravityScale = 0.5f; break;
                default: rb.gravityScale = 0.5f; break;
            }

            if (_col != null) _col.isTrigger = false;
            DestroyOutline();
        }

        //  COLLISION
        //
        //  BUG FIX: Previously, debris/stuck shards called BreakShard() for
        //  ANY collision with velocity >5, including other shrapnel. This caused
        //  chain destruction during explosions — 87 of 92 shards destroyed by
        //  other shards flying through debris trigger/collision volumes.
        //  Client mirrors vanished mid-flight because the server destroyed them
        //  before the state debounce could send a REST transition.
        //
        //  NOW: Only break debris from high-velocity impacts with non-shrapnel
        //  objects (terrain, entities). Other shrapnel fragments are filtered out.

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (state == State.Debris || state == State.Stuck)
            {
                // WHY: Filter out shrapnel-on-shrapnel contacts to prevent chain
                // destruction. Only non-shrapnel high-velocity impacts can break debris.
                if (collision.relativeVelocity.magnitude > 5f
                    && !collision.collider.TryGetComponent<ShrapnelProjectile>(out _))
                    BreakShard();
                return;
            }
            if (state != State.Flying) return;

            if (collision.collider.TryGetComponent(out Limb limb))
            { HitLimb(limb); return; }

            if (collision.collider.TryGetComponent(out Body body)
                && body.limbs != null && body.limbs.Length > 0)
            {
                Limb target = FindBestLimbByTrajectory(body, collision);
                HitLimb(target);
                return;
            }

            if (collision.collider.TryGetComponent(out BuildingEntity entity))
            { HitBuildingEntity(entity); return; }

            if (collision.gameObject.layer == GroundLayer)
                HitBlock(collision);
        }

        //  BUILDING ENTITY DAMAGE

        private void HitBuildingEntity(BuildingEntity entity)
        {
            try
            {
                if (entity.cantHit) { BecomeDebris(); return; }

                float decayMult = DamageDecayMultiplier;
                float dmg = Damage * decayMult;
                entity.health -= dmg;

                if (entity.animal)
                {
                    entity.gameObject.SendMessage("AnimalHit", dmg,
                        SendMessageOptions.DontRequireReceiver);
                    if (entity.health <= 0f)
                        entity.gameObject.SendMessage("AnimalDeath",
                            SendMessageOptions.DontRequireReceiver);
                }

                try
                {
                    SpriteRenderer entitySr = entity.GetComponent<SpriteRenderer>();
                    if (entitySr != null && entitySr.sprite != null)
                        WorldGeneration.world.CreateHitFlash(
                            entitySr.sprite, entity.transform.position,
                            entity.transform.rotation, Color.red, entity.transform);
                }
                catch (Exception e)
                {
                    Console.Error($"[Shrapnel] HitFlash: {e.Message}");
                }
            }
            catch (Exception e)
            {
                Console.Error($"[Shrapnel] BuildingEntity: {e.Message}");
            }
            Destroy(gameObject);
        }

        //  LIMB HELPERS

        private Limb FindBestLimbByTrajectory(Body body, Collision2D collision)
        {
            if (rb == null || rb.isKinematic || rb.bodyType == RigidbodyType2D.Static)
                return FindClosestLimb(body, collision);

            Vector2 vel = rb.velocity;
            if (vel.sqrMagnitude < 1f) return FindClosestLimb(body, collision);

            Vector2 pos = (Vector2)_transform.position;
            Vector2 velDir = vel.normalized;
            Limb best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < body.limbs.Length; i++)
            {
                Limb l = body.limbs[i];
                if (l == null || l.dismembered) continue;
                Vector2 toL = (Vector2)l.transform.position - pos;
                float fwd = Vector2.Dot(toL, velDir);
                Vector2 closest = pos + velDir * fwd;
                float perpSqr = ((Vector2)l.transform.position - closest).sqrMagnitude;
                float score = perpSqr + (fwd < 0f ? 4f : 0f);
                if (score < bestScore) { bestScore = score; best = l; }
            }
            return best ?? FindClosestLimb(body, collision);
        }

        private Limb FindClosestLimb(Body body, Collision2D collision)
        {
            Vector2 hitPos = collision.contactCount > 0
                ? collision.GetContact(0).point
                : (Vector2)_transform.position;

            Limb closest = body.limbs[0];
            float closestDist = float.MaxValue;

            for (int i = 0; i < body.limbs.Length; i++)
            {
                Limb l = body.limbs[i];
                if (l == null || l.dismembered) continue;
                float dist = ((Vector2)l.transform.position - hitPos).sqrMagnitude;
                if (dist < closestDist) { closestDist = dist; closest = l; }
            }
            return closest;
        }

        //  BLOCK HIT

        private void HitBlock(Collision2D collision)
        {
            if (Weight == ShrapnelWeight.Micro) { BreakShard(); return; }
            if (collision.contactCount == 0) return;

            float impactSpeed = collision.relativeVelocity.magnitude;
            if (impactSpeed < MinBlockImpactSpeed) { BecomeDebris(); return; }

            Vector2 hitPoint = collision.GetContact(0).point;
            Vector2 hitNormal = collision.GetContact(0).normal;

            if (TryRicochet(impactSpeed, hitPoint, hitNormal)) return;
            if (CanBreak && TryBreak(impactSpeed, hitPoint, hitNormal)) return;

            try
            {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(
                    hitPoint - hitNormal * 0.1f);
                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                if (blockId == 0) { BecomeDebris(); return; }

                BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                if (info == null) { BecomeDebris(); return; }

                float kineticDamage = impactSpeed * rb.mass * 10f;
                if (kineticDamage > info.health
                    && _blocksDestroyed < MaxBlocksToDestroy)
                {
                    if (ShrapnelFactory.TryDamageSlot())
                    {
                        WorldGeneration.world.DamageBlock(
                            hitPoint - hitNormal * 0.1f,
                            kineticDamage, true, false);
                        _blocksDestroyed++;
                    }
                    rb.velocity = -hitNormal * impactSpeed * 0.4f;
                    return;
                }
                if (_blocksDestroyed >= MaxBlocksToDestroy)
                { BecomeStuck(blockPos, hitPoint); return; }

                bool isSoft = !info.metallic && info.health <= 300f;
                if ((isSoft || impactSpeed > 30f) && impactSpeed > 5f)
                { BecomeStuck(blockPos, hitPoint); return; }

                if (info.metallic && impactSpeed > SparkImpactSpeed)
                    SpawnSparks(hitPoint, hitNormal, false);

                rb.velocity *= 0.4f;
            }
            catch { BecomeDebris(); }
        }

        //  RICOCHET / BREAK

        private bool TryRicochet(float impactSpeed, Vector2 hitPoint, Vector2 hitNormal)
        {
            if (Weight == ShrapnelWeight.Micro) return false;
            if (_ricochetCount >= MaxRicochets) return false;
            if (impactSpeed < RicochetMinSpeed) return false;

            try
            {
                Vector2Int bp = WorldGeneration.world.WorldToBlockPos(
                    hitPoint - hitNormal * 0.1f);
                ushort bid = WorldGeneration.world.GetBlock(bp);
                if (bid == 0) return false;
                BlockInfo info = WorldGeneration.world.GetBlockInfo(bid);
                if (info == null || !info.metallic) return false;
            }
            catch { return false; }

            float dot = Mathf.Abs(
                Vector2.Dot(rb.velocity.normalized, hitNormal));
            if (Mathf.Asin(dot) * Mathf.Rad2Deg > RicochetMaxAngleDeg) return false;

            rb.velocity = Vector2.Reflect(rb.velocity, hitNormal) * RicochetSpeedRetention;
            _ricochetCount++;
            SpawnSparks(hitPoint, hitNormal, true);
            return true;
        }

        private bool TryBreak(float impactSpeed, Vector2 hitPoint, Vector2 hitNormal)
        {
            if (Weight == ShrapnelWeight.Micro) return false;

            float breakThreshold, breakChance;
            switch (Weight)
            {
                case ShrapnelWeight.Massive: breakThreshold = 8f; breakChance = 0.6f; break;
                case ShrapnelWeight.Heavy: breakThreshold = 15f; breakChance = 0.35f; break;
                case ShrapnelWeight.Medium: breakThreshold = 20f; breakChance = 0.2f; break;
                default: return false;
            }

            if (impactSpeed < breakThreshold) return false;
            if (DeterministicRoll() > breakChance) return false;

            ShrapnelFactory.SpawnBreakFragments(
                hitPoint, hitNormal, _transform.localScale.x, Type, Weight, impactSpeed);
            BreakShard();
            return true;
        }

        /// <summary>
        /// Deterministic random roll [0, 1). Uses per-instance _rng seeded from
        /// factory's deterministic Seed — identical results host↔client.
        /// </summary>
        private float DeterministicRoll() => (float)_rng.NextDouble();

        //  SPARKS

        private void SpawnSparks(Vector2 pos, Vector2 normal, bool isRicochet)
        {
            int min = isRicochet
                ? ShrapnelConfig.RicochetSparksMin.Value
                : ShrapnelConfig.MetalImpactSparksMin.Value;
            int max = isRicochet
                ? ShrapnelConfig.RicochetSparksMax.Value
                : ShrapnelConfig.MetalImpactSparksMax.Value;

            int count = _rng.Range(min, max);
            Vector2 baseDir = isRicochet
                ? Vector2.Reflect(-rb.velocity.normalized, normal)
                : normal;

            for (int i = 0; i < count; i++)
            {
                Vector2 sp = pos + _rng.InsideUnitCircle() * 0.1f;
                float cv = _rng.NextFloat();
                Color sc = new Color(
                    1f,
                    Mathf.Lerp(0.5f, 0.95f, cv),
                    Mathf.Lerp(0.1f, 0.5f, cv));

                var vis = new VisualParticleParams(_rng.Range(0.02f, 0.06f), sc, 11,
                    ShrapnelVisuals.TriangleShape.Needle);
                var em = new EmissionParams(
                    new Color(3f, Mathf.Lerp(1.5f, 2.5f, cv), 0.5f));
                float spread = _rng.Range(-60f, 60f) * Mathf.Deg2Rad;
                Vector2 sd = MathHelper.RotateDirection(baseDir, spread);
                var spark = new SparkParams(sd, _rng.Range(3f, 10f),
                    _rng.Range(0.08f, 0.25f));

                ParticleHelper.SpawnSparkUnlit("Spark", sp, vis, spark, em);
            }

            if (isRicochet) SpawnRicochetDebris(pos, normal);
        }

        private void SpawnRicochetDebris(Vector2 pos, Vector2 normal)
        {
            int count = _rng.Range(
                ShrapnelConfig.RicochetDebrisMin.Value,
                ShrapnelConfig.RicochetDebrisMax.Value);

            for (int i = 0; i < count; i++)
            {
                float gray = _rng.Range(0.3f, 0.5f);
                var vis = new VisualParticleParams(
                    _rng.Range(0.03f, 0.08f),
                    new Color(gray, gray, gray, 0.9f), 10,
                    ShrapnelVisuals.TriangleShape.Chunk);

                float angle = _rng.Range(-70f, 70f) * Mathf.Deg2Rad;
                Vector2 dir = MathHelper.RotateDirection(normal, angle);
                var phy = new AshPhysicsParams(
                    dir * _rng.Range(2f, 6f),
                    _rng.Range(0.3f, 0.8f), 1.2f, 0.3f, 0.2f, 1f);

                ParticleHelper.SpawnLit("RicoDebris",
                    pos + _rng.InsideUnitCircle() * 0.08f,
                    vis, phy, _rng.Range(0f, 100f));
            }
        }

        //  LIMB HIT

        private void HitLimb(Limb limb)
        {
            if (limb.dismembered) { Destroy(gameObject); return; }

            if (Weight == ShrapnelWeight.Micro)
            { HitLimbMicro(limb); return; }

            float armor = limb.GetArmorReduction();
            float decayMult = DamageDecayMultiplier;
            float dmg = Damage * decayMult / armor;
            float bleed = BleedAmount * decayMult / armor;

            limb.skinHealth -= dmg * 0.7f;
            limb.muscleHealth -= dmg;
            limb.bleedAmount += bleed;

            float armorWear;
            switch (Weight)
            {
                case ShrapnelWeight.Hot: armorWear = 0.005f; break;
                case ShrapnelWeight.Medium: armorWear = 0.01f; break;
                case ShrapnelWeight.Heavy: armorWear = 0.02f; break;
                case ShrapnelWeight.Massive: armorWear = 0.05f; break;
                default: armorWear = 0.01f; break;
            }
            limb.DamageWearables(armorWear);

            float embedChance;
            switch (Weight)
            {
                case ShrapnelWeight.Hot: embedChance = 0.15f; break;
                case ShrapnelWeight.Medium: embedChance = 0.40f; break;
                case ShrapnelWeight.Heavy: embedChance = 0.70f; break;
                case ShrapnelWeight.Massive: embedChance = 0.90f; break;
                default: embedChance = 0.30f; break;
            }
            embedChance *= decayMult;

            if (DeterministicRoll() < embedChance / (armor * armor)
                && DeterministicRoll() > 0.2f)
                limb.shrapnel++;

            if (Weight == ShrapnelWeight.Massive)
            {
                limb.BreakBone();
            }
            else if (Weight == ShrapnelWeight.Heavy)
            {
                float bc = Type == ShrapnelType.HeavyMetal ? 0.15f : 0.08f;
                if (DeterministicRoll() < bc / armor)
                    limb.BreakBone();
            }

            if (limb.isVital && Weight != ShrapnelWeight.Hot)
            {
                float ic = Weight == ShrapnelWeight.Massive ? 0.6f
                    : (Weight == ShrapnelWeight.Heavy ? 0.3f : 0.15f);
                ic *= decayMult;
                if (DeterministicRoll() < ic / armor)
                    limb.body.internalBleeding += dmg * 0.3f;
            }

            if (limb.isHead)
            {
                limb.body.consciousness -= dmg * 2f;
                if ((Weight == ShrapnelWeight.Heavy || Weight == ShrapnelWeight.Massive)
                    && DeterministicRoll() < 0.2f * decayMult / armor)
                    limb.body.brainHealth -= dmg * 0.5f;
                if (Weight == ShrapnelWeight.Massive
                    && DeterministicRoll() < 0.3f * decayMult / armor)
                    limb.body.Disfigure();
            }

            limb.body.shock = Mathf.Max(limb.body.shock, Damage * decayMult * 2f);
            limb.body.adrenaline = Mathf.Max(limb.body.adrenaline,
                20f + Damage * decayMult);
            limb.body.DoGoreSound();
            limb.body.talker.Talk(Locale.GetCharacter("loud"), null, false, false);

            float ragdollThresh = state == State.Flying ? 15f : 25f;
            if (Weight == ShrapnelWeight.Heavy
                || Weight == ShrapnelWeight.Massive
                || Damage * decayMult > ragdollThresh)
            {
                Vector2 kd = (state == State.Flying
                    && rb != null && !rb.isKinematic)
                    ? rb.velocity.normalized
                    : Vector2.up;
                limb.body.lastTimeStepVelocity = kd
                    * (Weight == ShrapnelWeight.Massive ? 10f : 5f)
                    * decayMult;
                limb.body.Ragdoll();
            }

            ApplyWoundVisuals(limb);
            Destroy(gameObject);
        }

        private void HitLimbMicro(Limb limb)
        {
            float armor = limb.GetArmorReduction();
            float decayMult = DamageDecayMultiplier;

            float dmg = _rng.Range(
                ShrapnelConfig.MicroDamageMin.Value,
                ShrapnelConfig.MicroDamageMax.Value) * decayMult / armor;

            limb.skinHealth -= dmg;
            limb.bleedAmount += _rng.Range(
                ShrapnelConfig.MicroBleedMin.Value,
                ShrapnelConfig.MicroBleedMax.Value) * decayMult / armor;

            limb.DamageWearables(0.001f);

            limb.body.shock = Mathf.Max(limb.body.shock,
                dmg * ShrapnelConfig.MicroShockMultiplier.Value);
            limb.body.adrenaline = Mathf.Max(limb.body.adrenaline,
                ShrapnelConfig.MicroAdrenalineBase.Value + dmg * 0.3f);
            limb.body.DoGoreSound();

            Destroy(gameObject);
        }

        private void ApplyWoundVisuals(Limb limb)
        {
            try
            {
                if (ShrapnelFactory.WoundSprite != null)
                    limb.CreateTemporarySprite(ShrapnelFactory.WoundSprite,
                        0f, null, false, 600f, (Limb x) => !x.hasShrapnel);
                if (ShrapnelFactory.WoundPanel != null)
                    WoundView.view.AddImageToLimb(limb, ShrapnelFactory.WoundPanel,
                        false, (Limb x) => !x.hasShrapnel || x.dismembered);
            }
            catch (Exception e)
            {
                Console.Error($"[Shrapnel] Wound: {e.Message}");
            }
        }

        //  STATE TRANSITIONS

        private void BecomeStuck(Vector2Int blockPos, Vector2 hitPoint)
        {
            state = State.Stuck;
            stuckTimer = 0f;
            _outlineApplied = false;
            _originalScale = _transform.localScale;

            if (rb != null && !rb.isKinematic
                && rb.bodyType != RigidbodyType2D.Static)
            {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }
            rb.isKinematic = true;
            _transform.position = (Vector2)hitPoint;

            if (trail != null) trail.enabled = false;
            ClearHeatAndEmission();
        }

        private void BecomeDebris()
        {
            state = State.Debris;
            _outlineApplied = false;
            _originalScale = _transform.localScale;

            if (rb != null && !rb.isKinematic
                && rb.bodyType != RigidbodyType2D.Static)
            {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }
            rb.isKinematic = true;

            if (trail != null) trail.enabled = false;
            if (_col != null) _col.isTrigger = true;
            ClearHeatAndEmission();

            debrisTimer = 0f;
            switch (Type)
            {
                case ShrapnelType.Metal:
                    debrisLifetime = ShrapnelConfig.DebrisLifetimeMetal.Value; break;
                case ShrapnelType.HeavyMetal:
                    debrisLifetime = ShrapnelConfig.DebrisLifetimeHeavyMetal.Value; break;
                case ShrapnelType.Stone:
                    debrisLifetime = ShrapnelConfig.DebrisLifetimeStone.Value; break;
                case ShrapnelType.Wood:
                    debrisLifetime = ShrapnelConfig.DebrisLifetimeWood.Value; break;
                case ShrapnelType.Electronic:
                    debrisLifetime = ShrapnelConfig.DebrisLifetimeElectronic.Value; break;
                default:
                    debrisLifetime = 300f; break;
            }
        }

        //  TRIGGER (stepping on debris — players only)
        //
        //  BUG FIX: Previously called BreakShard() for ANY non-Body contact,
        //  including other flying shrapnel. During explosions with 50+ shrapnel,
        //  flying fragments pass through debris trigger volumes constantly,
        //  causing chain destruction. From logs: 87 of 92 shards were destroyed
        //  this way. Client mirrors vanished mid-flight because the server
        //  destroyed them before any REST state transition could be sent.
        //
        //  NOW: Only reacts to Body (player/npc) contacts. Other shrapnel,
        //  particles, and miscellaneous colliders are silently ignored.

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (state != State.Debris && state != State.Stuck) return;

            // WHY: return instead of BreakShard() for non-Body contacts.
            // Previously: if (!TryGetComponent(out Body body)) { BreakShard(); return; }
            // This destroyed debris when other shrapnel flew through it.
            if (!other.TryGetComponent(out Body body)) return;

            if (state != State.Debris) return;
            if (_submerged) return;

            bool isStanding = body.transform.position.y > _transform.position.y;
            if (isStanding)
            {
                Item footwear = body.GetWearableBySlotID("feet");
                if (footwear != null)
                {
                    footwear.SetCondition(footwear.condition
                        - 0.05f * footwear.Stats.wearableHitDurabilityLossMultiplier);
                    BreakShard();
                    return;
                }
            }

            Limb target = FindClosestUndamagedLimb(body);
            if (target == null) { BreakShard(); return; }

            float armor = target.GetArmorReduction();
            float decay = DamageDecayMultiplier;

            target.skinHealth -= _rng.Range(15f, 35f) * decay / armor;
            target.muscleHealth -= _rng.Range(5f, 15f) * decay / armor;
            target.bleedAmount += _rng.Range(3f, 12f) * decay / armor;
            target.pain += 50f * decay / armor;
            target.shrapnel++;
            target.DamageWearables(0.01f);

            body.adrenaline = Mathf.Max(body.adrenaline, 40f);
            body.DoGoreSound();
            body.talker.Talk(
                Locale.GetCharacter("steponglass"), null, false, false);
            ApplyWoundVisuals(target);
            BreakShard();
        }

        private Limb FindClosestUndamagedLimb(Body body)
        {
            if (body.limbs == null || body.limbs.Length == 0) return null;
            Vector2 myPos = (Vector2)_transform.position;
            Limb closest = null;
            float closestDist = float.MaxValue;

            for (int i = 0; i < body.limbs.Length; i++)
            {
                Limb l = body.limbs[i];
                if (l == null || l.dismembered) continue;
                float d = ((Vector2)l.transform.position - myPos).sqrMagnitude;
                if (d < closestDist) { closestDist = d; closest = l; }
            }
            return closest;
        }

        private void BreakShard()
        {
            try
            {
                Sound.Play("glassshard", _transform.position,
                    false, true, null, 1f, 1f, false, false);
            }
            catch { }
            Destroy(gameObject);
        }

        //  HEAT SYSTEM

        private void TickHeat(float dt)
        {
            if (Heat <= 0f) return;

            float cool = HeatCoolRate * dt;
            if (WorldGeneration.world != null
                && WorldGeneration.world.ambientTemperature < 5f)
                cool *= 2f;

            Heat = Mathf.Max(Heat - cool, 0f);
            sr.color = Color.Lerp(coldColor, ShrapnelVisuals.GetHotColor(), Heat);

            if (Mathf.Abs(Heat - lastEmissionHeat) > 0.05f)
            {
                lastEmissionHeat = Heat;
                UpdateEmission();
            }

            if (Heat <= 0f) ClearHeatAndEmission();

            if (!cooledInWater && Heat > 0.1f)
            {
                try
                {
                    Vector2Int wPos = WorldGeneration.world.WorldToBlockPos(
                        _transform.position);
                    if (FluidManager.main.WaterInfo(wPos).Item1 > 0f)
                    {
                        cooledInWater = true;
                        _submerged = true;
                        ClearHeatAndEmission();
                        Sound.Play("fizz", _transform.position,
                            false, true, null, 1f, 1f, false, false);
                        SpawnSteamPuff();
                    }
                }
                catch { }
            }
        }

        private void SpawnSteamPuff()
        {
            int count = _rng.Range(3, 6);
            for (int i = 0; i < count; i++)
            {
                float gray = _rng.Range(0.8f, 1f);
                var vis = new VisualParticleParams(
                    _rng.Range(0.03f, 0.07f),
                    new Color(gray, gray, gray, 0.5f), 12,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = new AshPhysicsParams(
                    new Vector2(_rng.Range(-0.3f, 0.3f), _rng.Range(1f, 2.5f)),
                    _rng.Range(0.5f, 1.2f), -0.1f, 0.5f, 0.8f, 2f);
                ParticleHelper.SpawnUnlit("Steam",
                    (Vector2)_transform.position, vis, phy, _rng.Range(0f, 100f));
            }
        }

        private void UpdateEmission()
        {
            if (Heat > 0.01f)
                ParticleHelper.ApplyEmission(sr,
                    ShrapnelVisuals.GetHotColor() * Heat * 1.3f);
            else
                ClearEmission();
        }

        private void ClearHeatAndEmission()
        {
            Heat = 0f;
            sr.color = coldColor;
            ClearEmission();
        }

        private void ClearEmission()
        {
            ParticleHelper.ClearEmission(sr);
            lastEmissionHeat = 0f;
        }
    }
}