#:package Mono.Cecil@0.11.6
// Android-only rewrites of the (already RO3Emu-patched) game DLLs in a staging Managed folder:
//   - Application.streamingAssetsPath -> Ro3Android.Hooks.Paths.StreamingAssets() (StreamingAssets lives on external storage)
//   - Shader.Find -> Ro3Android.Hooks.Shaders (a placeholder instead of null for shaders not ported yet)
//   - AssetBundle.LoadFromFile[Async] -> Ro3Android.Hooks.Bundles (retargets the PC bundles, loose or inside RO3V packs,
//     to Android while loading)
//   - Object.Instantiate -> its result also goes to Ro3Android.Hooks.Materials.FixInstance (shaders fixed before the
//     copy's first frame: the UI effects drew magenta until the once-a-second scan)
//   - unreachable instructions -> nop: Mono's JIT never looks at them, IL2CPP converts them and fails on IL the
//     client's reconstruction left behind (a `ret` with an empty stack after a `br`, VersionNoticeUtil.GetResAsync)
//   - HybridCLR.RuntimeApi's internal calls -> return LoadImageErrorCode.OK: they live in HybridCLR's own libil2cpp,
//     which a stock IL2CPP player lacks; the "differential hybrid" assemblies they would load are already AOT-compiled
//   - P/Invokes into natives the APK lacks (Windows DLLs, Wwise, Tencent SDKs, AVPro...) -> managed stubs returning the default
//     value: the client subclasses the Unity window, hooks the IME, reads the cursor... from GameMain.Awake on, and a
//     DllNotFoundException there stops the game before Lua starts
//   - [RuntimeInitializeOnLoadMethod] removed from initializers that must not run on Android (Unity registers every
//     other one from the plugins itself): the PC launcher gate, the Windows SDK plugins, the DeNA autopilot (a test runner)
// Usage: dotnet run AndroidPatch.cs -- <Managed dir> <Ro3Android.Hooks.dll>
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Text.RegularExpressions;

var managed = args[0];
var hooksPath = args[1];
// Unity's own assemblies keep the engine path and their IL; only the game's code is touched.
var skip = new Regex(@"^(UnityEngine(\..+)?|Unity\..+|mscorlib|netstandard|System(\..+)?|Mono\..+|Microsoft\..+|Ro3Android\.Hooks)\.dll$");
var noInit = new Regex(@"^(RecoveryLaunchGate|HL\.Windows\..+|DeNA\.Anjin\.Launcher)$");
// Natives the APK does not have: Windows system DLLs, and the PC client's plugins not ported (yet): Wwise (audio), the
// Tencent voice/chat SDKs, AVPro/VLC video, telemetry, the Windows device-info plugin, the SDK's native-lib. ikcp and
// the native HTTP handler are left out: if the game uses them, the real error is the useful one.
var windowsDll = new Regex(@"^(kernel32|user32|imm32|winmm|advapi32|gdi32|shell32|shcore|dwmapi|ntdll|ole32|psapi|version|dbghelp|setupapi|iphlpapi|ws2_32|winhttp|wininet|crypt32|comdlg32"
    + @"|AkUnitySoundEngine|ImSDK|gmesdk|AVProVideo|AVProVideoWinRT|libvlc|CrashSight64|httpdnsplugin|NetworkDiagnosis|windowsplugin|native-lib)(\.dll)?$", RegexOptions.IgnoreCase);

using var hooks = AssemblyDefinition.ReadAssembly(hooksPath);
var streamingAssets = hooks.MainModule.GetType("Ro3Android.Hooks.Paths").Methods.Single(m => m.Name == "StreamingAssets");
var bundleHooks = hooks.MainModule.GetType("Ro3Android.Hooks.Bundles").Methods.Where(m => m.Name.StartsWith("LoadFromFile")).ToList();
var shaderFind = hooks.MainModule.GetType("Ro3Android.Hooks.Shaders").Methods.Single(m => m.Name == "Find");
var materialCreate = hooks.MainModule.GetType("Ro3Android.Hooks.Materials").Methods.Single(m => m.Name == "Create");
var materialCopy = hooks.MainModule.GetType("Ro3Android.Hooks.Materials").Methods.Single(m => m.Name == "Copy");
var unityPc = hooks.MainModule.GetType("Ro3Android.Hooks.Platform").Methods.Single(m => m.Name == "UnityPc");
var luaLog = hooks.MainModule.GetType("Ro3Android.Hooks.Platform").Methods.Single(m => m.Name == "LuaLog");
var fixLoaded = hooks.MainModule.GetType("Ro3Android.Hooks.Materials").Methods.Single(m => m.Name == "FixLoaded");
var fixInstance = hooks.MainModule.GetType("Ro3Android.Hooks.Materials").Methods.Single(m => m.Name == "FixInstance");
var luaOverride = hooks.MainModule.GetType("Ro3Android.Hooks.Lua").Methods.Single(m => m.Name == "OverridePath");
var luaOverrideBytes = hooks.MainModule.GetType("Ro3Android.Hooks.Lua").Methods.Single(m => m.Name == "OverrideBytes");

// The resolver keeps every DLL it opens locked, so the rewritten ones are kept in memory and written at the end.
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var rewritten = new List<(string File, byte[] Bytes)>();
foreach (var file in Directory.GetFiles(managed, "*.dll").OrderBy(f => f))
{
    var name = Path.GetFileName(file);
    if (skip.IsMatch(name)) continue;
    using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(file)), new ReaderParameters { AssemblyResolver = resolver });
    MethodReference? target = null;
    int redirected = 0, dead = 0, stubbed = 0, inits = 0, bundleLoads = 0, windowsCalls = 0, shaderFinds = 0, platformChecks = 0, materials = 0, luaOverrides = 0, instances = 0;
    foreach (var type in asm.MainModule.GetTypes())
        foreach (var method in type.Methods)
        {
            if (noInit.IsMatch(type.FullName))
            {
                int before = method.CustomAttributes.Count;
                for (int i = method.CustomAttributes.Count - 1; i >= 0; i--)
                    if (method.CustomAttributes[i].AttributeType.FullName == "UnityEngine.RuntimeInitializeOnLoadMethodAttribute")
                        method.CustomAttributes.RemoveAt(i);
                inits += before - method.CustomAttributes.Count;
            }
            if (type.FullName == "HybridCLR.RuntimeApi" && method.IsInternalCall)
            {
                StubReturningZero(method);
                stubbed++;
                continue;
            }
            if (method.HasPInvokeInfo && windowsDll.IsMatch(method.PInvokeInfo.Module.Name))
            {
                StubReturningDefault(method);
                windowsCalls++;
                continue;
            }
            if (!method.HasBody) continue;
            // LA_Utils' UNITY_PC (the getter xLua exports as "UNITY_PC", a constant true in the PC build) decides the
            // PC or the mobile UI in Lua: ask Hooks.Platform instead (mobile unless the phone has a pc-ui file).
            if (type.FullName == "LA_Utils" && type.Properties.Any(p => p.Name == "Obf_i" && p.GetMethod == method)
                && method.Body.Instructions.Count == 2
                && method.Body.Instructions[0].OpCode == OpCodes.Ldc_I4_1)
            {
                method.Body.Instructions[0].OpCode = OpCodes.Call;
                method.Body.Instructions[0].Operand = asm.MainModule.ImportReference(unityPc);
                platformChecks++;
            }
            // GS_Debug.ShowLog (a constant true here) makes Lua log every step with a traceback: ask Hooks.Platform.
            if (type.FullName == "HappyEngine.GS_Debug" && type.Properties.Any(p => p.Name == "ShowLog" && p.GetMethod == method)
                && method.Body.Instructions.Count == 2 && method.Body.Instructions[0].OpCode == OpCodes.Ldc_I4_1)
            {
                method.Body.Instructions[0].OpCode = OpCodes.Call;
                method.Body.Instructions[0].Operand = asm.MainModule.ImportReference(luaLog);
                platformChecks++;
            }
            // The reconstructed client loads Lua from StreamingAssets/Recovery except on Android, where it waits for a
            // bootstrap (Recovery/recovery-bootstrap.json) the client does not ship: take the PC path. The terrain's
            // lightmap header picks astc_* data on Android, which only mobile builds ship: take the PC data too.
            if (type.FullName == "HappyEngine.Runtime.NewRuntimeSystem.RecoveryLuaRuntimeContext"
                || type.FullName == "HappyEngine.WorldStream.WorldStreamTool.TerrainLightmapInfo")
                foreach (var inst in method.Body.Instructions)
                    if (inst.OpCode == OpCodes.Call && inst.Operand is MethodReference pr
                        && pr.DeclaringType.FullName == "UnityEngine.Application" && pr.Name == "get_platform")
                    {
                        inst.OpCode = OpCodes.Ldc_I4;
                        inst.Operand = 2;               // RuntimePlatform.WindowsPlayer
                        platformChecks++;
                    }
            // Every asset HappyEngine's resource loader hands out passes AssetElement.DoLoadCompleted (Asset set, callbacks
            // not run yet): Hooks.Materials.FixLoaded(this.Asset) first, so no material is drawn with a PC-only shader.
            if (type.FullName == "HappyEngine.Runtime.NewRuntimeSystem.AssetElement" && method.Name == "DoLoadCompleted")
            {
                var getAsset = type.Properties.Single(p => p.Name == "Asset").GetMethod;
                var il = method.Body.GetILProcessor();
                var first = method.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, getAsset));
                il.InsertBefore(first, il.Create(OpCodes.Call, asm.MainModule.ImportReference(fixLoaded)));
                materials++;
            }
            // xLua's loader (LA_LuaLoader) reads every module through NewResourceManager.LoadLuaFromFile: a module the port
            // replaces (Hooks.Lua.OverridePath gives its file) is loaded from the port's file with the game's own reader.
            //   string path = Lua.OverridePath(fileName); if (path != null) return LoadBytesUtils.LoadDataFromFilePath(path, false);
            if (type.FullName == "HappyEngine.Runtime.NewRuntimeSystem.NewResourceManager" && method.Name == "LoadLuaFromFile")
            {
                var readFile = type.Module.GetType("HappyEngine.Runtime.NewRuntimeSystem.LoadBytesUtils").Methods
                    .Single(m => m.Name == "LoadDataFromFilePath" && m.Parameters.Count == 2);
                var il = method.Body.GetILProcessor();
                var first = method.Body.Instructions[0];
                var original = il.Create(OpCodes.Pop);
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
                il.InsertBefore(first, il.Create(OpCodes.Call, asm.MainModule.ImportReference(luaOverride)));
                il.InsertBefore(first, il.Create(OpCodes.Dup));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse, original));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, readFile));
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                il.InsertBefore(first, original);
                luaOverrides++;
            }
            // The byte[] loader the same way: byte[] bytes = Lua.OverrideBytes(fileName); if (bytes != null) return bytes;
            if (type.FullName == "HappyEngine.Runtime.NewRuntimeSystem.NewResourceManager" && method.Name == "LoadLuaBytesFromFile")
            {
                var il = method.Body.GetILProcessor();
                var first = method.Body.Instructions[0];
                var original = il.Create(OpCodes.Pop);
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
                il.InsertBefore(first, il.Create(OpCodes.Call, asm.MainModule.ImportReference(luaOverrideBytes)));
                il.InsertBefore(first, il.Create(OpCodes.Dup));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse, original));
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                il.InsertBefore(first, original);
                luaOverrides++;
            }
            // The scenery builds its materials from PC shaders and sets their textures right after, which a shader
            // without programs for this GPU drops: new Material(Shader) -> Hooks.Materials.Create (stand-in first).
            // The UI builds them too (masks, blend modes, UI Extensions effects), some for a moment only: gone before the
            // once-a-second scan, those were only ever drawn magenta. Every game assembly, then.
            foreach (var inst in method.Body.Instructions)
                if (inst.OpCode == OpCodes.Newobj && inst.Operand is MethodReference ctor && ctor.DeclaringType.FullName == "UnityEngine.Material"
                    && ctor.Parameters.Count == 1)
                {
                    var from = ctor.Parameters[0].ParameterType.FullName;
                    if (from != "UnityEngine.Shader" && from != "UnityEngine.Material") continue;
                    inst.OpCode = OpCodes.Call;
                    inst.Operand = asm.MainModule.ImportReference(from == "UnityEngine.Shader" ? materialCreate : materialCopy);
                    materials++;
                }
            foreach (var inst in method.Body.Instructions)
            {
                if (inst.OpCode != OpCodes.Call || inst.Operand is not MethodReference mr) continue;
                if (mr.DeclaringType.FullName == "UnityEngine.Application" && mr.Name == "get_streamingAssetsPath")
                {
                    inst.Operand = target ??= asm.MainModule.ImportReference(streamingAssets);
                    redirected++;
                }
                else if (mr.DeclaringType.FullName == "UnityEngine.Shader" && mr.Name == "Find")
                {
                    inst.Operand = asm.MainModule.ImportReference(shaderFind);
                    shaderFinds++;
                }
                else if (mr.DeclaringType.FullName == "UnityEngine.AssetBundle" && (mr.Name == "LoadFromFile" || mr.Name == "LoadFromFileAsync"))
                {
                    var hook = bundleHooks.Single(h => h.Name == mr.Name && h.Parameters.Count == mr.Parameters.Count);
                    inst.Operand = asm.MainModule.ImportReference(hook);
                    bundleLoads++;
                }
            }
            // Every effect reaches the screen through Object.Instantiate, whatever loaded its prefab (the UI effects skip
            // DoLoadCompleted, and only the once-a-second scan fixed them, after their first frames were drawn magenta):
            //   Instantiate(...); -> Instantiate(...) dup [box T] call Hooks.Materials.FixInstance
            instances += FixAfterInstantiate(method, fixInstance);
            dead += NopUnreachable(method.Body);
        }
    if (redirected + dead + stubbed + inits + bundleLoads + windowsCalls + shaderFinds + platformChecks + materials + instances == 0) continue;
    var output = new MemoryStream();
    asm.Write(output);
    rewritten.Add((file, output.ToArray()));
    Console.WriteLine($"{name}: streamingAssetsPath x{redirected}, unreachable instructions x{dead}, HybridCLR stubs x{stubbed}, initializers off x{inits}, bundle loads x{bundleLoads}, absent-native P/Invokes stubbed x{windowsCalls}, Shader.Find x{shaderFinds}, PC platform x{platformChecks}, new Material x{materials}, Lua overrides x{luaOverrides}, Instantiate x{instances}");
}
resolver.Dispose();
foreach (var (file, bytes) in rewritten) File.WriteAllBytes(file, bytes);
File.Copy(hooksPath, Path.Combine(managed, Path.GetFileName(hooksPath)), overwrite: true);
Console.WriteLine($"android patch: {rewritten.Count} assemblies rewritten");

// After each call to UnityEngine.Object.Instantiate, passes a copy of the result to fix (object -> void): dup, a box when
// the result is the caller's own generic parameter, call. Stack-neutral, inserted after the call. The inserted bytes can
// push a short branch (br.s, +-127 bytes) out of range, which Cecil writes as a wrong offset (IL2CPP's control-flow graph
// then failed on the method): every branch is made long first and shortened again where it fits.
static int FixAfterInstantiate(MethodDefinition method, MethodDefinition fixDefinition)
{
    if (!method.HasBody) return 0;
    var calls = method.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
        && i.Operand is MethodReference r && r.DeclaringType.FullName == "UnityEngine.Object" && r.Name == "Instantiate").ToList();
    if (calls.Count == 0) return 0;
    var fix = method.Module.ImportReference(fixDefinition);
    method.Body.SimplifyMacros();
    var il = method.Body.GetILProcessor();
    foreach (var call in calls)
    {
        var callee = (MethodReference)call.Operand;
        var returns = callee.ReturnType;
        // Instantiate<T> declares !!0; the call site's argument is the type on the stack.
        if (returns is GenericParameter p && p.Type == GenericParameterType.Method && callee is GenericInstanceMethod gim)
            returns = gim.GenericArguments[p.Position];
        il.InsertAfter(call, il.Create(OpCodes.Call, fix));
        if (returns.IsGenericParameter) il.InsertAfter(call, il.Create(OpCodes.Box, returns));
        il.InsertAfter(call, il.Create(OpCodes.Dup));
    }
    method.Body.OptimizeMacros();
    return calls.Count;
}

// Replaces every instruction no path from the entry point or an exception handler reaches with a nop. Instructions are
// kept (only their opcode changes), so branch targets and handler boundaries stay valid.
static int NopUnreachable(MethodBody body)
{
    var instructions = body.Instructions;
    if (instructions.Count == 0) return 0;
    var reached = new HashSet<Instruction>();
    var pending = new Stack<Instruction>();
    pending.Push(instructions[0]);
    foreach (var h in body.ExceptionHandlers)
    {
        pending.Push(h.TryStart);
        pending.Push(h.HandlerStart);
        if (h.FilterStart != null) pending.Push(h.FilterStart);
    }
    while (pending.Count > 0)
    {
        var inst = pending.Pop();
        if (inst == null || !reached.Add(inst)) continue;
        switch (inst.OpCode.FlowControl)
        {
            case FlowControl.Branch:
                pending.Push((Instruction)inst.Operand);
                break;
            case FlowControl.Cond_Branch:
                if (inst.Operand is Instruction[] targets) foreach (var t in targets) pending.Push(t);
                else pending.Push((Instruction)inst.Operand);
                pending.Push(inst.Next);
                break;
            case FlowControl.Return:
            case FlowControl.Throw:
                break;
            default:
                pending.Push(inst.Next);
                break;
        }
    }
    int count = 0;
    foreach (var inst in instructions)
        if (!reached.Contains(inst) && inst.OpCode != OpCodes.Nop)
        {
            inst.OpCode = OpCodes.Nop;
            inst.Operand = null;
            count++;
        }
    // A dead tail must not fall off the end of the method: its last instruction becomes a branch to itself.
    var last = instructions[^1];
    if (count > 0 && !reached.Contains(last)) { last.OpCode = OpCodes.Br; last.Operand = last; }
    return count;
}

// An extern P/Invoke turned into a managed method returning default(T); out/ref arguments are left as the caller
// initialised them.
static void StubReturningDefault(MethodDefinition method)
{
    var module = method.Module;
    method.PInvokeInfo = null;      // Cecil's setter turns IsPInvokeImpl back on, so it goes first
    method.IsPInvokeImpl = false;
    method.ImplAttributes &= ~MethodImplAttributes.PreserveSig;
    method.Body = new MethodBody(method);
    var il = method.Body.GetILProcessor();
    var ret = method.ReturnType;
    if (ret.FullName != "System.Void")
    {
        if (ret.IsValueType || ret.IsGenericParameter)
        {
            var local = new VariableDefinition(ret);
            method.Body.Variables.Add(local);
            method.Body.InitLocals = true;
            il.Append(il.Create(OpCodes.Ldloca, local));
            il.Append(il.Create(OpCodes.Initobj, ret));
            il.Append(il.Create(OpCodes.Ldloc, local));
        }
        else il.Append(il.Create(OpCodes.Ldnull));
    }
    il.Append(il.Create(OpCodes.Ret));
}

static void StubReturningZero(MethodDefinition method)
{
    method.IsInternalCall = false;
    method.ImplAttributes &= ~MethodImplAttributes.InternalCall;
    method.Body = new MethodBody(method);
    var il = method.Body.GetILProcessor();
    il.Append(il.Create(OpCodes.Ldc_I4_0));
    il.Append(il.Create(OpCodes.Ret));
}
