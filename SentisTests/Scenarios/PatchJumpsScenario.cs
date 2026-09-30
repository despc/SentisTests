using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using SentisTests.Core;
using Torch.Managers.PatchManager;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Every method the patch manager re-emits is entered by one jump instruction, none by Torch's own two.
    ///
    /// Torch sends a patched method to its copy with <c>mov rax, target; jmp rax</c>. A thread the runtime stops between
    /// the two, for a collection, is taken to be in the original method's prologue with pushes it never made, and the
    /// runtime's hijack stub lands in a slot of the calling frame: the old server's crashes and its "Collection was
    /// modified" from collections nobody changed (29.09.2026). SentisOptimisations' TorchJumpFix writes one
    /// instruction instead; this checks every entry.
    /// </summary>
    public sealed class PatchJumpsScenario : TestScenario
    {
        public const string ScenarioName = "patch_jumps";

        public override string Name => ScenarioName;

        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            var patterns = typeof(PatchManager).GetField("_rewritePatterns", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary;
            Check(patterns != null, "PatchManager._rewritePatterns not found");
            var entries = new List<DictionaryEntry>();
            var enumerator = patterns.GetEnumerator();
            while (enumerator.MoveNext()) entries.Add(enumerator.Entry);

            int checkedEntries = 0, oneInstruction = 0;
            var twoInstructions = new List<string>();
            foreach (var entry in entries)
            {
                var field = entry.Value.GetType().GetField("_revertAddress", BindingFlags.Instance | BindingFlags.NonPublic);
                var address = field == null ? 0L : (long)field.GetValue(entry.Value);
                if (address == 0) continue;
                checkedEntries++;
                var code = new byte[12];
                Marshal.Copy(new System.IntPtr(address), code, 0, 12);
                if (code[0] == 0x48 && code[1] == 0xB8 && code[10] == 0xFF && code[11] == 0xE0)
                    twoInstructions.Add(((MethodBase)entry.Key).DeclaringType?.FullName + "." + ((MethodBase)entry.Key).Name);
                else if (code[0] == 0xE9 || code[0] == 0xFF && code[1] == 0x25)
                    oneInstruction++;
            }
            Note("PATCH JUMPS | " + checkedEntries + " patched entries: " + oneInstruction + " one-instruction jumps, " + twoInstructions.Count + " two-instruction (mov rax; jmp rax)");
            foreach (var name in twoInstructions) Note("TWO-INSTRUCTION JUMP " + name);
            Check(checkedEntries > 0, "no patched entries found");
            Check(twoInstructions.Count == 0, twoInstructions.Count + " patched methods entered by Torch's two-instruction jump");
            yield break;
        }
    }
}
