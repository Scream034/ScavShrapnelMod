using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod.Effects
{
    /// <summary>
    /// Static storage for the most recent <see cref="ExplosionParams"/>.
    ///
    /// Bridge between Harmony Prefix and Postfix:
    ///   Prefix calls <see cref="Record"/> → Postfix / other systems read <see cref="LastParams"/>.
    ///
    /// <see cref="ExplosionParams"/> is a struct (value type), so
    /// <see cref="Record"/> creates a copy — safe for concurrent reads.
    /// </summary>
    public static class ExplosionLogger
    {
        /// <summary>Copy of the last recorded explosion parameters.</summary>
        public static ExplosionParams LastParams { get; private set; }

        /// <summary>
        /// Records a copy of explosion parameters.
        /// Called from <see cref="ShrapnelSpawnLogic.PreExplosion"/>.
        /// </summary>
        /// <param name="param">Explosion parameters to store (copied as value type).</param>
        public static void Record(ExplosionParams param)
        {
            LastParams = param;
        }
    }
}