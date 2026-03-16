using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ScavShrapnelMod.Console;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod
{
    /// <summary>
    /// BepInEx plugin entry point for Shrapnel Overhaul.
    ///
    /// Initialization order:
    /// 1. ShrapnelConfig.Bind — load configuration from .cfg file
    /// 2. Harmony.PatchAll — apply patches
    /// 3. ConsoleCommandRegistrar — wait for console readiness
    /// 4. WorldLoadHook — pre-warm visuals when world is loaded
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ScavShrapnelMod";
        public const string Name = "ScavShrapnelMod";
        public const string Version = "0.7.0";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal static bool CommandsRegistered;
        internal static bool VisualsWarmed;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Logger.LogInfo("╔═══════════════════════════════════════╗");
            Logger.LogInfo("║     Shrapnel Overhaul Mod v" + Version + "      ║");
            Logger.LogInfo("║     Realistic Explosion Fragments     ║");
            Logger.LogInfo("╚═══════════════════════════════════════╝");

            ShrapnelConfig.Bind(Config);

            Helpers.CustomResourceManager.Logger = Logger;

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

            // WHY: Manual patch for private coroutine method
            PatchFinishWorldGeneration();

            CreateCommandRegistrar();
        }

        /// <summary>
        /// Manually patches the private FinishWorldGeneration coroutine.
        /// Uses AccessTools to find the MoveNext method of the compiler-generated class.
        /// </summary>
        private void PatchFinishWorldGeneration()
        {
            try
            {
                // WHY: IEnumerator methods are compiled into nested classes with MoveNext.
                // The actual coroutine logic is in <FinishWorldGeneration>d__XX.MoveNext()
                // We need to find this nested type and patch its MoveNext method.
                
                // Alternative approach: patch UpdateChunkVisibility which is called
                // at the end of FinishWorldGeneration and IS public
                MethodInfo targetMethod = AccessTools.Method(typeof(WorldGeneration), "UpdateChunkVisibility");
                if (targetMethod != null)
                {
                    MethodInfo postfix = typeof(WorldLoadPatch).GetMethod("Postfix", 
                        BindingFlags.Static | BindingFlags.NonPublic);
                    _harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfix));
                    Logger.LogInfo("[Patch] WorldGeneration.UpdateChunkVisibility patched for PreWarm");
                }
                else
                {
                    Logger.LogWarning("[Patch] Could not find UpdateChunkVisibility, using fallback");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"[Patch] FinishWorldGeneration patch failed: {e.Message}");
            }
        }

        private void CreateCommandRegistrar()
        {
            try
            {
                GameObject registrar = new GameObject("ShrapnelMod_Registrar")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(registrar);
                registrar.AddComponent<ConsoleCommandRegistrar>();
            }
            catch (Exception e)
            {
                Logger.LogError($"[Init] Failed to create registrar: {e}");
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        internal static void RegisterCommands()
        {
            if (CommandsRegistered) return;

            try
            {
                ConsoleCommands.Register();
                CommandsRegistered = true;
                Log.LogInfo("[Console] Commands registered successfully");
                Debug.Log($"[ShrapnelMod] v{Version} loaded! Type 'help' to see shrapnel_ commands.");
            }
            catch (Exception e)
            {
                Log.LogError($"[Console] Registration failed: {e}");
            }
        }

        /// <summary>
        /// Pre-warms visual resources. Safe to call multiple times.
        /// </summary>
        internal static void WarmVisuals()
        {
            if (VisualsWarmed) return;

            try
            {
                ShrapnelVisuals.PreWarm();
                ShrapnelSpawnLogic.ResetThrottle();
                VisualsWarmed = true;
                Log.LogInfo("[Init] Visuals pre-warmed successfully");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[Init] PreWarm failed, will retry: {e.Message}");
            }
        }

        /// <summary>
        /// Resets visual state for new world. Called on world load.
        /// </summary>
        internal static void OnWorldLoad()
        {
            VisualsWarmed = false;
            ShrapnelVisuals.ResetMaterials();
            ShrapnelSpawnLogic.ResetThrottle();
            WarmVisuals();
        }
    }

    /// <summary>
    /// Waits for ConsoleScript readiness, registers commands.
    /// </summary>
    internal class ConsoleCommandRegistrar : MonoBehaviour
    {
        private void Update()
        {
            if (Plugin.CommandsRegistered)
            {
                Destroy(gameObject);
                return;
            }

            if (ConsoleScript.instance != null &&
                ConsoleScript.Commands != null &&
                ConsoleScript.Commands.Count > 0)
            {
                Plugin.RegisterCommands();
                Destroy(gameObject);
            }
        }
    }

    /// <summary>
    /// Backup: intercept ConsoleScript.Start().
    /// </summary>
    [HarmonyPatch(typeof(ConsoleScript), "Start")]
    internal static class ConsoleStartPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (ConsoleScript.Commands?.Count > 0 && !Plugin.CommandsRegistered)
            {
                Plugin.RegisterCommands();
            }
        }
    }

    /// <summary>
    /// Pre-warms visual resources when world finishes loading.
    /// Patched via manual Harmony in Plugin.PatchFinishWorldGeneration.
    ///
    /// WHY: FinishWorldGeneration is a private IEnumerator, can't use attribute.
    /// UpdateChunkVisibility is called at the end and is accessible.
    /// </summary>
    internal static class WorldLoadPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            // WHY: UpdateChunkVisibility is called frequently during gameplay.
            // Only warm once per world load, detected by checking if we already
            // warmed this session AND if generatingWorld just became false.
            try
            {
                if (WorldGeneration.world != null && 
                    !WorldGeneration.world.generatingWorld &&
                    !Plugin.VisualsWarmed)
                {
                    Plugin.OnWorldLoad();
                }
            }
            catch
            {
                // Silently ignore - non-critical
            }
        }
    }

    /// <summary>
    /// Alternative trigger: when PlayerCamera becomes available.
    /// Ensures PreWarm happens even if UpdateChunkVisibility patch fails.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), "Start")]
    internal static class PlayerCameraStartPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            // WHY: PlayerCamera.Start runs once per world load after player spawns.
            // Good fallback trigger for PreWarm.
            if (!Plugin.VisualsWarmed)
            {
                Plugin.WarmVisuals();
            }
        }
    }
}