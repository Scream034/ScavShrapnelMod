using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Net
{
    /// <summary>
    /// Lightweight visual mirror of a server-side ShrapnelProjectile.
    /// No Rigidbody2D, no Collider2D, no damage — purely cosmetic.
    ///
    /// INTERPOLATION:
    ///   Moving: parabolic extrapolation pos + vel*t + 0.5*g*gs*t²
    ///   Stopped: snap to last server position (no gravity applied)
    ///   Velocity back-computed from consecutive snapshots minus gravity.
    ///
    /// HEAT:
    ///   Cools at ShrapnelVisuals.HeatCoolRate — identical to server.
    ///
    /// TRAIL:
    ///   When hasTrail=true, a TrailRenderer is added matching server visuals.
    ///   Trail is disabled when fragment stops moving (same as server).
    /// </summary>
    public sealed class ClientMirrorShrapnel : MonoBehaviour
    {
        //  PUBLIC STATE

        /// <summary>Network sync ID matching ShrapnelProjectile.NetSyncId on server.</summary>
        public ushort NetId { get; private set; }

        //  COMPONENTS

        private SpriteRenderer _sr;
        private SpriteRenderer _outlineSr;
        private TrailRenderer _trail;
        private Transform _transform;

        //  HEAT

        private float _heat;
        private Color _coldColor;
        private Color _hotColor;

        //  INTERPOLATION

        private Vector2 _lastServerPos;
        private Vector2 _predictedVelocity;
        private float _timeSinceSnapshot;
        private float _noUpdateTimer;
        private bool _hasReceivedFirstSnapshot;
        private float _gravityScale;

        //  TRAIL STATE

        private bool _hasTrail;
        private bool _trailDisabled;

        /// <summary>
        /// Value 0.5 means: if predicted velocity is less than 0.5 units/sec → stopped.
        /// </summary>
        private const float StationaryThreshold = 0.5f;

        /// <summary>
        /// Distance threshold for teleporting mirror to predicted position.
        /// If mirror is more than this far from target, snap instantly.
        /// Handles packet loss recovery and late spawn messages.
        /// </summary>
        private const float TeleportDistance = 5f;

        /// <summary>
        /// Distance below which mirror snaps to target to avoid micro-jitter.
        /// </summary>
        private const float SnapDistance = 0.01f;

        /// <summary>
        /// Adaptive interpolation speed multiplier.
        /// Speed = max(InterpolationSpeed, distance * this).
        /// Higher = faster catch-up for fast-moving fragments.
        /// </summary>
        private const float AdaptiveSpeedFactor = 20f;

        //  TRAIL CONSTANTS (must match ShrapnelFactory trail config)

        /// <summary>Trail lifetime in seconds. Matches server TrailRenderer.time.</summary>
        private const float TrailTime = 0.2f;

        /// <summary>Trail start width multiplier relative to fragment scale.</summary>
        private const float TrailWidthMultiplier = 0.5f;

        /// <summary>Trail minimum vertex distance. Matches server config.</summary>
        private const float TrailMinVertexDistance = 0.05f;

        //  FACTORY

        /// <summary>
        /// Creates and configures a ClientMirrorShrapnel.
        /// Returns null if required assets are unavailable.
        /// </summary>
        public static ClientMirrorShrapnel Create(
            ushort netId,
            Vector2 position,
            ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight weight,
            float heat,
            ShrapnelVisuals.TriangleShape shape,
            float scale,
            bool hasTrail)
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
            go.layer = 0;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = mat;

            Color coldColor = ShrapnelVisuals.GetColdColor(type);
            Color hotColor = ShrapnelVisuals.GetHotColor();
            sr.color = Color.Lerp(coldColor, hotColor, Mathf.Clamp01(heat));

            var outlineGo = new GameObject("Outline");
            outlineGo.transform.SetParent(go.transform, false);
            outlineGo.transform.localScale = Vector3.one * ShrapnelVisuals.OutlineScale;

            var outlineSr = outlineGo.AddComponent<SpriteRenderer>();
            outlineSr.sprite = sprite;
            outlineSr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
            outlineSr.sortingOrder = 9;
            outlineSr.color = ShrapnelVisuals.GetOutlineBaseColor();

            var mirror = go.AddComponent<ClientMirrorShrapnel>();
            mirror.NetId = netId;
            mirror._sr = sr;
            mirror._outlineSr = outlineSr;
            mirror._transform = go.transform;
            mirror._heat = Mathf.Clamp01(heat);
            mirror._coldColor = coldColor;
            mirror._hotColor = hotColor;
            mirror._lastServerPos = position;
            mirror._predictedVelocity = Vector2.zero;
            mirror._timeSinceSnapshot = 0f;
            mirror._noUpdateTimer = 0f;
            mirror._hasReceivedFirstSnapshot = false;
            mirror._gravityScale = GravityScaleForWeight(weight);
            mirror._hasTrail = hasTrail;
            mirror._trailDisabled = false;

            // WHY: Trail makes small/hot fragments visible during flight.
            // Without it, Micro/Hot weights are nearly invisible on client.
            if (hasTrail)
                mirror.CreateTrail(scale, heat, coldColor, hotColor);

            return mirror;
        }

        /// <summary>
        /// Creates a TrailRenderer matching server-side ShrapnelFactory configuration.
        /// Trail fades from hot color (if heated) or cold color to transparent.
        /// </summary>
        private void CreateTrail(float scale, float heat, Color coldColor, Color hotColor)
        {
            Material trailMat = ShrapnelVisuals.TrailMaterial;
            if (trailMat == null) return;

            _trail = gameObject.AddComponent<TrailRenderer>();
            _trail.sharedMaterial = trailMat;
            _trail.sortingOrder = 8;
            _trail.time = TrailTime;
            _trail.minVertexDistance = TrailMinVertexDistance;
            _trail.autodestruct = false;
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _trail.receiveShadows = false;
            _trail.numCapVertices = 2;
            _trail.numCornerVertices = 2;

            float width = scale * TrailWidthMultiplier;
            _trail.startWidth = width;
            _trail.endWidth = 0f;

            // WHY: Hot fragments have glowing trails, cold ones have subtle trails
            Color startColor = heat > ShrapnelVisuals.HotThreshold
                ? Color.Lerp(coldColor, hotColor, Mathf.Clamp01(heat))
                : coldColor;
            startColor.a = 0.8f;

            Color endColor = startColor;
            endColor.a = 0f;

            _trail.startColor = startColor;
            _trail.endColor = endColor;
        }

        /// <summary>
        /// Returns Rigidbody2D.gravityScale the server uses for this weight.
        /// Must match ShrapnelFactory.ConfigureRigidbody exactly.
        /// </summary>
        private static float GravityScaleForWeight(ShrapnelWeight w)
        {
            switch (w)
            {
                case ShrapnelWeight.Hot:     return 0.3f;
                case ShrapnelWeight.Medium:  return 0.15f;
                case ShrapnelWeight.Heavy:   return 0.35f;
                case ShrapnelWeight.Massive: return 0.5f;
                case ShrapnelWeight.Micro:   return 0.1f;
                default:                     return 0.25f;
            }
        }

        //  NETWORK INPUT

        /// <summary>
        /// Called when a snapshot arrives with a new server position.
        /// Back-computes velocity from delta minus gravity contribution.
        /// </summary>
        public void SetTarget(Vector2 serverPos)
        {
            if (_hasReceivedFirstSnapshot && _timeSinceSnapshot > 0.001f)
            {
                float dt = _timeSinceSnapshot;
                Vector2 gravityContrib =
                    0.5f * Physics2D.gravity * _gravityScale * dt * dt;
                Vector2 rawVelocity =
                    (serverPos - _lastServerPos - gravityContrib) / dt;

                float maxSpd = ShrapnelNetSync.MaxExtrapolationSpeed;
                if (rawVelocity.sqrMagnitude > maxSpd * maxSpd)
                    rawVelocity = rawVelocity.normalized * maxSpd;

                _predictedVelocity = rawVelocity;
            }
            else
            {
                _predictedVelocity = Vector2.zero;
                _hasReceivedFirstSnapshot = true;
            }

            _lastServerPos = serverPos;
            _timeSinceSnapshot = 0f;
            _noUpdateTimer = 0f;
        }

        //  UPDATE

        private void Update()
        {
            float dt = Time.deltaTime;
            _timeSinceSnapshot += dt;
            _noUpdateTimer += dt;

            // Timeout: server destroyed or connection lost
            if (_noUpdateTimer > ShrapnelNetSync.MirrorTimeout)
            {
                ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
                Destroy(gameObject);
                return;
            }

            // Determine if fragment is moving or stationary
            // When stationary (stuck/debris on server), velocity ≈ 0
            // and applying gravity would drag mirror underground
            bool isMoving = _predictedVelocity.sqrMagnitude >
                StationaryThreshold * StationaryThreshold;

            // Calculate predicted position
            float t = _timeSinceSnapshot;
            Vector2 predicted;

            if (isMoving)
            {
                // Parabolic: pos + vel*t + 0.5*g*gs*t²
                predicted = _lastServerPos
                    + _predictedVelocity * t
                    + 0.5f * Physics2D.gravity * _gravityScale * t * t;
            }
            else
            {
                // Stationary: hold at last server position
                predicted = _lastServerPos;
            }

            // Adaptive interpolation
            Vector2 current = (Vector2)_transform.position;
            float distance = Vector2.Distance(current, predicted);

            if (distance < SnapDistance)
            {
                // Close enough — snap to avoid micro-jitter
                _transform.position = predicted;
            }
            else if (distance > TeleportDistance)
            {
                // Too far — teleport (packet loss recovery)
                _transform.position = predicted;
            }
            else
            {
                // Smooth catch-up: speed proportional to distance
                float speed = Mathf.Max(
                    ShrapnelNetSync.InterpolationSpeed,
                    distance * AdaptiveSpeedFactor);
                _transform.position = Vector2.MoveTowards(
                    current, predicted, dt * speed);
            }

            // Rotation only when moving
            if (isMoving)
            {
                Vector2 currentVel = _predictedVelocity
                    + Physics2D.gravity * _gravityScale * t;
                if (currentVel.sqrMagnitude > 1f)
                {
                    float angle = Mathf.Atan2(currentVel.y, currentVel.x)
                        * Mathf.Rad2Deg;
                    _transform.rotation = Quaternion.Euler(0f, 0f, angle);
                }
            }

            // Trail management: disable when stopped (same as server)
            if (_hasTrail && _trail != null && !_trailDisabled && !isMoving)
            {
                _trail.enabled = false;
                _trailDisabled = true;
            }

            // Heat cooling — same rate as server
            if (_heat > 0f)
            {
                _heat -= ShrapnelVisuals.HeatCoolRate * dt;
                if (_heat < 0f) _heat = 0f;

                Color blended = Color.Lerp(_coldColor, _hotColor, _heat);
                _sr.color = blended;

                // WHY: Trail color tracks heat so glow fades with cooling
                if (_trail != null && !_trailDisabled)
                {
                    blended.a = 0.8f * _heat;
                    _trail.startColor = blended;
                    Color endC = blended;
                    endC.a = 0f;
                    _trail.endColor = endC;
                }
            }

            // Outline pulsation
            if (_outlineSr != null)
                _outlineSr.color = ShrapnelVisuals.GetOutlineColor(Time.time);
        }

        private void OnDestroy()
        {
            ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
        }
    }
}