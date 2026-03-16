using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Точка входа BepInEx-мода Shrapnel Overhaul.
    /// 
    /// Мод добавляет реалистичную систему осколков при взрывах:
    /// - Физические осколки с уроном, застреванием, рикошетом
    /// - Визуальные эффекты (пепел, искры, пар)
    /// - Debris на полу с красной обводкой
    /// - Осколки от пуль при попадании в металл
    /// - Влияние температуры на визуалы
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.user.scavshrapnelmod";
        public const string Name = "ScavShrapnelMod";
        public const string Version = "0.5.2";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal static bool CommandsRegistered;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Logger.LogInfo("╔═══════════════════════════════════════╗");
            Logger.LogInfo("║     Shrapnel Overhaul Mod v" + Version + "      ║");
            Logger.LogInfo("║     Realistic Explosion Fragments     ║");
            Logger.LogInfo("╚═══════════════════════════════════════╝");

            Helpers.CustomResourceManager.Logger = Logger;

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

            // Создаём отдельный GameObject для регистрации команд.
            // ВАЖНО: нельзя использовать BepInEx_Manager (this.gameObject) —
            // он уничтожается после Chainloader startup!
            CreateCommandRegistrar();
        }

        private void CreateCommandRegistrar()
        {
            try
            {
                GameObject registrar = new GameObject("ShrapnelMod_Registrar");
                registrar.hideFlags = HideFlags.HideAndDontSave;
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

        /// <summary>
        /// Регистрирует консольные команды мода.
        /// Вызывается из ConsoleCommandRegistrar когда консоль готова.
        /// </summary>
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
    }

    /// <summary>
    /// MonoBehaviour для ожидания готовности ConsoleScript.
    /// 
    /// Почему отдельный GameObject:
    /// BepInEx_Manager уничтожается после Chainloader startup complete,
    /// поэтому AddComponent на Plugin.gameObject не работает.
    /// 
    /// Условие готовности:
    /// - ConsoleScript.instance != null
    /// - ConsoleScript.Commands.Count > 0 (игра зарегистрировала свои команды)
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

            // Ждём пока игра зарегистрирует свои команды
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
    /// Резервный вариант: перехват ConsoleScript.Start().
    /// Сработает если консоль создаётся после нашего Update-чека.
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
}