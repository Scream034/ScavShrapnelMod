using UnityEngine;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Визуальный (fake) осколок без физики.
    /// 
    /// Создаёт "вау-эффект" при взрыве — десятки раскалённых точек
    /// разлетаются от эпицентра и гаснут за ~0.2 сек.
    /// 
    /// Нет Rigidbody, нет коллайдеров — минимальная нагрузка на CPU.
    /// Движение через transform — O(1), zero-alloc.
    /// </summary>
    public sealed class VisualShrapnel : MonoBehaviour
    {
        private Vector3 _direction;
        private float _speed;
        private float _lifetime;

        /// <summary>
        /// Инициализация. Скорость клампится к GlobalMaxSpeed.
        /// </summary>
        /// <param name="dir">Нормализованное направление полёта.</param>
        /// <param name="speed">Скорость (м/с).</param>
        /// <param name="lifetime">Время жизни в секундах.</param>
        public void Initialize(Vector2 dir, float speed, float lifetime)
        {
            _direction = dir;
            _speed = Mathf.Min(speed, ShrapnelSpawnLogic.GlobalMaxSpeed);
            _lifetime = lifetime;
        }

        private void Update()
        {
            _lifetime -= Time.deltaTime;
            if (_lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            transform.position += _direction * (_speed * Time.deltaTime);
        }
    }
}