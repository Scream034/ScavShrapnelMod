using System;
using System.Reflection;

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
    /// </summary>
    public static class MultiplayerHelper
    {
        private static bool _detected;
        private static bool _mpPresent;
        private static PropertyInfo _isRunningProp;  // Property, not field!
        private static FieldInfo _isClientField;      // Field

        public static bool IsMultiplayerModPresent
        {
            get
            {
                if (!_detected) Detect();
                return _mpPresent;
            }
        }

        public static bool IsNetworkRunning
        {
            get
            {
                if (!IsMultiplayerModPresent) return false;
                try { return (bool)_isRunningProp.GetValue(null); }
                catch { return false; }
            }
        }

        public static bool IsClient
        {
            get
            {
                if (!IsMultiplayerModPresent) return false;
                try { return (bool)_isClientField.GetValue(null); }
                catch { return false; }
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

                    // network_system_is_running is a PROPERTY, not a field
                    _isRunningProp = mpType.GetProperty("network_system_is_running", allFlags);

                    // is_client is a regular public static field
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
        }
    }
}