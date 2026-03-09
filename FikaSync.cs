using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;

namespace MortarStrikes
{
    /// <summary>
    /// FIKA networking integration
    /// </summary>
    public static class FikaSync
    {
        private static ManualLogSource Log => Plugin.Log;
        private static bool _initDone;
        private static bool _fikaAvailable;
        private static bool _packetRegistered;
        private static bool _packetRegistrationAttempted;
        private static Assembly _fikaAsm;
        private static PropertyInfo _isServerProp;
        private static PropertyInfo _isInRaidProp;
        private static PropertyInfo _networkManagerSingletonProp;
        private static Type _packetType;
        private static FieldInfo _fxField;
        private static FieldInfo _fyField;
        private static FieldInfo _fzField;
        private static MethodInfo _sendDataResolved;

        public static bool Init()
        {
            if (_initDone) return _fikaAvailable;
            _initDone = true;

            try
            {
                _fikaAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Fika.Core");

                if (_fikaAsm == null)
                {
                    Log.LogInfo("[Mortar] FIKA not detected — running solo/host mode.");
                    return false;
                }

                _fikaAvailable = true;
                Log.LogInfo("[Mortar] FIKA detected! Setting up network sync...");

                var utilsType = _fikaAsm.GetTypes()
                    .FirstOrDefault(t => t.Name == "FikaBackendUtils");
                var globalsType = _fikaAsm.GetTypes()
                    .FirstOrDefault(t => t.Name == "FikaGlobals");

                if (utilsType != null)
                {
                    _isServerProp = utilsType.GetProperty("IsServer", BindingFlags.Public | BindingFlags.Static);
                    if (_isServerProp != null)
                        Log.LogInfo($"[Mortar] FIKA: Found {utilsType.FullName}.IsServer");
                    else
                    {
                        Log.LogWarning("[Mortar] FIKA: IsServer not found. Static props: "
                            + string.Join(", ", utilsType.GetProperties(BindingFlags.Public | BindingFlags.Static)
                                .Select(p => p.Name)));
                    }
                }

                if (globalsType != null)
                    _isInRaidProp = globalsType.GetProperty("IsInRaid", BindingFlags.Public | BindingFlags.Static);

                var ifikaType = FindTypeInAllAssemblies("IFikaNetworkManager");
                if (ifikaType != null)
                {
                    var singletonType = typeof(Comfort.Common.Singleton<>).MakeGenericType(ifikaType);
                    _networkManagerSingletonProp = singletonType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
                    if (_networkManagerSingletonProp != null)
                        Log.LogInfo("[Mortar] FIKA: Singleton<IFikaNetworkManager> accessor cached");
                }
                else
                {
                    Log.LogWarning("[Mortar] FIKA: IFikaNetworkManager type not found.");
                }

                BuildPacketType();
            }
            catch (Exception ex)
            {
                Log.LogError($"[Mortar] FIKA init error: {ex}");
            }

            return _fikaAvailable;
        }

        public static bool IsServer()
        {
            if (!_fikaAvailable) return true;
            if (_isServerProp != null)
            {
                try { return (bool)_isServerProp.GetValue(null); }
                catch { }
            }
            return true;
        }

        /// <summary>
        /// When FIKA is present (including headless), use FikaGlobals.IsInRaid instead of MainPlayer.
        /// Returns true if we got a value from FIKA; false means use the caller's fallback (GameWorld+MainPlayer).
        /// </summary>
        public static bool TryGetFikaIsInRaid(out bool isInRaid)
        {
            isInRaid = false;
            if (!_fikaAvailable || _isInRaidProp == null) return false;
            try
            {
                isInRaid = (bool)_isInRaidProp.GetValue(null);
                return true;
            }
            catch { return false; }
        }

        public static void TryRegisterPacket()
        {
            if (!_fikaAvailable || _packetRegistered || _packetRegistrationAttempted || _packetType == null) return;
            _packetRegistrationAttempted = true;

            try
            {
                var manager = GetNetworkManager();
                if (manager == null)
                {
                    Log.LogWarning("[Mortar] FIKA: NetworkManager not available — packet registration skipped.");
                    return;
                }

                var managerType = manager.GetType();
                Log.LogInfo($"[Mortar] FIKA: NetworkManager = {managerType.Name}");

                var registerMethod = FindGenericMethod(managerType, "RegisterPacket",
                    genericArgCount: 1, paramCount: 1);

                if (registerMethod == null)
                {
                    Log.LogError("[Mortar] FIKA: RegisterPacket<T>(Action<T>) not found.");
                    return;
                }

                var genericRegister = registerMethod.MakeGenericMethod(_packetType);

                var actionType = typeof(Action<>).MakeGenericType(_packetType);
                var trampoline = BuildReceiverTrampoline(_packetType);
                var handler = trampoline.CreateDelegate(actionType);

                genericRegister.Invoke(manager, new object[] { handler });
                Log.LogInfo("[Mortar] FIKA: Packet registered!");

                var sendMethod = FindGenericMethod(managerType, "SendData",
                    genericArgCount: 1, paramCount: -1);

                if (sendMethod != null)
                {
                    _sendDataResolved = sendMethod.MakeGenericMethod(_packetType);
                    Log.LogInfo($"[Mortar] FIKA: SendData cached ({sendMethod.GetParameters().Length} params)");
                }
                else
                {
                    Log.LogWarning("[Mortar] FIKA: SendData not found — broadcast won't work.");
                }

                _packetRegistered = true;
                Log.LogInfo("[Mortar] FIKA: Network sync ready!");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Mortar] FIKA: Packet registration failed: {ex}");
            }
        }

        public static void BroadcastSirenPacket(float x, float y, float z)
        {
            if (!_fikaAvailable || !_packetRegistered || _sendDataResolved == null || _packetType == null) return;
            if (!IsServer()) return;

            try
            {
                var manager = GetNetworkManager();
                if (manager == null) return;

                var packet = Activator.CreateInstance(_packetType);
                _fxField.SetValue(packet, x);
                _fyField.SetValue(packet, y);
                _fzField.SetValue(packet, z);

                var parms = _sendDataResolved.GetParameters();
                object[] args = new object[parms.Length];

                for (int i = 0; i < parms.Length; i++)
                {
                    var p = parms[i];
                    var pt = p.ParameterType;
                    if (pt.IsByRef) pt = pt.GetElementType();

                    if (pt == _packetType || pt.IsAssignableFrom(_packetType))
                        args[i] = packet;
                    else if (pt.IsEnum)
                    {
                        var reliableVal = Enum.GetValues(pt).Cast<object>()
                            .FirstOrDefault(v => v.ToString() == "ReliableOrdered");
                        args[i] = reliableVal ?? Enum.GetValues(pt).GetValue(0);
                    }
                    else if (pt == typeof(bool))
                        args[i] = true;
                    else if (p.HasDefaultValue)
                        args[i] = p.DefaultValue;
                    else
                        args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }

                _sendDataResolved.Invoke(manager, args);
                Log.LogInfo("[Mortar] FIKA: Siren packet broadcast to all clients!");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Mortar] FIKA: Broadcast failed: {ex.Message}");
            }
        }

        public static void ResetRaidState()
        {
            _packetRegistered = false;
            _packetRegistrationAttempted = false;
            _sendDataResolved = null;
        }

        private static void BuildPacketType()
        {
            try
            {
                var iNetSer = FindTypeInAllAssemblies("INetSerializable");
                var writerType = FindTypeInAllAssemblies("NetDataWriter");
                var readerType = FindTypeInAllAssemblies("NetDataReader");

                if (iNetSer == null || writerType == null || readerType == null)
                {
                    Log.LogError($"[Mortar] FIKA: Missing types — INetSerializable={iNetSer != null}, " +
                        $"NetDataWriter={writerType != null}, NetDataReader={readerType != null}");
                    return;
                }

                var putFloat = writerType.GetMethod("Put", new[] { typeof(float) });
                var getFloat = readerType.GetMethod("GetFloat", Type.EmptyTypes);

                if (putFloat == null || getFloat == null)
                {
                    Log.LogError($"[Mortar] FIKA: Missing methods — Put(float)={putFloat != null}, GetFloat()={getFloat != null}");
                    return;
                }

                var serializeInterfaceMethod = iNetSer.GetMethod("Serialize");
                var deserializeInterfaceMethod = iNetSer.GetMethod("Deserialize");
                var asmName = new AssemblyName("MortarStrikes.Dynamic");
                var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
                var modBuilder = asmBuilder.DefineDynamicModule("MortarStrikes.Dynamic");

                var typeBuilder = modBuilder.DefineType(
                    "MortarStrikes.MortarSirenPacket",
                    TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
                    typeof(object),
                    new[] { iNetSer });

                var fx = typeBuilder.DefineField("StrikeCenterX", typeof(float), FieldAttributes.Public);
                var fy = typeBuilder.DefineField("StrikeCenterY", typeof(float), FieldAttributes.Public);
                var fz = typeBuilder.DefineField("StrikeCenterZ", typeof(float), FieldAttributes.Public);

                var ctor = typeBuilder.DefineConstructor(
                    MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
                var ctorIl = ctor.GetILGenerator();
                ctorIl.Emit(OpCodes.Ldarg_0);
                ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
                ctorIl.Emit(OpCodes.Ret);

                var serialize = typeBuilder.DefineMethod("Serialize",
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    typeof(void), new[] { writerType });
                var sil = serialize.GetILGenerator();
                foreach (var f in new[] { fx, fy, fz })
                {
                    sil.Emit(OpCodes.Ldarg_1);
                    sil.Emit(OpCodes.Ldarg_0);
                    sil.Emit(OpCodes.Ldfld, f);
                    sil.Emit(OpCodes.Callvirt, putFloat);
                }
                sil.Emit(OpCodes.Ret);
                typeBuilder.DefineMethodOverride(serialize, serializeInterfaceMethod);

                var deserialize = typeBuilder.DefineMethod("Deserialize",
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    typeof(void), new[] { readerType });
                var dil = deserialize.GetILGenerator();
                foreach (var f in new[] { fx, fy, fz })
                {
                    dil.Emit(OpCodes.Ldarg_0);
                    dil.Emit(OpCodes.Ldarg_1);
                    dil.Emit(OpCodes.Callvirt, getFloat);
                    dil.Emit(OpCodes.Stfld, f);
                }
                dil.Emit(OpCodes.Ret);
                typeBuilder.DefineMethodOverride(deserialize, deserializeInterfaceMethod);

                _packetType = typeBuilder.CreateType();
                _fxField = _packetType.GetField("StrikeCenterX");
                _fyField = _packetType.GetField("StrikeCenterY");
                _fzField = _packetType.GetField("StrikeCenterZ");

                Log.LogInfo($"[Mortar] FIKA: Dynamic packet type built: {_packetType.FullName}");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Mortar] FIKA: Failed to build packet type: {ex}");
            }
        }

        private static DynamicMethod BuildReceiverTrampoline(Type packetType)
        {
            var target = typeof(FikaSync).GetMethod(nameof(OnPacketReceivedInternal),
                BindingFlags.Static | BindingFlags.Public);

            var dm = new DynamicMethod("MortarSirenReceiver", typeof(void),
                new[] { packetType }, typeof(FikaSync), true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, typeof(object));
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            return dm;
        }

        public static void OnPacketReceivedInternal(object packet)
        {
            try
            {
                float x = (float)_fxField.GetValue(packet);
                float y = (float)_fyField.GetValue(packet);
                float z = (float)_fzField.GetValue(packet);

                Log.LogInfo($"[Mortar] FIKA: Siren packet received! Strike at ({x:F1}, {y:F1}, {z:F1})");

                var mgr = MortarStrikeManager.Instance;
                if (mgr != null)
                {
                    mgr.PlaySiren(force: true);

                    try
                    {
                        var pos = new UnityEngine.Vector3(x, y, z);
                        mgr.SpawnWarningFlare(pos);
                        Log.LogInfo("[Mortar] FIKA: Smoke signal spawned on client");
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning($"[Mortar] FIKA: Smoke spawn on client failed: {ex.Message}");
                    }
                }
                else
                    Log.LogWarning("[Mortar] FIKA: StrikeManager not ready.");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Mortar] FIKA: Siren handler error: {ex.Message}");
            }
        }

        private static object GetNetworkManager()
        {
            if (_networkManagerSingletonProp == null) return null;
            try { return _networkManagerSingletonProp.GetValue(null); }
            catch { return null; }
        }

        private static Type FindTypeInAllAssemblies(string shortName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == shortName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static MethodInfo FindGenericMethod(Type type, string name, int genericArgCount, int paramCount)
        {
            foreach (var t in new[] { type }.Concat(type.GetInterfaces()))
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != name || !m.IsGenericMethod) continue;
                    if (m.GetGenericArguments().Length != genericArgCount) continue;
                    int pc = m.GetParameters().Length;
                    if (paramCount == -1 && pc >= 2) return m;
                    if (paramCount >= 0 && pc == paramCount) return m;
                }
            }
            return null;
        }
    }
}
