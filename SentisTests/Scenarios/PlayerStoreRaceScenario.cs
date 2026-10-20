using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The economy's set of players' stores (<c>MySessionComponentEconomy.m_playersStores</c>, a plain HashSet) is written
    /// by <c>MyStoreBlock.Init</c> when the store's builder has items (<c>RegisterPlayerStore</c>), and a grid made in
    /// parallel (<c>MyEntities.CreateFromObjectBuilderParallel</c>: a paste, a prefab, a restore) inits its blocks on the
    /// worker threads. Two stores made at once write the set at once, and a write cuts a bucket's chain into a loop: the
    /// next store registered on the game thread (an offer put up, <c>CreateNewOffer</c>) walks the loop forever and the
    /// server freezes (the watchdog's "frozen for 60 seconds", the game thread in <c>HashSet.AddIfNotPresent</c> under
    /// <c>MyStoreBlock.RegisterPlayerStore</c>, 20.10.2026, during reg_store).
    ///
    /// The set is looked at without walking it as the game does: each bucket's chain followed for at most as many steps as
    /// the set has slots, so a loop is found, not entered. Then 256 grids with a player's store are made in parallel at
    /// once, 8 rounds, the round before closed meanwhile, and the set is looked at after each. A broken set is put back together from what it holds, so the server lives on.
    /// </summary>
    public sealed class PlayerStoreRaceScenario : TestScenario
    {
        public const string ScenarioName = "player_store_race";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        private const int Stores = 256;
        private const int Rounds = 8;

        public override IEnumerator Run()
        {
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            if (economy == null) throw new ScenarioSkippedException("no economy in this world");
            var field = typeof(MySessionComponentEconomy).GetField("m_playersStores", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new ScenarioFailedException("MySessionComponentEconomy.m_playersStores is not in this game");
            var set = (HashSet<MyStoreBlock>)field.GetValue(economy);
            var before = Look(set);
            Note("the set of players' stores before: " + before.Text);
            if (before.Broken) Repair(economy, field, set);

            var owner = WorldApi.PlayerIdentityId();
            Check(!MySession.Static.Players.IdentityIsNpc(owner), "the test owner is an NPC: its stores are not registered");
            var origin = TestRunner.RunOrigin ?? new Vector3D(0, 0, 0);
            var store = MyDefinitionId.Parse("MyObjectBuilder_StoreBlock/StoreBlock");
                        var previous = new List<MyCubeGrid>();
            for (var round = 0; round < Rounds; round++)
            {
                var made = 0;
                var grids = new List<MyCubeGrid>();
                for (var i = 0; i < Stores; i++)
                {
                    var ob = new MyObjectBuilder_CubeGrid
                    {
                        Name = WorldApi.EntityPrefix + "store-race-" + i,
                        DisplayName = WorldApi.EntityPrefix + "store-race-" + i,
                        GridSizeEnum = MyCubeSize.Large,
                        IsStatic = true,
                        PositionAndOrientation = new MyPositionAndOrientation(origin + new Vector3D(i % 16 * 10, 300 + round * 10, i / 16 * 10), Vector3.Forward, Vector3.Up),
                        PersistentFlags = MyPersistentEntityFlags2.InScene,
                        CubeBlocks = new List<MyObjectBuilder_CubeBlock>(),
                    };
                    var block = (MyObjectBuilder_StoreBlock)MyObjectBuilderSerializer.CreateNewObject(store);
                    block.Owner = owner;
                    block.BuiltBy = owner;
                    // a store with its list of items, as saved: Init registers it
                    block.PlayerItems = new List<VRage.Game.ObjectBuilders.Definitions.MyObjectBuilder_StoreItem>();
                    ob.CubeBlocks.Add(block);
                    MyEntities.RemapObjectBuilder(ob);
                    MyEntities.CreateFromObjectBuilderParallel(ob, true, e =>
                    {
                        made++;
                        if (e is MyCubeGrid g) grids.Add(g);
                    });
                }
                // the last round's stores closed while these init: removals from the set beside additions
                foreach (var grid in previous) grid.Close();
                var waited = 0;
                while (made < Stores && waited++ < 60 * 30) yield return null;
                // a store registered off the game thread may be put in the set on it a frame later
                yield return WaitForTicks(2);
                foreach (var grid in grids) Track(grid);
                Check(made == Stores, made + " of " + Stores + " store grids made in 30 s");
                var after = Look(set);
                if (after.Broken)
                {
                    Repair(economy, field, set);
                    Check(false, "round " + round + ": the set of players' stores is broken by stores made in parallel: " + after.Text);
                }
                var mine = grids.Count(g => g.GetFatBlocks().OfType<MyStoreBlock>().Any(st => set.Contains(st)));
                Check(mine == Stores, "round " + round + ": " + mine + " of the " + Stores + " stores made in parallel are in the set");
                Note("round " + round + ": " + Stores + " stores made in parallel, the set whole (" + after.Text + ")");
                previous = grids;
                yield return null;
            }
            foreach (var grid in previous) grid.Close();
            Note(Rounds + " rounds of " + Stores + " stores made in parallel: the set stayed whole");
        }

        private struct Shape
        {
            public bool Broken;
            public string Text;
        }

        /// <summary>
        /// The set's chains walked as far as it has slots: a chain longer than that is a loop. The count the set keeps
        /// against what its chains hold.
        /// </summary>
        private static Shape Look(HashSet<MyStoreBlock> set)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = set.GetType();
            var buckets = (int[])type.GetField("m_buckets", any)?.GetValue(set);
            var slots = (Array)type.GetField("m_slots", any)?.GetValue(set);
            var count = (int)type.GetField("m_count", any).GetValue(set);
            if (buckets == null || slots == null) return new Shape { Text = "empty (" + count + ")" };
            var slotType = slots.GetType().GetElementType();
            var next = slotType.GetField("next", any);
            var held = 0;
            var loops = 0;
            foreach (var head in buckets)
            {
                var steps = 0;
                for (var i = head - 1; i >= 0; i = (int)next.GetValue(slots.GetValue(i)))
                {
                    held++;
                    if (++steps > slots.Length)
                    {
                        loops++;
                        break;
                    }
                }
            }
            var broken = loops > 0 || held != count;
            return new Shape { Broken = broken, Text = count + " stores, " + held + " in the chains" + (loops > 0 ? ", " + loops + " chains looped" : "") };
        }

        /// <summary>A new set of what the broken one holds (read slot by slot, not by its chains), in its place.</summary>
        private void Repair(MySessionComponentEconomy economy, FieldInfo field, HashSet<MyStoreBlock> set)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic;
            var slots = (Array)set.GetType().GetField("m_slots", any).GetValue(set);
            var last = (int)set.GetType().GetField("m_lastIndex", any).GetValue(set);
            var slotType = slots.GetType().GetElementType();
            var hash = slotType.GetField("hashCode", any);
            var value = slotType.GetField("value", any);
            var fresh = new HashSet<MyStoreBlock>();
            for (var i = 0; i < last && i < slots.Length; i++)
            {
                var slot = slots.GetValue(i);
                if ((int)hash.GetValue(slot) >= 0 && value.GetValue(slot) is MyStoreBlock block && !block.Closed) fresh.Add(block);
            }
            field.SetValue(economy, fresh);
            Log.Warn("player_store_race: the economy's set of players' stores was broken, put back together with {0} stores", fresh.Count);
        }
    }
}
