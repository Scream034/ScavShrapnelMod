using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Effects;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Net;
using ScavShrapnelMod.Patches;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod
{
    /// <summary>
    /// In-game console commands and logging facade.
    ///
    /// Commands:
    ///   shrapnel_explode   — spawn explosion at cursor or player
    ///   shrapnel_debris    — spawn debris fragments at cursor
    ///   shrapnel_shot      — test full bullet shot pipeline at cursor
    ///   shrapnel_guninfo   — dump GunScript fields for power diagnosis
    ///   shrapnel_clear     — destroy all active shrapnel and particles
    ///   shrapnel_status    — mod status, pools, config (mat, full)
    ///   shrapnel_net       — network sync diagnostics (diag)
    ///   shrapnel_highlight — show all shards through walls (toggle)
    /// </summary>
    public static class Console
    {
        /// <summary>Logs a message to Unity console with mod prefix.</summary>
        public static void Log(string msg)
            => Debug.Log($"[{Plugin.Name}] {msg}");

        /// <summary>Logs an error to both BepInEx logger and Unity console.</summary>
        public static void Error(string msg)
        {
            string formatted = $"[{Plugin.Name}] {msg}";
            Plugin.Log?.LogError(formatted);
            Debug.LogError(formatted);
        }

        private static bool ArgsContain(string[] args, string value)
        {
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool ArgsContainAny(string[] args, params string[] values)
        {
            for (int i = 1; i < args.Length; i++)
                for (int j = 0; j < values.Length; j++)
                    if (string.Equals(args[i], values[j], StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        private static int FindInt(string[] args, int defaultValue)
        {
            for (int i = 1; i < args.Length; i++)
                if (int.TryParse(args[i], out int val))
                    return val;
            return defaultValue;
        }

        private static float FindFloat(string[] args, float defaultValue)
        {
            bool foundInt = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (float.TryParse(args[i], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float val))
                {
                    if (int.TryParse(args[i], out _))
                    {
                        if (foundInt) return val;
                        foundInt = true;
                        continue;
                    }
                    return val;
                }
            }
            return defaultValue;
        }

        /// <summary>Parses a weapon preset name into bullet power + turret flag.</summary>
        private static (float power, bool isTurret) ParseWeaponPreset(string[] args)
        {
            if (ArgsContainAny(args, "rifle", "ar"))    return (189f, false);
            if (ArgsContain(args, "shotgun"))            return (500f, false);
            if (ArgsContain(args, "turret"))             return (80f,  true);
            if (ArgsContain(args, "pistol"))             return (45f,  false);
            float custom = FindFloat(args, 0f);
            return custom > 0f ? (custom, false) : (45f, false);
        }

        /// <summary>Parses fire direction from args (L/R/U/D), default left.</summary>
        private static Vector2 ParseDirection(string[] args)
        {
            if (ArgsContain(args, "R")) return Vector2.right;
            if (ArgsContain(args, "U")) return Vector2.up;
            if (ArgsContain(args, "D")) return Vector2.down;
            return Vector2.left;
        }

        /// <summary>
        /// Registers all shrapnel_ console commands.
        /// Called once from <see cref="Plugin.RegisterCommands"/>.
        /// </summary>
        public static void Register()
        {
            RegisterExplode();
            RegisterDebris();
            RegisterShot();
            RegisterGunInfo();
            RegisterClear();
            RegisterStatus();
            RegisterNet();
            RegisterHighlight();
        }

        #region shrapnel_explode

        private static void RegisterExplode()
        {
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_explode",
                "Explosion at cursor. Args: mine|dynamite|turret|gravbag, player, -e, -net",
                (args) =>
                {
                    if (!PlayerCamera.main) throw new Exception("No world loaded!");

                    bool effectsOnly = ArgsContainAny(args, "-e", "effects", "effectsonly");
                    bool netVerbose = ArgsContainAny(args, "-net", "net");

                    Vector2 pos = ArgsContain(args, "player")
                        ? (Vector2)PlayerCamera.main.body.transform.position
                        : (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);

                    string explosionType = "mine";
                    if (ArgsContainAny(args, "dynamite", "tnt")) explosionType = "dynamite";
                    else if (ArgsContain(args, "turret")) explosionType = "turret";
                    else if (ArgsContainAny(args, "gravbag", "grav")) explosionType = "gravbag";

                    ExplosionParams param = new() { position = pos };
                    switch (explosionType)
                    {
                        case "dynamite":
                            param.range = 18f;
                            param.structuralDamage = 2000f;
                            break;
                        case "turret":
                            param.range = 9f;
                            param.velocity = 15f;
                            break;
                        case "gravbag":
                            param.disfigureChance = 0.15f;
                            break;
                    }

                    bool isClient = MultiplayerHelper.IsNetworkRunning
                        && !MultiplayerHelper.IsServer;

                    if (isClient || effectsOnly)
                    {
                        ExplosionTracker.Track(pos);
                        ShrapnelSpawnLogic.PreExplosion(param);
                        ShrapnelSpawnLogic.PostExplosion(param, preScan: true);
                        string mode = isClient ? "CLIENT-EFFECTS" : "EFFECTS";
                        Log($"{explosionType.ToUpper()} {mode} at {pos:F1}");
                    }
                    else
                    {
                        ExplosionTracker.Track(pos);
                        ShrapnelSpawnLogic.PreExplosion(param);
                        WorldGeneration.CreateExplosion(param);
                        ShrapnelSpawnLogic.PostExplosion(param, preScan: false);
                        Log($"{explosionType.ToUpper()} FULL at {pos:F1}");
                    }

                    if (netVerbose)
                        Log($"POST-EXPLODE:\n{ShrapnelNetSync.GetDiagnostics()}");
                },
                new Dictionary<int, List<string>>
                {
                    { 1, new List<string>
                        { "mine", "dynamite", "turret", "gravbag", "grav",
                          "-e", "effects", "player", "-net", "net" } }
                },
                new (string, string)[]
                {
                    ("string args", "mine|dynamite|turret|gravbag player -e -net")
                }
            ));
        }

        #endregion

        #region shrapnel_debris

        private static void RegisterDebris()
        {
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_debris",
                "Debris at cursor. [count] [force] [metal|stone|heavy|wood|electronic|all] [-v] [-net]",
                (args) =>
                {
                    if (!PlayerCamera.main) throw new Exception("No world loaded!");

                    bool allTypes = ArgsContain(args, "all");
                    bool verbose = ArgsContainAny(args, "-v", "verbose");
                    bool netDiag = ArgsContainAny(args, "-net", "net");

                    ShrapnelProjectile.ShrapnelType type = ShrapnelProjectile.ShrapnelType.Metal;
                    if (!allTypes)
                    {
                        if (ArgsContain(args, "stone")) type = ShrapnelProjectile.ShrapnelType.Stone;
                        else if (ArgsContainAny(args, "heavy", "heavymetal"))
                            type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                        else if (ArgsContain(args, "wood")) type = ShrapnelProjectile.ShrapnelType.Wood;
                        else if (ArgsContainAny(args, "electronic", "elec"))
                            type = ShrapnelProjectile.ShrapnelType.Electronic;
                    }

                    int count = Mathf.Clamp(FindInt(args, 5), 1, 100);
                    float force = FindFloat(args, 0f);
                    Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                    System.Random rng = verbose ? new(42) : new();

                    ShrapnelFactory.EnsureWoundSprites();

                    ShrapnelWeight[] weightCycle = {
                        ShrapnelWeight.Micro, ShrapnelWeight.Hot,
                        ShrapnelWeight.Medium, ShrapnelWeight.Heavy,
                        ShrapnelWeight.Massive };

                    if (verbose) Log($"PRE  -> {ShrapnelNetSync.GetBriefStatus()}");

                    int spawned = 0;
                    for (int i = 0; i < count; i++)
                    {
                        ShrapnelWeight weight;
                        ShrapnelProjectile.ShrapnelType spawnType;

                        if (allTypes)
                        {
                            weight = weightCycle[i % weightCycle.Length];
                            spawnType = (ShrapnelProjectile.ShrapnelType)(i % 5);
                        }
                        else
                        {
                            float roll = (float)rng.NextDouble();
                            if (roll < 0.08f) weight = ShrapnelWeight.Micro;
                            else if (roll < 0.23f) weight = ShrapnelWeight.Hot;
                            else if (roll < 0.53f) weight = ShrapnelWeight.Medium;
                            else if (roll < 0.88f) weight = ShrapnelWeight.Heavy;
                            else weight = ShrapnelWeight.Massive;
                            spawnType = type;
                        }

                        ShrapnelProjectile proj = ShrapnelFactory.Spawn(
                            pos, force, spawnType, weight, i, rng);

                        if (verbose && proj != null)
                        {
                            spawned++;
                            Log($"  #{spawned} netId={proj.NetSyncId}" +
                                $" type={spawnType} weight={weight}" +
                                $" trail={proj.HasTrail} heat={proj.Heat:F2}" +
                                $" scale={proj.transform.localScale.x:F3}");
                        }
                        else if (proj != null) spawned++;
                    }

                    Log($"Spawned {spawned}/{count} " +
                        $"{(allTypes ? "ALL" : type.ToString())}" +
                        $" (force={force:F0}) at {pos:F1}");

                    if (verbose || netDiag)
                        Log($"POST -> {ShrapnelNetSync.GetBriefStatus()}");
                },
                new Dictionary<int, List<string>>
                {
                    { 1, new List<string>
                        { "metal", "stone", "heavy", "wood", "electronic",
                          "all", "-v", "verbose", "-net", "net" } }
                },
                new (string, string)[]
                {
                    ("string args", "[count] [force] metal|stone|heavy|wood|electronic|all -v -net")
                }
            ));
        }

        #endregion

        #region shrapnel_shot

        private static void RegisterShot()
        {
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_shot",
                "Test shot effects at cursor. [preset] [L|R|U|D] [-metal] [muzzle|impact|sparks]",
                (args) =>
                {
                    if (!PlayerCamera.main) throw new Exception("No world loaded!");

                    Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                    var (power, isTurret) = ParseWeaponPreset(args);
                    Vector2 fireDir = ParseDirection(args);

                    bool modeAll = true;
                    bool modeMuzzle = ArgsContain(args, "muzzle");
                    bool modeImpact = ArgsContain(args, "impact");
                    bool modeSparks = ArgsContain(args, "sparks");
                    if (modeMuzzle || modeImpact || modeSparks) modeAll = false;

                    bool forceMetal = ArgsContainAny(args, "-metal", "metal");
                    float powerRatio = power / 25f;
                    Vector2 barrelPos = pos - fireDir * 10f;
                    Vector2 hitNormal = -fireDir;

                    string mode = modeAll ? "FULL"
                        : modeMuzzle ? "MUZZLE"
                        : modeImpact ? "IMPACT" : "SPARKS";

                    Log($"SHOT [{mode}]: power={power:F0} dir={fireDir}" +
                        $" turret={isTurret}");

                    if (MultiplayerHelper.IsNetworkRunning && MultiplayerHelper.IsClient)
                        Log("  [!] CLIENT: Effects are local only.");

                    var rng = new System.Random(
                        Mathf.RoundToInt(pos.x * 100f) * 397 ^
                        Mathf.RoundToInt(pos.y * 100f) ^ Time.frameCount);

                    // -- Muzzle effects (at barrel) --
                    if (modeAll || modeMuzzle)
                    {
                        Try("Flash", () =>
                            BulletImpactEffects.SpawnMuzzleFlash(barrelPos, fireDir));
                        Try("MuzzleDust", () =>
                            GroundDebrisLogic.SpawnFromMuzzleBlast(barrelPos, powerRatio, isTurret));
                        Try("BarrelSmoke", () =>
                            SpawnSmoke(barrelPos, fireDir, powerRatio, rng, isBarrel: true));
                    }

                    // -- Sparks (at impact) --
                    if (modeAll || modeSparks)
                    {
                        float sparkScale = 1f + (powerRatio - 1f)
                            * ShrapnelConfig.BulletDamageSparkMultiplier.Value;
                        if (isTurret) sparkScale *= 2f;

                        Try("Sparks", () =>
                        {
                            if (forceMetal)
                                BulletImpactEffects.SpawnFullImpact(
                                    pos, hitNormal, rng, false, sparkScale);
                            else
                                BulletImpactEffects.SpawnSparkShower(
                                    pos, hitNormal, rng, sparkScale * 0.5f);
                        });
                    }

                    // -- Impact dust plume (at impact) --
                    if (modeAll || modeImpact)
                    {
                        Try("ImpactDust", () =>
                            GroundDebrisLogic.SpawnFromBulletImpact(pos, powerRatio, fireDir));
                    }

                    // -- Gunpowder smoke (at impact) --
                    if (modeAll)
                    {
                        Try("Smoke", () =>
                            SpawnSmoke(pos, hitNormal, powerRatio, rng, isBarrel: false));
                    }

                    Log($"  Done. Pools: {DebrisTracker.TotalAliveParticles}");
                },
                new Dictionary<int, List<string>>
                {
                    { 1, new List<string>
                        { "pistol", "rifle", "shotgun", "turret",
                          "L", "R", "U", "D", "-metal", "metal",
                          "muzzle", "impact", "sparks" } }
                },
                new (string, string)[]
                {
                    ("string args",
                     "[pistol|rifle|shotgun|turret] [L|R|U|D] [-metal] [muzzle|impact|sparks]")
                }
            ));
        }

        /// <summary>Shared smoke spawner for shot test command.</summary>
        private static void SpawnSmoke(Vector2 pos, Vector2 dir, float powerRatio,
            System.Random rng, bool isBarrel)
        {
            if (!AshParticlePoolManager.EnsureReady()) return;
            float ps = Mathf.Sqrt(powerRatio);
            int count = Mathf.Clamp(Mathf.RoundToInt(isBarrel ? ps : 2f * ps),
                isBarrel ? 1 : 2, isBarrel ? 3 : 10);

            for (int i = 0; i < count; i++)
            {
                Vector2 spos = pos + (isBarrel ? dir * rng.Range(0.05f, 0.2f) : Vector2.zero)
                             + rng.InsideUnitCircle() * (isBarrel ? 0.05f : 0.1f);
                float g = rng.Range(isBarrel ? 0.2f : 0.12f, isBarrel ? 0.35f : 0.28f);
                float a = rng.Range(isBarrel ? 0.25f : 0.2f, isBarrel ? 0.45f : 0.4f);
                float sc = rng.Range(0.04f, 0.08f) * (isBarrel ? 1f : (0.7f + ps * 0.15f));

                var vis = new VisualParticleParams(sc,
                    new Color(g, g, g, a), isBarrel ? 6 : 8,
                    ShrapnelVisuals.TriangleShape.Chunk);

                Vector2 vel = dir * rng.Range(0.2f, isBarrel ? 1f : 0.8f)
                    + Vector2.up * rng.Range(0.1f, isBarrel ? 0.4f : 0.6f)
                    + rng.InsideUnitCircle() * 0.15f;

                var phy = AshPhysicsParams.Smoke(vel,
                    rng.Range(isBarrel ? 0.8f : 1.5f, isBarrel ? 2f : 3.5f),
                    gravity: rng.Range(-0.03f, 0.02f), drag: isBarrel ? 0.4f : 0.5f,
                    turbulence: rng.Range(0.3f, isBarrel ? 0.6f : 0.8f),
                    wind: new Vector2(rng.Range(-0.08f, 0.08f), 0.02f),
                    thermalLift: rng.Range(0.05f, isBarrel ? 0.1f : 0.2f));
                ParticleHelper.SpawnLit(spos, vis, phy, rng.Range(0f, 100f));
            }
        }

        #endregion

        #region shrapnel_guninfo

        private static void RegisterGunInfo()
        {
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_guninfo",
                "Dump weapon GunScript fields. Args: all (every field)",
                (args) =>
                {
                    if (!PlayerCamera.main) throw new Exception("No world loaded!");

                    var guns = UnityEngine.Object.FindObjectsOfType<GunScript>();
                    if (guns.Length == 0) { Log("No GunScript found!"); return; }

                    Vector2 playerPos = PlayerCamera.main.body != null
                        ? (Vector2)PlayerCamera.main.body.transform.position
                        : (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);

                    GunScript gun = guns[0];
                    float closestDist = float.MaxValue;
                    for (int i = 0; i < guns.Length; i++)
                    {
                        if (guns[i] == null) continue;
                        float d = Vector2.Distance(playerPos, guns[i].transform.position);
                        if (d < closestDist) { closestDist = d; gun = guns[i]; }
                    }

                    Log($"=== GUN: {gun.gameObject.name} (dist={closestDist:F1}) ===");

                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var fields = gun.GetType().GetFields(flags);

                    string[] priority = {
                        "structuredamage", "damage", "bulletdamage", "basedamage",
                        "knockback", "shotsperfire", "firerate", "bulletspeed", "metallic" };

                    Log("  -- Power fields --");
                    for (int i = 0; i < fields.Length; i++)
                    {
                        string lower = fields[i].Name.ToLowerInvariant();
                        bool hit = false;
                        for (int j = 0; j < priority.Length; j++)
                            if (lower.Contains(priority[j])) { hit = true; break; }
                        if (!hit) continue;

                        try
                        {
                            object val = fields[i].GetValue(gun);
                            Log($"  * {fields[i].Name} ({fields[i].FieldType.Name}) = {val}");
                        }
                        catch (Exception e)
                        {
                            Log($"  * {fields[i].Name} ERROR: {e.Message}");
                        }
                    }

                    float readDmg = ShotEffectRouter.ReadGunDamage(gun);
                    Log($"  ReadGunDamage -> {readDmg:F1}");

                    if (ArgsContainAny(args, "all", "-all"))
                    {
                        Log("  -- All fields --");
                        for (int i = 0; i < fields.Length; i++)
                        {
                            try
                            {
                                object val = fields[i].GetValue(gun);
                                string s = val?.ToString() ?? "null";
                                if (s.Length > 50) s = s.Substring(0, 50) + "..";
                                Log($"  {fields[i].Name} ({fields[i].FieldType.Name}) = {s}");
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        Log($"  ({fields.Length} fields. Use 'shrapnel_guninfo all' for full dump)");
                    }
                },
                new Dictionary<int, List<string>>
                {
                    { 1, new List<string> { "all", "-all" } }
                },
                new (string, string)[]
                {
                    ("string args", "[all] dump every field")
                }
            ));
        }

        #endregion

        #region shrapnel_clear

        private static void RegisterClear()
        {
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_clear",
                "Destroy all active shrapnel and pool particles",
                (args) =>
                {
                    string stats = DebrisTracker.GetStats();
                    DebrisTracker.Clear();
                    Log($"Cleared: {stats}");
                },
                null,
                new (string, string)[] { }
            ));
        }

        #endregion

        #region shrapnel_status

        private static void RegisterStatus()
        {
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_status",
                "Mod status. Args: mat (materials), full (config+mat)",
                (args) =>
                {
                    bool showMat = ArgsContainAny(args, "mat", "material");
                    bool showFull = ArgsContainAny(args, "full", "all");

                    Log($"=== {Plugin.Name} v{Plugin.Version} ===");
                    Log($"  {DebrisTracker.GetStats()}");

                    string mpInfo = MultiplayerHelper.IsNetworkRunning
                        ? (MultiplayerHelper.IsServer ? "MP:HOST" : "MP:CLIENT")
                        : "SP";
                    Log($"  Total: {DebrisTracker.TotalAliveParticles}" +
                        $" | {mpInfo} | {ShrapnelNetSync.GetBriefStatus()}");

                    if (showFull)
                    {
                        Log($"  SpawnMult: {ShrapnelConfig.SpawnCountMultiplier.Value:F1}" +
                            $"  MaxDebris: {ShrapnelConfig.MaxAliveDebris.Value}" +
                            $"  MaxSpeed: {ShrapnelConfig.GlobalMaxSpeed.Value:F0}");
                        Log($"  EnableFrags: {ShrapnelConfig.EnableBulletFragments.Value}" +
                            $"  EnableImpact: {ShrapnelConfig.EnableBulletImpactEffects.Value}" +
                            $"  MinFragPower: {ShrapnelConfig.MinBulletPowerForFragments.Value:F0}");
                        Log($"  MuzzleRadius: {ShrapnelConfig.MuzzleBlastRadius.Value}" +
                            $"  ImpactRadius: {ShrapnelConfig.BulletImpactBlastRadius.Value}" +
                            $"  ImpactMax: {ShrapnelConfig.BulletImpactBlastMaxParticles.Value}");
                        showMat = true;
                    }

                    if (showMat)
                    {
                        Log("  --- Materials ---");
                        var lit = ShrapnelVisuals.LitMaterial;
                        var unlit = ShrapnelVisuals.UnlitMaterial;
                        var trail = ShrapnelVisuals.TrailMaterial;

                        Log(lit != null
                            ? $"  Lit: {lit.shader?.name ?? "NULL"} rq={lit.renderQueue}"
                            : "  Lit: NULL!");
                        Log(unlit != null
                            ? $"  Unlit: {unlit.shader?.name ?? "NULL"} rq={unlit.renderQueue}"
                            : "  Unlit: NULL!");
                        Log(trail != null
                            ? $"  Trail: {trail.shader?.name ?? "NULL"}"
                            : "  Trail: NULL!");

                        var renderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
                        int corrupt = 0;
                        foreach (var sr in renderers)
                            if (sr.sharedMaterial != null && sr.sharedMaterial.shader == null)
                                corrupt++;
                        Log($"  Corrupt: {corrupt}/{renderers.Length}");
                    }
                },
                new Dictionary<int, List<string>>
                {
                    { 1, new List<string> { "mat", "material", "full", "all" } }
                },
                new (string, string)[]
                {
                    ("string args", "mat | full")
                }
            ));
        }

        #endregion

        #region shrapnel_net

        private static void RegisterNet()
        {
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_net",
                "Network sync info. Args: diag (fragment diagnosis)",
                (args) =>
                {
                    bool doDiag = ArgsContainAny(args, "diag", "-d");

                    Log($"  MP mod: {MultiplayerHelper.IsMultiplayerModPresent}");
                    Log($"  Network: {MultiplayerHelper.IsNetworkRunning}");

                    if (MultiplayerHelper.IsNetworkRunning)
                    {
                        Log($"  Role: {(MultiplayerHelper.IsServer ? "HOST" : "CLIENT")}");
                        Log($"  SpawnPhysics: {MultiplayerHelper.ShouldSpawnPhysicsShrapnel}");

                        string[] lines = ShrapnelNetSync.GetDiagnostics().Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = lines[i].Trim();
                            if (line.Length > 0) Log(line);
                        }
                    }

                    if (doDiag)
                    {
                        Log("  --- Fragment Sync Diagnosis ---");
                        if (!MultiplayerHelper.IsNetworkRunning)
                        {
                            Log("  Singleplayer: fragments are local only.");
                            return;
                        }

                        if (MultiplayerHelper.IsClient)
                        {
                            Log("  CLIENT: Fragments spawn on HOST, sync via MSG_SPAWN.");
                            Log("  * If HOST has no mod -> recv=0/0/0 (expected).");
                            Log("  * Console test commands are LOCAL only.");
                            Log("  * Real shots produce local VFX + server frags.");
                        }
                        else
                        {
                            Log($"  HOST: MinPowerForFrags = " +
                                $"{ShrapnelConfig.MinBulletPowerForFragments.Value:F0}");
                            Log($"  HOST: EnableFrags = " +
                                $"{ShrapnelConfig.EnableBulletFragments.Value}");

                            var guns = UnityEngine.Object.FindObjectsOfType<GunScript>();
                            if (guns.Length > 0)
                            {
                                float dmg = ShotEffectRouter.ReadGunDamage(guns[0]);
                                Log($"  Nearest gun structDmg = {dmg:F1}");
                                Log(dmg >= ShrapnelConfig.MinBulletPowerForFragments.Value
                                    ? "  [+] Power OK for fragments."
                                    : "  [!!] POWER TOO LOW - fragments won't spawn!");
                            }
                        }
                    }
                },
                new Dictionary<int, List<string>>
                {
                    { 1, new List<string> { "diag", "-d" } }
                },
                new (string, string)[]
                {
                    ("string args", "diag")
                }
            ));
        }

        #endregion

        #region shrapnel_highlight

        private static void RegisterHighlight()
        {
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_highlight",
                "Show all shards through walls for [seconds] (default 10). Toggle off with 0.",
                (args) =>
                {
                    float duration = FindFloat(args, 10f);
                    int seconds = FindInt(args, -1);
                    if (seconds >= 0) duration = seconds;

                    if (duration <= 0f)
                    {
                        ShardHighlighter.ClearAll();
                        Log("Highlight OFF.");
                        return;
                    }

                    int count = ShardHighlighter.HighlightAll(duration);
                    Log($"Highlighting {count} shards for {duration:F0}s" +
                        " (bright glow, top sorting layer).");
                },
                null,
                new (string, string)[]
                {
                    ("string args", "[seconds] (0 = off, default 10)")
                }
            ));
        }

        #endregion

        #region Helpers

        /// <summary>Try-catch wrapper that logs errors without crashing.</summary>
        private static void Try(string label, Action action)
        {
            try { action(); }
            catch (Exception e) { Error($"{label}: {e.Message}"); }
        }

        #endregion
    }

    /// <summary>
    /// Temporary MonoBehaviour that highlights all shrapnel through walls.
    /// Sets high sorting order + bright emission glow, reverts after timer.
    /// Self-destructs when complete.
    /// </summary>
    internal sealed class ShardHighlighter : MonoBehaviour
    {
        private float _timer;

        /// <summary>Original state per-shard for clean revert.</summary>
        private readonly struct SavedState
        {
            public readonly SpriteRenderer Sr;
            public readonly int OrigOrder;
            public readonly Color OrigColor;

            public SavedState(SpriteRenderer sr)
            {
                Sr = sr;
                OrigOrder = sr.sortingOrder;
                OrigColor = sr.color;
            }
        }

        private static readonly List<SavedState> _saved = new(128);
        private static ShardHighlighter _instance;

        private const int HighlightOrder = 9999;
        private static readonly Color HighlightColor = new(1f, 0.6f, 0.15f, 1f);
        private static readonly Color HighlightEmission = new(2f, 1f, 0.3f);

        /// <summary>
        /// Highlights all ShrapnelProjectile instances in scene.
        /// </summary>
        /// <returns>Number of shards highlighted.</returns>
        public static int HighlightAll(float duration)
        {
            ClearAll();

            var shards = FindObjectsOfType<ShrapnelProjectile>();
            if (shards.Length == 0) return 0;

            for (int i = 0; i < shards.Length; i++)
            {
                if (shards[i] == null) continue;
                var sr = shards[i].GetComponent<SpriteRenderer>();
                if (sr == null) continue;

                _saved.Add(new SavedState(sr));

                sr.sortingOrder = HighlightOrder;
                sr.color = HighlightColor;
                ParticleHelper.ApplyEmission(sr, HighlightEmission);
            }

            var go = new GameObject("ShardHighlighter")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _instance = go.AddComponent<ShardHighlighter>();
            _instance._timer = duration;

            return shards.Length;
        }

        /// <summary>Immediately reverts all highlights and destroys timer.</summary>
        public static void ClearAll()
        {
            for (int i = 0; i < _saved.Count; i++)
            {
                var s = _saved[i];
                if (s.Sr != null)
                {
                    s.Sr.sortingOrder = s.OrigOrder;
                    s.Sr.color = s.OrigColor;
                    ParticleHelper.ClearEmission(s.Sr);
                }
            }
            _saved.Clear();

            if (_instance != null)
            {
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer <= 0f)
            {
                ClearAll();
                Console.Log("Highlight expired.");
            }
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}