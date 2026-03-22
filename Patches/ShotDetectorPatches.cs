using System;
using System.Reflection;
using UnityEngine;
using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod.Patches
{
    /// <summary>
    /// Shot detection via MonoBehaviour polling. Single proven method for
    /// Unity 2022.3 + BepInEx 5.4.23 where Harmony trampolines fail.
    /// </summary>
    public static class ShotDetectorPatches
    {
        /// <summary>
        /// Creates persistent MonoBehaviour detectors.
        /// </summary>
        public static void CreateDetectors()
        {
            Create<TurretShotMonitor>("TurretShotMonitor");
            Create<GunShotMonitor>("GunShotMonitor");
        }

        /// <summary>
        /// Forces all monitors to rescan on next frame.
        /// Call from Plugin.OnWorldLoad().
        /// </summary>
        public static void ForceRescan()
        {
            TurretShotMonitor.ForceRescan();
            GunShotMonitor.ForceRescan();
        }

        private static void Create<T>(string name) where T : MonoBehaviour
        {
            try
            {
                var go = new GameObject($"ShrapnelMod_{name}")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<T>();
                Plugin.Log.LogInfo($"[ShotDetector] ✓ {name} created");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShotDetector] ✗ {name}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Turret shot detection via didShoot field polling.
    /// Detects rising edge (false→true) per turret instance.
    /// </summary>
    internal sealed class TurretShotMonitor : MonoBehaviour
    {
        private TurretScript[] _turrets = Array.Empty<TurretScript>();
        private bool[] _lastDidShoot = Array.Empty<bool>();
        private bool[] _initialized = Array.Empty<bool>();
        private float _scanTimer;
        private int _logCount;

        private static FieldInfo _didShootField;
        private static bool _fieldInit;
        private static bool _fieldOk;
        private static bool _forceRescan;

        /// <summary>Forces rescan on next Update.</summary>
        public static void ForceRescan() => _forceRescan = true;

        private static bool EnsureField()
        {
            if (_fieldInit) return _fieldOk;
            _fieldInit = true;

            _didShootField = typeof(TurretScript).GetField("didShoot",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            _fieldOk = _didShootField != null;

            if (_fieldOk)
                Plugin.Log.LogInfo("[TurretMonitor] ✓ didShoot field found");
            else
                Plugin.Log.LogError("[TurretMonitor] ✗ didShoot field NOT FOUND");

            return _fieldOk;
        }

        private void Update()
        {
            if (!EnsureField()) return;

            // WHY: Force rescan clears stale references from previous world
            if (_forceRescan)
            {
                _forceRescan = false;
                _scanTimer = 0f;
                _turrets = Array.Empty<TurretScript>();
                _lastDidShoot = Array.Empty<bool>();
                _initialized = Array.Empty<bool>();
            }

            _scanTimer -= Time.deltaTime;
            if (_scanTimer <= 0f)
            {
                _scanTimer = 1f;
                RescanTurrets();
            }

            for (int i = 0; i < _turrets.Length; i++)
            {
                // WHY: Unity null check — destroyed objects evaluate to null via ==
                if (_turrets[i] == null) continue;

                bool didShoot;
                try { didShoot = (bool)_didShootField.GetValue(_turrets[i]); }
                catch { continue; }

                if (!_initialized[i])
                {
                    _initialized[i] = true;
                    _lastDidShoot[i] = didShoot;
                    continue;
                }

                if (didShoot && !_lastDidShoot[i])
                    OnTurretShot(_turrets[i]);

                _lastDidShoot[i] = didShoot;
            }
        }

        /// <summary>
        /// Rescans scene for turrets. Preserves tracking state for
        /// turrets that existed before the rescan.
        /// </summary>
        private void RescanTurrets()
        {
            var newTurrets = FindObjectsOfType<TurretScript>();

            // WHY: Must compare instance IDs, not Length.
            // Same count with different objects = stale references = missed shots.
            bool same = newTurrets.Length == _turrets.Length;
            if (same)
            {
                for (int i = 0; i < newTurrets.Length; i++)
                {
                    if (newTurrets[i] != _turrets[i]) { same = false; break; }
                }
            }
            if (same) return;

            if (_logCount < 5)
            {
                _logCount++;
                Plugin.Log.LogInfo($"[TurretMonitor] Rescan: {newTurrets.Length} turret(s)");
            }

            var newLast = new bool[newTurrets.Length];
            var newInit = new bool[newTurrets.Length];

            for (int n = 0; n < newTurrets.Length; n++)
            {
                for (int o = 0; o < _turrets.Length; o++)
                {
                    if (_turrets[o] != null && _turrets[o] == newTurrets[n])
                    {
                        newInit[n] = _initialized[o];
                        newLast[n] = _lastDidShoot[o];
                        break;
                    }
                }
            }

            _turrets = newTurrets;
            _lastDidShoot = newLast;
            _initialized = newInit;
        }

        private void OnTurretShot(TurretScript turret)
        {
            if (turret.barrel == null) return;
            if (!Plugin.VisualsWarmed) Plugin.WarmVisuals();

            Vector2 pos = turret.barrel.position;
            Vector2 dir = (Vector2)(turret.transform.right * turret.transform.localScale.x);

            _logCount++;
            if (_logCount <= 15)
                Plugin.Log.LogInfo($"[TurretShot] ▶ pos={pos} dir={dir}");

            ShotEffectRouter.OnBulletFired(pos, dir, ShotEffectRouter.ShotSource.Turret);
        }
    }

    /// <summary>
    /// Gun shot detection via muzzleParticle.isEmitting polling.
    /// Rising edge (not emitting → emitting) = confirmed shot.
    /// Forces rescan on world reload to clear stale GunScript references.
    /// </summary>
    internal sealed class GunShotMonitor : MonoBehaviour
    {
        private GunScript[] _guns = Array.Empty<GunScript>();
        private bool[] _wasEmitting = Array.Empty<bool>();
        private float _scanTimer = 1f;
        private int _logCount;
        private static bool _forceRescan;

        /// <summary>Forces rescan on next Update.</summary>
        public static void ForceRescan() => _forceRescan = true;

        private void Update()
        {
            // WHY: On world reload, old GunScript references are destroyed.
            // Without force-clear, length-only check may keep stale array
            // if new world has same number of guns.
            if (_forceRescan)
            {
                _forceRescan = false;
                _scanTimer = 0f;
                _guns = Array.Empty<GunScript>();
                _wasEmitting = Array.Empty<bool>();
            }

            _scanTimer -= Time.deltaTime;
            if (_scanTimer <= 0f)
            {
                _scanTimer = 2f;
                RescanGuns();
            }

            for (int i = 0; i < _guns.Length; i++)
            {
                // WHY: Unity null check catches destroyed GameObjects
                if (_guns[i] == null || _guns[i].muzzleParticle == null) continue;

                bool emitting = _guns[i].muzzleParticle.isEmitting;

                if (emitting && !_wasEmitting[i])
                    OnGunShot(_guns[i]);

                _wasEmitting[i] = emitting;
            }
        }

        /// <summary>
        /// Rescans for guns. Always rebuilds — no stale reference risk.
        /// </summary>
        private void RescanGuns()
        {
            var newGuns = FindObjectsOfType<GunScript>();

            // WHY: Compare by instance ID, not just Length.
            // Two different worlds can have same gun count but different instances.
            bool same = newGuns.Length == _guns.Length;
            if (same)
            {
                for (int i = 0; i < newGuns.Length; i++)
                {
                    if (newGuns[i] != _guns[i]) { same = false; break; }
                }
            }
            if (same) return;

            _guns = newGuns;
            _wasEmitting = new bool[_guns.Length];
            for (int i = 0; i < _guns.Length; i++)
            {
                if (_guns[i] != null && _guns[i].muzzleParticle != null)
                    _wasEmitting[i] = _guns[i].muzzleParticle.isEmitting;
            }
        }

        private void OnGunShot(GunScript gun)
        {
            if (gun.barrel == null) return;
            if (!Plugin.VisualsWarmed) Plugin.WarmVisuals();

            Body body = PlayerCamera.main?.body;
            if (body == null) return;

            float facing = body.isRight ? 1f : -1f;
            Vector2 barrelPos = gun.barrel.position;
            Vector2 fireDir = (Vector2)(gun.transform.right * facing);

            _logCount++;
            if (_logCount <= 15)
            {
                float dmg = ShotEffectRouter.ReadGunDamage(gun);
                Plugin.Log.LogInfo($"[GunShot] ▶ pos={barrelPos} structDmg={dmg:F1}");
            }

            ShotEffectRouter.OnBulletFired(barrelPos, fireDir, ShotEffectRouter.ShotSource.Gun, gun);
        }
    }
}