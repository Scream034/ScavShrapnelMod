using BepInEx.Configuration;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Централизованная конфигурация мода через BepInEx ConfigFile.
    /// 
    /// Все параметры доступны в файле BepInEx/config/ScavShrapnelMod.cfg.
    /// Изменения применяются при перезапуске игры.
    /// 
    /// Секции:
    /// - Performance: лимиты, throttle, lifetime
    /// - Bullets: осколки от пуль
    /// - Explosions: классификация взрывов
    /// - Visuals: эффекты, trails, пепел
    /// - Damage: урон, кровотечение, переломы
    /// - GroundDebris: частицы грунта
    /// </summary>
    public static class ShrapnelConfig
    {
        // ── Performance ──

        /// <summary>Максимум живых debris-объектов в мире. Старые удаляются первыми.</summary>
        public static ConfigEntry<int> MaxAliveDebris;

        /// <summary>Глобальный множитель количества спавнов (0.1 = 10%, 2.0 = 200%).</summary>
        public static ConfigEntry<float> SpawnCountMultiplier;

        /// <summary>Максимальная скорость любого осколка (м/с).</summary>
        public static ConfigEntry<float> GlobalMaxSpeed;

        /// <summary>Множитель скорости для всех осколков.</summary>
        public static ConfigEntry<float> GlobalSpeedBoost;

        /// <summary>Минимальная дистанция между спавнами одного кадра (м).</summary>
        public static ConfigEntry<float> MinDistanceBetweenSpawns;

        /// <summary>Макс DamageBlock-вызовов за кадр.</summary>
        public static ConfigEntry<int> MaxDamagePerFrame;

        /// <summary>Включить Debug.Log для каждого взрыва.</summary>
        public static ConfigEntry<bool> DebugLogging;

        // ── Bullets ──

        /// <summary>Минимальный интервал между спавнами осколков от пуль (кадры).</summary>
        public static ConfigEntry<int> BulletMinFramesBetweenSpawns;

        /// <summary>Количество осколков от пули: min.</summary>
        public static ConfigEntry<int> BulletFragmentsMin;

        /// <summary>Количество осколков от пули: max (exclusive).</summary>
        public static ConfigEntry<int> BulletFragmentsMax;

        /// <summary>Базовая скорость осколков от пуль (м/с).</summary>
        public static ConfigEntry<float> BulletBaseSpeed;

        /// <summary>Количество искр от пули: min.</summary>
        public static ConfigEntry<int> BulletSparksMin;

        /// <summary>Количество искр от пули: max (exclusive).</summary>
        public static ConfigEntry<int> BulletSparksMax;

        // ── Explosions: классификация ──

        /// <summary>Допуск сравнения параметров взрыва (±epsilon).</summary>
        public static ConfigEntry<float> ClassifyEpsilon;

        // ── Dynamite ──
        public static ConfigEntry<float> DynamiteRange;
        public static ConfigEntry<float> DynamiteStructuralDamage;
        public static ConfigEntry<int> DynamitePrimaryMin;
        public static ConfigEntry<int> DynamitePrimaryMax;
        public static ConfigEntry<float> DynamiteSpeed;
        public static ConfigEntry<int> DynamiteVisualMin;
        public static ConfigEntry<int> DynamiteVisualMax;

        // ── Turret ──
        public static ConfigEntry<float> TurretRange;
        public static ConfigEntry<float> TurretVelocity;
        public static ConfigEntry<int> TurretPrimaryMin;
        public static ConfigEntry<int> TurretPrimaryMax;
        public static ConfigEntry<float> TurretSpeed;
        public static ConfigEntry<int> TurretVisualMin;
        public static ConfigEntry<int> TurretVisualMax;

        // ── Mine (fallback) ──
        public static ConfigEntry<int> MinePrimaryMin;
        public static ConfigEntry<int> MinePrimaryMax;
        public static ConfigEntry<float> MineSpeed;
        public static ConfigEntry<int> MineVisualMin;
        public static ConfigEntry<int> MineVisualMax;

        // ── Visuals ──

        /// <summary>Множитель масштаба осколков от пуль (относительно взрывных).</summary>
        public static ConfigEntry<float> BulletScaleMultiplier;

        /// <summary>Множитель нагрева осколков от пуль.</summary>
        public static ConfigEntry<float> BulletHeatMultiplier;

        // ── Debris lifetime ──

        /// <summary>Время жизни Metal debris (сек).</summary>
        public static ConfigEntry<float> DebrisLifetimeMetal;

        /// <summary>Время жизни HeavyMetal debris (сек).</summary>
        public static ConfigEntry<float> DebrisLifetimeHeavyMetal;

        /// <summary>Время жизни Stone debris (сек).</summary>
        public static ConfigEntry<float> DebrisLifetimeStone;

        /// <summary>Время жизни Wood debris (сек).</summary>
        public static ConfigEntry<float> DebrisLifetimeWood;

        /// <summary>Время жизни Electronic debris (сек).</summary>
        public static ConfigEntry<float> DebrisLifetimeElectronic;

        /// <summary>Время жизни Stuck осколка (сек).</summary>
        public static ConfigEntry<float> StuckLifetime;

        // ── Interact ──

        /// <summary>Максимальная дистанция для уничтожения кликом (тайлы).</summary>
        public static ConfigEntry<float> MaxInteractDistance;

        /// <summary>
        /// Инициализирует все конфиг-записи. Вызывать из Plugin.Awake().
        /// </summary>
        public static void Bind(ConfigFile cfg)
        {
            // ── Performance ──
            MaxAliveDebris = cfg.Bind("Performance", "MaxAliveDebris", 800,
                new ConfigDescription("Максимум живых debris в мире. Старые удаляются первыми.",
                    new AcceptableValueRange<int>(100, 5000)));

            SpawnCountMultiplier = cfg.Bind("Performance", "SpawnCountMultiplier", 1f,
                new ConfigDescription("Множитель количества спавнов (0.1–3.0).",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            GlobalMaxSpeed = cfg.Bind("Performance", "GlobalMaxSpeed", 128f,
                new ConfigDescription("Макс скорость осколка (м/с).",
                    new AcceptableValueRange<float>(30f, 500f)));

            GlobalSpeedBoost = cfg.Bind("Performance", "GlobalSpeedBoost", 1.5f,
                new ConfigDescription("Множитель скорости всех осколков.",
                    new AcceptableValueRange<float>(0.5f, 5f)));

            MinDistanceBetweenSpawns = cfg.Bind("Performance", "MinDistanceBetweenSpawns", 1.5f,
                "Минимальная дистанция между спавнами (м).");

            MaxDamagePerFrame = cfg.Bind("Performance", "MaxDamagePerFrame", 5,
                new ConfigDescription("Макс DamageBlock вызовов за кадр.",
                    new AcceptableValueRange<int>(1, 20)));

            DebugLogging = cfg.Bind("Performance", "DebugLogging", false,
                "Включить Debug.Log для каждого взрыва.");

            // ── Bullets ──
            BulletMinFramesBetweenSpawns = cfg.Bind("Bullets", "MinFramesBetweenSpawns", 3,
                new ConfigDescription("Мин. кадров между спавнами осколков от пуль.",
                    new AcceptableValueRange<int>(1, 30)));

            BulletFragmentsMin = cfg.Bind("Bullets", "FragmentsMin", 1,
                new ConfigDescription("Мин. осколков от пули.",
                    new AcceptableValueRange<int>(0, 10)));

            BulletFragmentsMax = cfg.Bind("Bullets", "FragmentsMax", 4,
                new ConfigDescription("Макс. осколков от пули (exclusive).",
                    new AcceptableValueRange<int>(1, 15)));

            BulletBaseSpeed = cfg.Bind("Bullets", "BaseSpeed", 25f,
                "Базовая скорость осколков от пуль (м/с).");

            BulletSparksMin = cfg.Bind("Bullets", "SparksMin", 4,
                new ConfigDescription("Мин. искр от пули.",
                    new AcceptableValueRange<int>(0, 20)));

            BulletSparksMax = cfg.Bind("Bullets", "SparksMax", 8,
                new ConfigDescription("Макс. искр от пули (exclusive).",
                    new AcceptableValueRange<int>(1, 30)));

            BulletScaleMultiplier = cfg.Bind("Bullets", "ScaleMultiplier", 0.72f,
                "Множитель масштаба осколков от пуль.");

            BulletHeatMultiplier = cfg.Bind("Bullets", "HeatMultiplier", 0.5f,
                "Множитель нагрева осколков от пуль.");

            // ── Explosions ──
            ClassifyEpsilon = cfg.Bind("Explosions", "ClassifyEpsilon", 0.5f,
                "Допуск сравнения параметров взрыва для классификации.");

            DynamiteRange = cfg.Bind("Explosions.Dynamite", "Range", 18f,
                "Ожидаемый range динамита для классификации.");
            DynamiteStructuralDamage = cfg.Bind("Explosions.Dynamite", "StructuralDamage", 2000f,
                "Ожидаемый structuralDamage динамита.");
            DynamitePrimaryMin = cfg.Bind("Explosions.Dynamite", "PrimaryMin", 12, "");
            DynamitePrimaryMax = cfg.Bind("Explosions.Dynamite", "PrimaryMax", 21, "");
            DynamiteSpeed = cfg.Bind("Explosions.Dynamite", "Speed", 35f, "Базовая скорость (м/с).");
            DynamiteVisualMin = cfg.Bind("Explosions.Dynamite", "VisualMin", 150, "");
            DynamiteVisualMax = cfg.Bind("Explosions.Dynamite", "VisualMax", 226, "");

            TurretRange = cfg.Bind("Explosions.Turret", "Range", 9f, "");
            TurretVelocity = cfg.Bind("Explosions.Turret", "Velocity", 15f, "");
            TurretPrimaryMin = cfg.Bind("Explosions.Turret", "PrimaryMin", 6, "");
            TurretPrimaryMax = cfg.Bind("Explosions.Turret", "PrimaryMax", 13, "");
            TurretSpeed = cfg.Bind("Explosions.Turret", "Speed", 40f, "");
            TurretVisualMin = cfg.Bind("Explosions.Turret", "VisualMin", 45, "");
            TurretVisualMax = cfg.Bind("Explosions.Turret", "VisualMax", 76, "");

            MinePrimaryMin = cfg.Bind("Explosions.Mine", "PrimaryMin", 25, "");
            MinePrimaryMax = cfg.Bind("Explosions.Mine", "PrimaryMax", 37, "");
            MineSpeed = cfg.Bind("Explosions.Mine", "Speed", 45f, "");
            MineVisualMin = cfg.Bind("Explosions.Mine", "VisualMin", 75, "");
            MineVisualMax = cfg.Bind("Explosions.Mine", "VisualMax", 121, "");

            // ── Debris Lifetime ──
            DebrisLifetimeMetal = cfg.Bind("Lifetime", "Metal", 600f,
                "Время жизни Metal debris (сек).");
            DebrisLifetimeHeavyMetal = cfg.Bind("Lifetime", "HeavyMetal", 750f, "");
            DebrisLifetimeStone = cfg.Bind("Lifetime", "Stone", 360f, "");
            DebrisLifetimeWood = cfg.Bind("Lifetime", "Wood", 240f, "");
            DebrisLifetimeElectronic = cfg.Bind("Lifetime", "Electronic", 450f, "");
            StuckLifetime = cfg.Bind("Lifetime", "Stuck", 15f,
                "Время жизни осколка в стене (сек).");

            // ── Interact ──
            MaxInteractDistance = cfg.Bind("Interact", "MaxClickDistance", 3f,
                "Макс. дистанция уничтожения кликом (тайлы).");
        }
    }
}