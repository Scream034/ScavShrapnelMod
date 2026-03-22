using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Net;
using ScavShrapnelMod.Patches;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// BepInEx plugin entry point.
    ///
    /// DETECTION ARCHITECTURE:
    ///   Layer 1: Harmony Prefix/Postfix — WorldGeneration.CreateExplosion (public static)
    ///   Layer 2: Harmony PatchAll — DestroyBackupPatch, WorldLoadPatch, etc.
    ///   Layer 3: MonoBehaviour polling — TurretShotMonitor, GunShotMonitor
    ///            (Harmony can't intercept private Update() on Unity 2022.3)
    ///   Layer 4: MonoBehaviour polling — TurretDeathMonitor (health ≤ 0)
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ScavShrapnelMod";
        public const string Name = "ScavShrapnelMod";
        public const string Version = "0.9.2";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal static bool CommandsRegistered;
        internal static bool VisualsWarmed;

        private Harmony _harmony;
        private static bool _resetNotificationShown;

        private void Awake()
        {
            Log = Logger;

            Logger.LogInfo("╔═══════════════════════════════════════╗");
            Logger.LogInfo("║     Shrapnel Overhaul Mod v" + Version + "      ║");
            Logger.LogInfo("║     Realistic Explosion Fragments     ║");
            Logger.LogInfo("╚═══════════════════════════════════════╝");

            ShrapnelConfig.Bind(Config);
            GameVersionChecker.Check();

            _harmony = new Harmony(Guid);
            ApplyPatches();
            CreateCommandRegistrar();
        }

        private void ApplyPatches()
        {
            // LAYER 1: WorldGeneration.CreateExplosion (manual — needs priority)
            try
            {
                var targetMethod = AccessTools.Method(
                    typeof(WorldGeneration),
                    nameof(WorldGeneration.CreateExplosion),
                    new[] { typeof(ExplosionParams) });

                if (targetMethod != null)
                {
                    _harmony.Patch(
                        targetMethod,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(
                                typeof(CreateExplosionPatch),
                                nameof(CreateExplosionPatch.Prefix)))
                        { priority = Priority.Low },
                        postfix: new HarmonyMethod(
                            AccessTools.Method(
                                typeof(CreateExplosionPatch),
                                nameof(CreateExplosionPatch.Postfix)))
                        { priority = Priority.Last });

                    Logger.LogInfo(
                        "[Patch] ✓ Layer 1: WorldGeneration.CreateExplosion");
                }
                else
                {
                    Logger.LogError(
                        "[Patch] ✗ Layer 1: CreateExplosion not found!");
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"[Patch] ✗ Layer 1: {e.Message}");
            }

            // LAYER 2: PatchAll (DestroyBackupPatch, ConsoleStartPatch,
            //           WorldLoadPatch, PlayerCameraStartPatch)
            try
            {
                _harmony.PatchAll();
                Logger.LogInfo("[Patch] ✓ PatchAll complete");
            }
            catch (Exception e)
            {
                Logger.LogError(
                    $"[Patch] PatchAll FAILED: {e.Message}\n{e.StackTrace}");
            }

            // LAYER 3: Shot detectors (MonoBehaviour — proven stable)
            ShotDetectorPatches.CreateDetectors();

            // LAYER 4: Turret death monitor
            CreateMonoBehaviour<TurretDeathMonitor>("TurretDeathMonitor");

            Logger.LogInfo("[Patch] ✓ All detection layers active");
        }

        /// <summary>
        /// Creates a persistent DontDestroyOnLoad MonoBehaviour.
        /// </summary>
        private void CreateMonoBehaviour<T>(string name) where T : MonoBehaviour
        {
            try
            {
                var go = new GameObject($"ShrapnelMod_{name}")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(go);
                go.AddComponent<T>();
                Logger.LogInfo($"[Patch] ✓ {name} created");
            }
            catch (Exception e)
            {
                Logger.LogError($"[Patch] ✗ {name}: {e.Message}");
            }
        }

        private void CreateCommandRegistrar()
        {
            try
            {
                var registrar = new GameObject("ShrapnelMod_Registrar")
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
                Console.Register();
                CommandsRegistered = true;
                Log.LogInfo("[Console] Commands registered successfully");
                Console.Log(
                    $"v{Version} loaded! Type 'help' to see shrapnel_ commands.");
            }
            catch (Exception e)
            {
                Log.LogError($"[Console] Registration failed: {e}");
            }
        }

        internal static void WarmVisuals()
        {
            if (VisualsWarmed) return;
            try
            {
                ShrapnelVisuals.PreWarm();
                AshParticlePoolManager.Initialize();
                ParticlePoolManager.Initialize();
                ShrapnelSpawnLogic.ResetThrottle();
                VisualsWarmed = true;

                if (MultiplayerHelper.IsNetworkRunning)
                    ShrapnelNetSync.Initialize();

                Log.LogInfo(
                    $"[Init] Warmed. AshPools={AshParticlePoolManager.Initialized}" +
                    $" SparkPool={ParticlePoolManager.Initialized}");
            }
            catch (Exception e)
            {
                Log.LogError(
                    $"[Init] PreWarm FAILED: {e.Message}\n{e.StackTrace}");
            }
        }

        internal static void OnWorldLoad()
        {
            ShrapnelNetSync.Shutdown();
            VisualsWarmed = false;
            ShrapnelVisuals.ResetMaterials();
            AshParticlePoolManager.Shutdown();
            ParticlePoolManager.Shutdown();
            ShrapnelSpawnLogic.ResetThrottle();

            // WHY: Clear stale GunScript/TurretScript references from previous world
            ShotDetectorPatches.ForceRescan();

            WarmVisuals();
            ShowConfigResetNotification();
        }

        private static void ShowConfigResetNotification()
        {
            if (_resetNotificationShown) return;
            _resetNotificationShown = true;

            string notification = ShrapnelConfig.GetResetNotification();
            if (notification == null) return;

            Log.LogWarning(notification);
            Console.Log(notification);
        }
    }

    internal sealed class ConsoleCommandRegistrar : MonoBehaviour
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

    [HarmonyPatch(typeof(ConsoleScript), "Start")]
    internal static class ConsoleStartPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (ConsoleScript.Commands?.Count > 0
                && !Plugin.CommandsRegistered)
                Plugin.RegisterCommands();
        }
    }

    [HarmonyPatch(typeof(WorldGeneration), "UpdateChunkVisibility")]
    internal static class WorldLoadPatch
    {
        private static WorldGeneration _lastWarmedWorld;

        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                var world = WorldGeneration.world;
                if (world == null || world.generatingWorld) return;
                if (world == _lastWarmedWorld) return;

                _lastWarmedWorld = world;
                Plugin.OnWorldLoad();
                Plugin.Log.LogInfo(
                    "[WorldLoad] Visuals pre-warmed on world load");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), "Start")]
    internal static class PlayerCameraStartPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (!Plugin.VisualsWarmed)
            {
                Plugin.WarmVisuals();
                Plugin.Log.LogInfo(
                    "[PlayerCamera] Visuals pre-warmed on camera start");
            }
        }
    }
}