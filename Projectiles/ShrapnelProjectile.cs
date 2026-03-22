using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod.Projectiles
{
    /// <summary>
    /// Main shrapnel component. FSM: Flying → Stuck / Debris.
    ///
    /// REFACTORED:
    ///   • Damage logic extracted to ShrapnelDamage (static, pure functions)
    ///   • Weight data from ShrapnelWeightData table (no switch duplication)
    ///   • Trail config from TrailConfig (shared with client shards)
    ///   • Break params from ShrapnelDamage.GetBreakParams
    ///
    /// MULTIPLAYER:
    ///   Server: IsServerAuthoritative=true (default). Full damage, block breaking, net sync.
    ///   Client: IsServerAuthoritative=false. Real physics for bouncing, all damage gated.
    /// </summary>
    public sealed class ShrapnelProjectile : MonoBehaviour
    {
        public enum ShrapnelType { Metal, Stone, Wood, Electronic, HeavyMetal }
        private enum State { Flying, Stuck, Debris }

        /// <summary>
        /// External state enum for net sync. Maps to internal State.
        /// </summary>
        public enum ExternalState { Flying = 0, Stuck = 1, Debris = 2 }

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
        private const float HeatCoolRate = ShrapnelVisuals.HeatCoolRate;
        private const float HotThreshold = ShrapnelVisuals.HotThreshold;
        private const float OutlineScaleMultiplier = ShrapnelVisuals.OutlineScale;
        private const float ClientFadeDuration = 0.15f;

        //  PUBLIC FIELDS

        public ShrapnelType Type;
        public ShrapnelWeight Weight;
        public float Damage;
        public float BleedAmount;
        public float Heat = 1f;
        public bool HasTrail;
        public bool CanBreak = true;
        public int Seed;
        [NonSerialized] public ushort NetSyncId;
        [NonSerialized] public bool IsServerAuthoritative = true;
        public int CurrentState => (int)state;

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

        private System.Random _rng;

        // Client fade-out state
        private bool _clientFading;
        private float _clientFadeTimer;
        private Vector3 _clientFadeScale;

        //  PROPERTIES

        private float NormalizedLifetime
        {
            get
            {
                if (state == State.Stuck)
                    return 1f - Mathf.Clamp01(stuckTimer / ShrapnelConfig.StuckLifetime.Value);
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
            if (sr == null) return;

            // PERF: Stagger material self-heal check every 60 frames
            if (Time.frameCount % 60 == frameSlot % 60)
                TryHealMaterials();

            if (_clientFading)
            {
                UpdateClientFade();
                return;
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
            if (sr != null && lastEmissionHeat > 0f)
                ParticleHelper.ClearEmission(sr);
            if (IsServerAuthoritative && NetSyncId != 0)
                Net.ShrapnelNetSync.ServerUnregister(NetSyncId);
            if (!IsServerAuthoritative && NetSyncId != 0)
                Net.ShrapnelNetSync.NotifyClientShardDestroyed(NetSyncId);
        }

        //  MATERIAL SELF-HEAL (staggered)

        private void TryHealMaterials()
        {
            if (sr.sharedMaterial != null && sr.sharedMaterial.shader == null)
            {
                sr.sharedMaterial = ShrapnelFactory.SelectMaterial(Heat);
                if (Heat > 0.01f) UpdateEmission();
            }
            if (trail != null && trail.sharedMaterial != null && trail.sharedMaterial.shader == null)
                trail.sharedMaterial = ShrapnelVisuals.TrailMaterial;
        }

        //  CLIENT FADE

        private void UpdateClientFade()
        {
            _clientFadeTimer += Time.deltaTime;
            float t = Mathf.Clamp01(_clientFadeTimer / ClientFadeDuration);

            _transform.localScale = _clientFadeScale * Mathf.Max(1f - t, 0.01f);
            if (sr != null)
            {
                Color c = sr.color;
                c.a *= 1f - t;
                sr.color = c;
            }

            if (t >= 1f)
            {
                if (!IsServerAuthoritative && NetSyncId != 0)
                    Net.ShrapnelNetSync.NotifyClientShardDestroyed(NetSyncId);
                Destroy(gameObject);
            }
        }

        //  FSM UPDATES

        private void UpdateFlying(int frame)
        {
            if (IsServerAuthoritative && _physicsDelay > 0f)
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
            if (ApplyVisualDecay()) return;

            if (frame % 5 == frameSlot % 5) PulseOutline();
            if (frame % 30 == frameSlot) CheckSubmerged();
            if (frame % 10 == frameSlot) CheckSupportAndFall();
        }

        //  VISUAL DECAY

        private bool ApplyVisualDecay()
        {
            float t = NormalizedLifetime;
            if (t > 0.3f) return false;

            if (t <= 0.02f)
            { Destroy(gameObject); return true; }

            float decayT = t / 0.3f;
            float decayFactor = decayT * decayT * (3f - 2f * decayT);

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

            // WHY: Subtle glow emission makes debris visible in dark caves.
            // Without this, fragments blend into dark ground tiles.
            ParticleHelper.ApplyEmission(_outlineSr,
                new Color(0.4f, 0.05f, 0.02f));
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
            if (_outlineSr.sharedMaterial != null && _outlineSr.sharedMaterial.shader == null)
                _outlineSr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
            if (_submerged) { DestroyOutline(); _outlineApplied = false; return; }
            _outlineSr.color = ShrapnelVisuals.GetOutlineColor(Time.time, frameSlot * 0.628f);
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

            // WHY: Use weight data table instead of switch
            rb.gravityScale = ShrapnelWeightData.Get(Weight).GravityScale;

            if (_col != null) _col.isTrigger = false;
            DestroyOutline();
        }

        //  COLLISION

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (state == State.Debris || state == State.Stuck)
            {
                if (IsServerAuthoritative
                    && collision.relativeVelocity.magnitude > 5f
                    && !collision.collider.TryGetComponent<ShrapnelProjectile>(out _))
                    BreakShard();
                return;
            }
            if (state != State.Flying) return;
            if (!IsServerAuthoritative) return;

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
            ShrapnelDamage.ApplyToBuildingEntity(entity, Damage, DamageDecayMultiplier);
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

            // Spawn material-appropriate debris on every block hit
            SpawnBlockImpactDebris(hitPoint, hitNormal, impactSpeed);

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
                if (kineticDamage > info.health && _blocksDestroyed < MaxBlocksToDestroy)
                {
                    if (ShrapnelFactory.TryDamageSlot())
                    {
                        WorldGeneration.world.DamageBlock(
                            hitPoint - hitNormal * 0.1f, kineticDamage, true, false);
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

        /// <summary>
        /// Spawns material-appropriate debris particles on shrapnel→block collision.
        /// Metal = few sparks. Soft blocks = more dust/chunks.
        /// Uses BlockClassifier for consistent material detection.
        /// </summary>
        private void SpawnBlockImpactDebris(Vector2 hitPoint, Vector2 hitNormal,
            float impactSpeed)
        {
            if (!AshParticlePoolManager.EnsureReady()) return;
            if (impactSpeed < 3f) return;

            BlockInfo info = null;
            try
            {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(
                    hitPoint - hitNormal * 0.1f);
                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                if (blockId != 0)
                    info = WorldGeneration.world.GetBlockInfo(blockId);
            }
            catch { /* Out of bounds */ }

            MaterialCategory cat = BlockClassifier.Classify(info);
            float dustMult = BlockClassifier.GetDustMultiplier(cat);
            bool isMetal = cat == MaterialCategory.Metal || (info != null && info.metallic);

            // KISS: energyScale 0-1 based on speed
            float energy = Mathf.Clamp01(impactSpeed / 25f);
            float softBonus = isMetal ? 0.5f : dustMult;

            int chunkCount = Mathf.Clamp(Mathf.RoundToInt(2 * softBonus * energy), 1, 5);
            int dustCount = Mathf.Clamp(Mathf.RoundToInt(3 * softBonus * energy), 1, 8);

            for (int i = 0; i < chunkCount; i++)
            {
                Vector2 pos = hitPoint + _rng.InsideUnitCircle() * 0.12f;
                Color col = BlockClassifier.GetColorWithAlpha(cat, _rng, 0.85f);

                Vector2 tangent = new(-hitNormal.y, hitNormal.x);
                Vector2 vel = hitNormal * _rng.Range(1.5f, 3.5f) * energy
                            + tangent * _rng.Range(-1.5f, 1.5f);

                var vis = new VisualParticleParams(
                    _rng.Range(0.03f, 0.08f), col, 11,
                    (ShrapnelVisuals.TriangleShape)_rng.Next(0, 6));
                var phy = AshPhysicsParams.Chunk(vel, _rng.Range(1f, 3f), _rng);
                ParticleHelper.SpawnLit(pos, vis, phy, _rng.Range(0f, 100f));
            }

            for (int i = 0; i < dustCount; i++)
            {
                Vector2 pos = hitPoint + _rng.InsideUnitCircle() * 0.2f;
                Color baseCol = BlockClassifier.GetColor(cat, _rng);
                Color dustCol = Color.Lerp(baseCol, new Color(0.5f, 0.5f, 0.5f), 0.3f);
                dustCol.a = 0.4f;

                Vector2 tangent = new(-hitNormal.y, hitNormal.x);
                Vector2 vel = hitNormal * _rng.Range(0.3f, 1.5f) * energy
                            + tangent * _rng.Range(-0.6f, 0.6f);

                var vis = new VisualParticleParams(
                    _rng.Range(0.02f, 0.05f), dustCol, 10,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.Dust(vel, _rng.Range(0.6f, 2f), _rng);
                ParticleHelper.SpawnLit(pos, vis, phy, _rng.Range(0f, 100f));
            }

            // Metal: bright sparks
            if (isMetal && impactSpeed > 6f)
            {
                int sparkCount = Mathf.Clamp(Mathf.RoundToInt(2 * energy), 1, 4);
                for (int i = 0; i < sparkCount; i++)
                {
                    Vector2 pos = hitPoint + _rng.InsideUnitCircle() * 0.05f;
                    float heat = _rng.Range(0.6f, 1f);
                    Color col = new(1f, Mathf.Lerp(0.5f, 0.9f, heat),
                        Mathf.Lerp(0.1f, 0.4f, heat));

                    var vis = new VisualParticleParams(
                        _rng.Range(0.01f, 0.025f), col, 13,
                        ShrapnelVisuals.TriangleShape.Needle);
                    float angle = _rng.Range(-55f, 55f) * Mathf.Deg2Rad;
                    Vector2 dir = MathHelper.RotateDirection(hitNormal, angle);
                    var spark = new SparkParams(dir,
                        _rng.Range(4f, 10f) * energy, _rng.Range(0.05f, 0.12f));
                    ParticleHelper.SpawnSpark(pos, vis, spark);
                }
            }
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

            float dot = Mathf.Abs(Vector2.Dot(rb.velocity.normalized, hitNormal));
            if (Mathf.Asin(dot) * Mathf.Rad2Deg > RicochetMaxAngleDeg) return false;

            rb.velocity = Vector2.Reflect(rb.velocity, hitNormal) * RicochetSpeedRetention;
            _ricochetCount++;
            SpawnSparks(hitPoint, hitNormal, true);
            return true;
        }

        private bool TryBreak(float impactSpeed, Vector2 hitPoint, Vector2 hitNormal)
        {
            if (Weight == ShrapnelWeight.Micro) return false;

            if (!ShrapnelDamage.GetBreakParams(Weight, out float breakThreshold, out float breakChance))
                return false;

            if (impactSpeed < breakThreshold) return false;
            if ((float)_rng.NextDouble() > breakChance) return false;

            ShrapnelFactory.SpawnBreakFragments(
                hitPoint, hitNormal, _transform.localScale.x, Type, Weight, impactSpeed);
            BreakShard();
            return true;
        }

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
                Color sc = new(1f, Mathf.Lerp(0.5f, 0.95f, cv), Mathf.Lerp(0.1f, 0.5f, cv));

                var vis = new VisualParticleParams(_rng.Range(0.02f, 0.06f), sc, 11,
                    ShrapnelVisuals.TriangleShape.Needle);
                float spread = _rng.Range(-60f, 60f) * Mathf.Deg2Rad;
                Vector2 sd = MathHelper.RotateDirection(baseDir, spread);
                var spark = new SparkParams(sd, _rng.Range(3f, 10f),
                    _rng.Range(0.08f, 0.25f));

                ParticleHelper.SpawnSparkUnlit("Spark", sp, vis, spark,
                    new EmissionParams(new Color(3f, Mathf.Lerp(1.5f, 2.5f, cv), 0.5f)));
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

        //  LIMB HIT — delegates to ShrapnelDamage

        private void HitLimb(Limb limb)
        {
            if (limb.dismembered) { Destroy(gameObject); return; }

            if (Weight == ShrapnelWeight.Micro)
            {
                ShrapnelDamage.ApplyMicroToLimb(limb, DamageDecayMultiplier, _rng);
                Destroy(gameObject);
                return;
            }

            ShrapnelDamage.ApplyToLimb(
                limb, Weight, Type, Damage, BleedAmount,
                DamageDecayMultiplier, _rng,
                state == State.Flying, rb);

            ShrapnelDamage.ApplyWoundVisuals(limb);
            Destroy(gameObject);
        }

        //  STATE TRANSITIONS

        private void BecomeStuck(Vector2Int blockPos, Vector2 hitPoint)
        {
            state = State.Stuck;
            stuckTimer = 0f;
            _outlineApplied = false;
            _originalScale = _transform.localScale;

            FreezeRigidbody();  // DEDUPLICATED
            _transform.position = hitPoint;

            if (trail != null) trail.enabled = false;
            ClearHeatAndEmission();
        }

        private void BecomeDebris()
        {
            state = State.Debris;
            _outlineApplied = false;
            _originalScale = _transform.localScale;

            FreezeRigidbody();

            if (trail != null) trail.enabled = false;
            if (_col != null) _col.isTrigger = true;
            ClearHeatAndEmission();

            debrisTimer = 0f;

            float baseLifetime = Type switch
            {
                ShrapnelType.Metal => ShrapnelConfig.DebrisLifetimeMetal.Value,
                ShrapnelType.HeavyMetal => ShrapnelConfig.DebrisLifetimeHeavyMetal.Value,
                ShrapnelType.Stone => ShrapnelConfig.DebrisLifetimeStone.Value,
                ShrapnelType.Wood => ShrapnelConfig.DebrisLifetimeWood.Value,
                ShrapnelType.Electronic => ShrapnelConfig.DebrisLifetimeElectronic.Value,
                _ => 300f,
            };

            // WHY: Micro/Hot fragments vanish fast — too small to persist.
            // Heavy/Massive stay full duration. Uses lookup table.
            float weightMult = ShrapnelVisuals.DebrisLifetimeMultiplier[(int)Weight];
            debrisLifetime = baseLifetime * weightMult;
        }

        //  TRIGGER (stepping on debris — server only)

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (state != State.Debris && state != State.Stuck) return;
            if (!IsServerAuthoritative) return;
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
            ShrapnelDamage.ApplyStepOnDamage(target, DamageDecayMultiplier, armor, _rng);

            body.adrenaline = Mathf.Max(body.adrenaline, 40f);
            body.DoGoreSound();
            body.talker.Talk(Locale.GetCharacter("steponglass"), null, false, false);
            ShrapnelDamage.ApplyWoundVisuals(target);
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
            if (!IsServerAuthoritative) return;
            try { Sound.Play("glassshard", _transform.position, false, true, null, 1f, 1f, false, false); }
            catch { }
            Destroy(gameObject);
        }

        //  PUBLIC API: NET SYNC

        /// <summary>
        /// Forces internal state machine to a specific state.
        /// Used by ShrapnelNetSync for client-side REST corrections.
        /// </summary>
        public void ForceToState(ExternalState externalState, Vector2 position)
        {
            _transform.position = position;

            switch (externalState)
            {
                case ExternalState.Stuck:
                    state = State.Stuck;
                    stuckTimer = 0f;
                    _outlineApplied = false;
                    _originalScale = _transform.localScale;
                    FreezeRigidbody();  // DEDUPLICATED
                    if (trail != null) trail.enabled = false;
                    ClearHeatAndEmission();
                    break;

                case ExternalState.Debris:
                    state = State.Debris;
                    _outlineApplied = false;
                    _originalScale = _transform.localScale;
                    FreezeRigidbody();  // DEDUPLICATED
                    if (trail != null) trail.enabled = false;
                    if (_col != null) _col.isTrigger = true;
                    ClearHeatAndEmission();
                    debrisTimer = 0f;
                    debrisLifetime = Type switch
                    {
                        ShrapnelType.Metal => ShrapnelConfig.DebrisLifetimeMetal.Value,
                        ShrapnelType.HeavyMetal => ShrapnelConfig.DebrisLifetimeHeavyMetal.Value,
                        ShrapnelType.Stone => ShrapnelConfig.DebrisLifetimeStone.Value,
                        ShrapnelType.Wood => ShrapnelConfig.DebrisLifetimeWood.Value,
                        ShrapnelType.Electronic => ShrapnelConfig.DebrisLifetimeElectronic.Value,
                        _ => 300f,
                    };
                    break;

                case ExternalState.Flying:
                    state = State.Flying;
                    _outlineApplied = false;
                    lifeTimer = 0f;
                    _originalScale = _transform.localScale;
                    if (rb != null) rb.isKinematic = false;
                    if (_col != null) _col.isTrigger = false;
                    DestroyOutline();
                    break;
            }
        }

        /// <summary>
        /// Begins graceful 150ms shrink+fade for a client flying shard.
        /// </summary>
        public void BeginClientFadeOut()
        {
            if (_clientFading) return;
            _clientFading = true;
            _clientFadeTimer = 0f;
            _clientFadeScale = _transform.localScale;
            if (trail != null && trail.enabled)
                trail.enabled = false;
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

            if (!cooledInWater && Heat > 0.1f)
            {
                try
                {
                    Vector2Int wPos = WorldGeneration.world.WorldToBlockPos(_transform.position);
                    if (FluidManager.main.WaterInfo(wPos).Item1 > 0f)
                    {
                        cooledInWater = true;
                        _submerged = true;
                        ClearHeatAndEmission();
                        Sound.Play("fizz", _transform.position, false, true, null, 1f, 1f, false, false);
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

        /// <summary>Freezes rigidbody motion. Single source of truth.</summary>
        private void FreezeRigidbody()
        {
            if (rb != null && !rb.isKinematic && rb.bodyType != RigidbodyType2D.Static)
            {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }
            if (rb != null)
                rb.isKinematic = true;
        }
    }
}