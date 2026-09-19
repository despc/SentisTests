using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SentisTests.Core
{
    /// <summary>
    /// Works around a Torch patch manager bug that silently changes the control flow of patched
    /// methods, and finds the methods it affects.
    ///
    /// Any patch - even a prefix - makes Torch re-emit the whole original body. When a <c>leave</c>
    /// is directly followed by the start of a finally, catch or fault block or by the end of the
    /// exception block, Torch drops it and relies on the <c>leave</c> the ILGenerator emits for the
    /// block, which always goes to the end of the block. The C# compiler, however, points a
    /// <c>leave</c> straight at its final destination when the end of the block is only a jump to
    /// somewhere else - typically a <c>foreach</c> followed by a <c>while</c> loop, whose condition is
    /// the real target. After the re-emit the jump lands at the end of the block instead: seen as
    /// MySessionComponentContractSystem.UpdateAfterSimulation running the body of
    /// <c>while (queue.Count &gt; 0) queue.Dequeue()</c> on an empty queue.
    /// </summary>
    public static class LeaveTargets
    {
        public static long Kept;

        /// <summary>Transpiler: a nop after such a leave makes Torch emit the leave itself, with its target.</summary>
        public static IEnumerable<MsilInstruction> KeepLeaves(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                yield return list[i];
                if (IsLeave(list[i]) && i + 1 < list.Count && EndsOrSwitchesBlock(list[i + 1]))
                {
                    Kept++;
                    yield return new MsilInstruction(OpCodes.Nop);
                }
            }
        }

        public static readonly MethodInfo KeepLeavesMethod =
            typeof(LeaveTargets).GetMethod(nameof(KeepLeaves), BindingFlags.Static | BindingFlags.Public);

        /// <summary>
        /// Every method Torch currently re-emits whose body has a leave the bug would redirect, with
        /// where it should go and where it goes instead.
        /// </summary>
        public static List<string> Audit(out int methodsChecked) => Audit(out methodsChecked, out _);

        /// <summary>As above; methods that already carry a KeepLeaves transpiler (from any plugin) count as fixed.</summary>
        public static List<string> Audit(out int methodsChecked, out List<string> fixedMethods)
        {
            var found = new List<string>();
            fixedMethods = new List<string>();
            methodsChecked = 0;
            var patterns = typeof(PatchManager).GetField("_rewritePatterns", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) as IDictionary;
            if (patterns == null)
            {
                found.Add("PatchManager._rewritePatterns not found");
                return found;
            }
            // The non-generic enumerator yields DictionaryEntries; LINQ would get KeyValuePairs.
            var entries = new List<DictionaryEntry>();
            var enumerator = patterns.GetEnumerator();
            while (enumerator.MoveNext()) entries.Add(enumerator.Entry);
            foreach (var entry in entries)
            {
                var method = (MethodBase)entry.Key;
                methodsChecked++;
                var transpilers = entry.Value.GetType().GetProperty("Transpilers")?.GetValue(entry.Value) as IEnumerable<MethodInfo>;
                if (transpilers != null && transpilers.Any(t => t.Name == nameof(KeepLeaves)))
                {
                    fixedMethods.Add(method.DeclaringType?.FullName + "." + method.Name);
                    continue;
                }
                try
                {
                    foreach (var problem in Redirected(method))
                        found.Add(method.DeclaringType?.FullName + "." + method.Name + ": " + problem);
                }
                catch (Exception e)
                {
                    found.Add(method.DeclaringType?.FullName + "." + method.Name + ": could not read (" + e.GetType().Name + ")");
                }
            }
            return found;
        }

        /// <summary>The leaves of one method that the re-emit would send somewhere else; empty when none.</summary>
        public static IEnumerable<string> Redirected(MethodBase method)
        {
            var list = PatchUtilities.ReadInstructions(method).ToList();
            var labelAt = new Dictionary<MsilLabel, int>();
            for (var i = 0; i < list.Count; i++)
                foreach (var label in list[i].Labels)
                    labelAt[label] = i;

            // Exception blocks: which one each instruction is in, the instruction that ends each
            // (the first one after it), and the instructions where a handler of a block starts -
            // the ILGenerator emits its own leave to the end of that block right before them.
            var open = new Stack<int>();
            var blockOf = new int[list.Count];
            var blockEnd = new Dictionary<int, int>();
            var handlerStart = new Dictionary<int, int>();
            var nextBlock = 0;
            for (var i = 0; i < list.Count; i++)
            {
                foreach (var op in list[i].TryCatchOperations)
                {
                    switch (op.Type)
                    {
                        case MsilTryCatchOperationType.BeginExceptionBlock:
                            open.Push(nextBlock++);
                            break;
                        case MsilTryCatchOperationType.EndExceptionBlock:
                            if (open.Count > 0) blockEnd[open.Pop()] = i;
                            break;
                        default:
                            if (open.Count > 0) handlerStart[i] = open.Peek();
                            break;
                    }
                }
                blockOf[i] = open.Count > 0 ? open.Peek() : -1;
            }

            bool Dropped(int i) => IsLeave(list[i]) && i + 1 < list.Count && EndsOrSwitchesBlock(list[i + 1]);

            // Where control really ends up from an instruction, following the leaves on the way:
            // the generator's own leave before a handler, explicit leaves, and dropped leaves.
            int Resolve(int at)
            {
                for (var guard = 0; guard < 32; guard++)
                {
                    if (handlerStart.TryGetValue(at, out var block) && blockEnd.TryGetValue(block, out var end))
                    {
                        at = end;
                        continue;
                    }
                    if (!IsLeave(list[at])) break;
                    if (Dropped(at))
                    {
                        if (blockOf[at] < 0 || !blockEnd.TryGetValue(blockOf[at], out var droppedEnd)) break;
                        at = droppedEnd;
                    }
                    else if (list[at].Operand is MsilOperandBrTarget t && labelAt.TryGetValue(t.Target, out var next))
                        at = next;
                    else break;
                }
                return at;
            }

            for (var i = 0; i + 1 < list.Count; i++)
            {
                if (!Dropped(i)) continue;
                if (!(list[i].Operand is MsilOperandBrTarget target) || !labelAt.TryGetValue(target.Target, out var targetIndex)) continue;
                if (blockOf[i] < 0 || !blockEnd.TryGetValue(blockOf[i], out var end)) continue;
                var meant = Resolve(targetIndex);
                var actual = Resolve(end);
                if (meant != actual)
                    yield return "leave #" + i + " goes to #" + meant + " (" + list[meant].OpCode + "), after the re-emit to #" +
                                 actual + " (" + list[actual].OpCode + ") [block end #" + end + " ops " +
                                 string.Join(",", list[end].TryCatchOperations.Select(op => op.Type.ToString())) + "]";
            }
        }

        private static bool IsLeave(MsilInstruction instruction) =>
            instruction.OpCode == OpCodes.Leave || instruction.OpCode == OpCodes.Leave_S;

        private static bool EndsOrSwitchesBlock(MsilInstruction instruction) =>
            instruction.TryCatchOperations.Any(op =>
                op.Type == MsilTryCatchOperationType.EndExceptionBlock ||
                op.Type == MsilTryCatchOperationType.BeginClauseBlock ||
                op.Type == MsilTryCatchOperationType.BeginFaultBlock ||
                op.Type == MsilTryCatchOperationType.BeginFinallyBlock);
    }
}
