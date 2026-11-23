using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Patch = AuroraPatch.Patch;

namespace NoModCheck
{
    // Neutralises the anti-modding checks added in Aurora 3.3:
    //  1. A detection method (l1.e() in 3.3.0) that asks Harmony whether Aurora is patched. If it is,
    //     the game sets a session flag that disables standing orders, auto commander assignment
    //     and industry "Create". Fix: force it to return false.
    //  2/3. Assembly.GetEntryAssembly() != Aurora -> Naval Organization / Economics skip their Load setup.
    //  4. Process.MainModule.FileName must end with "Aurora.exe" -> shipyard "Create Task".
    //  5. new StackTrace().GetFrames() containing an AuroraPatch frame -> Commanders race selector.
    //  6. A static int counter (l4.ad in 3.3.1, l1.ac in 3.3.0) is seeded at 23 in the config class's
    //     static constructor, incremented once in Aurora's entry point and once more in the
    //     TacticalMap constructor, and then compared against a literal in the fleet window's order
    //     handler:  if (counter != 25) return;  - "Add Move" and the map's double-click do nothing.
    //     AuroraPatch creates the map itself, so the entry point never runs and the counter stays one
    //     short. Fix: a postfix on that constructor sets the counter to the value the game's own
    //     comparison expects.
    // Fix for 2-5: inside Aurora's own methods only, redirect those calls to helpers that report
    // what a vanilla Aurora.exe launch would see. Everything is located by IL content, not by
    // obfuscated names, so it should survive renames in later builds.
    public class NoModCheck : Patch
    {
        public override string Description => "Neutralises Aurora 3.3's anti-modding checks.";

        private const string DetectionMarker = "GetAllPatchedMethods";

        private static Assembly s_aurora;
        private static string s_auroraPath;
        private static Dictionary<MethodBase, MethodInfo> s_redirects;
        private static FieldInfo s_initCounter;
        private static int s_initCounterExpected;
        private static Action<string> s_logError;

        protected override void Loaded(Harmony harmony)
        {
            s_aurora = AuroraAssembly;
            s_auroraPath = AuroraExecutablePath;
            s_logError = LogError;
            s_redirects = new Dictionary<MethodBase, MethodInfo>
            {
                { AccessTools.Method(typeof(Assembly), nameof(Assembly.GetEntryAssembly)), AccessTools.Method(typeof(NoModCheck), nameof(EntryAssembly)) },
                { AccessTools.PropertyGetter(typeof(ProcessModule), nameof(ProcessModule.FileName)), AccessTools.Method(typeof(NoModCheck), nameof(ModuleFileName)) },
                { AccessTools.Method(typeof(StackTrace), nameof(StackTrace.GetFrames)), AccessTools.Method(typeof(NoModCheck), nameof(FramesWithoutPatcher)) },
            };

            var detection = new List<MethodInfo>();
            var redirected = new List<MethodBase>();
            ScanAurora(detection, redirected);

            var prefix = new HarmonyMethod(typeof(NoModCheck), nameof(ReturnFalse));
            foreach (var m in detection)
                TryPatch(() => harmony.Patch(m, prefix: prefix), m, "mod detection -> false");

            var transpiler = new HarmonyMethod(typeof(NoModCheck), nameof(Redirect));
            foreach (var m in redirected)
                TryPatch(() => harmony.Patch(m, transpiler: transpiler), m, "environment checks redirected");

            if (detection.Count == 0 && redirected.Count == 0)
                LogInfo("No anti-modding checks found (expected on pre-3.3 builds); nothing patched.");

            // Check 6 is the one check that is not a rejection test: the game counts its own
            // startup and gates the fleet window's order commands on the count, so the missing
            // increment has to be supplied rather than suppressed.
            FieldInfo counterField;
            List<MethodBase> counterCtors;
            int counterExpected;
            if (TryFindStartupCounter(out counterField, out counterCtors, out counterExpected))
            {
                s_initCounter = counterField;
                s_initCounterExpected = counterExpected;

                var postfix = new HarmonyMethod(typeof(NoModCheck), nameof(SetStartupCounter));
                foreach (var ctor in counterCtors)
                    TryPatch(() => harmony.Patch(ctor, postfix: postfix), ctor, $"startup counter -> {counterExpected}");
            }
        }

        private void TryPatch(Action patch, MethodBase m, string what)
        {
            try
            {
                patch();
                LogInfo($"{m.DeclaringType?.Name}.{m.Name}: {what}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to patch {m.DeclaringType?.Name}.{m.Name}: {ex}");
            }
        }

        // ---- replacements -------------------------------------------------------------------

        private static bool ReturnFalse(ref bool __result)
        {
            __result = false;
            return false;
        }

        public static Assembly EntryAssembly() => s_aurora;

        public static string ModuleFileName(ProcessModule module) => s_auroraPath;

        public static StackFrame[] FramesWithoutPatcher(StackTrace trace)
        {
            var frames = trace.GetFrames();
            if (frames == null) return null;
            return frames.Where(f =>
            {
                try { return f.GetMethod()?.DeclaringType?.Assembly.GetName().Name != "AuroraPatch"; }
                catch { return true; }
            }).ToArray();
        }

        // Postfix on the constructor that would normally supply the second increment. It sets the
        // value the game expects instead of adding one: AuroraPatch may construct the form more than
        // once, and another patch may correct the same counter - 24 -> 25 -> 26 would put the counter
        // back out of range and re-break the orders this is meant to fix.
        //
        // Logging goes through s_logError because a Harmony postfix has to be static while LogError is
        // an instance method; a throw here would abort the form's construction, which is far worse
        // than a quiet failure.
        private static void SetStartupCounter()
        {
            if (s_initCounter == null) return;

            try { s_initCounter.SetValue(null, s_initCounterExpected); }
            catch (Exception ex) { s_logError?.Invoke($"Failed to set the startup counter: {ex.Message}"); }
        }

        private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                    && ins.operand is MethodBase target && s_redirects.TryGetValue(target, out var helper))
                {
                    ins.opcode = OpCodes.Call; // helpers are static; instance arg stays on the stack
                    ins.operand = helper;
                }
                yield return ins;
            }
        }

        // ---- discovery ----------------------------------------------------------------------

        private void ScanAurora(List<MethodInfo> detection, List<MethodBase> redirected)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var module = s_aurora.ManifestModule;
            var tokenCache = new Dictionary<int, bool>();

            Type[] types;
            try { types = s_aurora.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            foreach (var type in types)
            {
                foreach (var m in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
                {
                    byte[] il;
                    try { il = m.GetMethodBody()?.GetILAsByteArray(); }
                    catch { continue; }
                    if (il == null) continue;

                    bool isDetection = false, needsRedirect = false;
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        byte op = il[i];
                        if (op != 0x72 && op != 0x28 && op != 0x6F) continue; // ldstr, call, callvirt
                        int token = BitConverter.ToInt32(il, i + 1);
                        int table = (token >> 24) & 0xFF;
                        if (op == 0x72 && table == 0x70)
                        {
                            try { if (module.ResolveString(token) == DetectionMarker) isDetection = true; } catch { }
                        }
                        else if (op != 0x72 && (table == 0x0A || table == 0x06)) // MemberRef / MethodDef
                        {
                            if (!tokenCache.TryGetValue(token, out bool hit))
                            {
                                try { hit = s_redirects.ContainsKey(module.ResolveMethod(token)); } catch { hit = false; }
                                tokenCache[token] = hit;
                            }
                            if (hit) needsRedirect = true;
                        }
                    }

                    if (isDetection && m is MethodInfo mi && mi.ReturnType == typeof(bool) && mi.IsStatic && mi.GetParameters().Length == 0)
                        detection.Add(mi);
                    else if (needsRedirect)
                        redirected.Add(m);
                }
            }
        }

        // ---- check 6: startup counter --------------------------------------------------------

        // A normal start seeds the counter in the config class's static constructor, increments it
        // in the entry point and increments it once more in the TacticalMap constructor. The fleet
        // window's order handler then returns early unless it reads the result, so AuroraPatch -
        // which builds the map itself and never runs the entry point - leaves orders dead.
        //
        // The field is found from the entry point's increment and the expected value is read out of
        // the game's own comparison, so a renamed field or a different constant in a later build
        // needs no change here. If either cannot be found the check is skipped rather than guessed:
        // writing a wrong value would move the failure somewhere unrelated and much harder to spot.
        private bool TryFindStartupCounter(out FieldInfo field, out List<MethodBase> ctors, out int expected)
        {
            field = null;
            ctors = null;
            expected = 0;

            var entry = s_aurora.EntryPoint;
            var il = entry?.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                LogInfo("startup counter: entry point not readable, check 6 not applied");
                return false;
            }

            field = FindIncrementedField(il, entry.Module);
            if (field == null)
            {
                LogInfo("startup counter: no incremented static int in the entry point, check 6 not applied");
                return false;
            }

            int? gate = FindGateConstant(field);
            if (!gate.HasValue)
            {
                LogInfo($"startup counter: {field.Name} found but no comparison constant, check 6 not applied");
                field = null;
                return false;
            }

            ctors = FindIncrementingConstructors(field);
            if (ctors.Count == 0)
            {
                LogInfo($"startup counter: {field.Name} found but no constructor increments it, check 6 not applied");
                field = null;
                ctors = null;
                return false;
            }

            expected = gate.Value;
            LogInfo($"startup counter: {field.DeclaringType?.Name}.{field.Name} expected to be {expected} after a normal start");
            return true;
        }

        private static FieldInfo FindIncrementedField(byte[] il, Module module)
        {
            foreach (var token in FindIncrementTokens(il))
            {
                var field = ResolveField(module, token);
                if (field != null && field.IsStatic && field.FieldType == typeof(int)) return field;
            }

            return null;
        }

        // Normally exactly one instance constructor does this - the TacticalMap's. Collecting them
        // all means a build that adds a second one is still covered, and patching is safe either way
        // because the postfix sets an absolute value.
        private static List<MethodBase> FindIncrementingConstructors(FieldInfo field)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var ctors = new List<MethodBase>();

            Type[] types;
            try { types = s_aurora.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            foreach (var type in types)
            {
                foreach (var ctor in type.GetConstructors(flags))
                {
                    if (ctor.IsStatic) continue; // the static constructor seeds the field, it does not increment it

                    byte[] il;
                    try { il = ctor.GetMethodBody()?.GetILAsByteArray(); }
                    catch { continue; }
                    if (il == null) continue;

                    if (IncrementsField(il, ctor.Module, field)) ctors.Add(ctor);
                }
            }

            return ctors;
        }

        // ldsfld counter; ldc.i4.1; add; stsfld counter. Both tokens are compared so an unrelated
        // field store cannot match.
        private static IEnumerable<int> FindIncrementTokens(byte[] il)
        {
            for (int i = 0; i + 5 < il.Length; i++)
            {
                if (il[i] != 0x80) continue; // stsfld
                int token = BitConverter.ToInt32(il, i + 1);
                if (!IsFieldToken(token)) continue;

                int p = i - 7; // ldsfld (5 bytes), ldc.i4.1 (1), add (1)
                if (p < 0 || il[p] != 0x7E || il[p + 5] != 0x17 || il[p + 6] != 0x58) continue;
                if (BitConverter.ToInt32(il, p + 1) != token) continue;

                yield return token;
            }
        }

        private static bool IncrementsField(byte[] il, Module module, FieldInfo field)
        {
            foreach (var token in FindIncrementTokens(il))
            {
                var other = ResolveField(module, token);
                if (other != null && other.MetadataToken == field.MetadataToken) return true;
            }

            return false;
        }

        // The value the game compares the counter against, e.g. the 25 in "if (counter != 25) return;".
        private static int? FindGateConstant(FieldInfo field)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var module = field.Module;

            Type[] types;
            try { types = s_aurora.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            foreach (var type in types)
            {
                foreach (var m in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
                {
                    byte[] il;
                    try { il = m.GetMethodBody()?.GetILAsByteArray(); }
                    catch { continue; }
                    if (il == null) continue;

                    for (int i = 0; i + 5 < il.Length; i++)
                    {
                        if (il[i] != 0x7E) continue; // ldsfld
                        int token = BitConverter.ToInt32(il, i + 1);
                        if (!IsFieldToken(token)) continue;

                        var other = ResolveField(module, token);
                        if (other == null || other.MetadataToken != field.MetadataToken) continue;

                        int value, next;
                        if (!TryReadInt32(il, i + 5, out value, out next)) continue;
                        if (next >= il.Length || !IsConditionalBranch(il[next])) continue;

                        return value;
                    }
                }
            }

            return null;
        }

        // ldc.i4.0-8 (1 byte), ldc.i4.s (2), ldc.i4 (5); next is the offset just past the instruction
        private static bool TryReadInt32(byte[] il, int pos, out int value, out int next)
        {
            value = 0;
            next = pos;
            if (pos >= il.Length) return false;

            byte op = il[pos];
            if (op >= 0x16 && op <= 0x1E) { value = op - 0x16; next = pos + 1; return true; }
            if (op == 0x1F && pos + 1 < il.Length) { value = (sbyte)il[pos + 1]; next = pos + 2; return true; }
            if (op == 0x20 && pos + 4 < il.Length) { value = BitConverter.ToInt32(il, pos + 1); next = pos + 5; return true; }
            return false;
        }

        // beq / beq.s come out of "if (counter != literal) return;" because the compiler branches over
        // the early return; bne.un is the inverted spelling. Both have been seen in these gates.
        private static bool IsConditionalBranch(byte op) =>
            op == 0x2E || op == 0x2F || op == 0x33 || op == 0x40;

        private static bool IsFieldToken(int token)
        {
            int table = (token >> 24) & 0xFF;
            return table == 0x04 || table == 0x0A; // FieldDef, MemberRef
        }

        private static FieldInfo ResolveField(Module module, int token)
        {
            try { return module.ResolveField(token); }
            catch { return null; }
        }
    }
}
