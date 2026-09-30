using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using SentisTests.Core;
using Torch.Managers.PatchManager;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Lists every method the patch manager re-emits (patches of all plugins) that the re-emit calls with its arguments
    /// out of place.
    ///
    /// Torch re-emits a patched method as a static method whose first parameter is the instance, and puts a jump to it
    /// at the start of the original. The two are called alike - except when the method returns a structure through a
    /// hidden buffer (any structure but one of 1, 2, 4 or 8 bytes on x64): an instance method takes the instance first
    /// and the buffer second, a static one the buffer first. The game's callers pass them for the instance method, the
    /// re-emitted copy reads them the other way round: it writes its result over the object and takes the caller's stack
    /// for the instance - writes there too. Saved registers, the stack cookie and locals of the callers are overwritten
    /// then (the old server's crashes and "Collection was modified" with nothing modified, 29.09.2026).
    /// </summary>
    public sealed class PatchRetBufScenario : TestScenario
    {
        public const string ScenarioName = "patch_retbuf";

        public override string Name => ScenarioName;

        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            var patterns = typeof(PatchManager).GetField("_rewritePatterns", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary;
            Check(patterns != null, "PatchManager._rewritePatterns not found");
            // the non-generic enumerator yields DictionaryEntries
            var methods = new List<MethodBase>();
            var enumerator = patterns.GetEnumerator();
            while (enumerator.MoveNext()) methods.Add((MethodBase)enumerator.Entry.Key);

            var found = new List<string>();
            foreach (var method in methods)
            {
                var problem = Problem(method);
                if (problem == null) continue;
                found.Add(method.DeclaringType?.FullName + "." + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) +
                          "): " + problem + " | " + string.Join("; ", LeaveTargets.PatchesOf(method)));
            }
            Note("PATCH RETBUF | " + methods.Count + " re-emitted methods checked, " + found.Count + " called with their arguments out of place");
            foreach (var line in found) Note("RETBUF " + line);
            Check(found.Count == 0, found.Count + " patched methods take their arguments out of place");
            yield break;
        }

        /// <summary>Why the re-emitted copy of the method reads its arguments out of place; null when it does not.</summary>
        public static string Problem(MethodBase method)
        {
            if (method.IsStatic || !(method is MethodInfo info)) return null;
            if (method.DeclaringType != null && method.DeclaringType.IsValueType)
                return "an instance method of a structure (" + method.DeclaringType.Name + "): the instance is a reference, the copy takes an object";
            var type = info.ReturnType;
            if (!ReturnsThroughBuffer(type)) return null;
            return "returns " + type.Name + " (" + SizeOf(type) + " bytes) through a hidden buffer";
        }

        /// <summary>A structure the x64 calling convention returns through a hidden buffer: all but those of 1, 2, 4 or 8 bytes.</summary>
        public static bool ReturnsThroughBuffer(Type type)
        {
            if (type == typeof(void) || !type.IsValueType || type.IsPrimitive || type.IsEnum || type.IsPointer) return false;
            var size = SizeOf(type);
            return size != 1 && size != 2 && size != 4 && size != 8;
        }

        private static readonly Dictionary<Type, int> Sizes = new Dictionary<Type, int>();

        /// <summary>The managed size of a value type (the IL sizeof).</summary>
        public static int SizeOf(Type type)
        {
            if (Sizes.TryGetValue(type, out var size)) return size;
            var method = new DynamicMethod("SizeOf_" + type.Name, typeof(int), Type.EmptyTypes, typeof(PatchRetBufScenario).Module, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Sizeof, type);
            il.Emit(OpCodes.Ret);
            size = (int)method.Invoke(null, null);
            Sizes[type] = size;
            return size;
        }
    }
}
