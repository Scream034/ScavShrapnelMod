using System.Collections.Generic;
using UnityEngine;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Глобальный трекер живых debris/stuck объектов.
    /// 
    /// При превышении лимита (<see cref="ShrapnelConfig.MaxAliveDebris"/>)
    /// удаляет старейшие объекты (FIFO).
    /// 
    /// Предотвращает неограниченный рост GameObject'ов при серии взрывов.
    /// 
    /// Реализация: List с удалением с начала.
    /// Для 800 элементов O(n) RemoveAt(0) приемлемо (~0.01ms).
    /// </summary>
    public static class DebrisTracker
    {
        /// <summary>
        /// Список живых debris. Новые добавляются в конец, старые удаляются с начала.
        /// Null-элементы (уничтоженные Unity) очищаются периодически.
        /// </summary>
        private static readonly List<GameObject> _alive = new List<GameObject>();

        /// <summary>Счётчик для периодической очистки null-ов.</summary>
        private static int _cleanupCounter;

        /// <summary>Очистка null каждые N регистраций.</summary>
        private const int CleanupInterval = 50;

        /// <summary>
        /// Регистрирует новый debris-объект.
        /// Если лимит превышен — удаляет старейший.
        /// </summary>
        /// <param name="obj">GameObject осколка/debris.</param>
        public static void Register(GameObject obj)
        {
            if (obj == null) return;

            _alive.Add(obj);
            _cleanupCounter++;

            // Периодическая очистка мёртвых ссылок
            if (_cleanupCounter >= CleanupInterval)
            {
                _cleanupCounter = 0;
                PurgeNulls();
            }

            int max = ShrapnelConfig.MaxAliveDebris.Value;
            while (_alive.Count > max)
            {
                // Удаляем старейший (с начала списка)
                GameObject oldest = _alive[0];
                _alive.RemoveAt(0);

                // Unity может уже уничтожить объект
                if (oldest != null)
                    Object.Destroy(oldest);
            }
        }

        /// <summary>
        /// Удаляет все null-ссылки из списка.
        /// Unity уничтожает GameObject, но наша ссылка остаётся.
        /// 
        /// Итерация с конца для безопасного удаления.
        /// </summary>
        private static void PurgeNulls()
        {
            for (int i = _alive.Count - 1; i >= 0; i--)
            {
                // Unity overloads == null for destroyed objects
                if (_alive[i] == null)
                    _alive.RemoveAt(i);
            }
        }

        /// <summary>
        /// Текущее количество отслеживаемых объектов (включая потенциальные null).
        /// </summary>
        public static int Count => _alive.Count;

        /// <summary>
        /// Принудительно очищает все отслеживаемые объекты.
        /// Полезно при перезагрузке сцены.
        /// </summary>
        public static void Clear()
        {
            for (int i = 0; i < _alive.Count; i++)
            {
                if (_alive[i] != null)
                    Object.Destroy(_alive[i]);
            }
            _alive.Clear();
            _cleanupCounter = 0;
        }
    }
}