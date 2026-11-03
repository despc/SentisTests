using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sandbox.Game.Entities;
using SentisTests.Game;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ModAPI;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Grids loaded by a plugin's command on the game thread, as ALE GridBackup's restore does (MyEntities.Load from
    /// the command): the game thread takes a share of the load's parallel loop, and that share takes the thread's own
    /// entity store and empties it at the end (InitEntityData.CallInitEntity) - the game thread's own store is the
    /// world's. SentisClusters' EntityStore gives the job a store of its own and puts the world's back (prod
    /// 03.11.2026: its warning at a !gridbackup restore). The grid loaded is in the world and found by id on the game
    /// thread and on another; the world's entities are all still found.
    /// </summary>
    public sealed class EntityLoadOnGameThreadCheck : RegressionCheck
    {
        public override string Name => Prefix + "entity_load_game_thread";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -10));
            var source = Build("load-game-thread", Site.Origin, true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0));
            yield return null;
            var builder = (MyObjectBuilder_CubeGrid)source.GetObjectBuilder();
            source.Close();
            yield return null;
            MyEntities.RemapObjectBuilder(builder);
            builder.DisplayName = builder.DisplayName + "-loaded";

            var world = MyEntities.GetEntities().Where(e => !e.Closed && e.EntityId != 0).Select(e => e.EntityId).Take(500).ToList();
            var ok = MyEntities.Load(new List<MyObjectBuilder_EntityBase> { builder }, out var error);
            yield return null;
            MyEntities.TryGetEntityById(builder.EntityId, out var loaded);
            if (loaded != null) Track(loaded);
            var parked = Core.Integrations.TypeOf("SentisClusters", "SentisClusters.Run.EntityStore")
                ?.GetField("_parkedWorld", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
            Note("SentisClusters' world store left parked after the load: " + (parked != null));
            var lost = world.Count(id => !MyEntities.TryGetEntityById(id, out _));
            var missedElsewhere = -1;
            var thread = new Thread(() => missedElsewhere = (MyEntities.GetEntityById(builder.EntityId) == null ? 1 : 0) +
                                                            world.Count(id => MyEntities.GetEntityById(id) == null));
            thread.Start();
            thread.Join();
            var state = "loaded " + ok + (error.HasValue ? " (" + error + ")" : "") + "; the grid " + (loaded == null ? "not found" : "found, in the scene " + loaded.InScene + ", " +
                        ((MyCubeGrid)loaded).BlocksCount + " blocks") + "; of " + world.Count + " entities of the world " + lost + " lost on the game thread, " +
                        missedElsewhere + " (with the grid) not found from another thread";
            Note(state);
            Check(ok && loaded is MyCubeGrid grid && grid.InScene && grid.BlocksCount == 3, "the grid was not loaded whole: " + state);
            Check(lost == 0, "the world lost entities on the game thread: " + state);
            Check(missedElsewhere == 0, "entities not found from another thread: " + state);
            Result(state);
        }
    }
}
