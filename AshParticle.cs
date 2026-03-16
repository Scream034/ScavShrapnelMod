using UnityEngine;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Визуальная частица пепла/грунта/пара.
    /// 
    /// Без физики — только transform.
    /// Настраиваемая гравитация позволяет:
    /// - Пепел: нормальная (1.5) — оседает за 2-3 сек
    /// - Ground debris: слабая (0.3-0.5) — "висит" у земли долго
    /// - Пар: очень слабая (0.2) — поднимается и растворяется
    /// </summary>
    public sealed class AshParticle : MonoBehaviour
    {
        private float _lifetime;
        private float _maxLifetime;
        private Vector3 _velocity;
        private SpriteRenderer _sr;
        private Color _baseColor;
        private float _wobblePhase;
        private float _gravity;

        /// <summary>Гравитация по умолчанию (для пепла).</summary>
        private const float DefaultGravity = 1.5f;

        /// <summary>Амплитуда покачивания (м/с).</summary>
        private const float WobbleAmplitude = 0.4f;

        /// <summary>Частота покачивания (рад/с).</summary>
        private const float WobbleFrequency = 2.5f;

        /// <summary>
        /// Инициализация с гравитацией по умолчанию (1.5).
        /// Для пепла и стандартных частиц.
        /// </summary>
        public void Initialize(Vector2 velocity, float lifetime, Color color, float wobblePhase)
        {
            Initialize(velocity, lifetime, color, wobblePhase, DefaultGravity);
        }

        /// <summary>
        /// Инициализация с кастомной гравитацией.
        /// 
        /// gravity = 0.3 → частица "парит" у земли (ground debris)
        /// gravity = 1.5 → нормальное оседание (пепел)
        /// gravity = 0.2 → поднимается вверх долго (пар)
        /// </summary>
        public void Initialize(Vector2 velocity, float lifetime, Color color, 
            float wobblePhase, float gravity)
        {
            _velocity = velocity;
            _lifetime = lifetime;
            _maxLifetime = lifetime;
            _baseColor = color;
            _wobblePhase = wobblePhase;
            _gravity = gravity;
            _sr = GetComponent<SpriteRenderer>();
            if (_sr != null) _sr.color = color;
        }

        private void Update()
        {
            _lifetime -= Time.deltaTime;
            if (_lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            float dt = Time.deltaTime;

            _velocity.y -= _gravity * dt;

            float wobble = Mathf.Sin(Time.time * WobbleFrequency + _wobblePhase) * WobbleAmplitude;
            Vector3 movement = _velocity * dt;
            movement.x += wobble * dt;

            transform.position += movement;

            float t = _lifetime / _maxLifetime;
            _sr.color = new Color(_baseColor.r, _baseColor.g, _baseColor.b, _baseColor.a * t);
        }
    }
}