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
    /// Server tracks real ShrapnelProjectile instances and broadcasts position data.
    /// Clients create lightweight visual mirrors (ClientMirrorShrapnel) with interpolation.
    ///
    /// PROTOCOL (v3 — batched spawns + state debounce):
    ///   MSG_SPAWN:    Reliable, batched — 2 + 15*N bytes
    ///   MSG_SNAPSHOT: Unreliable, chunked — position updates for FLYING shards only
    ///   MSG_DESTROY:  Reliable, batched — 2 + 2*N bytes
    ///   MSG_STATE:    Reliable, batched — state transitions with 150ms hysteresis
    ///
    /// PERF vs v2:
    ///   - Spawns batched: 46 individual packets → 1 packet per frame
    ///   - Destroys batched: individual → batched per tick
    ///   - State transitions debounced: 150ms hysteresis prevents REST↔FLY spam
    ///   - Cached Rigidbody2D/Transform: eliminates GetComponent in hot paths
    ///   - Pre-allocated constructor args: eliminates per-call object[] allocation
    ///   - Per-type write arg arrays: reduces boxing isolation
    ///   - Reflection calls reduced ~90% during explosion bursts
    ///
    /// All NGO types accessed via reflection — no compile-time dependency on Unity.Netcode.
    /// </summary>
    public sealed class ShrapnelNetSync : MonoBehaviour
    {
        //  TUNING CONSTANTS

        /// <summary>How many times per second the server sends position snapshots.</summary>
        public const float SnapshotHz = 10f;

        /// <summary>Client-side interpolation speed (units/sec toward predicted position).</summary>
        public const float InterpolationSpeed = 15f;

        /// <summary>Seconds without snapshot update before client destroys a mirror.</summary>
        public const float MirrorTimeout = 3f;

        /// <summary>Maximum tracked shrapnel count (leak protection).</summary>
        public const int MaxShrapnel = 1000;

        /// <summary>Maximum plausible velocity magnitude for clamping extrapolation.</summary>
        public const float MaxExtrapolationSpeed = 150f;

        /// <summary>
        /// Max entries per snapshot packet to stay under UDP MTU (~1300 bytes).
        /// 2 (header) + 6 * 200 = 1202 bytes — safe margin for all transports.
        /// PERF: Splitting prevents "Writing past the end of the buffer" when
        /// tracked count exceeds ~216 which overflows a single UDP packet.
        /// </summary>
        private const int MaxEntriesPerSnapshotPacket = 200;

        /// <summary>
        /// Extra bytes added to FastBufferWriter allocation for NGO internal overhead.
        /// Prevents edge-case buffer overflows from alignment or framing bytes.
        /// </summary>
        private const int BufferHeadroom = 32;

        /// <summary>
        /// Max state transitions per batch message.
        /// Limits MSG_STATE packet size: 2 + 9*100 = 902 bytes.
        /// </summary>
        private const int MaxStateTransitionsPerPacket = 100;

        /// <summary>
        /// Max spawns per batch message.
        /// 2 + 17*70 = 1192 bytes — fits in single UDP packet.
        /// WHY: Was 80 at 15 bytes/entry. Now 70 at 17 bytes/entry (added RotZ).
        /// </summary>
        private const int MaxSpawnsPerPacket = 70;

        /// <summary>
        /// Max destroys per batch message.
        /// 2 + 2*200 = 402 bytes.
        /// </summary>
        private const int MaxDestroysPerPacket = 200;

        /// <summary>
        /// Minimum time a shard must remain at-rest before sending REST state.
        /// Prevents REST→FLY→REST spam when shards bounce on surfaces.
        /// PERF: Logs showed shards transitioning 3-5 times in 2 seconds,
        /// each generating a reliable network message. Debounce eliminates this.
        /// </summary>
        private const float StateDebounceTime = 0.15f;

        //  MESSAGE NAMES (prefixed "Shr" to avoid collisions)

        private const string MSG_SPAWN = "ShrShrapnelSpawn";
        private const string MSG_SNAPSHOT = "ShrShrapnelSnapshot";
        private const string MSG_DESTROY = "ShrShrapnelDestroy";
        private const string MSG_STATE = "ShrShrapnelState";

        //  SINGLETON

        private static ShrapnelNetSync _instance;

        //  SERVER STATE — cached tracking data

        /// <summary>
        /// Cached data for tracked projectiles. Stores component references
        /// to avoid GetComponent calls in hot paths (10Hz snapshot tick).
        /// PERF: Previously IsProjectileAtRest() called GetComponent<Rigidbody2D>()
        /// for every tracked shard every tick. Now cached at registration.
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

        private readonly Dictionary<ushort, TrackedShard> _tracked =
            new(256);

        /// <summary>
        /// Tracks which shards the server has reported as "at rest" to clients.
        /// When a shard's isKinematic transitions, we detect the mismatch and
        /// queue a MSG_STATE after the debounce period. Zero-GC after warmup.
        /// </summary>
        private readonly HashSet<ushort> _serverAtRest = new();

        /// <summary>
        /// Tracks when each shard's state change was first detected, for debounce.
        /// Key = netId, Value = Time.time when the mismatch was first observed.
        /// Removed once the state transition is confirmed and sent.
        /// </summary>
        private readonly Dictionary<ushort, float> _stateChangeTime =
            new(64);

        private ushort _nextId = 1;
        private float _snapshotTimer;

        /// <summary>Reusable list for dead-entry cleanup (zero-GC).</summary>
        private readonly List<ushort> _deadIds = new(64);

        /// <summary>Reusable list for snapshot iteration — flying shards only.</summary>
        private readonly List<KeyValuePair<ushort, TrackedShard>> _snapshotCache =
            new(256);

        /// <summary>Reusable list for state transition batch (zero-GC).</summary>
        private readonly List<StateTransition> _pendingStateChanges =
            new(64);

        /// <summary>Queued spawn data for batched sending at end of tick.</summary>
        private readonly List<SpawnData> _pendingSpawns = new(64);

        /// <summary>Queued destroy IDs for batched sending.</summary>
        private readonly List<ushort> _pendingDestroys = new(32);

        /// <summary>Compact state transition record for batching.</summary>
        private readonly struct StateTransition
        {
            public readonly ushort NetId;
            public readonly bool AtRest;
            public readonly short PosX;
            public readonly short PosY;
            /// <summary>Z rotation in degrees × 100. Only meaningful when AtRest=true.</summary>
            public readonly short RotZ;

            public StateTransition(ushort netId, bool atRest, Vector2 pos, float rotationZ)
            {
                NetId = netId;
                AtRest = atRest;
                PosX = (short)(pos.x * 10f);
                PosY = (short)(pos.y * 10f);
                // WHY: ×100 gives 0.01° precision, short range ±327° covers full circle
                RotZ = (short)Mathf.Clamp(rotationZ * 100f, short.MinValue, short.MaxValue);
            }
        }

        /// <summary>
        /// Pre-packed spawn data to avoid re-reading components at send time.
        /// PERF: All data packed once at registration, reused across all send attempts.
        /// </summary>
        private readonly struct SpawnData
        {
            public readonly ushort NetId;
            public readonly short PosX, PosY;
            public readonly byte TypePacked;
            public readonly byte HeatPacked;
            public readonly byte ShapeIndex;
            public readonly ushort ScalePacked;
            public readonly short VelX, VelY;
            /// <summary>Z rotation in degrees × 100. Provides 0.01° precision for at-rest shards.</summary>
            public readonly short RotZ;

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

        private readonly Dictionary<ushort, ClientMirrorShrapnel> _mirrors =
            new(256);

        /// <summary>Reusable list for stale mirror cleanup (zero-GC).</summary>
        private readonly List<ushort> _staleMirrorIds = new(32);

        private bool _handlersRegistered;

        //  REFLECTION CACHE: ServerMain.AllClientIdsExceptHost

        private PropertyInfo _allClientIdsProp;
        private bool _serverMainResolved;
        private IReadOnlyList<ulong> _cachedClientIds;
        private float _clientIdsCacheTimer;
        private const float ClientIdsCacheDuration = 0.5f;

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
        private object _deliveryUnreliable;
        private object _deliveryReliableSequenced;
        private object _allocatorTemp;
        private object _forPrimitivesDefault;

        //  PRE-ALLOCATED ARGS FOR REFLECTION (zero-GC writes)

        // PERF: Separate arg arrays per value type to isolate boxing.
        // Each array holds [0]=value(boxed), [1]=ForPrimitives(default, set once).
        // Previously shared _writeArgs2 re-assigned ForPrimitives every call.
        private readonly object[] _writeArgsUshort = new object[2];
        private readonly object[] _writeArgsShort = new object[2];
        private readonly object[] _writeArgsByte = new object[2];
        private readonly object[] _readArgs2 = new object[2];
        private readonly object[] _sendArgs4 = new object[4];
        private readonly object[] _regArgs2 = new object[2];
        private readonly object[] _unregArgs1 = new object[1];

        // PERF: Pre-allocated constructor args for CreateWriter — eliminates
        // `new object[]` allocation per call. Was 46+ allocations per explosion.
        private object[] _writerCtorArgs;

        //  DIAGNOSTICS COUNTERS

        private int _spawnsSent;
        private int _snapshotsSent;
        private int _destroysSent;
        private int _statesSent;
        private int _spawnsReceived;
        private int _snapshotsReceived;
        private int _destroysReceived;
        private int _statesReceived;
        private float _lastSnapshotReceiveTime;

        //  STATIC HANDLER DISPATCH (for DynamicMethod delegates)

        private static readonly List<Action<ulong, object>> _handlerSlots =
            new(4);

        // ReSharper disable once UnusedMember.Local — called via IL emit
        private static void DispatchHandler(int index, ulong senderId, object reader)
        {
            if (index >= 0 && index < _handlerSlots.Count)
                _handlerSlots[index]?.Invoke(senderId, reader);
        }

        //  LIFECYCLE

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

        public static void Shutdown()
        {
            if (_instance == null) return;

            try
            {
                _instance.UnregisterHandlers();
                _instance.DestroyAllMirrors();
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
                _deliveryUnreliable = Enum.Parse(deliveryType, "Unreliable");
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

                // PERF: Pre-allocate constructor args array once
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

                // PERF: Initialize per-type write arg arrays with ForPrimitives default.
                // Set once here instead of on every Write call.
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

        //  NGO WRAPPERS (reflection-based, zero compile-time deps)

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
                // PERF: Mutate pre-allocated args array instead of allocating new one
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

        // PERF: Each Write method uses its own arg array to isolate boxing
        // and avoid re-assigning ForPrimitives every call.
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

        public static void ServerRegister(ShrapnelProjectile proj)
        {
            if (_instance == null || !MultiplayerHelper.IsServer) return;
            if (proj == null) return;
            _instance.ServerRegisterInternal(proj);
        }

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

            // PERF: Queue spawn data instead of sending immediately.
            // Previously each of 46 spawns sent an individual reliable message.
            // Now all spawns in a frame are batched into 1 packet.
            QueueSpawnData(proj, shard, id);

#if DEBUG
            Vector2 vel = (shard.Rb != null && !shard.Rb.isKinematic)
                ? shard.Rb.velocity : Vector2.zero;
            Plugin.Log.LogInfo(
                $"[NetSend:SPAWN] id={id} type={proj.Type} weight={proj.Weight}" +
                $" heat={proj.Heat:F2} trail={proj.HasTrail}" +
                $" atRest={shard.IsAtRest}" +
                $" vel=({vel.x:F1},{vel.y:F1})");
#endif
        }

        /// <summary>
        /// Pre-packs spawn data into SpawnData struct at registration time.
        /// PERF: Avoids reading components again at send time.
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

            // WHY: Capture rotation for ALL shards, not just at-rest.
            // Flying shards derive rotation from velocity on client, but initial
            // rotation matters for the first frame before velocity correction kicks in.
            // At-rest shards need exact rotation since they never update it.
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

            // Flush pending spawns and destroys every frame to minimize latency
            if (_pendingSpawns.Count > 0)
                FlushSpawnBatch();

            if (_pendingDestroys.Count > 0)
                FlushDestroyBatch();

            _snapshotTimer += Time.deltaTime;
            if (_snapshotTimer < 1f / SnapshotHz) return;
            _snapshotTimer = 0f;

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

            // Phase 2: Detect state transitions with debounce + build flying snapshot cache
            _pendingStateChanges.Clear();
            _snapshotCache.Clear();

            foreach (var kvp in _tracked)
            {
                if (kvp.Value.IsNull) continue;

                bool currentlyAtRest = kvp.Value.IsAtRest;
                bool wasAtRest = _serverAtRest.Contains(kvp.Key);

                if (currentlyAtRest != wasAtRest)
                {
                    // PERF: Debounce. Only send after StateDebounceTime elapsed.
                    // Prevents REST→FLY→REST spam from bouncing shards.
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
                    // State matches — clear any pending debounce (state reverted naturally)
                    _stateChangeTime.Remove(kvp.Key);
                }

                if (!currentlyAtRest)
                    _snapshotCache.Add(kvp);
            }

            if (_pendingStateChanges.Count > 0)
                SendStateBatch();

            if (_snapshotCache.Count > 0)
                SendSnapshot();
        }

        //  SERVER → CLIENT: BATCHED SPAWN (Reliable)
        //  Packet: 2 + 15*N bytes per chunk, max 80 entries

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
            // WHY: 17 bytes per entry (was 15) — added 2 bytes for RotZ
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

        //  SERVER → CLIENT: SNAPSHOT (Unreliable, chunked)
        //  Only FLYING shards — grounded shards excluded.
        //  Packet: 2 + 6*N bytes per chunk, max 200 entries.

        private void SendSnapshot()
        {
            var clientIds = GetClientIds();
            if (clientIds == null || clientIds.Count == 0) return;

            var cmm = GetCustomMessagingManager();
            if (cmm == null) return;

            int totalCount = _snapshotCache.Count;
            if (totalCount == 0) return;

            int offset = 0;
            while (offset < totalCount)
            {
                int chunkCount = Mathf.Min(totalCount - offset, MaxEntriesPerSnapshotPacket);
                SendSnapshotChunk(cmm, clientIds, offset, chunkCount);
                offset += chunkCount;
            }
        }

        private void SendSnapshotChunk(object cmm, IReadOnlyList<ulong> clientIds,
            int offset, int count)
        {
            ushort countU = (ushort)count;
            int size = 2 + 6 * countU;
            object writer = CreateWriter(size + BufferHeadroom);
            if (writer == null) return;

            try
            {
                WriteUshort(writer, countU);

                for (int i = 0; i < countU; i++)
                {
                    var kvp = _snapshotCache[offset + i];
                    // PERF: Use cached Transform instead of kvp.Value.Proj.transform
                    Vector2 pos = kvp.Value.Transform.position;
                    short px = (short)(pos.x * 10f);
                    short py = (short)(pos.y * 10f);

                    WriteUshort(writer, kvp.Key);
                    WriteShort(writer, px);
                    WriteShort(writer, py);
                }

                SendMessage(cmm, MSG_SNAPSHOT, clientIds, writer, _deliveryUnreliable);
                _snapshotsSent++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelNetSync] SendSnapshot failed: {e.Message}");
            }
            finally
            {
                DisposeWriter(writer);
            }
        }

        //  SERVER → CLIENT: STATE TRANSITION (Reliable, batched)
        //  Packet: 2 + 9*N bytes per chunk, max 100 entries
        //    count(2) + N × (netId:2 + state:1 + posX:2 + posY:2 + rotZ:2)

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
                // WHY: Count individual transitions, not packets.
                // Previously _statesSent++ counted packets while client counted
                // individual transitions → logs showed 39 sent vs 154 received.
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
        //  Packet: 2 + 2*N bytes per chunk, max 200 entries

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
                RegisterSingleHandler(cmm, MSG_SNAPSHOT,
                    new Action<ulong, object>(OnReceiveSnapshotRaw));
                RegisterSingleHandler(cmm, MSG_DESTROY,
                    new Action<ulong, object>(OnReceiveDestroyRaw));
                RegisterSingleHandler(cmm, MSG_STATE,
                    new Action<ulong, object>(OnReceiveStateRaw));

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
                    UnregisterSingle(cmm, MSG_SNAPSHOT);
                    UnregisterSingle(cmm, MSG_DESTROY);
                    UnregisterSingle(cmm, MSG_STATE);
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

        //  CLIENT: RECEIVE HANDLERS (v3 — batched spawns/destroys)

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
                    if (_mirrors.TryGetValue(netId, out var existing))
                    {
                        if (existing != null && existing.gameObject != null)
                            Destroy(existing.gameObject);
                        _mirrors.Remove(netId);
                    }

                    var mirror = ClientMirrorShrapnel.Create(
                        netId, new Vector2(x, y), type, weight, heat, shape, scale,
                        hasTrail, atRest, new Vector2(velX, velY), rotZ);

                    if (mirror != null)
                        _mirrors[netId] = mirror;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] OnReceiveSpawn failed: {e.Message}");
            }
        }


        private void OnReceiveSnapshotRaw(ulong senderId, object readerObj)
        {
            try
            {
                _snapshotsReceived++;
                _lastSnapshotReceiveTime = Time.time;

                ushort count = ReadUshort(readerObj);

                for (int i = 0; i < count; i++)
                {
                    ushort netId = ReadUshort(readerObj);
                    short posX = ReadShort(readerObj);
                    short posY = ReadShort(readerObj);

                    float x = posX / 10f;
                    float y = posY / 10f;

                    if (_mirrors.TryGetValue(netId, out var mirror))
                    {
                        if (mirror != null)
                            mirror.OnSnapshotReceived(new Vector2(x, y));
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] OnReceiveSnapshot failed: {e.Message}");
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

                    if (_mirrors.TryGetValue(netId, out var mirror))
                    {
                        if (mirror != null && mirror.gameObject != null)
                        {
                            // WHY: BeginFadeDestroy instead of instant Destroy.
                            // Mirror may still be visually flying (no collisions on client).
                            // Instant destroy causes jarring mid-flight vanish.
                            mirror.BeginFadeDestroy();
                        }
                        // Don't remove from _mirrors here — mirror removes itself
                        // in OnDestroy via NotifyMirrorDestroyed.
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] OnReceiveDestroy failed: {e.Message}");
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

                    if (_mirrors.TryGetValue(netId, out var mirror))
                    {
                        if (mirror != null)
                        {
                            if (atRest)
                                mirror.TransitionToRest(new Vector2(x, y), rotZ);
                            else
                                mirror.TransitionToFlying(new Vector2(x, y));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] OnReceiveState failed: {e.Message}");
            }
        }

        //  DIAGNOSTICS API

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

                return "=== SHRAPNEL NET SYNC ===\n" +
                    $"  Role: SERVER (host)\n" +
                    $"  Tracked shrapnel: {i._tracked.Count}\n" +
                    $"  Flying: {flyingCount} | At rest: {i._serverAtRest.Count}\n" +
                    $"  Pending spawns: {i._pendingSpawns.Count}\n" +
                    $"  Pending destroys: {i._pendingDestroys.Count}\n" +
                    $"  Debouncing states: {i._stateChangeTime.Count}\n" +
                    $"  Next ID: {i._nextId}\n" +
                    $"  Spawns sent: {i._spawnsSent}\n" +
                    $"  Snapshots sent: {i._snapshotsSent}\n" +
                    $"  States sent: {i._statesSent}\n" +
                    $"  Destroys sent: {i._destroysSent}\n" +
                    $"  Connected clients: {clientCount}\n" +
                    $"  Snapshot rate: {SnapshotHz} Hz\n" +
                    $"  State debounce: {StateDebounceTime * 1000f:F0}ms\n" +
                    $"  Max tracked: {MaxShrapnel}";
            }
            else
            {
                float snapshotAge = i._lastSnapshotReceiveTime > 0f
                    ? Time.time - i._lastSnapshotReceiveTime
                    : -1f;
                string ageStr = snapshotAge >= 0f ? $"{snapshotAge:F2}s ago" : "never";

                int restMirrors = 0;
                foreach (var kvp in i._mirrors)
                {
                    if (kvp.Value != null && kvp.Value.IsAtRest)
                        restMirrors++;
                }

                return "=== SHRAPNEL NET SYNC ===\n" +
                    $"  Role: CLIENT\n" +
                    $"  Active mirrors: {i._mirrors.Count}\n" +
                    $"  Flying: {i._mirrors.Count - restMirrors} | At rest: {restMirrors}\n" +
                    $"  Spawns received: {i._spawnsReceived}\n" +
                    $"  Snapshots received: {i._snapshotsReceived}\n" +
                    $"  States received: {i._statesReceived}\n" +
                    $"  Destroys received: {i._destroysReceived}\n" +
                    $"  Last snapshot: {ageStr}\n" +
                    $"  Handlers registered: {i._handlersRegistered}\n" +
                    $"  NGO available: {i._ngoAvailable}";
            }
        }

        public static string GetBriefStatus()
        {
            if (_instance == null) return "NET:off";

            var i = _instance;

            if (MultiplayerHelper.IsServer)
            {
                int flyingCount = i._tracked.Count - i._serverAtRest.Count;
                return $"NET:SERVER tracked={i._tracked.Count}" +
                    $" fly={flyingCount} rest={i._serverAtRest.Count}" +
                    $" sent={i._spawnsSent}/{i._snapshotsSent}/{i._statesSent}/{i._destroysSent}";
            }
            else
            {
                return $"NET:CLIENT mirrors={i._mirrors.Count}" +
                    $" recv={i._spawnsReceived}/{i._snapshotsReceived}/{i._statesReceived}/{i._destroysReceived}";
            }
        }

        //  CLIENT: MIRROR CLEANUP

        internal static void NotifyMirrorDestroyed(ushort netId)
        {
            if (_instance == null) return;
            _instance._mirrors.Remove(netId);
        }

        private void DestroyAllMirrors()
        {
            _staleMirrorIds.Clear();
            foreach (var kvp in _mirrors)
                _staleMirrorIds.Add(kvp.Key);

            for (int i = 0; i < _staleMirrorIds.Count; i++)
            {
                ushort id = _staleMirrorIds[i];
                if (_mirrors.TryGetValue(id, out var mirror))
                {
                    if (mirror != null && mirror.gameObject != null)
                        Destroy(mirror.gameObject);
                }
            }
            _mirrors.Clear();
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