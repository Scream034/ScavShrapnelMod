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
    ///   Uses parabolic extrapolation between snapshots:
    ///   pos = lastServerPos + vel*t + 0.5*gravity*gravScale*t²
    ///   Velocity is back-computed from consecutive snapshots minus gravity contribution.
    ///   This ensures smooth in-flight motion even at 10 Hz snapshot rate.
    ///
    /// HEAT:
    ///   Cools at ShrapnelVisuals.HeatCoolRate — identical to server.
    ///   No network traffic needed: both sides start with same heat and cool at same rate.
    ///
    /// OUTLINE:
    ///   Created immediately at spawn (server creates it lazily on Stuck/Debris).
    ///   Pulses using ShrapnelVisuals.GetOutlineColor() — same code as server side.
    /// </summary>
    public sealed class ClientMirrorShrapnel : MonoBehaviour
    {
        //  PUBLIC STATE

        /// <summary>Network sync ID matching ShrapnelProjectile.NetSyncId on the server.</summary>
        public ushort NetId { get; private set; }

        //  COMPONENTS

        private SpriteRenderer _sr;
        private SpriteRenderer _outlineSr;
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

        /// <summary>
        /// Gravity scale matching server Rigidbody2D.gravityScale for this weight.
        /// Used for parabolic extrapolation.
        /// Values must match ShrapnelFactory.ConfigureRigidbody exactly.
        /// </summary>
        private float _gravityScale;

        //  FACTORY

        /// <summary>
        /// Creates and configures a ClientMirrorShrapnel.
        /// Returns null if required assets (sprite, material) are unavailable.
        /// </summary>
        public static ClientMirrorShrapnel Create(
            ushort netId,
            Vector2 position,
            ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight weight,
            float heat,
            ShrapnelVisuals.TriangleShape shape,
            float scale)
        {
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return null;

            Material mat = heat > ShrapnelVisuals.HotThreshold
                ? ShrapnelVisuals.UnlitMaterial
                : (ShrapnelVisuals.LitMaterial ?? ShrapnelVisuals.UnlitMaterial);
            if (mat == null) return null;

            // Main GameObject
            var go = new GameObject($"ShrMirror_{netId}")
            {
                hideFlags = HideFlags.DontSave
            };
            go.transform.position = position;
            go.transform.localScale = Vector3.one * scale;
            go.layer = 0;

            // Main SpriteRenderer
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = mat;

            Color coldColor = ShrapnelVisuals.GetColdColor(type);
            Color hotColor = ShrapnelVisuals.GetHotColor();
            sr.color = Color.Lerp(coldColor, hotColor, Mathf.Clamp01(heat));

            // Outline child (mirrors create this immediately; server creates it lazily)
            var outlineGo = new GameObject("Outline");
            outlineGo.transform.SetParent(go.transform, false);
            outlineGo.transform.localScale =
                Vector3.one * ShrapnelVisuals.OutlineScale;

            var outlineSr = outlineGo.AddComponent<SpriteRenderer>();
            outlineSr.sprite = sprite;
            outlineSr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
            outlineSr.sortingOrder = 9;
            outlineSr.color = ShrapnelVisuals.GetOutlineBaseColor();

            // Component
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

            return mirror;
        }

        /// <summary>
        /// Returns Rigidbody2D.gravityScale the server uses for this weight.
        /// Must match ShrapnelFactory.ConfigureRigidbody exactly.
        /// </summary>
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

        //  NETWORK INPUT

        /// <summary>
        /// Called when a snapshot arrives with a new server position.
        /// Back-computes velocity from position delta minus gravity contribution
        /// so extrapolation follows the correct parabolic arc.
        /// </summary>
        public void SetTarget(Vector2 serverPos)
        {
            if (_hasReceivedFirstSnapshot && _timeSinceSnapshot > 0.001f)
            {
                // serverPos = lastPos + vel*dt + 0.5*g*gs*dt²
                // = vel = (serverPos - lastPos - 0.5*g*gs*dt²) / dt
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

            if (_noUpdateTimer > ShrapnelNetSync.MirrorTimeout)
            {
                ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
                Destroy(gameObject);
                return;
            }

            // Parabolic extrapolation: pos + vel*t + 0.5*g*gs*t²
            float t = _timeSinceSnapshot;
            Vector2 predicted = _lastServerPos
                + _predictedVelocity * t
                + 0.5f * Physics2D.gravity * _gravityScale * t * t;

            // Adaptive interpolation: faster when further from target,
            // snaps instantly when close. Handles both fast-flying and
            // stationary (stuck) shrapnel smoothly.
            Vector2 current = (Vector2)_transform.position;
            float distance = Vector2.Distance(current, predicted);

            if (distance < 0.01f)
            {
                // Close enough — snap to avoid micro-jitter
                _transform.position = predicted;
            }
            else if (distance > 5f)
            {
                // Too far — teleport (packet loss recovery, spawn desync)
                _transform.position = predicted;
            }
            else
            {
                // Smooth lerp: speed proportional to distance
                // At distance=1, speed=20. At distance=3, speed=60.
                float speed = Mathf.Max(
                    ShrapnelNetSync.InterpolationSpeed,
                    distance * 20f);
                _transform.position = Vector2.MoveTowards(
                    current, predicted, dt * speed);
            }

            // Rotation: angle of current velocity including gravity accumulation
            Vector2 currentVel = _predictedVelocity
                + Physics2D.gravity * _gravityScale * t;
            if (currentVel.sqrMagnitude > 1f)
            {
                float angle = Mathf.Atan2(currentVel.y, currentVel.x) * Mathf.Rad2Deg;
                _transform.rotation = Quaternion.Euler(0f, 0f, angle);
            }

            // Heat cooling — same rate as server, no sync needed
            if (_heat > 0f)
            {
                _heat -= ShrapnelVisuals.HeatCoolRate * dt;
                if (_heat < 0f) _heat = 0f;
                _sr.color = Color.Lerp(_coldColor, _hotColor, _heat);
            }

            // Outline pulsation — uses same helper as ShrapnelProjectile.PulseOutline
            if (_outlineSr != null)
                _outlineSr.color = ShrapnelVisuals.GetOutlineColor(Time.time);
        }

        private void OnDestroy()
        {
            ShrapnelNetSync.NotifyMirrorDestroyed(NetId);
        }
    }
}