using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Net
{
    /// <summary>
    /// Lightweight visual mirror of a server-side ShrapnelProjectile.
    /// No Rigidbody2D, no Collider2D, no damage — purely cosmetic.
    ///
    /// SNAPSHOT-CORRECTED LOCAL SIMULATION:
    ///   Local physics (gravity + drag) provides smooth inter-frame movement.
    ///   Server snapshots correct position every 100ms, preventing drift.
    ///   Velocity is also corrected to converge future predictions.
    ///   Result: smooth flight + bounded drift (typically less than 2 units).
    ///
    /// WHY NOT PURE LOCAL SIMULATION:
    ///   Server shards collide with terrain (bounce, stick, ricochet).
    ///   Client has no collision detection — shards fly through walls.
    ///   Without correction, drift grows unboundedly (observed: 260+ units).
    ///
    /// WHY NOT PURE INTERPOLATION:
    ///   Snapshots arrive at 10Hz (100ms intervals). Pure interpolation
    ///   between snapshots adds 100ms visual latency and cannot show the
    ///   initial explosion burst (no snapshots during first 100ms).
    ///   Hybrid approach: local physics for smoothness, snapshots for accuracy.
    ///
    /// STATES:
    ///   LocalFlight:  local physics + snapshot correction, trail active
    ///   LandingBlend: smoothstep to server rest position (0.2s)
    ///   AtRest:       snapped to server position, outline, visual decay
    ///
    /// PERF LOD:
    ///   Distance: mirrors >50m from camera update every 3rd frame.
    ///   AtRest: mirrors update every 5th frame (0.5Hz outline pulse is plenty).
    ///   Fade: destroy messages use graceful 150ms shrink+fade, not instant vanish.
    ///
    /// TRAIL WIDTH (matches server ShrapnelFactory.ConfigureTrail exactly):
    ///   Hot:     startWidth = 0.06 × scale × 10, time=0.25s
    ///   Massive: startWidth = 0.12 × scale × 5,  time=0.40s
    ///   Default: startWidth = 0.04 × scale × 10, time=0.15s
    /// </summary>
    public sealed class ClientMirrorShrapnel : MonoBehaviour
    {
        //  PUBLIC STATE

        /// <summary>Network sync ID matching ShrapnelProjectile.NetSyncId on server.</summary>
        public ushort NetId { get; private set; }

        /// <summary>True when this mirror is in at-rest state.</summary>
        public bool IsAtRest => _state == MirrorState.AtRest;

        //  STATE ENUM

        private enum MirrorState
        {
            LocalFlight,
            LandingBlend,
            AtRest
        }

        //  COMPONENTS

        private SpriteRenderer _sr;
        private TrailRenderer _trail;
        private Transform _transform;

        //  OUTLINE (only visible when at rest)

        private GameObject _outlineGo;
        private SpriteRenderer _outlineSr;

        //  IDENTITY

        private ShrapnelProjectile.ShrapnelType _type;
        private ShrapnelWeight _weight;

        //  LOCAL PHYSICS SIMULATION

        private Vector2 _simVelocity;
        private float _gravityScale;
        private float _drag;

        //  SNAPSHOT CORRECTION

        private Vector2 _lastServerPos;
        private Vector2 _serverVelocity;
        private float _timeSinceSnapshot;
        private bool _hasReceivedSnapshot;

        //  HEAT & EMISSION

        private float _heat;
        private Color _coldColor;
        private Color _hotColor;
        private float _lastEmissionHeat = -1f;

        //  STATE MANAGEMENT

        private MirrorState _state;
        private float _spawnTime;
        private bool _hasTrail;
        private bool _trailDisabled;
        private bool _wasEverFlying;
        private float _noUpdateTimer;

        //  DEFERRED REST (minimum flight visual duration)

        private bool _pendingRestTransition;
        private Vector2 _pendingRestPosition;
        private float _pendingRestRotation;

        //  LANDING BLEND

        private Vector2 _blendStartPos;
        private Vector2 _blendEndPos;
        private float _blendTimer;
        private float _blendStartRotZ;
        private float _blendEndRotZ;

        //  AT-REST VISUAL DECAY

        private float _restTimer;
        private Vector3 _originalScale;
        private float _debrisLifetime;

        //  IMPACT EFFECTS

        private System.Random _rng;

        //  DESTROY FADE

        private bool _fadingOut;
        private float _fadeTimer;
        private Vector3 _fadeStartScale;

        /// <summary>Duration of fade-out when destroy message arrives during flight.</summary>
        private const float DestroyFadeDuration = 0.15f;

        //  CAMERA CACHE (for distance LOD)

        private static Camera _cachedCamera;
        private static int _cameraCacheFrame = -1;
        private static Vector3 _cameraPos;

        /// <summary>
        /// PERF: Squared distance threshold for LOD.
        /// Mirrors beyond this distance update every 3rd frame.
        /// 50 units = enough to cover most visible screen area.
        /// </summary>
        private const float DistanceLodSqr = 50f * 50f;

        //  CONSTANTS

        private const float MinFlightVisualDuration = 0.4f;
        private const float LandingBlendDuration = 0.2f;
        private const float FlyingTimeout = 30f;
        private const float MinImpactSpeedForEffects = 2f;
        private const float MaxImpactSpeedForEffects = 60f;

        /// <summary>
        /// Seconds without ANY snapshot before a flying mirror begins fade-out.
        /// WHY: If server's MSG_DESTROY was lost, the mirror becomes orphaned.
        /// 5 seconds = 50 missed snapshots at 10Hz — virtually impossible under
        /// normal network conditions, safe threshold for leak protection.
        /// Triggers graceful fade instead of instant vanish (FlyingTimeout).
        /// </summary>
        private const float SnapshotStaleTimeout = 5f;

        /// <summary>
        /// Position correction rate (per second). Controls how aggressively
        /// local simulation is blended toward server position each frame.
        /// 12 = converges 95% within ~0.2s (2 snapshot intervals).
        /// </summary>
        private const float CorrectionRate = 12f;

        /// <summary>
        /// Velocity correction gain (1/s). Nudges simulated velocity toward
        /// reducing position error, preventing repeated drift in same direction.
        /// </summary>
        private const float VelocityCorrectionGain = 5f;

        /// <summary>
        /// Drift above this threshold triggers instant teleport to server position.
        /// Below this, smooth blending corrects the error over 2-3 snapshots.
        /// </summary>
        private const float TeleportThreshold = 5f;

        /// <summary>
        /// Maximum time to predict ahead from last snapshot.
        /// 0.3s = 3 snapshot intervals at 10Hz.
        /// </summary>
        private const float MaxPredictionTime = 0.3f;

        //  FACTORY

        /// <summary>
        /// Creates and configures a ClientMirrorShrapnel.
        /// Returns null if required assets are unavailable.
        /// </summary>
        /// <param name="netId">Network sync ID matching server ShrapnelProjectile.NetSyncId.</param>
        /// <param name="position">World position from server.</param>
        /// <param name="type">Shrapnel material type.</param>
        /// <param name="weight">Shrapnel weight class.</param>
        /// <param name="heat">Initial heat [0..1].</param>
        /// <param name="shape">Triangle shape for sprite selection.</param>
        /// <param name="scale">Uniform scale.</param>
        /// <param name="hasTrail">Whether to create a TrailRenderer.</param>
        /// <param name="atRest">If true, mirror starts in AtRest state immediately.</param>
        /// <param name="initialVelocity">Initial velocity for local flight simulation.</param>
        /// <param name="initialRotationZ">Initial Z rotation in degrees from server.</param>
        public static ClientMirrorShrapnel Create(
            ushort netId,
            Vector2 position,
            ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight weight,
            float heat,
            ShrapnelVisuals.TriangleShape shape,
            float scale,
            bool hasTrail,
            bool atRest = false,
            Vector2 initialVelocity = default,
            float initialRotationZ = 0f)
        {
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return null;

            Material mat = heat > ShrapnelVisuals.HotThreshold
                ? ShrapnelVisuals.UnlitMaterial
                : (ShrapnelVisuals.LitMaterial ?? ShrapnelVisuals.UnlitMaterial);
            if (mat == null) return null;

            var go = new GameObject($"ShrMirror_{netId}")
            {
                hideFlags = HideFlags.DontSave
            };
            go.transform.position = position;
            go.transform.localScale = Vector3.one * scale;
            // WHY: Apply server rotation on spawn. Previously always Quaternion.identity,
            // causing at-rest shards to face wrong direction. Flying shards override
            // rotation from velocity in UpdateLocalFlight after first frame, but the
            // initial visual is now correct. At-rest shards keep this rotation permanently.
            go.transform.rotation = Quaternion.Euler(0f, 0f, initialRotationZ);
            go.layer = 0;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = mat;

            Color coldColor = ShrapnelVisuals.GetColdColor(type);
            Color hotColor = ShrapnelVisuals.GetHotColor();
            sr.color = Color.Lerp(coldColor, hotColor, Mathf.Clamp01(heat));

            var mirror = go.AddComponent<ClientMirrorShrapnel>();
            mirror.NetId = netId;
            mirror._sr = sr;
            mirror._transform = go.transform;
            mirror._type = type;
            mirror._weight = weight;
            mirror._gravityScale = GravityScaleForWeight(weight);
            mirror._drag = DragForWeight(weight);
            mirror._heat = Mathf.Clamp01(heat);
            mirror._coldColor = coldColor;
            mirror._hotColor = hotColor;
            mirror._hasTrail = hasTrail;
            mirror._trailDisabled = false;
            mirror._noUpdateTimer = 0f;
            mirror._spawnTime = Time.time;
            mirror._pendingRestTransition = false;
            mirror._outlineGo = null;
            mirror._outlineSr = null;
            mirror._rng = new System.Random(netId * 397 + 42);
            mirror._debrisLifetime = DebrisLifetimeForType(type);
            mirror._lastServerPos = position;
            mirror._serverVelocity = Vector2.zero;
            mirror._timeSinceSnapshot = 0f;
            mirror._hasReceivedSnapshot = false;
            mirror._fadingOut = false;

            // WHY: Must set _originalScale for ALL paths. Previously only set in
            // ApplyRestState (landing blend completion). Flying mirrors and mirrors
            // created as atRest=true had _originalScale = (0,0,0) which caused
            // BeginFadeDestroy and UpdateAtRest decay to produce zero scale → invisible.
            mirror._originalScale = go.transform.localScale;

            if (atRest)
            {
                mirror._state = MirrorState.AtRest;
                mirror._wasEverFlying = false;
                mirror._simVelocity = Vector2.zero;
                mirror._heat = 0f;
                mirror._restTimer = 0f;
                sr.color = coldColor;
                mirror.CreateOutline();
            }
            else
            {
                mirror._state = MirrorState.LocalFlight;
                mirror._wasEverFlying = true;
                mirror._simVelocity = initialVelocity;

                if (hasTrail)
                    mirror.CreateTrail(scale);

                if (heat > ShrapnelVisuals.HotThreshold)
                {
                    ParticleHelper.ApplyEmission(sr, hotColor * heat * 1.3f);
                    mirror._lastEmissionHeat = heat;
                }
            }

            return mirror;
        }

        //  TRAIL (matches server ShrapnelFactory.ConfigureTrail)

        private void CreateTrail(float scale)
        {
            Material trailMat = ShrapnelVisuals.TrailMaterial;
            if (trailMat == null) return;

            _trail = gameObject.AddComponent<TrailRenderer>();
            _trail.sharedMaterial = trailMat;
            _trail.sortingOrder = 8;
            _trail.autodestruct = false;
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _trail.receiveShadows = false;
            _trail.numCapVertices = 1;
            _trail.endWidth = 0f;

            switch (_weight)
            {
                case ShrapnelWeight.Massive:
                    _trail.time = 0.4f;
                    _trail.startWidth = 0.12f * scale * 5f;
                    _trail.startColor = new Color(0.3f, 0.25f, 0.2f, 0.8f);
                    _trail.endColor = new Color(0.2f, 0.2f, 0.2f, 0f);
                    break;

                case ShrapnelWeight.Hot:
                    _trail.time = 0.25f;
                    _trail.startWidth = 0.06f * scale * 10f;
                    _trail.startColor = new Color(1f, 0.5f, 0.1f, 0.9f);
                    _trail.endColor = new Color(1f, 0.2f, 0f, 0f);
                    break;

                default:
                    _trail.time = 0.15f;
                    _trail.startWidth = 0.04f * scale * 10f;
                    _trail.startColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
                    _trail.endColor = new Color(0.4f, 0.4f, 0.4f, 0f);
                    break;
            }
        }

        //  OUTLINE

        private void CreateOutline()
        {
            if (_outlineGo != null) return;
            _outlineGo = new GameObject("Outline");
            _outlineGo.transform.SetParent(_transform, false);
            _outlineGo.transform.localScale = Vector3.one * ShrapnelVisuals.OutlineScale;
            _outlineSr = _outlineGo.AddComponent<SpriteRenderer>();
            _outlineSr.sprite = _sr.sprite;
            _outlineSr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
            _outlineSr.sortingOrder = _sr.sortingOrder - 1;
            _outlineSr.color = ShrapnelVisuals.GetOutlineBaseColor();
        }

        private void DestroyOutline()
        {
            if (_outlineGo != null)
            {
                Destroy(_outlineGo);
                _outlineGo = null;
                _outlineSr = null;
            }
        }

        //  WEIGHT LOOKUPS (must match ShrapnelFactory.ConfigureRigidbody)

        private static float GravityScaleForWeight(ShrapnelWeight w)
        {
            switch (w)
            {
                case ShrapnelWeight.Hot: return 0.3f;
                case ShrapnelWeight.Medium: return 0.15f;
                case ShrapnelWeight.Heavy: return 0.35f;
                case ShrapnelWeight.Massive: return 0.5f;
                case ShrapnelWeight.Micro: return 0.1f;
                default: return 0.25f;
            }
        }

        private static float DragForWeight(ShrapnelWeight w)
        {
            switch (w)
            {
                case ShrapnelWeight.Hot: return 0.4f;
                case ShrapnelWeight.Medium: return 0.2f;
                case ShrapnelWeight.Heavy: return 0.2f;
                case ShrapnelWeight.Massive: return 0.1f;
                case ShrapnelWeight.Micro: return 0.5f;
                default: return 0.3f;
            }
        }

        private static float DebrisLifetimeForType(ShrapnelProjectile.ShrapnelType type)
        {
            switch (type)
            {
                case ShrapnelProjectile.ShrapnelType.Metal: return ShrapnelConfig.DebrisLifetimeMetal.Value;
                case ShrapnelProjectile.ShrapnelType.HeavyMetal: return ShrapnelConfig.DebrisLifetimeHeavyMetal.Value;
                case ShrapnelProjectile.ShrapnelType.Stone: return ShrapnelConfig.DebrisLifetimeStone.Value;
                case ShrapnelProjectile.ShrapnelType.Wood: return ShrapnelConfig.DebrisLifetimeWood.Value;
                case ShrapnelProjectile.ShrapnelType.Electronic: return ShrapnelConfig.DebrisLifetimeElectronic.Value;
                default: return 300f;
            }
        }

        private float GetWeightImpactMultiplier()
        {
            switch (_weight)
            {
                case ShrapnelWeight.Micro: return 0.3f;
                case ShrapnelWeight.Hot: return 0.6f;
                case ShrapnelWeight.Medium: return 1.0f;
                case ShrapnelWeight.Heavy: return 1.5f;
                case ShrapnelWeight.Massive: return 2.2f;
                default: return 1.0f;
            }
        }

        //  STATE TRANSITIONS

        public void TransitionToRest(Vector2 finalPos, float rotationZ = 0f)
        {
            _noUpdateTimer = 0f;

            if (_state == MirrorState.AtRest)
            {
                _transform.position = finalPos;
                _transform.rotation = Quaternion.Euler(0f, 0f, rotationZ);
                return;
            }

            if (_state == MirrorState.LandingBlend)
            {
                _blendEndPos = finalPos;
                _pendingRestRotation = rotationZ;
                return;
            }

            float timeSinceSpawn = Time.time - _spawnTime;

            if (timeSinceSpawn < MinFlightVisualDuration && !_pendingRestTransition)
            {
                _pendingRestTransition = true;
                _pendingRestPosition = finalPos;
                _pendingRestRotation = rotationZ;
                return;
            }

            StartLandingBlend(finalPos, rotationZ);
        }

        public void TransitionToFlying(Vector2 currentPos)
        {
            _noUpdateTimer = 0f;
            _pendingRestTransition = false;
            _state = MirrorState.LocalFlight;
            _wasEverFlying = true;
            _transform.position = currentPos;
            _simVelocity = Vector2.zero;
            _lastServerPos = currentPos;
            _serverVelocity = Vector2.zero;
            _timeSinceSnapshot = 0f;
            _hasReceivedSnapshot = false;

            if (_hasTrail && _trail != null && _trailDisabled)
            {
                _trail.Clear();
                _trail.enabled = true;
                _trailDisabled = false;
            }

            DestroyOutline();
        }

        private void StartLandingBlend(Vector2 finalPos, float rotationZ)
        {
            _state = MirrorState.LandingBlend;
            _pendingRestTransition = false;
            _blendStartPos = (Vector2)_transform.position;
            _blendEndPos = finalPos;
            _blendStartRotZ = _transform.rotation.eulerAngles.z;
            _blendEndRotZ = rotationZ;
            _blendTimer = 0f;
        }

        private void ApplyRestState(Vector2 finalPos, float rotationZ = 0f)
        {
            float impactSpeed = _simVelocity.magnitude;
            float weightMult = GetWeightImpactMultiplier();
            float intensity = Mathf.Clamp01(
                (impactSpeed - MinImpactSpeedForEffects)
                / (MaxImpactSpeedForEffects - MinImpactSpeedForEffects)
                * weightMult);

            _state = MirrorState.AtRest;
            _transform.position = finalPos;
            _transform.rotation = Quaternion.Euler(0f, 0f, rotationZ);
            _simVelocity = Vector2.zero;
            _noUpdateTimer = 0f;
            _restTimer = 0f;
            _originalScale = _transform.localScale;

            if (_heat > 0f)
            {
                _heat = 0f;
                _sr.color = _coldColor;
                ParticleHelper.ClearEmission(_sr);
                _lastEmissionHeat = 0f;
            }

            if (_hasTrail && _trail != null && !_trailDisabled)
            {
                _trail.enabled = false;
                _trailDisabled = true;
            }

            CreateOutline();

            if (_wasEverFlying && intensity > 0.05f)
            {
                SpawnImpactSparks(finalPos, intensity);
                SpawnImpactDust(finalPos, intensity);
            }
        }

        //  NETWORK INPUT

        public void OnSnapshotReceived(Vector2 serverPos)
        {
            _noUpdateTimer = 0f;

            if (_state != MirrorState.LocalFlight) return;

            if (_hasReceivedSnapshot && _timeSinceSnapshot > 0.01f)
            {
                _serverVelocity = (serverPos - _lastServerPos) / _timeSinceSnapshot;

                float maxVel = 150f;
                if (_serverVelocity.sqrMagnitude > maxVel * maxVel)
                    _serverVelocity = _serverVelocity.normalized * maxVel;
            }

            _lastServerPos = serverPos;
            _timeSinceSnapshot = 0f;
            _hasReceivedSnapshot = true;
        }

        //  DESTROY FADE

        /// <summary>
        /// Begins graceful fade-out instead of instant destruction.
        /// WHY: Server destroys arrive while the mirror is still visually flying
        /// (client has no collision detection). Instant Destroy() causes jarring
        /// mid-flight vanish. Short fade makes it look like the fragment broke apart.
        /// </summary>
        public void BeginFadeDestroy()
        {
            if (_fadingOut) return;

            // At-rest mirrors can vanish instantly (already static, no visual jarring)
            if (_state == MirrorState.AtRest)
            {
                ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
                Destroy(gameObject);
                return;
            }

            _fadingOut = true;
            _fadeTimer = 0f;
            // WHY: Capture CURRENT scale, not _originalScale. Flying mirrors
            // may have _originalScale=(0,0,0) if they never transitioned to rest.
            // Using current scale ensures the fade starts from the visible size.
            _fadeStartScale = _transform.localScale;

            // Disable trail immediately for clean visual
            if (_hasTrail && _trail != null && !_trailDisabled)
            {
                _trail.enabled = false;
                _trailDisabled = true;
            }
        }

        //  UPDATE (with distance LOD + fade support)

        private void Update()
        {
            float dt = Time.deltaTime;

            // Fade-out takes priority — always runs at full rate for clean visual
            if (_fadingOut)
            {
                _fadeTimer += dt;
                float t = Mathf.Clamp01(_fadeTimer / DestroyFadeDuration);

                float fadeScale = 1f - t;
                _transform.localScale = _fadeStartScale * Mathf.Max(fadeScale, 0.01f);
                if (_sr != null)
                {
                    Color c = _sr.color;
                    c.a *= 1f - t;
                    _sr.color = c;
                }

                // Continue flight physics during fade for smooth visual
                if (_state == MirrorState.LocalFlight)
                {
                    _simVelocity.y += Physics2D.gravity.y * _gravityScale * dt;
                    _transform.position += (Vector3)(_simVelocity * dt);
                }

                if (t >= 1f)
                {
                    ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
                    Destroy(gameObject);
                }
                return;
            }

            // ── Timeout logic (state-dependent) ──
            switch (_state)
            {
                case MirrorState.LocalFlight:
                    {
                        _noUpdateTimer += dt;

                        // Safety net: absolute maximum flying time (leak protection)
                        if (_noUpdateTimer > FlyingTimeout)
                        {
                            ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
                            Destroy(gameObject);
                            return;
                        }

                        // WHY: Detect orphaned flying mirrors from lost MSG_DESTROY.
                        // Server snapshots arrive every 100ms for flying shards. If we
                        // haven't received any snapshot in 5s (50 missed), the shard was
                        // likely destroyed server-side but we never got the message.
                        // Fade out gracefully instead of waiting full FlyingTimeout (30s).
                        // Only check after first snapshot — before that, we're in initial
                        // flight from spawn data and haven't had a chance to receive updates.
                        if (_hasReceivedSnapshot && _timeSinceSnapshot > SnapshotStaleTimeout)
                        {
                            BeginFadeDestroy();
                            return;
                        }
                        break;
                    }

                case MirrorState.LandingBlend:
                    {
                        // Landing blend uses flying timeout (still transitioning)
                        _noUpdateTimer += dt;
                        if (_noUpdateTimer > FlyingTimeout)
                        {
                            ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
                            Destroy(gameObject);
                            return;
                        }
                        break;
                    }

                case MirrorState.AtRest:
                    {
                        // WHY: NO _noUpdateTimer timeout for at-rest mirrors.
                        // Server sends no further messages after REST state transition.
                        // Previously _noUpdateTimer grew unboundedly and hit RestTimeout (90s),
                        // destroying mirrors while server shard was still alive (debris
                        // lifetime = 300s from config). Now at-rest mirrors self-destruct
                        // in UpdateAtRest when _restTimer >= _debrisLifetime, matching
                        // server ShrapnelProjectile.UpdateDebris lifetime exactly.
                        // Server sends MSG_DESTROY when it cleans up — that's the
                        // authoritative signal for early removal.
                        break;
                    }
            }

            // LOD: Distance-based update throttling
            RefreshCameraCache();
            if (_cachedCamera != null)
            {
                float dx = _transform.position.x - _cameraPos.x;
                float dy = _transform.position.y - _cameraPos.y;
                float distSq = dx * dx + dy * dy;

                // PERF: Far mirrors update every 3rd frame (staggered by NetId)
                if (distSq > DistanceLodSqr)
                {
                    if ((Time.frameCount % 3) != (NetId % 3))
                    {
                        // Still accumulate timer for at-rest decay, skip expensive work
                        if (_state == MirrorState.AtRest)
                            _restTimer += dt;
                        return;
                    }
                }
            }

            switch (_state)
            {
                case MirrorState.LocalFlight:
                    UpdateLocalFlight(dt);
                    break;
                case MirrorState.LandingBlend:
                    UpdateLandingBlend(dt);
                    break;
                case MirrorState.AtRest:
                    UpdateAtRest(dt);
                    break;
            }
        }

        /// <summary>Caches main camera reference once per frame. Zero-alloc.</summary>
        private static void RefreshCameraCache()
        {
            int frame = Time.frameCount;
            if (frame == _cameraCacheFrame) return;
            _cameraCacheFrame = frame;
            if (_cachedCamera == null)
                _cachedCamera = Camera.main;
            if (_cachedCamera != null)
                _cameraPos = _cachedCamera.transform.position;
        }

        //  LOCAL FLIGHT UPDATE (with snapshot correction)

        private void UpdateLocalFlight(float dt)
        {
            if (_pendingRestTransition)
            {
                float timeSinceSpawn = Time.time - _spawnTime;
                if (timeSinceSpawn >= MinFlightVisualDuration)
                {
                    StartLandingBlend(_pendingRestPosition, _pendingRestRotation);
                    UpdateLandingBlend(dt);
                    return;
                }
            }

            _timeSinceSnapshot += dt;

            // Step 1: Local physics (manual vector math for fewer temp structs)
            Vector2 gravity = Physics2D.gravity;
            _simVelocity.x += gravity.x * _gravityScale * dt;
            _simVelocity.y += gravity.y * _gravityScale * dt;
            float dragFactor = 1f - _drag * dt;
            if (dragFactor < 0f) dragFactor = 0f;
            _simVelocity.x *= dragFactor;
            _simVelocity.y *= dragFactor;

            float sqrMag = _simVelocity.x * _simVelocity.x
                         + _simVelocity.y * _simVelocity.y;
            if (sqrMag > 10000f)
            {
                float invMag = 100f / Mathf.Sqrt(sqrMag);
                _simVelocity.x *= invMag;
                _simVelocity.y *= invMag;
            }

            // Step 2: Advance local position
            float localX = _transform.position.x + _simVelocity.x * dt;
            float localY = _transform.position.y + _simVelocity.y * dt;

            // Step 3: Server correction
            if (_hasReceivedSnapshot)
            {
                float predTime = _timeSinceSnapshot < MaxPredictionTime
                    ? _timeSinceSnapshot : MaxPredictionTime;
                float halfGravPredSq = 0.5f * _gravityScale * predTime * predTime;
                float predX = _lastServerPos.x + _serverVelocity.x * predTime
                            + gravity.x * halfGravPredSq;
                float predY = _lastServerPos.y + _serverVelocity.y * predTime
                            + gravity.y * halfGravPredSq;

                float errX = predX - localX;
                float errY = predY - localY;
                float driftSq = errX * errX + errY * errY;

                if (driftSq > TeleportThreshold * TeleportThreshold)
                {
                    localX = predX;
                    localY = predY;
                    _simVelocity = _serverVelocity;
                }
                else
                {
                    float blend = CorrectionRate * dt;
                    if (blend > 1f) blend = 1f;
                    localX += errX * blend;
                    localY += errY * blend;
                    _simVelocity.x += errX * VelocityCorrectionGain * dt;
                    _simVelocity.y += errY * VelocityCorrectionGain * dt;
                }
            }

            _transform.position = new Vector3(localX, localY, 0f);

            // Rotation: face velocity direction
            if (sqrMag > 4f)
            {
                float angle = Mathf.Atan2(_simVelocity.y, _simVelocity.x) * Mathf.Rad2Deg;
                _transform.rotation = Quaternion.Euler(0f, 0f, angle);
            }

            // Trail: disable when stopped
            if (_hasTrail && _trail != null && !_trailDisabled && sqrMag < 0.25f)
            {
                _trail.enabled = false;
                _trailDisabled = true;
            }

            UpdateHeat(dt);
        }

        //  LANDING BLEND UPDATE

        private void UpdateLandingBlend(float dt)
        {
            _blendTimer += dt;
            float t = Mathf.Clamp01(_blendTimer / LandingBlendDuration);
            float smooth = t * t * (3f - 2f * t);

            _transform.position = Vector2.Lerp(_blendStartPos, _blendEndPos, smooth);

            Quaternion startRot = Quaternion.Euler(0f, 0f, _blendStartRotZ);
            Quaternion endRot = Quaternion.Euler(0f, 0f, _blendEndRotZ);
            _transform.rotation = Quaternion.Slerp(startRot, endRot, smooth);

            _simVelocity *= Mathf.Max(0f, 1f - 5f * dt);

            if (t >= 1f)
                ApplyRestState(_blendEndPos, _blendEndRotZ);
        }

        //  AT-REST UPDATE (frame-staggered LOD)

        /// <summary>
        /// Updates visual decay and outline pulse for resting shrapnel.
        /// PERF LOD: Only runs full update every 5th frame — outline pulse is 0.5Hz
        /// and visual decay changes slowly, so 12fps update is indistinguishable.
        /// Staggered by NetId % 5 to spread across frames evenly.
        ///
        /// WHY self-destruct here: At-rest mirrors now use _restTimer vs _debrisLifetime
        /// for lifetime, matching server ShrapnelProjectile.UpdateDebris exactly.
        /// This replaced the old _noUpdateTimer > RestTimeout (90s) which caused
        /// premature mirror death because server sends no messages to at-rest shards
        /// and _noUpdateTimer grew unboundedly.
        /// </summary>
        private void UpdateAtRest(float dt)
        {
            _restTimer += dt;

            // WHY: Self-destruct when debris lifetime expires, matching server exactly.
            // Server's ShrapnelProjectile.UpdateDebris calls Destroy after debrisLifetime.
            // Previously used _noUpdateTimer > 90s which didn't match server's 300s+ lifetime.
            if (_restTimer >= _debrisLifetime)
            {
                ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
                Destroy(gameObject);
                return;
            }

            int frameSlot = NetId % 5;
            if ((Time.frameCount % 5) != frameSlot) return;

            if (_outlineSr != null)
                _outlineSr.color = ShrapnelVisuals.GetOutlineColor(Time.time);

            float normalized = 1f - Mathf.Clamp01(_restTimer / _debrisLifetime);
            if (normalized <= 0.3f)
            {
                float decayT = normalized / 0.3f;
                float decayFactor = decayT * decayT * (3f - 2f * decayT);

                _transform.localScale = _originalScale * (0.2f + 0.8f * decayFactor);

                if (_sr != null)
                {
                    Color c = _sr.color;
                    c.a = Mathf.Lerp(0.1f, 1f, decayFactor);
                    _sr.color = c;
                }
            }
        }

        //  HEAT

        private void UpdateHeat(float dt)
        {
            if (_heat <= 0f) return;

            _heat -= ShrapnelVisuals.HeatCoolRate * dt;
            if (_heat < 0f) _heat = 0f;

            Color blended = Color.Lerp(_coldColor, _hotColor, _heat);
            _sr.color = blended;

            if (_trail != null && !_trailDisabled)
            {
                blended.a = 0.8f * _heat;
                _trail.startColor = blended;
                Color endC = blended;
                endC.a = 0f;
                _trail.endColor = endC;
            }

            if (Mathf.Abs(_heat - _lastEmissionHeat) > 0.05f)
            {
                _lastEmissionHeat = _heat;
                if (_heat > 0.01f)
                    ParticleHelper.ApplyEmission(_sr,
                        ShrapnelVisuals.GetHotColor() * _heat * 1.3f);
                else
                    ParticleHelper.ClearEmission(_sr);
            }
        }

        //  IMPACT EFFECTS

        private void SpawnImpactSparks(Vector2 pos, float intensity)
        {
            int minSparks, maxSparks;
            Color sparkColorA, sparkColorB;

            switch (_type)
            {
                case ShrapnelProjectile.ShrapnelType.Metal:
                case ShrapnelProjectile.ShrapnelType.HeavyMetal:
                    minSparks = 3; maxSparks = 12;
                    sparkColorA = new Color(1f, 0.9f, 0.5f, 1f);
                    sparkColorB = new Color(1f, 0.5f, 0.1f, 1f);
                    break;
                case ShrapnelProjectile.ShrapnelType.Stone:
                    minSparks = 2; maxSparks = 8;
                    sparkColorA = new Color(0.8f, 0.8f, 0.75f, 0.9f);
                    sparkColorB = new Color(0.5f, 0.5f, 0.45f, 0.9f);
                    break;
                case ShrapnelProjectile.ShrapnelType.Wood:
                    minSparks = 2; maxSparks = 6;
                    sparkColorA = new Color(0.9f, 0.6f, 0.2f, 0.9f);
                    sparkColorB = new Color(0.7f, 0.4f, 0.1f, 0.9f);
                    break;
                case ShrapnelProjectile.ShrapnelType.Electronic:
                    minSparks = 2; maxSparks = 8;
                    sparkColorA = new Color(0.6f, 0.8f, 1f, 1f);
                    sparkColorB = new Color(0.3f, 0.5f, 1f, 1f);
                    break;
                default:
                    minSparks = 2; maxSparks = 8;
                    sparkColorA = new Color(1f, 0.7f, 0.3f, 1f);
                    sparkColorB = new Color(0.8f, 0.5f, 0.2f, 1f);
                    break;
            }

            int count = Mathf.RoundToInt(Mathf.Lerp(minSparks, maxSparks, intensity));
            if (count < 1) return;

            Vector2 baseDir = Vector2.up;
            if (_simVelocity.sqrMagnitude > 1f)
            {
                Vector2 incoming = _simVelocity.normalized;
                baseDir = new Vector2(-incoming.x * 0.5f, Mathf.Abs(incoming.y) + 0.3f)
                    .normalized;
            }

            for (int i = 0; i < count; i++)
            {
                float t = _rng.NextFloat();
                Color col = Color.Lerp(sparkColorA, sparkColorB, t);
                float spreadRad = _rng.Range(-70f, 70f) * Mathf.Deg2Rad;
                Vector2 dir = MathHelper.RotateDirection(baseDir, spreadRad);
                float size = _rng.Range(0.02f, 0.06f);
                float speed = _rng.Range(3f, 10f) * (0.5f + intensity);
                float life = _rng.Range(0.08f, 0.25f);

                var vis = new VisualParticleParams(size, col, 11,
                    ShrapnelVisuals.TriangleShape.Needle);
                var spark = new SparkParams(dir, speed, life);
                ParticleHelper.SpawnSpark(pos + _rng.InsideUnitCircle() * 0.1f, vis, spark);
            }
        }

        private void SpawnImpactDust(Vector2 pos, float intensity)
        {
            int count = Mathf.RoundToInt(Mathf.Lerp(1f, 5f, intensity));
            if (count < 1) return;

            Color dustColor;
            switch (_type)
            {
                case ShrapnelProjectile.ShrapnelType.Wood:
                    dustColor = new Color(0.5f, 0.35f, 0.2f, 0.6f); break;
                case ShrapnelProjectile.ShrapnelType.Electronic:
                    dustColor = new Color(0.25f, 0.25f, 0.3f, 0.5f); break;
                default:
                    dustColor = new Color(0.4f, 0.4f, 0.38f, 0.6f); break;
            }

            float sizeMultiplier = GetWeightImpactMultiplier() * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float gray = _rng.Range(-0.08f, 0.08f);
                Color col = new(
                    dustColor.r + gray, dustColor.g + gray,
                    dustColor.b + gray, dustColor.a);

                float angle = _rng.Range(-90f, 90f) * Mathf.Deg2Rad;
                float speed = _rng.Range(1f, 4f) * (0.3f + intensity);
                Vector2 vel = new(
                    Mathf.Cos(angle) * speed,
                    Mathf.Abs(Mathf.Sin(angle)) * speed + _rng.Range(0.5f, 2f));

                float size = _rng.Range(0.04f, 0.12f) * (1f + sizeMultiplier);
                var vis = new VisualParticleParams(
                    size, col, 10, ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.Chunk(vel, _rng.Range(0.4f, 1.2f), _rng);
                ParticleHelper.SpawnLit(
                    pos + _rng.InsideUnitCircle() * 0.15f,
                    vis, phy, _rng.Range(0f, 100f));
            }
        }

        //  CLEANUP

        private void OnDestroy()
        {
            if (_sr != null && _lastEmissionHeat > 0f)
                ParticleHelper.ClearEmission(_sr);
            ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
        }
    }
}