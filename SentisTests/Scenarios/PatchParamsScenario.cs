using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SentisTests.Core;
using Torch.Managers.PatchManager;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Every prefix and suffix (Torch) and every prefix, postfix and finalizer (Harmony) of all plugins takes what the
    /// patched method passes it with the type it has.
    ///
    /// The patch managers load the argument by its name (or the instance, the result, a field) and call the patch with
    /// it; Torch checks no types but the result's, and that one by assignability. A parameter of another type than the
    /// argument's is read as that type: a number or a structure taken for an object is a reference the collector follows
    /// - the heap damaged or the collector crashed, as the structure taken for an object in
    /// <see cref="PatchRetBufScenario"/> did (struct_this_gc).
    /// </summary>
    public sealed class PatchParamsScenario : TestScenario
    {
        public const string ScenarioName = "patch_params";

        public override string Name => ScenarioName;

        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            var found = new List<string>();
            var checkedPatches = 0;

            var patterns = typeof(PatchManager).GetField("_rewritePatterns", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary;
            Check(patterns != null, "PatchManager._rewritePatterns not found");
            var enumerator = patterns.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var method = (MethodBase)enumerator.Entry.Key;
                foreach (var kind in new[] { "Prefixes", "Suffixes" })
                    if (enumerator.Entry.Value.GetType().GetProperty(kind)?.GetValue(enumerator.Entry.Value) is IEnumerable<MethodInfo> list)
                        foreach (var patch in list)
                        {
                            checkedPatches++;
                            foreach (var problem in Problems(method, patch, harmony: false))
                                found.Add("Torch " + kind.TrimEnd('s', 'e') + " " + Describe(patch) + " on " + Describe(method) + ": " + problem);
                        }
            }

            var harmonyPatches = 0;
            foreach (var (method, kind, patch) in HarmonyPatches())
            {
                harmonyPatches++;
                foreach (var problem in Problems(method, patch, harmony: true))
                    found.Add("Harmony " + kind + " " + Describe(patch) + " on " + Describe(method) + ": " + problem);
            }

            Note("PATCH PARAMS | " + checkedPatches + " Torch and " + harmonyPatches + " Harmony patches checked, " + found.Count + " take an argument as another type");
            foreach (var line in found) Note("PARAM " + line);
            Check(found.Count == 0, found.Count + " patches take an argument as another type");
            yield break;
        }

        private static string Describe(MethodBase m) =>
            m.DeclaringType?.FullName + "." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";

        /// <summary>What a patch takes as another type than the patched method passes; empty when nothing.</summary>
        public static IEnumerable<string> Problems(MethodBase method, MethodInfo patch, bool harmony)
        {
            var returnType = (method as MethodInfo)?.ReturnType ?? typeof(void);
            foreach (var param in patch.GetParameters())
            {
                var name = param.Name ?? "";
                var type = param.ParameterType.IsByRef ? param.ParameterType.GetElementType() : param.ParameterType;
                switch (name)
                {
                    case "__instance":
                        if (method.IsStatic) { yield return "__instance on a static method"; continue; }
                        var owner = method.DeclaringType;
                        if (owner != null && owner.IsValueType)
                        {
                            if (!harmony) yield return "__instance of a structure (" + owner.Name + ")";
                            else if (!param.ParameterType.IsByRef || type != owner) yield return "__instance of a structure not taken as ref " + owner.Name;
                            continue;
                        }
                        if (type.IsValueType) yield return "__instance taken as the value type " + type.Name;
                        else if (param.ParameterType.IsByRef && !harmony) yield return "__instance taken by ref";
                        else if (owner != null && !type.IsAssignableFrom(owner) && !owner.IsAssignableFrom(type))
                            yield return "__instance taken as " + type.Name + ", the method's is " + owner.Name;
                        continue;
                    case "__result":
                        if (returnType == typeof(void)) { yield return "__result of a void method"; continue; }
                        var problem = Mismatch(returnType, type, param.ParameterType.IsByRef);
                        if (problem != null) yield return "__result: " + problem;
                        continue;
                    case "__original":
                    case "__prefixSkipped":
                    case "__runOriginal":
                    case "__state":
                    case "__exception":
                    case "__args":
                    case "__originalMethod":
                        continue;
                }
                if (name.StartsWith("__field_") || name.StartsWith("___"))
                {
                    var fieldName = name.StartsWith("___") ? name.Substring(3) : name.Substring(8);
                    var field = FindField(method.DeclaringType, fieldName);
                    if (field == null) { yield return name + ": no such field"; continue; }
                    var problem = Mismatch(field.FieldType, type, param.ParameterType.IsByRef);
                    if (problem != null) yield return name + ": " + problem;
                    continue;
                }
                if (name.StartsWith("__")) continue;
                var declared = method.GetParameters().FirstOrDefault(p => p.Name == name);
                if (declared == null)
                {
                    // Harmony matches by index as well ("__0"); a name it does not know is an error it reports itself
                    continue;
                }
                var declaredType = declared.ParameterType.IsByRef ? declared.ParameterType.GetElementType() : declared.ParameterType;
                var mismatch = Mismatch(declaredType, type, param.ParameterType.IsByRef);
                if (mismatch != null) yield return name + ": " + mismatch;
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }

        /// <summary>
        /// Why a value of <paramref name="actual"/> taken as <paramref name="taken"/> is read wrong; null when it is not.
        /// A value type must be the very type (an enum may be taken as its underlying type), a reference compatible - by
        /// ref exactly the type, the patch may write it.
        /// </summary>
        public static string Mismatch(Type actual, Type taken, bool byRef)
        {
            if (actual == taken) return null;
            if (actual.IsValueType || taken.IsValueType)
            {
                if (actual.IsEnum && Enum.GetUnderlyingType(actual) == taken) return null;
                if (taken.IsEnum && Enum.GetUnderlyingType(taken) == actual) return null;
                return actual.Name + " taken as " + taken.Name;
            }
            // by ref as object: the way to take a type the patch cannot name (not public); such a patch writes only objects
            // of the real type (reviewed: SentisOptimisations ReplicablesPatch, the client's UpdateLayer)
            if (byRef && taken == typeof(object)) return null;
            if (byRef) return actual.Name + " taken by ref as " + taken.Name;
            if (taken.IsAssignableFrom(actual)) return null;
            // an object of the patch's type may still be passed (a declared base, a patch for one subtype): not a type error
            if (actual.IsAssignableFrom(taken)) return null;
            if (actual.IsInterface || taken.IsInterface) return null;
            return actual.Name + " taken as " + taken.Name;
        }

        /// <summary>Every Harmony patch of every plugin: (patched method, kind, patch).</summary>
        private static IEnumerable<(MethodBase, string, MethodInfo)> HarmonyPatches()
        {
            var result = new List<(MethodBase, string, MethodInfo)>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var harmony = assembly.GetType("HarmonyLib.Harmony", false);
                if (harmony == null) continue;
                var all = harmony.GetMethod("GetAllPatchedMethods", BindingFlags.Static | BindingFlags.Public);
                var info = harmony.GetMethod("GetPatchInfo", BindingFlags.Static | BindingFlags.Public);
                if (all == null || info == null) continue;
                foreach (MethodBase method in (IEnumerable)all.Invoke(null, null))
                {
                    var patches = info.Invoke(null, new object[] { method });
                    if (patches == null) continue;
                    foreach (var kind in new[] { "Prefixes", "Postfixes", "Finalizers" })
                        if ((patches.GetType().GetField(kind)?.GetValue(patches) ?? patches.GetType().GetProperty(kind)?.GetValue(patches)) is IEnumerable list)
                            foreach (var patch in list)
                                if ((patch.GetType().GetProperty("PatchMethod")?.GetValue(patch) ?? patch.GetType().GetField("PatchMethod")?.GetValue(patch)) is MethodInfo patchMethod)
                                    result.Add((method, kind.TrimEnd('s').TrimEnd('e'), patchMethod));
                }
            }
            return result;
        }
    }
}
