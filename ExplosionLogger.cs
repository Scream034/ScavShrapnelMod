namespace ScavShrapnelMod
{
    /// <summary>
    /// Статическое хранилище последнего ExplosionParams.
    /// 
    /// Мост между Harmony Prefix и Postfix:
    /// Prefix записывает → Postfix/другие системы читают.
    /// 
    /// ExplosionParams — struct (value type), Record() создаёт копию — thread-safe.
    /// </summary>
    public static class ExplosionLogger
    {
        /// <summary>Последние записанные параметры взрыва (копия struct).</summary>
        public static ExplosionParams LastParams { get; private set; }

        /// <summary>
        /// Записывает копию параметров взрыва.
        /// Вызывается из ShrapnelSpawnLogic.TrySpawnFromExplosion.
        /// </summary>
        public static void Record(ExplosionParams param)
        {
            LastParams = param;
        }
    }
}