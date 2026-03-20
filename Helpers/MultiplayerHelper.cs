using System;
using System.Reflection;
using UnityEngine;

namespace ScavShrapnelMod.Helpers
{
    /// <summary>
    /// Detects KrokoshaCasualtiesMP multiplayer mod at runtime via reflection.
    /// No hard dependency — shrapnel mod works with or without MP mod.
    ///
    /// FIELD vs PROPERTY:
    ///   is_client                  = public static field (GetField works)
    ///   network_system_is_running  = public static property (GetProperty needed!)
    ///     Compiler generates backing field named
    ///     &lt;network_system_is_running&gt;k__BackingField which is private.
    ///     GetField("network_system_is_running") returns null.
    ///
    /// PERF: IsNetworkRunning/IsClient/IsServer are cached per frame.
    /// Without caching, each call does PropertyInfo.GetValue + FieldInfo.GetValue
    /// via reflection. With 197+ mirrors calling per frame + ServerUpdate,
    /// this adds up to thousands of reflection calls per second.
    /// </summary>
    public static class MultiplayerHelper
    {
        private static bool _detected;
        private static bool _mpPresent;
        private static PropertyInfo _isRunningProp;
        private static FieldInfo _isClientField;

        // PERF: Per-frame cache. IsNetworkRunning/IsClient/IsServer are called
        // from Update() on every mirror + every server tick. Reflection GetValue
        // is expensive when done hundreds of times per frame.
        private static int _cachedFrame = -1;
        private static bool _cachedIsRunning;
        private static bool _cachedIsClient;

        public static bool IsMultiplayerModPresent
        {
            get
            {
                if (!_detected) Detect();
                return _mpPresent;
            }
        }

        /// <summary>Refreshes cached values if the frame has changed.</summary>
        private static void RefreshCache()
        {
            int frame = Time.frameCount;
            if (frame == _cachedFrame) return;
            _cachedFrame = frame;

            if (!IsMultiplayerModPresent)
            {
                _cachedIsRunning = false;
                _cachedIsClient = false;
                return;
            }

            try { _cachedIsRunning = (bool)_isRunningProp.GetValue(null); }
            catch { _cachedIsRunning = false; }

            if (!_cachedIsRunning)
            {
                _cachedIsClient = false;
                return;
            }

            try { _cachedIsClient = (bool)_isClientField.GetValue(null); }
            catch { _cachedIsClient = false; }
        }

        public static bool IsNetworkRunning
        {
            get
            {
                RefreshCache();
                return _cachedIsRunning;
            }
        }

        public static bool IsClient
        {
            get
            {
                RefreshCache();
                return _cachedIsClient;
            }
        }

        public static bool IsServer => IsNetworkRunning && !IsClient;
        public static bool ShouldSpawnPhysicsShrapnel => !IsNetworkRunning || IsServer;

        private static void Detect()
        {
            _detected = true;
            _mpPresent = false;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type mpType = null;
                    try { mpType = asm.GetType("KrokoshaCasualtiesMP.KrokoshaScavMultiplayer"); }
                    catch { continue; }
                    if (mpType == null) continue;

                    var allFlags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.FlattenHierarchy;

                    _isRunningProp = mpType.GetProperty("network_system_is_running", allFlags);
                    _isClientField = mpType.GetField("is_client", allFlags);

                    if (_isRunningProp != null && _isClientField != null)
                    {
                        _mpPresent = true;
                        Plugin.Log.LogInfo($"[MP] ✓ Detected! asm={asm.GetName().Name}");
                        return;
                    }

                    Plugin.Log.LogWarning($"[MP] Type found in {asm.GetName().Name}" +
                        $" prop={_isRunningProp != null} field={_isClientField != null}");
                }

                Plugin.Log.LogInfo("[MP] KrokoshaCasualtiesMP not found");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[MP] Detection error: {e.Message}");
            }
        }

        public static void Reset()
        {
            _detected = false;
            _mpPresent = false;
            _cachedFrame = -1;
        }
    }
}