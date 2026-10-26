using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sandbox.Game.Entities;
using VRage;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The world's entities are found by their id on any thread. The game keeps them in one store
    /// (MyEntityIdentifier.m_mainData); a thread may have a store of its own for entities it makes aside (prefabs,
    /// pasted grids: m_perThreadData, swapped in and back through one slot for the whole process). The game thread
    /// must look at the world's store, and an entity of the world must be found from another thread too: SentisClusters
    /// updates the clusters' blocks on worker threads, and there an event controller did not find the door it watched
    /// (25.10.2026: the game thread's store was not the world's, and the world's held one entity - so it is after every
    /// load, the game thread takes a share of MyEntities.Load's parallel loop and gets a store of its own; SentisClusters'
    /// EntityStore looks there for other threads). Needs SentisClusters.
    /// </summary>
    public sealed class EntityStoreCheck : RegressionCheck
    {
        public override string Name => Prefix + "entity_store";

        protected override IEnumerator Check()
        {
            SkipUnless(System.AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "SentisClusters"),
                "the vanilla game finds the world's entities by id on the game thread only (its store is the game thread's own after the load)");
            yield return null;
            const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(MyEntityIdentifier);
            var mainField = type.GetField("m_mainData", st);
            var ownField = type.GetField("m_perThreadData", st);
            Check(mainField != null && ownField != null, "MyEntityIdentifier has no m_mainData / m_perThreadData");
            var main = mainField.GetValue(null);
            var own = ownField.GetValue(null);
            IDictionary List(object data) => data?.GetType().GetField("EntityList")?.GetValue(data) as IDictionary;
            var mainCount = List(main)?.Count ?? -1;
            var ownCount = List(own)?.Count ?? -1;

            // a few of the world's grids, looked up from a worker thread
            var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().Take(50).Select(g => g.EntityId).ToList();
            var missed = 0;
            // and an id for an entity made there: the process' store had its allocation of ids suspended since the
            // world's load, every entity made off the game thread got id 0 and MyEntities.CreateFromObjectBuilderAndAdd
            // threw it away (a dead character's backpack in a detached cluster, 25.10.2026)
            var madeId = -1L;
            var suspended = false;
            var thread = new Thread(() =>
            {
                foreach (var id in grids)
                    if (MyEntities.GetEntityById(id) == null) missed++;
                suspended = MyEntityIdentifier.AllocationSuspended;
                madeId = suspended ? 0 : MyEntityIdentifier.AllocateId();
            });
            thread.Start();
            thread.Join();

            var state = "game thread's store " + (own == null ? "none" : ReferenceEquals(own, main) ? "the world's" : "its own (" + ownCount + ")") +
                        ", world's store " + mainCount + " entities, swapped " + type.GetField("m_isSwapped", st)?.GetValue(null) + "; " +
                        missed + " of " + grids.Count + " grids not found from another thread; an id made there " + (madeId != 0 ? "fine" : "none (allocation suspended " + suspended + ")");
            Check(missed == 0 && madeId != 0, state);
            Result(state);
        }
    }
}
