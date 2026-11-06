using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Replication;
using VRage.Network;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A replicable destroyed with its network id and no state groups (prod 06.11.2026 22:00: an inventory's, as its entity
    /// was deleted - KeyNotFoundException out of MyReplicationServer.RemoveStateGroups, a fatal exception). Made here: a
    /// cargo container's inventory has its groups taken out of the replication's list, then the grid is deleted. With
    /// SentisOptimisations' ReplicableGroupsGuard the server goes on, the replicable is gone from the replication and the
    /// guard has told it. Without the guard this takes the server down: the check is skipped then.
    /// </summary>
    public sealed class ReplicableNoGroupsCheck : RegressionCheck
    {
        public override string Name => Prefix + "replicable_no_groups";

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        protected override IEnumerator Check()
        {
            var guard = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.CrashFix.ReplicableGroupsGuard")).FirstOrDefault(t => t != null);
            var seenField = guard?.GetField("_seen", Any);
            if (seenField == null)
            {
                throw new Core.ScenarioSkippedException("no SentisOptimisations ReplicableGroupsGuard: the destroy would take the server down");
            }
            yield return Arrive(Site.At(0, 6, -8));
            var grid = Build("no-groups", Site.Origin, true, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0));
            yield return Seconds(2);
            var inventory = Inventory(Block<MyCargoContainer>(grid));
            var server = (MyReplicationServer)MyMultiplayer.ReplicationLayer;
            var replicable = MyExternalReplicable.FindByObject(inventory);
            Check(replicable != null, "the cargo's inventory has no replicable");
            var groups = (IDictionary)typeof(MyReplicationServer).GetField("m_replicableGroups", Any).GetValue(server);
            Check(groups.Contains(replicable), "the inventory's replicable has no state groups to start with");
            var idOf = typeof(MyReplicationLayer).GetMethods(Any).First(m => m.Name == "GetNetworkIdByObject" && m.GetParameters().Length == 1);
            bool networkId(IMyReplicable r)
            {
                var id = idOf.Invoke(server, new object[] { r });
                return !(bool)id.GetType().GetProperty("IsInvalid").GetValue(id);
            }
            Check(networkId(replicable), "the inventory's replicable has no network id");

            // its groups lost, as on the prod
            groups.Remove(replicable);
            var seen = (int)seenField.GetValue(null);
            grid.Close();
            yield return WaitForTicks(5);
            var told = (int)seenField.GetValue(null) - seen;
            Note($"the grid deleted: the guard told {told}; the replicable's network id {(networkId(replicable) ? "still there" : "dropped")}, its entry {(groups.Contains(replicable) ? "still there" : "gone")}");
            Check(told >= 1, "the guard did not see the replicable with no groups (the destroy went another way?)");
            Check(!networkId(replicable), "the replicable kept its network id after its destroy");
            Check(!groups.Contains(replicable), "the replicable's empty entry was left behind");
            Result("an inventory's replicable with no state groups destroyed with its grid: the server went on, the replicable gone, told in the log");
        }
    }
}
