using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SentisTests.Core;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Methods patched both by Torch's patch manager (a plugin's [PatchShim] or context) and by Harmony. Each writes its
    /// own jump into the start of the method, and the one written last is the only one that runs: Torch, committing
    /// after a Harmony patch, threw out SentisOptimisations' save window of the grid storage, which VirtualGarage's
    /// Torch suffix on the same MySessionSnapshot.Save replaced (22.10.2026). Lists every such method with the patches of
    /// both and fails while there is one.
    /// </summary>
    public sealed class PatchConflictsScenario : TestScenario
    {
        public const string ScenarioName = "patch_conflicts";
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public override string Name => ScenarioName;

        public override IEnumerator Run()
        {
            var torch = TorchPatched();
            var harmony = HarmonyPatched();
            Note("Torch patches " + torch.Count + " methods, Harmony " + harmony.Count);
            var both = torch.Keys.Where(harmony.ContainsKey).ToList();
            foreach (var method in both)
                Note("BOTH: " + Describe(method) + " | runs: " + Runs(method) + " | Torch: " + string.Join(", ", torch[method]) +
                     " | Harmony: " + string.Join(", ", harmony[method]));
            Check(both.Count == 0, both.Count + " methods patched by both Torch and Harmony (only one of them runs): " +
                                   string.Join("; ", both.Select(Describe)));
            Note("PATCH CONFLICTS RESULT | " + torch.Count + " methods by Torch, " + harmony.Count + " by Harmony, none by both");
            yield break;
        }

        private static string Describe(MethodBase method) => method.DeclaringType?.FullName + "." + method.Name;

        /// <summary>
        /// Which patch runs: where the jump at the start of the method's code leads (followed through trampolines) - into
        /// Torch's patched copy of it, or into Harmony's replacement.
        /// </summary>
        private static string Runs(MethodBase method)
        {
            try
            {
                var torch = TorchTarget(method);
                var harmony = HarmonyTarget(method);
                var at = BodyStart(method);
                for (var hop = 0; hop < 6 && at != 0; hop++)
                {
                    if (at == torch) return "Torch (Harmony's patch is not run)";
                    if (at == harmony) return "Harmony (Torch's patch is not run)";
                    at = JumpTarget(at);
                }
                return "unknown (torch 0x" + torch.ToString("X") + ", harmony 0x" + harmony.ToString("X") + ")";
            }
            catch (Exception e)
            {
                return "unknown (" + e.GetType().Name + ": " + e.Message + ")";
            }
        }

        // Torch's own reading of code addresses (internal to it)
        private static readonly Type Memory = typeof(Torch.Managers.PatchManager.PatchManager).Assembly.GetType("Torch.Managers.PatchManager.AssemblyMemory");

        private static long BodyStart(MethodBase method) => (long)Memory.GetMethod("GetMethodBodyStart", Any).Invoke(null, new object[] { method });

        private static byte[] Read(long at, int bytes) => (byte[])Memory.GetMethod("ReadMemory", Any).Invoke(null, new object[] { at, bytes });

        // where a jump at the address goes; 0 when there is none there
        private static long JumpTarget(long at)
        {
            var b = Read(at, 16);
            if (b[0] == 0xE9) return at + 5 + BitConverter.ToInt32(b, 1);                                   // jmp rel32
            if (b[0] == 0xEB) return at + 2 + (sbyte)b[1];                                                   // jmp rel8
            if (b[0] == 0x48 && b[1] == 0xB8 && b[10] == 0xFF && b[11] == 0xE0) return BitConverter.ToInt64(b, 2);   // mov rax, imm64; jmp rax
            if (b[0] == 0x49 && b[1] == 0xBB && b[10] == 0x41 && b[11] == 0xFF && b[12] == 0xE3) return BitConverter.ToInt64(b, 2); // mov r11, imm64; jmp r11
            if (b[0] == 0xFF && b[1] == 0x25)                                                                 // jmp [rip+disp32]
            {
                var slot = at + 6 + BitConverter.ToInt32(b, 2);
                return BitConverter.ToInt64(Read(slot, 8), 0);
            }
            return 0;
        }

        // the start of Torch's patched copy of the method (its DecoratedMethod's pinned DynamicMethod)
        private static long TorchTarget(MethodBase method)
        {
            var patterns = (IDictionary)typeof(Torch.Managers.PatchManager.PatchManager).GetField("_rewritePatterns", Any).GetValue(null);
            var decorated = patterns[method];
            var pinned = decorated?.GetType().GetField("_pinnedPatch", Any)?.GetValue(decorated) as System.Runtime.InteropServices.GCHandle?;
            if (pinned == null || !(pinned.Value.Target is MethodBase copy)) return -1;
            return BodyStart(copy);
        }

        // the start of Harmony's replacement of the method (HarmonySharedState's replacement -> original)
        private static long HarmonyTarget(MethodBase method)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "0Harmony"))
            {
                var shared = assembly.GetType("HarmonyLib.HarmonySharedState");
                if (!(shared?.GetField("originals", Any)?.GetValue(null) is IDictionary originals)) continue;
                foreach (DictionaryEntry entry in originals)
                {
                    if (!Equals(entry.Value, method)) continue;
                    var memory = assembly.GetType("HarmonyLib.Memory");
                    var start = memory?.GetMethod("GetMethodStart", BindingFlags.Static | BindingFlags.Public);
                    if (start == null) return -1;
                    var args = new object[] { entry.Key, null };
                    return (long)start.Invoke(null, args);
                }
            }
            return -1;
        }

        // method -> the patch methods on it, "Type.Method"
        private static Dictionary<MethodBase, List<string>> TorchPatched()
        {
            var manager = typeof(Torch.Managers.PatchManager.PatchManager);
            var field = manager.GetField("_rewritePatterns", Any) ?? throw new ScenarioFailedException("Torch's PatchManager._rewritePatterns is not there");
            var patterns = (IDictionary)field.GetValue(null);
            var result = new Dictionary<MethodBase, List<string>>();
            lock (patterns.SyncRoot)
            {
                foreach (DictionaryEntry entry in patterns)
                {
                    var pattern = (Torch.Managers.PatchManager.MethodRewritePattern)entry.Value;
                    var patches = pattern.Prefixes.Concat(pattern.Suffixes).Concat(pattern.Transpilers).Concat(pattern.PostTranspilers)
                        .Select(m => m.DeclaringType?.Name + "." + m.Name).ToList();
                    if (patches.Count > 0) result[(MethodBase)entry.Key] = patches;
                }
            }
            return result;
        }

        // the same for Harmony, of every copy of it loaded (each keeps its own list)
        private static Dictionary<MethodBase, List<string>> HarmonyPatched()
        {
            var result = new Dictionary<MethodBase, List<string>>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "0Harmony"))
            {
                var harmony = assembly.GetType("HarmonyLib.Harmony");
                var all = harmony?.GetMethod("GetAllPatchedMethods", BindingFlags.Static | BindingFlags.Public);
                var info = harmony?.GetMethod("GetPatchInfo", BindingFlags.Static | BindingFlags.Public);
                if (all == null || info == null) continue;
                foreach (MethodBase method in (IEnumerable)all.Invoke(null, null))
                {
                    var patches = info.Invoke(null, new object[] { method });
                    var owners = new List<string>();
                    foreach (var kind in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                    {
                        var holder = (object)patches?.GetType().GetField(kind)?.GetValue(patches) ?? patches?.GetType().GetProperty(kind)?.GetValue(patches);
                        if (!(holder is IEnumerable list)) continue;
                        foreach (var patch in list)
                        {
                            var patchMethod = (patch.GetType().GetProperty("PatchMethod")?.GetValue(patch) ?? patch.GetType().GetField("patch")?.GetValue(patch)) as MethodInfo;
                            var owner = patch.GetType().GetField("owner")?.GetValue(patch) as string ?? patch.GetType().GetProperty("owner")?.GetValue(patch) as string;
                            owners.Add((owner ?? "?") + ":" + patchMethod?.DeclaringType?.Name + "." + patchMethod?.Name);
                        }
                    }
                    if (!result.TryGetValue(method, out var known)) result[method] = known = new List<string>();
                    known.AddRange(owners);
                }
            }
            return result;
        }
    }
}
