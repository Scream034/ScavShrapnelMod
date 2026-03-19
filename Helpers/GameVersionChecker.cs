using UnityEngine;

namespace ScavShrapnelMod.Helpers
{
    /// <summary>
    /// Validates the running game version against known-compatible versions.
    ///
    /// Uses <see cref="Application.version"/> — Unity's built-in version string
    /// set in Player Settings. Available immediately in Plugin.Awake(), no
    /// scene scanning or patching required.
    ///
    /// On mismatch: warning in BepInEx log + in-game console notification.
    /// The mod continues to load — graceful degradation, not a hard block.
    /// </summary>
    public static class GameVersionChecker
    {
        /// <summary>Game versions this mod is tested and confirmed working with.</summary>
        private static readonly string[] SupportedVersions = { "5.1" };

        /// <summary>
        /// Raw version string from <see cref="Application.version"/>,
        /// e.g. <c>"5.1"</c>. Set after <see cref="Check"/> is called.
        /// </summary>
        public static string DetectedVersion { get; private set; } = string.Empty;

        /// <summary>
        /// True if <see cref="DetectedVersion"/> is in <see cref="SupportedVersions"/>.
        /// Defaults to true until <see cref="Check"/> is called.
        /// </summary>
        public static bool IsSupported { get; private set; } = true;

        /// <summary>
        /// Reads <see cref="Application.version"/>, evaluates compatibility,
        /// and logs the result. Call once from <see cref="Plugin.Awake"/>.
        /// </summary>
        public static void Check()
        {
            DetectedVersion = Application.version;
            IsSupported = IsVersionSupported(DetectedVersion);

            if (IsSupported)
            {
                Plugin.Log.LogInfo(
                    $"[Version] Game version '{DetectedVersion}' ✓ supported");
                return;
            }

            string warning =
                $"[{Plugin.Name}] WARNING: Game version '{DetectedVersion}' " +
                $"is not tested with mod v{Plugin.Version}. " +
                $"Supported: {string.Join(", ", SupportedVersions)}. " +
                "Proceed with caution — some effects may not work correctly.";

            Plugin.Log.LogWarning(warning);
            Console.Error(warning);
        }

        private static bool IsVersionSupported(string version)
        {
            foreach (string v in SupportedVersions)
                if (v == version) return true;
            return false;
        }
    }
}