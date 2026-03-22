using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Projectiles;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod.Net
{
    /// <summary>
    /// Server-authoritative network synchronization for physics shrapnel.
    /// Server tracks real ShrapnelProjectile instances. Clients create identical
    /// physics objects (real Rigidbody2D + Collider2D) with all damage gated.
    ///
    /// CLIENT SHARDS:
    ///   Real physics: bounce off walls, floors naturally via Unity physics engine.
    ///   All damage/world-modification gated by ShrapnelProjectile.IsServerAuthoritative.
    ///   REST correction snaps to server final position/rotation on MSG_STATE.
    ///   DESTROY handles cleanup (fade for flying, instant for at-rest).
    ///
    /// PROTOCOL (v4 — no snapshots):
    ///   MSG_SPAWN:    Reliable, batched — 2 + 17*N bytes per chunk
    ///   MSG_STATE:    Reliable, batched — rest corrections with position + rotation
    ///   MSG_DESTROY:  Reliable, batched — cleanup
    ///
    /// PERF vs v3:
    ///   MSG_SNAPSHOT deleted entirely (~50+ packets/explosion eliminated).
    ///   Client uses real physics engine (no manual gravity/drag/extrapolation).
    ///   No interpolation logic, no mirror system.
    ///   Server Update simplified (no snapshot gathering).
    ///
    /// All NGO types accessed via reflection — no compile-time dependency on Unity.Netcode.
    /// </summary>
    public sealed class ShrapnelNetSync : MonoBehaviour
    {
        //  TUNING CONSTANTS

        /// <summary>Maximum tracked shrapnel count (leak protection).</summary>
        public const int MaxShrapnel = 1000;

        /// <summary>
        /// Minimum time a shard must remain at-rest before sending REST state.
        /// Prevents REST→FLY→REST spam when shards bounce on surfaces.
        /// </summary>
        private const float StateDebounceTime = 0.15f;

        /// <summary>Extra bytes for FastBufferWriter allocation overhead.</summary>
        private const int BufferHeadroom = 32;

        /// <summary>Max state transitions per batch message (2 + 9*100 = 902 bytes).</summary>
        private const int MaxStateTransitionsPerPacket = 100;

        /// <summary>Max spawns per batch message (2 + 17*70 = 1192 bytes).</summary>
        private const int MaxSpawnsPerPacket = 70;

        /// <summary>Max destroys per batch message (2 + 2*200 = 402 bytes).</summary>
        private const int MaxDestroysPerPacket = 200;

        /// <summary>Cache duration for client ID list lookups.</summary>
        private const float ClientIdsCacheDuration = 0.5f;

        //  MESSAGE NAMES (prefixed "Shr" to avoid collisions)

        private const string MSG_SPAWN = "ShrShrapnelSpawn";
        private const string MSG_STATE = "ShrShrapnelState";
        private const string MSG_DESTROY = "ShrShrapnelDestroy";

        //  SINGLETON

        private static ShrapnelNetSync _instance;

        //  SERVER STATE

        /// <summary>
        /// Cached data for tracked projectiles. Stores component references
        /// to avoid GetComponent calls in hot paths.
        /// </summary>
        private readonly struct TrackedShard
        {
            public readonly ShrapnelProjectile Proj;
            public readonly Rigidbody2D Rb;
            public readonly Transform Transform;

            public TrackedShard(ShrapnelProjectile proj)
            {
                Proj = proj;
                Rb = proj.GetComponent<Rigidbody2D>();
                Transform = proj.transform;
            }

            public bool IsNull => Proj == null;
            public bool IsAtRest => Rb == null || Rb.isKinematic;
        }

        private readonly Dictionary<ushort, TrackedShard> _tracked = new(256);
        private readonly HashSet<ushort> _serverAtRest = new();
        private readonly Dictionary<ushort, float> _stateChangeTime = new(64);

        private ushort _nextId = 1;

        /// <summary>Reusable list for dead-entry cleanup (zero-GC).</summary>
        private readonly List<ushort> _deadIds = new(64);

        /// <summary>Reusable list for state transition batch (zero-GC).</summary>
        private readonly List<StateTransition> _pendingStateChanges = new(64);

        /// <summary>Queued spawn data for batched sending at end of tick.</summary>
        private readonly List<SpawnData> _pendingSpawns = new(64);

        /// <summary>Queued destroy IDs for batched sending.</summary>
        private readonly List<ushort> _pendingDestroys = new(32);

        /// <summary>Compact state transition record for batching.</summary>
        private readonly struct StateTransition
        {
            public readonly ushort NetId;
            public readonly bool AtRest;
            public readonly short PosX, PosY, RotZ;

            public StateTransition(ushort netId, bool atRest, Vector2 pos, float rotationZ)
            {
                NetId = netId;
                AtRest = atRest;
                PosX = (short)(pos.x * 10f);
                PosY = (short)(pos.y * 10f);
                RotZ = (short)Mathf.Clamp(rotationZ * 100f, short.MinValue, short.MaxValue);
            }
        }

        /// <summary>
        /// Pre-packed spawn data to avoid re-reading components at send time.
        /// </summary>
        private readonly struct SpawnData
        {
            public readonly ushort NetId;
            public readonly short PosX, PosY;
            public readonly byte TypePacked, HeatPacked, ShapeIndex;
            public readonly ushort ScalePacked;
            public readonly short VelX, VelY, RotZ;

            public SpawnData(ushort netId, short posX, short posY,
                byte typePacked, byte heatPacked, byte shapeIndex,
                ushort scalePacked, short velX, short velY, short rotZ)
            {
                NetId = netId;
                PosX = posX;
                PosY = posY;
                TypePacked = typePacked;
                HeatPacked = heatPacked;
                ShapeIndex = shapeIndex;
                ScalePacked = scalePacked;
                VelX = velX;
                VelY = velY;
                RotZ = rotZ;
            }
        }

        //  CLIENT STATE

        /// <summary>
        /// Client-side real physics shards indexed by server-assigned NetId.
        /// These are full ShrapnelProjectile instances with IsServerAuthoritative=false.
        /// </summary>
        private readonly Dictionary<ushort, ShrapnelProjectile> _clientShards = new(256);

        /// <summary>Reusable list for stale shard cleanup (zero-GC).</summary>
        private readonly List<ushort> _staleIds = new(32);

        private bool _handlersRegistered;

        //  REFLECTION CACHE: NGO types and methods

        private bool _ngoResolved;
        private bool _ngoAvailable;

        private Type _networkManagerType;
        private PropertyInfo _nmSingletonProp;
        private PropertyInfo _nmCustomMessagingProp;
        private Type _cmmType;
        private MethodInfo _sendNamedMessageMethod;
        private MethodInfo _registerHandlerMethod;
        private MethodInfo _unregisterHandlerMethod;
        private Type _handleNamedMessageDelegateType;
        private Type _fastBufferWriterType;
        private ConstructorInfo _fbwCtor;
        private MethodInfo _fbwDispose;
        private MethodInfo _fbwWriteUshort;
        private MethodInfo _fbwWriteShort;
        private MethodInfo _fbwWriteByte;
        private Type _fastBufferReaderType;
        private MethodInfo _fbrReadUshort;
        private MethodInfo _fbrReadShort;
        private MethodInfo _fbrReadByte;
        private object _deliveryReliable;
        private object _deliveryReliableSequenced;
        private object _allocatorTemp;
        private object _forPrimitivesDefault;

        //  PRE-ALLOCATED ARGS FOR REFLECTION (zero-GC writes)

        private readonly object[] _writeArgsUshort = new object[2];
        private readonly object[] _writeArgsShort = new object[2];
        private readonly object[] _writeArgsByte = new object[2];
        private readonly object[] _readArgs2 = new object[2];
        private readonly object[] _sendArgs4 = new object[4];
        private readonly object[] _regArgs2 = new object[2];
        private readonly object[] _unregArgs1 = new object[1];

        /// <summary>Pre-allocated constructor args for CreateWriter.</summary>
        private object[] _writerCtorArgs;

        //  REFLECTION CACHE: ServerMain.AllClientIdsExceptHost

        private PropertyInfo _allClientIdsProp;
        private bool _serverMainResolved;
        private IReadOnlyList<ulong> _cachedClientIds;
        private float _clientIdsCacheTimer;

        //  DIAGNOSTICS COUNTERS

        private int _spawnsSent;
        private int _statesSent;
        private int _destroysSent;
        private int _spawnsReceived;
        private int _statesReceived;
        private int _destroysReceived;

        //  STATIC HANDLER DISPATCH (for DynamicMethod delegates)

        private static readonly List<Action<ulong, object>> _handlerSlots = new(4);

        // ReSharper disable once UnusedMember.Local — called via IL emit
        private static void DispatchHandler(int index, ulong senderId, object reader)
        {
            if (index >= 0 && index < _handlerSlots.Count)
                _handlerSlots[index]?.Invoke(senderId, reader);
        }

        //  CLIENT PHYSICS MATERIAL (matches server exactly)

        private static PhysicsMaterial2D _clientPhysMat;

        /// <summary>
        /// Shared physics material for all client shards.
        /// Matches server ShrapnelFactory.PhysMat exactly:
        /// bounciness=0.15, friction=0.6.
        /// </summary>
        private static PhysicsMaterial2D ClientPhysMat => _clientPhysMat ??= new PhysicsMaterial2D("ClientShrMat")
        {
            bounciness = 0.15f,
            friction = 0.6f
        };

        //  LIFECYCLE

        /// <summary>
        /// Initializes the network sync system. Safe to call multiple times.
        /// Only creates the singleton if a network is running.
        /// </summary>
        public static void Initialize()
        {
            if (_instance != null) return;
            if (!MultiplayerHelper.IsNetworkRunning) return;

            var go = new GameObject("ShrapnelNetSync")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ShrapnelNetSync>();

            Plugin.Log.LogInfo("[ShrapnelNetSync] Initialized" +
                $" (Server={MultiplayerHelper.IsServer}," +
                $" Client={MultiplayerHelper.IsClient})");
        }

        /// <summary>
        /// Shuts down the network sync system. Cleans up all state.
        /// </summary>
        public static void Shutdown()
        {
            if (_instance == null) return;

            try
            {
                _instance.UnregisterHandlers();
                _instance.DestroyAllClientShards();
                _instance._tracked.Clear();
                _instance._serverAtRest.Clear();
                _instance._stateChangeTime.Clear();
                _instance._pendingSpawns.Clear();
                _instance._pendingDestroys.Clear();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelNetSync] Shutdown error: {e.Message}");
            }

            if (_instance != null && _instance.gameObject != null)
                Destroy(_instance.gameObject);

            _instance = null;
        }

        private void Awake()
        {
            ResolveNGO();

            if (!_ngoAvailable)
            {
                Plugin.Log.LogError("[ShrapnelNetSync] NGO reflection failed — destroying");
                Destroy(gameObject);
                return;
            }

            if (MultiplayerHelper.IsClient)
                RegisterHandlers();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        //  NGO REFLECTION RESOLUTION

        private void ResolveNGO()
        {
            if (_ngoResolved) return;
            _ngoResolved = true;

            try
            {
                const BindingFlags allFlags =
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance;

                Assembly ngoAssembly = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Unity.Netcode.Runtime")
                    {
                        ngoAssembly = asm;
                        break;
                    }
                }
                if (ngoAssembly == null) { LogMissing("Unity.Netcode.Runtime"); return; }

                _networkManagerType = ngoAssembly.GetType("Unity.Netcode.NetworkManager");
                if (_networkManagerType == null) { LogMissing("NetworkManager"); return; }

                _nmSingletonProp = _networkManagerType.GetProperty("Singleton", allFlags);
                if (_nmSingletonProp == null) { LogMissing("NetworkManager.Singleton"); return; }

                _nmCustomMessagingProp = _networkManagerType.GetProperty(
                    "CustomMessagingManager", allFlags);
                if (_nmCustomMessagingProp == null) { LogMissing("CustomMessagingManager prop"); return; }

                _cmmType = ngoAssembly.GetType("Unity.Netcode.CustomMessagingManager");
                if (_cmmType == null) { LogMissing("CustomMessagingManager type"); return; }

                _handleNamedMessageDelegateType = null;
                foreach (var nested in _cmmType.GetNestedTypes(allFlags))
                {
                    if (nested.Name == "HandleNamedMessageDelegate")
                    {
                        _handleNamedMessageDelegateType = nested;
                        break;
                    }
                }
                if (_handleNamedMessageDelegateType == null)
                {
                    _handleNamedMessageDelegateType = ngoAssembly.GetType(
                        "Unity.Netcode.CustomMessagingManager+HandleNamedMessageDelegate");
                }
                if (_handleNamedMessageDelegateType == null) { LogMissing("HandleNamedMessageDelegate"); return; }

                _fastBufferWriterType = ngoAssembly.GetType("Unity.Netcode.FastBufferWriter");
                if (_fastBufferWriterType == null) { LogMissing("FastBufferWriter"); return; }

                _fastBufferReaderType = ngoAssembly.GetType("Unity.Netcode.FastBufferReader");
                if (_fastBufferReaderType == null) { LogMissing("FastBufferReader"); return; }

                var deliveryType = ngoAssembly.GetType("Unity.Netcode.NetworkDelivery");
                if (deliveryType == null) { LogMissing("NetworkDelivery"); return; }
                _deliveryReliable = Enum.Parse(deliveryType, "Reliable");
                _deliveryReliableSequenced = Enum.Parse(deliveryType, "ReliableSequenced");

                var allocatorType = typeof(Unity.Collections.Allocator);
                _allocatorTemp = Enum.Parse(allocatorType, "Temp");

                var forPrimType = _fastBufferWriterType.GetNestedType("ForPrimitives", allFlags);
                if (forPrimType != null)
                    _forPrimitivesDefault = Activator.CreateInstance(forPrimType);

                _fbwCtor = _fastBufferWriterType.GetConstructor(
                    new[] { typeof(int), allocatorType, typeof(int) })
                    ?? _fastBufferWriterType.GetConstructor(
                        new[] { typeof(int), allocatorType });
                if (_fbwCtor == null) { LogMissing("FastBufferWriter ctor"); return; }

                var ctorParams = _fbwCtor.GetParameters();
                _writerCtorArgs = ctorParams.Length == 3
                    ? new object[] { 0, _allocatorTemp, -1 }
                    : new object[] { 0, _allocatorTemp };

                _fbwDispose = _fastBufferWriterType.GetMethod("Dispose", Type.EmptyTypes);
                if (_fbwDispose == null) { LogMissing("FastBufferWriter.Dispose"); return; }

                ResolveWriteMethods(allFlags);
                if (_fbwWriteUshort == null) { LogMissing("WriteValueSafe<ushort>"); return; }
                if (_fbwWriteShort == null) { LogMissing("WriteValueSafe<short>"); return; }
                if (_fbwWriteByte == null) { LogMissing("WriteValueSafe<byte>"); return; }

                ResolveReadMethods(allFlags);
                if (_fbrReadUshort == null) { LogMissing("ReadValueSafe<ushort>"); return; }
                if (_fbrReadShort == null) { LogMissing("ReadValueSafe<short>"); return; }
                if (_fbrReadByte == null) { LogMissing("ReadValueSafe<byte>"); return; }

                foreach (var m in _cmmType.GetMethods(allFlags))
                {
                    if (m.Name != "SendNamedMessage") continue;
                    var p = m.GetParameters();
                    if (p.Length == 4 &&
                        p[0].ParameterType == typeof(string) &&
                        p[1].ParameterType != typeof(ulong) &&
                        p[2].ParameterType == _fastBufferWriterType &&
                        p[3].ParameterType == deliveryType)
                    {
                        _sendNamedMessageMethod = m;
                        break;
                    }
                }
                if (_sendNamedMessageMethod == null) { LogMissing("SendNamedMessage"); return; }

                foreach (var m in _cmmType.GetMethods(allFlags))
                {
                    if (m.Name != "RegisterNamedMessageHandler") continue;
                    var p = m.GetParameters();
                    if (p.Length == 2 && p[0].ParameterType == typeof(string))
                    {
                        _registerHandlerMethod = m;
                        break;
                    }
                }
                if (_registerHandlerMethod == null) { LogMissing("RegisterNamedMessageHandler"); return; }

                _unregisterHandlerMethod = _cmmType.GetMethod(
                    "UnregisterNamedMessageHandler", new[] { typeof(string) });

                _writeArgsUshort[1] = _forPrimitivesDefault;
                _writeArgsShort[1] = _forPrimitivesDefault;
                _writeArgsByte[1] = _forPrimitivesDefault;
                _readArgs2[1] = _forPrimitivesDefault;

                _ngoAvailable = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] NGO reflection error: {e.Message}\n{e.StackTrace}");
            }
        }

        private void ResolveWriteMethods(BindingFlags flags)
        {
            foreach (var m in _fastBufferWriterType.GetMethods(flags))
            {
                if (m.Name != "WriteValueSafe" || !m.IsGenericMethodDefinition) continue;
                var p = m.GetParameters();
                if (p.Length == 2 && p[0].ParameterType.IsByRef)
                {
                    try
                    {
                        _fbwWriteUshort = m.MakeGenericMethod(typeof(ushort));
                        _fbwWriteShort = m.MakeGenericMethod(typeof(short));
                        _fbwWriteByte = m.MakeGenericMethod(typeof(byte));
                        return;
                    }
                    catch { /* try next overload */ }
                }
            }
        }

        private void ResolveReadMethods(BindingFlags flags)
        {
            foreach (var m in _fastBufferReaderType.GetMethods(flags))
            {
                if (m.Name != "ReadValueSafe" || !m.IsGenericMethodDefinition) continue;
                var p = m.GetParameters();
                if (p.Length == 2 && p[0].IsOut)
                {
                    try
                    {
                        _fbrReadUshort = m.MakeGenericMethod(typeof(ushort));
                        _fbrReadShort = m.MakeGenericMethod(typeof(short));
                        _fbrReadByte = m.MakeGenericMethod(typeof(byte));
                        return;
                    }
                    catch { /* try next overload */ }
                }
            }
        }

        private static void LogMissing(string name)
        {
            Plugin.Log.LogError($"[ShrapnelNetSync] Reflection: missing {name}");
        }

        //  NGO WRAPPERS

        private object GetNetworkManager()
        {
            try { return _nmSingletonProp?.GetValue(null); }
            catch { return null; }
        }

        private object GetCustomMessagingManager()
        {
            var nm = GetNetworkManager();
            if (nm == null) return null;
            try { return _nmCustomMessagingProp?.GetValue(nm); }
            catch { return null; }
        }

        /// <summary>Creates a boxed FastBufferWriter. Zero-alloc args via _writerCtorArgs.</summary>
        private object CreateWriter(int size)
        {
            try
            {
                _writerCtorArgs[0] = size;
                return _fbwCtor.Invoke(_writerCtorArgs);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelNetSync] CreateWriter failed: {e.Message}");
                return null;
            }
        }

        private void DisposeWriter(object writer)
        {
            if (writer == null) return;
            try { _fbwDispose.Invoke(writer, null); }
            catch { /* best effort */ }
        }

        private void WriteUshort(object writer, ushort value)
        {
            _writeArgsUshort[0] = value;
            _fbwWriteUshort.Invoke(writer, _writeArgsUshort);
        }

        private void WriteShort(object writer, short value)
        {
            _writeArgsShort[0] = value;
            _fbwWriteShort.Invoke(writer, _writeArgsShort);
        }

        private void WriteByte(object writer, byte value)
        {
            _writeArgsByte[0] = value;
            _fbwWriteByte.Invoke(writer, _writeArgsByte);
        }

        private ushort ReadUshort(object reader)
        {
            _readArgs2[0] = (ushort)0;
            _fbrReadUshort.Invoke(reader, _readArgs2);
            return (ushort)_readArgs2[0];
        }

        private short ReadShort(object reader)
        {
            _readArgs2[0] = (short)0;
            _fbrReadShort.Invoke(reader, _readArgs2);
            return (short)_readArgs2[0];
        }

        private byte ReadByte(object reader)
        {
            _readArgs2[0] = (byte)0;
            _fbrReadByte.Invoke(reader, _readArgs2);
            return (byte)_readArgs2[0];
        }

        private void SendMessage(object cmm, string msgName,
            IReadOnlyList<ulong> clientIds, object writer, object delivery)
        {
            _sendArgs4[0] = msgName;
            _sendArgs4[1] = clientIds;
            _sendArgs4[2] = writer;
            _sendArgs4[3] = delivery;
            _sendNamedMessageMethod.Invoke(cmm, _sendArgs4);
        }

        //  SERVER PUBLIC API

        /// <summary>
        /// Registers a server-side projectile for network tracking.
        /// No-op in singleplayer or on clients.
        /// </summary>
        public static void ServerRegister(ShrapnelProjectile proj)
        {
            if (_instance == null || !MultiplayerHelper.IsServer) return;
            if (proj == null) return;
            _instance.ServerRegisterInternal(proj);
        }

        /// <summary>
        /// Unregisters a server-side projectile. Queues MSG_DESTROY.
        /// No-op in singleplayer or on clients.
        /// </summary>
        public static void ServerUnregister(ushort netId)
        {
            if (_instance == null || !MultiplayerHelper.IsServer) return;
            if (netId == 0) return;
            _instance.ServerUnregisterInternal(netId);
        }

        private void ServerRegisterInternal(ShrapnelProjectile proj)
        {
            if (_tracked.Count >= MaxShrapnel) return;

            ushort id = _nextId++;
            if (_nextId == 0) _nextId = 1;

            proj.NetSyncId = id;
            var shard = new TrackedShard(proj);
            _tracked[id] = shard;

            if (shard.IsAtRest)
                _serverAtRest.Add(id);

            QueueSpawnData(proj, shard, id);
        }

        /// <summary>
        /// Pre-packs spawn data into SpawnData struct at registration time.
        /// </summary>
        private void QueueSpawnData(ShrapnelProjectile proj, TrackedShard shard, ushort netId)
        {
            Vector2 pos = shard.Transform.position;
            short posX = (short)(pos.x * 10f);
            short posY = (short)(pos.y * 10f);

            bool atRest = shard.IsAtRest;

            byte typePacked = (byte)(
                (int)proj.Type |
                ((int)proj.Weight << 3) |
                (proj.HasTrail ? (1 << 6) : 0) |
                (atRest ? (1 << 7) : 0));

            byte heatPacked = (byte)(Mathf.Clamp01(proj.Heat) * 255f);

            byte shapeIndex = 0;
            var sr = proj.GetComponent<SpriteRenderer>();
            if (sr != null && sr.sprite != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    var shape = (ShrapnelVisuals.TriangleShape)i;
                    if (ShrapnelVisuals.GetTriangleSprite(shape) == sr.sprite)
                    {
                        shapeIndex = (byte)i;
                        break;
                    }
                }
            }

            float scale = shard.Transform.localScale.x;
            ushort scalePacked = (ushort)(Mathf.Clamp(scale, 0f, 65.535f) * 1000f);

            Vector2 vel = Vector2.zero;
            if (!atRest && shard.Rb != null && !shard.Rb.isKinematic)
                vel = shard.Rb.velocity;

            short velX = (short)Mathf.Clamp(vel.x * 10f, short.MinValue, short.MaxValue);
            short velY = (short)Mathf.Clamp(vel.y * 10f, short.MinValue, short.MaxValue);

            float rotZ = shard.Transform.rotation.eulerAngles.z;
            short rotZPacked = (short)Mathf.Clamp(rotZ * 100f, short.MinValue, short.MaxValue);

            _pendingSpawns.Add(new SpawnData(
                netId, posX, posY, typePacked, heatPacked,
                shapeIndex, scalePacked, velX, velY, rotZPacked));
        }

        private void ServerUnregisterInternal(ushort netId)
        {
            _tracked.Remove(netId);
            _serverAtRest.Remove(netId);
            _stateChangeTime.Remove(netId);
            _pendingDestroys.Add(netId);
        }

        //  SERVER UPDATE

        private void Update()
        {
            if (MultiplayerHelper.IsServer)
                ServerUpdate();
        }

        private void ServerUpdate()
        {
            float now = Time.time;

            if (_pendingSpawns.Count > 0)
                FlushSpawnBatch();

            if (_pendingDestroys.Count > 0)
                FlushDestroyBatch();

            // Phase 1: Purge dead entries
            _deadIds.Clear();
            foreach (var kvp in _tracked)
            {
                if (kvp.Value.IsNull)
                    _deadIds.Add(kvp.Key);
            }
            for (int i = 0; i < _deadIds.Count; i++)
            {
                ushort deadId = _deadIds[i];
                _tracked.Remove(deadId);
                _serverAtRest.Remove(deadId);
                _stateChangeTime.Remove(deadId);
                _pendingDestroys.Add(deadId);
            }

            if (_pendingDestroys.Count > 0)
                FlushDestroyBatch();

            // Phase 2: Detect state transitions with debounce
            _pendingStateChanges.Clear();

            foreach (var kvp in _tracked)
            {
                if (kvp.Value.IsNull) continue;

                bool currentlyAtRest = kvp.Value.IsAtRest;
                bool wasAtRest = _serverAtRest.Contains(kvp.Key);

                if (currentlyAtRest != wasAtRest)
                {
                    if (!_stateChangeTime.ContainsKey(kvp.Key))
                    {
                        _stateChangeTime[kvp.Key] = now;
                    }
                    else if (now - _stateChangeTime[kvp.Key] >= StateDebounceTime)
                    {
                        Vector2 pos = kvp.Value.Transform.position;
                        float rotZ = kvp.Value.Transform.rotation.eulerAngles.z;
                        _pendingStateChanges.Add(
                            new StateTransition(kvp.Key, currentlyAtRest, pos, rotZ));

                        if (currentlyAtRest)
                            _serverAtRest.Add(kvp.Key);
                        else
                            _serverAtRest.Remove(kvp.Key);

                        _stateChangeTime.Remove(kvp.Key);
                    }
                }
                else
                {
                    _stateChangeTime.Remove(kvp.Key);
                }
            }

            if (_pendingStateChanges.Count > 0)
                SendStateBatch();
        }

        //  SERVER → CLIENT: BATCHED SPAWN (Reliable)

        private void FlushSpawnBatch()
        {
            var clientIds = GetClientIds();
            if (clientIds == null || clientIds.Count == 0)
            {
                _pendingSpawns.Clear();
                return;
            }

            var cmm = GetCustomMessagingManager();
            if (cmm == null)
            {
                _pendingSpawns.Clear();
                return;
            }

            int total = _pendingSpawns.Count;
            int offset = 0;

            while (offset < total)
            {
                int chunkCount = Mathf.Min(total - offset, MaxSpawnsPerPacket);
                SendSpawnChunk(cmm, clientIds, offset, chunkCount);
                offset += chunkCount;
            }

            _spawnsSent += total;
            _pendingSpawns.Clear();
        }

        private void SendSpawnChunk(object cmm, IReadOnlyList<ulong> clientIds,
            int offset, int count)
        {
            ushort countU = (ushort)count;
            int size = 2 + 17 * count;
            object writer = CreateWriter(size + BufferHeadroom);
            if (writer == null) return;

            try
            {
                WriteUshort(writer, countU);

                for (int i = 0; i < count; i++)
                {
                    var sd = _pendingSpawns[offset + i];
                    WriteUshort(writer, sd.NetId);
                    WriteShort(writer, sd.PosX);
                    WriteShort(writer, sd.PosY);
                    WriteByte(writer, sd.TypePacked);
                    WriteByte(writer, sd.HeatPacked);
                    WriteByte(writer, sd.ShapeIndex);
                    WriteUshort(writer, sd.ScalePacked);
                    WriteShort(writer, sd.VelX);
                    WriteShort(writer, sd.VelY);
                    WriteShort(writer, sd.RotZ);
                }

                SendMessage(cmm, MSG_SPAWN, clientIds, writer, _deliveryReliable);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelNetSync] SendSpawnBatch failed: {e.Message}");
            }
            finally
            {
                DisposeWriter(writer);
            }
        }

        //  SERVER → CLIENT: STATE TRANSITION (Reliable, batched)

        private void SendStateBatch()
        {
            var clientIds = GetClientIds();
            if (clientIds == null || clientIds.Count == 0) return;

            var cmm = GetCustomMessagingManager();
            if (cmm == null) return;

            int total = _pendingStateChanges.Count;
            int offset = 0;

            while (offset < total)
            {
                int chunkCount = Mathf.Min(total - offset, MaxStateTransitionsPerPacket);
                SendStateChunk(cmm, clientIds, offset, chunkCount);
                offset += chunkCount;
            }
        }

        private void SendStateChunk(object cmm, IReadOnlyList<ulong> clientIds,
            int offset, int count)
        {
            ushort countU = (ushort)count;
            int size = 2 + 9 * countU;
            object writer = CreateWriter(size + BufferHeadroom);
            if (writer == null) return;

            try
            {
                WriteUshort(writer, countU);

                for (int i = 0; i < countU; i++)
                {
                    var st = _pendingStateChanges[offset + i];
                    WriteUshort(writer, st.NetId);
                    WriteByte(writer, (byte)(st.AtRest ? 1 : 0));
                    WriteShort(writer, st.PosX);
                    WriteShort(writer, st.PosY);
                    WriteShort(writer, st.RotZ);
                }

                SendMessage(cmm, MSG_STATE, clientIds, writer, _deliveryReliable);
                _statesSent += count;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelNetSync] SendState failed: {e.Message}");
            }
            finally
            {
                DisposeWriter(writer);
            }
        }

        //  SERVER → CLIENT: BATCHED DESTROY (ReliableSequenced)

        private void FlushDestroyBatch()
        {
            var clientIds = GetClientIds();
            if (clientIds == null || clientIds.Count == 0)
            {
                _pendingDestroys.Clear();
                return;
            }

            var cmm = GetCustomMessagingManager();
            if (cmm == null)
            {
                _pendingDestroys.Clear();
                return;
            }

            int total = _pendingDestroys.Count;
            int offset = 0;

            while (offset < total)
            {
                int chunkCount = Mathf.Min(total - offset, MaxDestroysPerPacket);
                SendDestroyChunk(cmm, clientIds, offset, chunkCount);
                offset += chunkCount;
            }

            _destroysSent += total;
            _pendingDestroys.Clear();
        }

        private void SendDestroyChunk(object cmm, IReadOnlyList<ulong> clientIds,
            int offset, int count)
        {
            ushort countU = (ushort)count;
            int size = 2 + 2 * count;
            object writer = CreateWriter(size + BufferHeadroom);
            if (writer == null) return;

            try
            {
                WriteUshort(writer, countU);

                for (int i = 0; i < count; i++)
                    WriteUshort(writer, _pendingDestroys[offset + i]);

                SendMessage(cmm, MSG_DESTROY, clientIds, writer, _deliveryReliableSequenced);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelNetSync] SendDestroyBatch failed: {e.Message}");
            }
            finally
            {
                DisposeWriter(writer);
            }
        }

        //  CLIENT: HANDLER REGISTRATION

        private void RegisterHandlers()
        {
            if (_handlersRegistered) return;

            var cmm = GetCustomMessagingManager();
            if (cmm == null)
            {
                Plugin.Log.LogError("[ShrapnelNetSync] Cannot register handlers: CMM not ready");
                return;
            }

            try
            {
                RegisterSingleHandler(cmm, MSG_SPAWN,
                    new Action<ulong, object>(OnReceiveSpawnRaw));
                RegisterSingleHandler(cmm, MSG_STATE,
                    new Action<ulong, object>(OnReceiveStateRaw));
                RegisterSingleHandler(cmm, MSG_DESTROY,
                    new Action<ulong, object>(OnReceiveDestroyRaw));

                _handlersRegistered = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] RegisterHandlers failed: {e.Message}\n{e.StackTrace}");
            }
        }

        private void RegisterSingleHandler(object cmm, string msgName,
            Action<ulong, object> handler)
        {
            int slotIndex = _handlerSlots.Count;
            _handlerSlots.Add(handler);

            var dm = new DynamicMethod(
                "ShrNS_" + msgName,
                typeof(void),
                new[] { typeof(ulong), _fastBufferReaderType },
                typeof(ShrapnelNetSync).Module,
                skipVisibility: true);

            ILGenerator il = dm.GetILGenerator();

            il.Emit(OpCodes.Ldc_I4, slotIndex);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Box, _fastBufferReaderType);

            MethodInfo dispatchMI = typeof(ShrapnelNetSync).GetMethod(
                nameof(DispatchHandler),
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(int), typeof(ulong), typeof(object) },
                null);
            il.Emit(OpCodes.Call, dispatchMI);
            il.Emit(OpCodes.Ret);

            Delegate del = dm.CreateDelegate(_handleNamedMessageDelegateType);

            _regArgs2[0] = msgName;
            _regArgs2[1] = del;
            _registerHandlerMethod.Invoke(cmm, _regArgs2);
        }

        private void UnregisterHandlers()
        {
            if (!_handlersRegistered) return;

            try
            {
                var cmm = GetCustomMessagingManager();
                if (cmm != null && _unregisterHandlerMethod != null)
                {
                    UnregisterSingle(cmm, MSG_SPAWN);
                    UnregisterSingle(cmm, MSG_STATE);
                    UnregisterSingle(cmm, MSG_DESTROY);
                }
            }
            catch { /* best effort */ }

            _handlersRegistered = false;
            _handlerSlots.Clear();
        }

        private void UnregisterSingle(object cmm, string msgName)
        {
            _unregArgs1[0] = msgName;
            _unregisterHandlerMethod.Invoke(cmm, _unregArgs1);
        }

        //  CLIENT: RECEIVE HANDLERS

        private void OnReceiveSpawnRaw(ulong senderId, object readerObj)
        {
            try
            {
                ushort count = ReadUshort(readerObj);
                _spawnsReceived += count;

                for (int n = 0; n < count; n++)
                {
                    ushort netId = ReadUshort(readerObj);
                    short posX = ReadShort(readerObj);
                    short posY = ReadShort(readerObj);
                    byte typePacked = ReadByte(readerObj);
                    byte heatPacked = ReadByte(readerObj);
                    byte shapeIndex = ReadByte(readerObj);
                    ushort scalePacked = ReadUshort(readerObj);
                    short velXPacked = ReadShort(readerObj);
                    short velYPacked = ReadShort(readerObj);
                    short rotZPacked = ReadShort(readerObj);

                    float x = posX / 10f;
                    float y = posY / 10f;
                    var type = (ShrapnelProjectile.ShrapnelType)(typePacked & 0x07);
                    var weight = (ShrapnelWeight)((typePacked >> 3) & 0x07);
                    bool hasTrail = (typePacked & (1 << 6)) != 0;
                    bool atRest = (typePacked & (1 << 7)) != 0;
                    float heat = heatPacked / 255f;
                    var shape = (ShrapnelVisuals.TriangleShape)Mathf.Clamp(shapeIndex, 0, 5);
                    float scale = scalePacked / 1000f;
                    float velX = velXPacked / 10f;
                    float velY = velYPacked / 10f;
                    float rotZ = rotZPacked / 100f;

                    // Overwrite duplicate (protection against double-delivery)
                    if (_clientShards.TryGetValue(netId, out var existing))
                    {
                        if (existing != null && existing.gameObject != null)
                            Destroy(existing.gameObject);
                        _clientShards.Remove(netId);
                    }

                    var shard = CreateClientShard(
                        netId, new Vector2(x, y), type, weight, heat,
                        shape, scale, hasTrail, atRest,
                        new Vector2(velX, velY), rotZ);

                    if (shard != null)
                        _clientShards[netId] = shard;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] OnReceiveSpawn failed: {e.Message}");
            }
        }

        private void OnReceiveStateRaw(ulong senderId, object readerObj)
        {
            try
            {
                ushort count = ReadUshort(readerObj);
                _statesReceived += count;

                for (int i = 0; i < count; i++)
                {
                    ushort netId = ReadUshort(readerObj);
                    byte state = ReadByte(readerObj);
                    short posX = ReadShort(readerObj);
                    short posY = ReadShort(readerObj);
                    short rotZPacked = ReadShort(readerObj);

                    bool atRest = state != 0;
                    float x = posX / 10f;
                    float y = posY / 10f;
                    float rotZ = rotZPacked / 100f;

                    if (_clientShards.TryGetValue(netId, out var shard) && shard != null)
                    {
                        if (atRest)
                            ClientTransitionToRest(shard, new Vector2(x, y), rotZ);
                        // WHY: FLY transitions not handled — client shard already
                        // has real physics. If server shard re-enters flight (support
                        // block destroyed), the client shard's CheckSupportAndFall
                        // will detect it independently via identical terrain.
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] OnReceiveState failed: {e.Message}");
            }
        }

        private void OnReceiveDestroyRaw(ulong senderId, object readerObj)
        {
            try
            {
                ushort count = ReadUshort(readerObj);
                _destroysReceived += count;

                for (int n = 0; n < count; n++)
                {
                    ushort netId = ReadUshort(readerObj);

                    if (_clientShards.TryGetValue(netId, out var shard))
                    {
                        if (shard != null && shard.gameObject != null)
                            ClientDestroyShard(shard);
                        _clientShards.Remove(netId);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] OnReceiveDestroy failed: {e.Message}");
            }
        }

        //  CLIENT: REAL PHYSICS SHARD CREATION

        /// <summary>
        /// Creates a client-side physics shard by delegating to ShrapnelFactory.
        /// Eliminates code duplication between NetSync and Factory.
        /// </summary>
        private ShrapnelProjectile CreateClientShard(
            ushort netId, Vector2 position,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            float heat, ShrapnelVisuals.TriangleShape shape, float scale,
            bool hasTrail, bool atRest, Vector2 velocity, float rotationZ)
        {
            // WHY: Delegate to ShrapnelFactory.SpawnClientShard which maintains
            // the single source of truth for shard creation.
            // Previous version duplicated 60+ lines of setup code.
            var shard = ShrapnelFactory.SpawnClientShard(
                netId, position, type, weight, heat, shape, scale,
                hasTrail, atRest, velocity, rotationZ);

            return shard;
        }

        //  CLIENT: SHARD LIFECYCLE

        /// <summary>
        /// Transitions a client shard to rest at server-corrected position.
        /// </summary>
        private void ClientTransitionToRest(ShrapnelProjectile shard,
            Vector2 finalPos, float rotationZ)
        {
            if (shard == null || shard.gameObject == null) return;

            // Already resting/debris — just snap position correction
            if (shard.CurrentState >= 1)
            {
                shard.transform.position = finalPos;
                shard.transform.rotation = Quaternion.Euler(0f, 0f, rotationZ);
                return;
            }

            // Force-land flying shard
            var rb = shard.GetComponent<Rigidbody2D>();
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.isKinematic = true;
            }

            shard.transform.position = finalPos;
            shard.transform.rotation = Quaternion.Euler(0f, 0f, rotationZ);

            // WHY: ForceToState handles internal state machine without
            // requiring block position lookup. BecomeStuck needs
            // WorldToBlockPos which may fail during world load.
            shard.ForceToState(ShrapnelProjectile.ExternalState.Stuck, finalPos);
        }

        /// <summary>
        /// Destroys a client shard. At-rest/debris: instant.
        /// Flying: 150ms fade to avoid jarring mid-flight vanish.
        /// </summary>
        private void ClientDestroyShard(ShrapnelProjectile shard)
        {
            if (shard == null || shard.gameObject == null) return;

            if (shard.CurrentState >= 1)
            {
                Destroy(shard.gameObject);
            }
            else
            {
                shard.BeginClientFadeOut();
            }
        }

        /// <summary>
        /// Called from ShrapnelProjectile.OnDestroy for client shards.
        /// Removes the shard from the tracking dictionary.
        /// </summary>
        internal static void NotifyClientShardDestroyed(ushort netId)
        {
            if (_instance == null) return;
            _instance._clientShards.Remove(netId);
        }

        private void DestroyAllClientShards()
        {
            _staleIds.Clear();
            foreach (var kvp in _clientShards)
                _staleIds.Add(kvp.Key);

            for (int i = 0; i < _staleIds.Count; i++)
            {
                ushort id = _staleIds[i];
                if (_clientShards.TryGetValue(id, out var shard))
                {
                    if (shard != null && shard.gameObject != null)
                        Destroy(shard.gameObject);
                }
            }
            _clientShards.Clear();
        }

        //  DIAGNOSTICS API

        /// <summary>Returns detailed diagnostic string for console output.</summary>
        public static string GetDiagnostics()
        {
            if (_instance == null)
                return "[ShrapnelNetSync] Not initialized (singleplayer or no MP mod)";

            var i = _instance;

            if (MultiplayerHelper.IsServer)
            {
                var clientIds = i.GetClientIds();
                int clientCount = clientIds != null ? clientIds.Count : 0;
                int flyingCount = i._tracked.Count - i._serverAtRest.Count;

                return "=== SHRAPNEL NET SYNC (v4 — client physics) ===\n" +
                    $"  Role: SERVER (host)\n" +
                    $"  Tracked shrapnel: {i._tracked.Count}\n" +
                    $"  Flying: {flyingCount} | At rest: {i._serverAtRest.Count}\n" +
                    $"  Pending spawns: {i._pendingSpawns.Count}\n" +
                    $"  Pending destroys: {i._pendingDestroys.Count}\n" +
                    $"  Debouncing states: {i._stateChangeTime.Count}\n" +
                    $"  Next ID: {i._nextId}\n" +
                    $"  Spawns sent: {i._spawnsSent}\n" +
                    $"  States sent: {i._statesSent}\n" +
                    $"  Destroys sent: {i._destroysSent}\n" +
                    $"  Connected clients: {clientCount}\n" +
                    $"  State debounce: {StateDebounceTime * 1000f:F0}ms\n" +
                    $"  Max tracked: {MaxShrapnel}";
            }
            else
            {
                int flyingShards = 0;
                int restShards = 0;
                foreach (var kvp in i._clientShards)
                {
                    if (kvp.Value == null) continue;
                    if (kvp.Value.CurrentState == 0) flyingShards++;
                    else restShards++;
                }

                return "=== SHRAPNEL NET SYNC (v4 — client physics) ===\n" +
                    $"  Role: CLIENT\n" +
                    $"  Client shards: {i._clientShards.Count}\n" +
                    $"  Flying: {flyingShards} | At rest/debris: {restShards}\n" +
                    $"  Spawns received: {i._spawnsReceived}\n" +
                    $"  States received: {i._statesReceived}\n" +
                    $"  Destroys received: {i._destroysReceived}\n" +
                    $"  Handlers registered: {i._handlersRegistered}\n" +
                    $"  NGO available: {i._ngoAvailable}";
            }
        }

        /// <summary>Returns brief one-line status for HUD/log.</summary>
        public static string GetBriefStatus()
        {
            if (_instance == null) return "NET:off";

            var i = _instance;

            if (MultiplayerHelper.IsServer)
            {
                int flyingCount = i._tracked.Count - i._serverAtRest.Count;
                return $"NET:SERVER tracked={i._tracked.Count}" +
                    $" fly={flyingCount} rest={i._serverAtRest.Count}" +
                    $" sent={i._spawnsSent}/{i._statesSent}/{i._destroysSent}";
            }
            else
            {
                return $"NET:CLIENT shards={i._clientShards.Count}" +
                    $" recv={i._spawnsReceived}/{i._statesReceived}/{i._destroysReceived}";
            }
        }

        //  REFLECTION: GET CLIENT IDS (cached 0.5s)

        private IReadOnlyList<ulong> GetClientIds()
        {
            _clientIdsCacheTimer -= Time.deltaTime;
            if (_clientIdsCacheTimer > 0f && _cachedClientIds != null)
                return _cachedClientIds;

            _clientIdsCacheTimer = ClientIdsCacheDuration;
            _cachedClientIds = ResolveClientIds();
            return _cachedClientIds;
        }

        private IReadOnlyList<ulong> ResolveClientIds()
        {
            if (!_serverMainResolved)
            {
                _serverMainResolved = true;
                try
                {
                    const BindingFlags allFlags =
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static | BindingFlags.Instance;

                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var serverMainType = asm.GetType("KrokoshaCasualtiesMP.ServerMain");
                        if (serverMainType != null)
                        {
                            _allClientIdsProp = serverMainType.GetProperty(
                                "AllClientIdsExceptHost", allFlags);
                            if (_allClientIdsProp != null) break;
                        }
                    }

                    if (_allClientIdsProp == null)
                        Plugin.Log.LogError(
                            "[ShrapnelNetSync] Could not find ServerMain.AllClientIdsExceptHost");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(
                        $"[ShrapnelNetSync] ServerMain reflection: {e.Message}");
                }
            }

            if (_allClientIdsProp == null) return null;

            try
            {
                return _allClientIdsProp.GetValue(null) as IReadOnlyList<ulong>;
            }
            catch
            {
                return null;
            }
        }
    }
}