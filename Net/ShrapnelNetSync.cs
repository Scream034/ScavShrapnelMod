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
    /// All NGO types (NetworkManager, FastBufferWriter/Reader, CustomMessagingManager,
    /// NetworkDelivery) accessed via reflection — no compile-time dependency on Unity.Netcode.
    /// This allows the mod to compile and run without the MP mod installed.
    /// </summary>
    public sealed class ShrapnelNetSync : MonoBehaviour
    {
        //  TUNING CONSTANTS

        /// <summary>How many times per second the server sends position snapshots.</summary>
        public const float SnapshotHz = 10f;

        /// <summary>Client-side interpolation speed (units/sec toward predicted position).</summary>
        public const float InterpolationSpeed = 15f;

        /// <summary>Seconds without snapshot update before client destroys a mirror.</summary>
        public const float MirrorTimeout = 2f;

        /// <summary>Maximum tracked shrapnel count (leak protection).</summary>
        public const int MaxShrapnel = 1000;

        /// <summary>Maximum plausible velocity magnitude for clamping extrapolation.</summary>
        public const float MaxExtrapolationSpeed = 150f;

        //  MESSAGE NAMES (prefixed "Shr" to avoid collisions)

        private const string MSG_SPAWN = "ShrShrapnelSpawn";
        private const string MSG_SNAPSHOT = "ShrShrapnelSnapshot";
        private const string MSG_DESTROY = "ShrShrapnelDestroy";

        //  SINGLETON

        private static ShrapnelNetSync _instance;

        //  SERVER STATE

        private readonly Dictionary<ushort, ShrapnelProjectile> _tracked =
            new Dictionary<ushort, ShrapnelProjectile>(256);

        private ushort _nextId = 1;
        private float _snapshotTimer;

        /// <summary>Reusable list for dead-entry cleanup (zero-GC).</summary>
        private readonly List<ushort> _deadIds = new List<ushort>(64);

        /// <summary>Reusable list for snapshot iteration (zero-GC).</summary>
        private readonly List<KeyValuePair<ushort, ShrapnelProjectile>> _snapshotCache =
            new List<KeyValuePair<ushort, ShrapnelProjectile>>(256);

        //  CLIENT STATE

        private readonly Dictionary<ushort, ClientMirrorShrapnel> _mirrors =
            new Dictionary<ushort, ClientMirrorShrapnel>(256);

        /// <summary>Reusable list for stale mirror cleanup (zero-GC).</summary>
        private readonly List<ushort> _staleMirrorIds = new List<ushort>(32);

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

        // NetworkManager
        private Type _networkManagerType;
        private PropertyInfo _nmSingletonProp;
        private PropertyInfo _nmCustomMessagingProp;

        // CustomMessagingManager type
        private Type _cmmType;

        // SendNamedMessage(string, IReadOnlyList<ulong>, FastBufferWriter, NetworkDelivery)
        private MethodInfo _sendNamedMessageMethod;

        // RegisterNamedMessageHandler(string, HandleNamedMessageDelegate)
        private MethodInfo _registerHandlerMethod;

        // UnregisterNamedMessageHandler(string)
        private MethodInfo _unregisterHandlerMethod;

        // HandleNamedMessageDelegate type
        private Type _handleNamedMessageDelegateType;

        // FastBufferWriter
        private Type _fastBufferWriterType;
        private ConstructorInfo _fbwCtor;
        private MethodInfo _fbwDispose;
        private MethodInfo _fbwWriteUshort;
        private MethodInfo _fbwWriteShort;
        private MethodInfo _fbwWriteByte;

        // FastBufferReader
        private Type _fastBufferReaderType;
        private MethodInfo _fbrReadUshort;
        private MethodInfo _fbrReadShort;
        private MethodInfo _fbrReadByte;

        // NetworkDelivery enum values (boxed)
        private object _deliveryReliable;
        private object _deliveryUnreliable;
        private object _deliveryReliableSequenced;

        // Allocator.Temp (boxed)
        private object _allocatorTemp;

        // ForPrimitives default instance (boxed)
        private object _forPrimitivesDefault;

        //  DIAGNOSTICS COUNTERS

        private int _spawnsSent;
        private int _snapshotsSent;
        private int _destroysSent;
        private int _spawnsReceived;
        private int _snapshotsReceived;
        private int _destroysReceived;
        private float _lastSnapshotReceiveTime;

        //  REUSABLE ARG ARRAYS (zero-GC for reflection Invoke)

        private readonly object[] _writeArgs2 = new object[2];
        private readonly object[] _readArgs2 = new object[2];
        private readonly object[] _sendArgs4 = new object[4];
        private readonly object[] _regArgs2 = new object[2];
        private readonly object[] _unregArgs1 = new object[1];

        //  STATIC HANDLER DISPATCH (for DynamicMethod delegates)

        private static readonly List<Action<ulong, object>> _handlerSlots =
            new List<Action<ulong, object>>(4);

        /// <summary>
        /// Static dispatch target called by DynamicMethod delegates.
        /// Matches signature used in IL emit: (int index, ulong senderId, object boxedReader).
        /// </summary>
        // ReSharper disable once UnusedMember.Local — called via IL emit
        private static void DispatchHandler(int index, ulong senderId, object reader)
        {
            if (index >= 0 && index < _handlerSlots.Count)
                _handlerSlots[index]?.Invoke(senderId, reader);
        }

        //  LIFECYCLE

        /// <summary>
        /// Creates the ShrapnelNetSync singleton. Call only when
        /// MultiplayerHelper.IsNetworkRunning is true.
        /// Safe to call multiple times — subsequent calls are no-ops.
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
        /// Destroys the singleton and cleans up all state.
        /// Safe to call when _instance is null.
        /// </summary>
        public static void Shutdown()
        {
            if (_instance == null) return;

            try
            {
                _instance.UnregisterHandlers();
                _instance.DestroyAllMirrors();
                _instance._tracked.Clear();
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

                // ── Find Unity.Netcode.Runtime assembly ──
                Assembly ngoAssembly = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Unity.Netcode.Runtime")
                    {
                        ngoAssembly = asm;
                        break;
                    }
                }
                if (ngoAssembly == null)
                {
                    LogMissing("Unity.Netcode.Runtime assembly");
                    return;
                }

                // ── NetworkManager ──
                _networkManagerType = ngoAssembly.GetType("Unity.Netcode.NetworkManager");
                if (_networkManagerType == null) { LogMissing("NetworkManager type"); return; }

                _nmSingletonProp = _networkManagerType.GetProperty("Singleton", allFlags);
                if (_nmSingletonProp == null) { LogMissing("NetworkManager.Singleton"); return; }

                _nmCustomMessagingProp = _networkManagerType.GetProperty(
                    "CustomMessagingManager", allFlags);
                if (_nmCustomMessagingProp == null) { LogMissing("CustomMessagingManager prop"); return; }

                // ── CustomMessagingManager ──
                _cmmType = ngoAssembly.GetType("Unity.Netcode.CustomMessagingManager");
                if (_cmmType == null) { LogMissing("CustomMessagingManager type"); return; }

                // ── HandleNamedMessageDelegate ──
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
                    // Try non-nested
                    _handleNamedMessageDelegateType = ngoAssembly.GetType(
                        "Unity.Netcode.CustomMessagingManager+HandleNamedMessageDelegate");
                }
                if (_handleNamedMessageDelegateType == null)
                {
                    LogMissing("HandleNamedMessageDelegate");
                    return;
                }

                // ── FastBufferWriter ──
                _fastBufferWriterType = ngoAssembly.GetType("Unity.Netcode.FastBufferWriter");
                if (_fastBufferWriterType == null) { LogMissing("FastBufferWriter"); return; }

                // ── FastBufferReader ──
                _fastBufferReaderType = ngoAssembly.GetType("Unity.Netcode.FastBufferReader");
                if (_fastBufferReaderType == null) { LogMissing("FastBufferReader"); return; }

                // ── NetworkDelivery enum ──
                var deliveryType = ngoAssembly.GetType("Unity.Netcode.NetworkDelivery");
                if (deliveryType == null) { LogMissing("NetworkDelivery"); return; }
                _deliveryReliable = Enum.Parse(deliveryType, "Reliable");
                _deliveryUnreliable = Enum.Parse(deliveryType, "Unreliable");
                _deliveryReliableSequenced = Enum.Parse(deliveryType, "ReliableSequenced");

                // ── Allocator.Temp ──
                var allocatorType = typeof(Unity.Collections.Allocator);
                _allocatorTemp = Enum.Parse(allocatorType, "Temp");

                // ── ForPrimitives default ──
                var forPrimType = _fastBufferWriterType.GetNestedType("ForPrimitives", allFlags);
                if (forPrimType != null)
                    _forPrimitivesDefault = Activator.CreateInstance(forPrimType);

                // ── FastBufferWriter constructor ──
                _fbwCtor = _fastBufferWriterType.GetConstructor(
                    new[] { typeof(int), allocatorType, typeof(int) }) ?? _fastBufferWriterType.GetConstructor(
                        new[] { typeof(int), allocatorType });
                if (_fbwCtor == null) { LogMissing("FastBufferWriter ctor"); return; }

                // ── FastBufferWriter.Dispose ──
                _fbwDispose = _fastBufferWriterType.GetMethod("Dispose", Type.EmptyTypes);
                if (_fbwDispose == null) { LogMissing("FastBufferWriter.Dispose"); return; }

                // ── WriteValueSafe<T>(in T, ForPrimitives) ──
                ResolveWriteMethods(allFlags);
                if (_fbwWriteUshort == null) { LogMissing("WriteValueSafe<ushort>"); return; }
                if (_fbwWriteShort == null) { LogMissing("WriteValueSafe<short>"); return; }
                if (_fbwWriteByte == null) { LogMissing("WriteValueSafe<byte>"); return; }

                // ── ReadValueSafe<T>(out T, ForPrimitives) ──
                ResolveReadMethods(allFlags);
                if (_fbrReadUshort == null) { LogMissing("ReadValueSafe<ushort>"); return; }
                if (_fbrReadShort == null) { LogMissing("ReadValueSafe<short>"); return; }
                if (_fbrReadByte == null) { LogMissing("ReadValueSafe<byte>"); return; }

                // ── SendNamedMessage ──
                // void SendNamedMessage(string, IReadOnlyList<ulong>, FastBufferWriter, NetworkDelivery)
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

                // ── RegisterNamedMessageHandler ──
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

                // ── UnregisterNamedMessageHandler ──
                _unregisterHandlerMethod = _cmmType.GetMethod(
                    "UnregisterNamedMessageHandler", new[] { typeof(string) });

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
            // Looking for generic: WriteValueSafe<T>(in T value, ForPrimitives unused)
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
            // Looking for generic: ReadValueSafe<T>(out T value, ForPrimitives unused)
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

        /// <summary>Creates a boxed FastBufferWriter(size, Allocator.Temp).</summary>
        private object CreateWriter(int size)
        {
            try
            {
                var ctorParams = _fbwCtor.GetParameters();
                if (ctorParams.Length == 3)
                    return _fbwCtor.Invoke(new object[] { size, _allocatorTemp, -1 });
                return _fbwCtor.Invoke(new object[] { size, _allocatorTemp });
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
            _writeArgs2[0] = value;
            _writeArgs2[1] = _forPrimitivesDefault;
            _fbwWriteUshort.Invoke(writer, _writeArgs2);
        }

        private void WriteShort(object writer, short value)
        {
            _writeArgs2[0] = value;
            _writeArgs2[1] = _forPrimitivesDefault;
            _fbwWriteShort.Invoke(writer, _writeArgs2);
        }

        private void WriteByte(object writer, byte value)
        {
            _writeArgs2[0] = value;
            _writeArgs2[1] = _forPrimitivesDefault;
            _fbwWriteByte.Invoke(writer, _writeArgs2);
        }

        private ushort ReadUshort(object reader)
        {
            _readArgs2[0] = (ushort)0;
            _readArgs2[1] = _forPrimitivesDefault;
            _fbrReadUshort.Invoke(reader, _readArgs2);
            return (ushort)_readArgs2[0];
        }

        private short ReadShort(object reader)
        {
            _readArgs2[0] = (short)0;
            _readArgs2[1] = _forPrimitivesDefault;
            _fbrReadShort.Invoke(reader, _readArgs2);
            return (short)_readArgs2[0];
        }

        private byte ReadByte(object reader)
        {
            _readArgs2[0] = (byte)0;
            _readArgs2[1] = _forPrimitivesDefault;
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
        /// Registers a newly spawned ShrapnelProjectile for network sync.
        /// Assigns NetSyncId and sends spawn message to all clients.
        /// No-op in singleplayer, on client, or when instance is null.
        /// </summary>
        public static void ServerRegister(ShrapnelProjectile proj)
        {
            if (_instance == null || !MultiplayerHelper.IsServer) return;
            if (proj == null) return;
            _instance.ServerRegisterInternal(proj);
        }

        /// <summary>
        /// Unregisters a destroyed ShrapnelProjectile and notifies clients.
        /// No-op in singleplayer, on client, or if netId is 0.
        /// </summary>
        public static void ServerUnregister(ushort netId)
        {
            if (_instance == null || !MultiplayerHelper.IsServer) return;
            if (netId == 0) return;
            _instance.ServerUnregisterInternal(netId);
        }

        private void ServerRegisterInternal(ShrapnelProjectile proj)
        {
            if (_tracked.Count >= MaxShrapnel)
                return; // leak protection

            ushort id = _nextId++;
            if (_nextId == 0) _nextId = 1; // wraparound, skip 0

            proj.NetSyncId = id;
            _tracked[id] = proj;
            SendSpawn(proj, id);
        }

        private void ServerUnregisterInternal(ushort netId)
        {
            _tracked.Remove(netId);
            SendDestroy(netId);
        }

        //  SERVER UPDATE

        private void Update()
        {
            if (MultiplayerHelper.IsServer)
                ServerUpdate();
        }

        private void ServerUpdate()
        {
            _snapshotTimer += Time.deltaTime;
            if (_snapshotTimer < 1f / SnapshotHz) return;
            _snapshotTimer = 0f;

            // Purge dead entries (destroyed without OnDestroy, or null ref)
            _deadIds.Clear();
            foreach (var kvp in _tracked)
            {
                if (kvp.Value == null)
                    _deadIds.Add(kvp.Key);
            }
            for (int i = 0; i < _deadIds.Count; i++)
            {
                ushort deadId = _deadIds[i];
                _tracked.Remove(deadId);
                SendDestroy(deadId);
            }

            if (_tracked.Count > 0)
                SendSnapshot();
        }

        //  SERVER → CLIENT: SPAWN (Reliable, 11 bytes)
        //  Packet layout:
        //    netId(2) + posX(2) + posY(2) + typePacked(1) + heat(1) + shape(1) + scale(2) = 11
        //  typePacked bit layout:
        //    bits 0-2: ShrapnelType (0-4)
        //    bits 3-5: ShrapnelWeight (0-4)
        //    bit 6:    HasTrail
        //    bit 7:    reserved

        private void SendSpawn(ShrapnelProjectile proj, ushort netId)
        {
            var clientIds = GetClientIds();
            if (clientIds == null || clientIds.Count == 0) return;

            var cmm = GetCustomMessagingManager();
            if (cmm == null) return;

            Vector2 pos = proj.transform.position;
            short posX = (short)(pos.x * 10f);
            short posY = (short)(pos.y * 10f);

            // WHY: Pack type(3 bits) + weight(3 bits) + hasTrail(1 bit) into single byte
            byte typePacked = (byte)(
                (int)proj.Type |
                ((int)proj.Weight << 3) |
                (proj.HasTrail ? (1 << 6) : 0));

            byte heatPacked = (byte)(Mathf.Clamp01(proj.Heat) * 255f);

            // Determine shape by matching sprite
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

            float scale = proj.transform.localScale.x;
            ushort scalePacked = (ushort)(Mathf.Clamp(scale, 0f, 65.535f) * 1000f);

            // netId(2) + posX(2) + posY(2) + type(1) + heat(1) + shape(1) + scale(2) = 11
            const int size = 11;
            object writer = CreateWriter(size);
            if (writer == null) return;

            try
            {
                WriteUshort(writer, netId);
                WriteShort(writer, posX);
                WriteShort(writer, posY);
                WriteByte(writer, typePacked);
                WriteByte(writer, heatPacked);
                WriteByte(writer, shapeIndex);
                WriteUshort(writer, scalePacked);

                SendMessage(cmm, MSG_SPAWN, clientIds, writer, _deliveryReliable);
                _spawnsSent++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelNetSync] SendSpawn failed: {e.Message}");
            }
            finally
            {
                DisposeWriter(writer);
            }
        }

        //  SERVER = CLIENT: SNAPSHOT (Unreliable, 2 + 6*N bytes)

        private void SendSnapshot()
        {
            var clientIds = GetClientIds();
            if (clientIds == null || clientIds.Count == 0) return;

            var cmm = GetCustomMessagingManager();
            if (cmm == null) return;

            // Build cache to avoid Dictionary enumerator allocation during write
            _snapshotCache.Clear();
            foreach (var kvp in _tracked)
            {
                if (kvp.Value != null)
                    _snapshotCache.Add(kvp);
            }

            int count = _snapshotCache.Count;
            if (count == 0) return;

            ushort countU = (ushort)Mathf.Min(count, ushort.MaxValue);
            int size = 2 + 6 * countU;
            object writer = CreateWriter(size);
            if (writer == null) return;

            try
            {
                WriteUshort(writer, countU);

                for (int i = 0; i < countU; i++)
                {
                    var kvp = _snapshotCache[i];
                    ushort nid = kvp.Key;
                    Vector2 pos = kvp.Value.transform.position;
                    short px = (short)(pos.x * 10f);
                    short py = (short)(pos.y * 10f);

                    WriteUshort(writer, nid);
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

        //  SERVER = CLIENT: DESTROY (ReliableSequenced, 2 bytes)

        private void SendDestroy(ushort netId)
        {
            var clientIds = GetClientIds();
            if (clientIds == null || clientIds.Count == 0) return;

            var cmm = GetCustomMessagingManager();
            if (cmm == null) return;

            const int size = 2;
            object writer = CreateWriter(size);
            if (writer == null) return;

            try
            {
                WriteUshort(writer, netId);
                SendMessage(cmm, MSG_DESTROY, clientIds, writer, _deliveryReliableSequenced);
                _destroysSent++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ShrapnelNetSync] SendDestroy failed: {e.Message}");
            }
            finally
            {
                DisposeWriter(writer);
            }
        }

        //  CLIENT: HANDLER REGISTRATION
        //
        //  HandleNamedMessageDelegate has signature:
        //    void(ulong senderId, FastBufferReader messagePayload)
        //
        //  FastBufferReader is a STRUCT we can't reference at compile time.
        //  Solution: emit DynamicMethod that boxes the struct and forwards
        //  to our static DispatchHandler(int, ulong, object).

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

                _handlersRegistered = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] RegisterHandlers failed: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Creates a DynamicMethod matching HandleNamedMessageDelegate(ulong, FastBufferReader)
        /// that boxes the FastBufferReader struct and forwards to DispatchHandler(int, ulong, object).
        /// This is necessary because we cannot reference FastBufferReader at compile time.
        /// </summary>
        private void RegisterSingleHandler(object cmm, string msgName,
            Action<ulong, object> handler)
        {
            int slotIndex = _handlerSlots.Count;
            _handlerSlots.Add(handler);

            // DynamicMethod signature matches HandleNamedMessageDelegate:
            //   void(ulong senderId, FastBufferReader payload)
            var dm = new DynamicMethod(
                "ShrNS_" + msgName,
                typeof(void),
                new[] { typeof(ulong), _fastBufferReaderType },
                typeof(ShrapnelNetSync).Module,
                skipVisibility: true);

            ILGenerator il = dm.GetILGenerator();

            // Push slotIndex (int constant)
            il.Emit(OpCodes.Ldc_I4, slotIndex);

            // Push senderId (arg 0, ulong)
            il.Emit(OpCodes.Ldarg_0);

            // Push reader (arg 1, FastBufferReader struct) and box to object
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Box, _fastBufferReaderType);

            // Call static DispatchHandler(int, ulong, object)
            MethodInfo dispatchMI = typeof(ShrapnelNetSync).GetMethod(
                nameof(DispatchHandler),
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(int), typeof(ulong), typeof(object) },
                null);
            il.Emit(OpCodes.Call, dispatchMI);

            il.Emit(OpCodes.Ret);

            // Create delegate of the correct type
            Delegate del = dm.CreateDelegate(_handleNamedMessageDelegateType);

            // Register with CMM
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
        //  Parameter 'readerObj' is a boxed FastBufferReader.
        //  Read methods operate on it via reflection (unboxing
        //  happens inside the reflected method call).

        private void OnReceiveSpawnRaw(ulong senderId, object readerObj)
        {
            try
            {
                _spawnsReceived++;

                ushort netId = ReadUshort(readerObj);
                short posX = ReadShort(readerObj);
                short posY = ReadShort(readerObj);
                byte typePacked = ReadByte(readerObj);
                byte heatPacked = ReadByte(readerObj);
                byte shapeIndex = ReadByte(readerObj);
                ushort scalePacked = ReadUshort(readerObj);

                float x = posX / 10f;
                float y = posY / 10f;
                var type = (ShrapnelProjectile.ShrapnelType)(typePacked & 0x07);
                var weight = (ShrapnelWeight)((typePacked >> 3) & 0x07);
                bool hasTrail = (typePacked & (1 << 6)) != 0;
                float heat = heatPacked / 255f;
                var shape = (ShrapnelVisuals.TriangleShape)Mathf.Clamp(shapeIndex, 0, 5);
                float scale = scalePacked / 1000f;

                // Overwrite duplicate (protection against double-delivery)
                if (_mirrors.TryGetValue(netId, out var existing))
                {
                    if (existing != null && existing.gameObject != null)
                        Destroy(existing.gameObject);
                    _mirrors.Remove(netId);
                }

                var mirror = ClientMirrorShrapnel.Create(
                    netId, new Vector2(x, y), type, weight, heat, shape, scale,
                    hasTrail);

                if (mirror != null)
                    _mirrors[netId] = mirror;
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
                            mirror.SetTarget(new Vector2(x, y));
                    }
                    // else: spawn not yet received — silently ignore
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
                _destroysReceived++;

                ushort netId = ReadUshort(readerObj);

                if (_mirrors.TryGetValue(netId, out var mirror))
                {
                    if (mirror != null && mirror.gameObject != null)
                        Destroy(mirror.gameObject);
                    _mirrors.Remove(netId);
                }
                // else: already gone or never arrived — silently ignore
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[ShrapnelNetSync] OnReceiveDestroy failed: {e.Message}");
            }
        }

        //  DIAGNOSTICS API

        /// <summary>
        /// Returns detailed diagnostic string for shrapnel_net console command.
        /// </summary>
        public static string GetDiagnostics()
        {
            if (_instance == null)
                return "[ShrapnelNetSync] Not initialized (singleplayer or no MP mod)";

            var i = _instance;

            if (MultiplayerHelper.IsServer)
            {
                var clientIds = i.GetClientIds();
                int clientCount = clientIds != null ? clientIds.Count : 0;

                // Count fragments with trails for diagnostics
                int trailCount = 0;
                foreach (var kvp in i._tracked)
                {
                    if (kvp.Value != null && kvp.Value.HasTrail)
                        trailCount++;
                }

                return "=== SHRAPNEL NET SYNC ===\n" +
                    $"  Role: SERVER (host)\n" +
                    $"  Tracked shrapnel: {i._tracked.Count} ({trailCount} with trail)\n" +
                    $"  Next ID: {i._nextId}\n" +
                    $"  Spawns sent: {i._spawnsSent}\n" +
                    $"  Snapshots sent: {i._snapshotsSent}\n" +
                    $"  Destroys sent: {i._destroysSent}\n" +
                    $"  Connected clients: {clientCount}\n" +
                    $"  Snapshot rate: {SnapshotHz} Hz\n" +
                    $"  Max tracked: {MaxShrapnel}";
            }
            else
            {
                float snapshotAge = i._lastSnapshotReceiveTime > 0f
                    ? Time.time - i._lastSnapshotReceiveTime
                    : -1f;
                string ageStr = snapshotAge >= 0f ? $"{snapshotAge:F2}s ago" : "never";

                // Count mirrors with trails for diagnostics
                int trailMirrors = 0;
                foreach (var kvp in i._mirrors)
                {
                    if (kvp.Value != null)
                    {
                        var tr = kvp.Value.GetComponent<TrailRenderer>();
                        if (tr != null) trailMirrors++;
                    }
                }

                return "=== SHRAPNEL NET SYNC ===\n" +
                    $"  Role: CLIENT\n" +
                    $"  Active mirrors: {i._mirrors.Count} ({trailMirrors} with trail)\n" +
                    $"  Spawns received: {i._spawnsReceived}\n" +
                    $"  Snapshots received: {i._snapshotsReceived}\n" +
                    $"  Destroys received: {i._destroysReceived}\n" +
                    $"  Last snapshot: {ageStr}\n" +
                    $"  Handlers registered: {i._handlersRegistered}\n" +
                    $"  NGO available: {i._ngoAvailable}";
            }
        }

        /// <summary>
        /// Returns brief one-line status for shrapnel_status command.
        /// </summary>
        public static string GetBriefStatus()
        {
            if (_instance == null) return "NET:off";

            var i = _instance;

            if (MultiplayerHelper.IsServer)
            {
                return $"NET:SERVER tracked={i._tracked.Count}" +
                    $" sent={i._spawnsSent}/{i._snapshotsSent}/{i._destroysSent}";
            }
            else
            {
                return $"NET:CLIENT mirrors={i._mirrors.Count}" +
                    $" recv={i._spawnsReceived}/{i._snapshotsReceived}/{i._destroysReceived}";
            }
        }

        //  CLIENT: MIRROR CLEANUP

        /// <summary>
        /// Called by ClientMirrorShrapnel.OnDestroy to remove itself
        /// from the tracking dictionary. Safe to call with unknown netId.
        /// </summary>
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