using System.Collections;
using SentisTests.Core;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Lists every method the patch manager re-emits (patches of all plugins) whose body has a
    /// leave the Torch re-emit bug would redirect (see <see cref="LeaveTargets"/>). Such a method
    /// runs with changed control flow on the live server; the check fails while there is any.
    /// </summary>
    public sealed class PatchAuditScenario : TestScenario
    {
        public const string ScenarioName = "patch_audit";

        public override string Name => ScenarioName;

        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            // Known to break when re-emitted (seen as "Queue empty" from its while loop): the
            // audit must find it, or it would find nothing anywhere.
            var control = typeof(Sandbox.Game.SessionComponents.MySessionComponentContractSystem)
                .GetMethod("UpdateAfterSimulation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var controlFound = System.Linq.Enumerable.ToList(LeaveTargets.Redirected(control));
            Note("PATCH AUDIT control: " + (controlFound.Count > 0 ? string.Join("; ", controlFound) : "nothing found"));
            Check(controlFound.Count > 0, "the audit does not find the known redirected leave of the contract system");

            var found = LeaveTargets.Audit(out var methods, out var fixedMethods);
            Note("PATCH AUDIT | " + methods + " re-emitted methods checked, " + fixedMethods.Count + " carry the leave fix (" +
                 string.Join(", ", fixedMethods) + "), " + found.Count + " redirected leaves left");
            foreach (var line in found) Note("REDIRECTED " + line);
            Check(found.Count == 0, found.Count + " patched methods run with changed control flow");
            yield break;
        }
    }
}
