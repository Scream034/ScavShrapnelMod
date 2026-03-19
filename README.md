
# ЗАДАЧА: Сетевая синхронизация физических осколков (ShrapnelNetSync) для мода Casualties Unknown

---

## 1. КОНТЕКСТ ПРОЕКТА

2D сайд-скроллер "Casualties Unknown" на Unity 2022.3 (URP 2D).

Мод ScavShrapnelMod добавляет реалистичную физическую шрапнель к взрывам:
- `ShrapnelProjectile` — физический осколок с Rigidbody2D, CircleCollider2D, уроном, застреванием в стенах, красной пульсирующей обводкой, остыванием цвета
- `ShrapnelFactory` — создаёт ShrapnelProjectile через SpawnCore()
- Визуальные эффекты (пыль, дым, искры) синхронизируются отдельно — не наша задача

Есть мультиплеерный мод KrokoshaCasualtiesMP v2.1.2 (далее "MP мод") на Unity.Netcode (NGO).

### Текущая проблема:
Сервер создаёт настоящие ShrapnelProjectile → клиент их не видит вообще.
Нужно: клиент видит осколки на точно тех же позициях что и сервер.

---

## 2. АРХИТЕКТУРА: Server Authority + Client Mirror

```
СЕРВЕР (хост):
  ShrapnelProjectile (реальная физика, урон, коллайдеры)
       ↓  при создании
  ShrapnelNetSync.ServerRegister(proj)
       ↓  10 раз в секунду
  Отправляет ShrapnelSnapshot (позиции всех живых осколков)
       ↓  при уничтожении
  ShrapnelNetSync.ServerUnregister(netId)

КЛИЕНТ:
  Получает ShrapnelSpawn → создаёт ClientMirrorShrapnel (визуал без физики)
  Получает ShrapnelSnapshot → интерполирует позицию зеркала к серверной
  Получает ShrapnelDestroy → уничтожает зеркало
```

### ЖЁСТКИЕ ПРАВИЛА АРХИТЕКТУРЫ:

1. `ShrapnelProjectile` НЕ ЗНАЕТ о сети. Единственное добавление: поле `public int NetSyncId;`
2. `ShrapnelFactory.SpawnCore()` — единственная точка вызова ServerRegister
3. `ShrapnelNetSync` существует ТОЛЬКО когда `MultiplayerHelper.IsNetworkRunning == true`
4. В singleplayer ShrapnelNetSync не создаётся и не влияет на производительность
5. Весь код проверяет роль через `MultiplayerHelper.IsServer` / `MultiplayerHelper.IsClient`
6. Визуальные эффекты (particles, ash, sparks) НЕ синхронизируются — они cosmetic

---

## 3. СЕТЕВОЙ ПРОТОКОЛ (оптимизированный)

### 3.1. ShrapnelSpawn (Reliable, при создании осколка)
Отправляется ОДИН раз при создании ShrapnelProjectile на сервере.

Данные:
```
ushort netId          — уникальный ID (counter 1..65535, затем wraparound)
short  posX           — position.x × 10 (точность 0.1 юнита, диапазон ±3276)
short  posY           — position.y × 10
byte   typePacked     — bits[0..2] = ShrapnelType (0-4), bits[3..5] = ShrapnelWeight (0-4)
byte   heatPacked     — heat × 255 (0.0-1.0 → 0-255)
byte   shapeIndex     — TriangleShape enum (0-5)
ushort scalePacked    — scale × 1000 (точность 0.001)
```
Итого: 11 байт на осколок.

### 3.2. ShrapnelSnapshot (Unreliable, каждые 100мс)
Отправляется периодически со всеми живыми осколками.

Данные:
```
ushort count          — количество осколков (0-65535)
[ для каждого осколка:
  ushort netId        — ID осколка
  short  posX         — position.x × 10
  short  posY         — position.y × 10
]
```
Итого: 2 + 6×N байт.
При 100 осколках: 602 байта.
При 10 Гц: ~6 КБ/сек — безопасно для LAN и интернета.

### 3.3. ShrapnelDestroy (ReliableSequenced, при уничтожении)
Данные:
```
ushort netId          — ID уничтоженного осколка
```
Итого: 2 байта.

---

## 4. ИНТЕРПОЛЯЦИЯ НА КЛИЕНТЕ (для ping 50-200мс)

Осколки летят по баллистике — траектория предсказуема между snapshot'ами.
Используем экстраполяцию по velocity + коррекцию к серверной позиции:

```csharp
// При получении ShrapnelSnapshot:
Vector2 velocity = (newServerPos - _lastServerPos) / _timeSinceLastSnapshot;
_lastServerPos = newServerPos;
_predictedVelocity = velocity;
_timeSinceSnapshot = 0f;

// В Update:
_timeSinceSnapshot += Time.deltaTime;
Vector2 predicted = _lastServerPos + _predictedVelocity * _timeSinceSnapshot;
// Плавная коррекция к предсказанной позиции
transform.position = Vector2.MoveTowards(
    transform.position,
    predicted,
    Time.deltaTime * InterpolationSpeed  // константа ~15f
);
```

Если snapshot не приходил > 0.5 сек — осколок застрял (зашёл в стену на сервере),
визуально это нормально — зеркало тоже останавливается.

---

## 5. СЕТЕВОЙ API (из декомпиляции KrokoshaCasualtiesMP v2.1.2)

### 5.1. Ключевые поля (проверено через reflection dump):
```csharp
// ВАЖНО: network_system_is_running — это PROPERTY с private setter, не field!
// Поэтому ищем через GetProperty(), а не GetField()
public static bool network_system_is_running { get; private set; }

// is_client — это обычный public static field
public static bool is_client;
// true = клиент, false = хост/сервер

// Derived (не через reflection, просто знаем):
// is_server = !is_client
// is_dedicated_server — тоже property
```

### 5.2. MultiplayerHelper — наш класс для проверки роли:
```csharp
// Уже существует в проекте. Использует:
_isRunningProp = mpType.GetProperty("network_system_is_running", allFlags);
_isClientField = mpType.GetField("is_client", allFlags);

// Публичный API:
MultiplayerHelper.IsNetworkRunning  → bool
MultiplayerHelper.IsClient          → bool
MultiplayerHelper.IsServer          → bool (= IsNetworkRunning && !IsClient)
MultiplayerHelper.ShouldSpawnPhysicsShrapnel  → bool (false на клиенте)
```

### 5.3. Отправка сообщений сервером всем клиентам:
```csharp
// ServerMain.AllClientIdsExceptHost — через reflection:
Type serverMainType = /* найти в AppDomain.CurrentDomain.GetAssemblies() */;
// Тип: KrokoshaCasualtiesMP.ServerMain
// Property: public static IReadOnlyList<ulong> AllClientIdsExceptHost { get; }

FastBufferWriter writer = new FastBufferWriter(size, Allocator.Temp);
writer.WriteValueSafe<ushort>(in netId, default(FastBufferWriter.ForPrimitives));
writer.WriteValueSafe(in position);  // Vector2
NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
    "ShrShrapnelSpawn",          // имя сообщения — уникальный префикс "Shr"
    allClientIds,                 // IReadOnlyList<ulong>
    writer,
    NetworkDelivery.ReliableSequenced
);
writer.Dispose();  // ОБЯЗАТЕЛЬНО
```

### 5.4. Отправка клиентом серверу (не нужна в нашем случае):
```csharp
// Наш синк только Server → Client. Клиент ничего не отправляет.
```

### 5.5. Регистрация handler'ов на клиенте:
```csharp
// ВАЖНО: Регистрируется ОДИН РАЗ при инициализации ShrapnelNetSync.
// Не регистрировать повторно при каждом взрыве!
NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
    "ShrShrapnelSpawn",
    (ulong senderId, FastBufferReader reader) =>
    {
        reader.ReadValueSafe<ushort>(out ushort netId, default(FastBufferWriter.ForPrimitives));
        reader.ReadValueSafe(out Vector2 pos);
        // ... create ClientMirrorShrapnel ...
    }
);
```

### 5.6. FastBufferWriter/Reader — правильный синтаксис:
```csharp
// ЗАПИСЬ примитивов (int, ushort, byte, float, bool):
writer.WriteValueSafe<ushort>(in value, default(FastBufferWriter.ForPrimitives));
writer.WriteValueSafe<byte>(in value, default(FastBufferWriter.ForPrimitives));
writer.WriteValueSafe<float>(in value, default(FastBufferWriter.ForPrimitives));
writer.WriteValueSafe<bool>(in value, default(FastBufferWriter.ForPrimitives));

// ЗАПИСЬ Vector2 (без ForPrimitives!):
writer.WriteValueSafe(in vector2);

// ЗАПИСЬ short (не примитив в NGO, пишем как 2 байта вручную):
writer.WriteValueSafe<short>(in value, default(FastBufferWriter.ForPrimitives));

// ЧТЕНИЕ:
reader.ReadValueSafe<ushort>(out ushort val, default(FastBufferWriter.ForPrimitives));
reader.ReadValueSafe(out Vector2 pos);
reader.ReadValueSafe<short>(out short val, default(FastBufferWriter.ForPrimitives));

// Allocator.Temp — автоматически очищается в конце кадра Unity.
// Всё равно вызывай writer.Dispose() для немедленного освобождения.
```

### 5.7. NetworkDelivery:
```csharp
NetworkDelivery.Unreliable        // snapshot (может потеряться — нормально)
NetworkDelivery.Reliable          // spawn (должен дойти)
NetworkDelivery.ReliableSequenced // destroy (должен дойти И в правильном порядке)
```

### 5.8. Имена сообщений — используй префикс "Shr":
```csharp
const string MSG_SPAWN    = "ShrShrapnelSpawn";
const string MSG_SNAPSHOT = "ShrShrapnelSnapshot";
const string MSG_DESTROY  = "ShrShrapnelDestroy";
```

---

## 6. СУЩЕСТВУЮЩИЙ КОД (краткое описание файлов)

### ShrapnelProjectile.cs:
- MonoBehaviour на физическом осколке
- Поля: Type (ShrapnelType enum), Weight (ShrapnelWeight enum), Heat (float 0-1), Damage, BleedAmount, Seed, CanBreak, HasTrail
- FSM: Flying → Stuck / Debris
- Визуал: SpriteRenderer с triangle sprite + outline child GameObject с пульсирующей красной обводкой
- Heat cooling: Heat -= 0.42f * dt → меняет цвет от оранжевого к холодному
- OnDestroy: нужно добавить вызов ShrapnelNetSync.ServerUnregister(NetSyncId)

### ShrapnelFactory.cs:
- static class с методами Spawn(), SpawnDirectional(), SpawnCore()
- SpawnCore() создаёт GameObject → добавляет Rigidbody2D, CircleCollider2D, SpriteRenderer, ShrapnelProjectile
- В конце SpawnCore(): `DebrisTracker.Register(obj);` — здесь нужно добавить `ShrapnelNetSync.ServerRegister(proj);`

### MultiplayerHelper.cs:
- static class, обнаруживает MP мод через reflection
- IsNetworkRunning, IsClient, IsServer, ShouldSpawnPhysicsShrapnel
- Уже написан и работает

### ShrapnelVisuals.cs:
- GetTriangleSprite(TriangleShape) → Sprite
- GetColdColor(ShrapnelType) → Color
- GetHotColor() → Color (оранжевый)
- LitMaterial, UnlitMaterial

### Plugin.cs:
- WarmVisuals() — вызывать ShrapnelNetSync.Initialize() после пулов
- OnWorldLoad() — вызывать ShrapnelNetSync.Shutdown()

---

## 7. ТРЕБОВАНИЯ К ВЫХОДНОМУ КОДУ

### Namespace: ScavShrapnelMod.Net

### 7.1. ShrapnelNetSync.cs — главный менеджер:

```
MonoBehaviour. Создаётся в Plugin.WarmVisuals() только если IsNetworkRunning.
DontDestroyOnLoad. hideFlags = HideAndDontSave.

КОНСТАНТЫ (вверху файла, легко менять):
  SnapshotHz = 10f          — частота snapshot'ов
  InterpolationSpeed = 15f  — скорость интерполяции на клиенте
  MirrorTimeout = 2f        — секунд без обновления → уничтожить зеркало
  MaxShrapnel = 1000        — максимум отслеживаемых осколков (защита от утечки)

СЕРВЕР:
  Dictionary<ushort, ShrapnelProjectile> _tracked — живые осколки
  ushort _nextId — счётчик ID (wraparound на 65535)
  List<ushort> _deadIds — reusable список для очистки (zero-GC)
  float _snapshotTimer

  ServerRegister(ShrapnelProjectile proj):
    Если !IsServer → return
    Назначить proj.NetSyncId = _nextId++
    Добавить в _tracked
    Отправить ShrapnelSpawn всем клиентам

  ServerUnregister(ushort netId):
    Если !IsServer → return
    Удалить из _tracked
    Отправить ShrapnelDestroy всем клиентам

  Update():
    Если !IsServer → return
    _snapshotTimer += dt
    Если >= 1/SnapshotHz:
      Очистить мёртвые записи из _tracked (proj == null)
      Если count > 0: SendSnapshot()
      _snapshotTimer = 0

КЛИЕНТ:
  Dictionary<ushort, ClientMirrorShrapnel> _mirrors
  RegisterHandlers() — вызывается один раз в Initialize()

  OnReceiveSpawn(reader):
    Читает данные → CreateMirror()

  OnReceiveSnapshot(reader):
    Для каждого netId: если mirror существует → mirror.SetTarget(pos)
    Если mirror не существует → игнорировать (spawn ещё не пришёл)

  OnReceiveDestroy(reader):
    Найти mirror → Destroy(go) → удалить из _mirrors

ОБЩЕЕ:
  Initialize() — static factory метод
  Shutdown() — static, очищает всё
```

### 7.2. ClientMirrorShrapnel.cs — визуальная пустышка:

```
MonoBehaviour. Без Rigidbody2D, без Collider2D.
Компоненты: SpriteRenderer + дочерний outline SpriteRenderer.

Поля:
  ushort NetId
  SpriteRenderer SR
  SpriteRenderer OutlineSR
  float Heat
  Color ColdColor
  Vector2 _lastServerPos
  Vector2 _predictedVelocity
  float _timeSinceSnapshot
  float _noUpdateTimer

SetTarget(Vector2 serverPos):
  _predictedVelocity = (serverPos - _lastServerPos) / _timeSinceSnapshot
  Clamp velocity по разумному максимуму (защита от телепорта)
  _lastServerPos = serverPos
  _timeSinceSnapshot = 0
  _noUpdateTimer = 0

Update():
  _timeSinceSnapshot += dt
  _noUpdateTimer += dt

  // Если долго нет обновлений — осколок застрял на сервере
  if (_noUpdateTimer > MirrorTimeout):
    Destroy(gameObject)
    return

  // Экстраполяция + интерполяция
  Vector2 predicted = _lastServerPos + _predictedVelocity * _timeSinceSnapshot
  transform.position = Vector2.MoveTowards(transform.position, predicted, dt * InterpolationSpeed)

  // Остывание heat (локально, без сети)
  if (Heat > 0):
    Heat -= 0.42f * dt
    Clamp(0, 1)
    SR.color = Color.Lerp(ColdColor, ShrapnelVisuals.GetHotColor(), Heat)

  // Пульсация outline
  float phase = Time.time * 3.14f
  OutlineSR.color = new Color(0.9f, 0.1f, 0.05f, 0.35f + Sin(phase) * 0.15f)
```

### 7.3. Изменения в ShrapnelProjectile.cs:

```csharp
// Добавить поле:
/// <summary>Network sync ID assigned by ShrapnelNetSync. 0 = singleplayer.</summary>
public ushort NetSyncId;

// Добавить метод:
private void OnDestroy()
{
    if (NetSyncId != 0)
        ShrapnelNetSync.ServerUnregister(NetSyncId);
}
```

### 7.4. Изменения в ShrapnelFactory.SpawnCore():

```csharp
// В конце метода, ПОСЛЕ DebrisTracker.Register(obj):
ShrapnelNetSync.ServerRegister(proj);
```

### 7.5. Изменения в Plugin.WarmVisuals():

```csharp
// После инициализации пулов:
if (MultiplayerHelper.IsNetworkRunning)
    ShrapnelNetSync.Initialize();
```

### 7.6. Изменения в Plugin.OnWorldLoad():

```csharp
// В начале метода:
ShrapnelNetSync.Shutdown();
```

---

## 8. ТРЕБОВАНИЯ К КАЧЕСТВУ КОДА

### 8.1. Zero-GC в горячих путях:
- `_deadIds` — `List<ushort>` выделяется один раз в конструкторе, Clear() перед каждым использованием
- `FastBufferWriter` — `Allocator.Temp`, `Dispose()` в finally
- Snapshot loop: никаких `new List`, `LINQ`, `foreach` по Dictionary.Values (использовать for по кешированному массиву или .Keys/.Values без аллокаций)
- ClientMirrorShrapnel.Update(): никаких аллокаций

### 8.2. Graceful handling ошибок:
- Snapshot для неизвестного netId → тихо игнорировать
- Spawn для уже существующего netId → перезаписать (защита от дублей)
- Destroy для несуществующего netId → тихо игнорировать
- NetworkManager.Singleton == null → проверять перед каждой отправкой
- ServerMain.AllClientIdsExceptHost → reflection может вернуть null → проверить

### 8.3. Упаковка данных:
```csharp
// Упаковка типа и веса в один байт:
byte typePacked = (byte)((int)type | ((int)weight << 3));
// Распаковка:
ShrapnelType type = (ShrapnelType)(typePacked & 0x07);
ShrapnelWeight weight = (ShrapnelWeight)((typePacked >> 3) & 0x07);

// Упаковка позиции:
short posX = (short)(pos.x * 10f);
short posY = (short)(pos.y * 10f);
// Распаковка:
float x = posX / 10f;
float y = posY / 10f;

// Упаковка heat:
byte heatPacked = (byte)(heat * 255f);
float heat = heatPacked / 255f;

// Упаковка scale:
ushort scalePacked = (ushort)(scale * 1000f);
float scale = scalePacked / 1000f;
```

### 8.4. Логирование:
```csharp
// Только при первой инициализации и критических ошибках:
Plugin.Log.LogInfo("[ShrapnelNetSync] Initialized");
Plugin.Log.LogError("[ShrapnelNetSync] Failed to send: " + e.Message);
// НЕ логировать каждый spawn/snapshot/destroy — это hotpath
```

### 8.5. Документация XML на английском:
Для всех public методов и констант.

---

## 9. ЧТО НЕ НУЖНО ДЕЛАТЬ

- НЕ синхронизировать визуальные эффекты (ash, particles, sparks)
- НЕ добавлять физику (Rigidbody2D) в ClientMirrorShrapnel
- НЕ обрабатывать урон на клиенте
- НЕ синхронизировать rotation (клиент вычисляет из velocity)
- НЕ использовать NetworkObject/NetworkBehaviour (только CustomMessagingManager)
- НЕ хранить ссылки на ClientMirrorShrapnel в ShrapnelProjectile
- НЕ регистрировать handler'ы повторно при каждом взрыве

---

## 10. ФОРМАТ ОТВЕТА

Напиши полный рабочий код для:

1. `ScavShrapnelMod/Net/ShrapnelNetSync.cs` — полный файл
2. `ScavShrapnelMod/Net/ClientMirrorShrapnel.cs` — полный файл  
3. Точные изменения для `ShrapnelProjectile.cs` (показать строки до/после)
4. Точные изменения для `ShrapnelFactory.cs` (показать строки до/после)
5. Точные изменения для `Plugin.cs` (показать строки до/после)

Код должен компилироваться без ошибок в Unity 2022.3 + BepInEx 5.4 + C# 9.