using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Merge blocks ("Соединитель") and the grids the game spawns for NPCs (complaints from the live server, 19.10.2026:
    /// a static structure with a prototech refinery from an encounter would not merge to a station, its merge block never
    /// yellow). The game merges such a grid with a player's only once it is claimed: its claim timer runs from the first
    /// thing a player does to it (a block built or owned by a player, damage...) for NPCGridClaimTimeLimit minutes, the
    /// grid stops being the NPCs' and the blocks the NPCs built are taken off it (MyNPCGridClaimSessionComponent,
    /// MyCubeGrid.RemoveNPCBlocks). The limit is set to one minute here. A static station of the player's with a merge
    /// block, a player beside it, and two ships pressed to it merge block to merge block:
    /// <list type="bullet">
    /// <item>the player's own ship: merges at once;</item>
    /// <item>a ship the game spawned for the NPCs (its hull built by the Factorum, its merge block by the player): its
    /// claim timer must start, it must not merge while that runs, and must merge once it is over.</item>
    /// </list>
    /// </summary>
    public sealed class MergeNpcClaimScenario : TestScenario
    {
        public const string ScenarioName = "merge_npc_claim";
        private const string Prefix = "MergeClaim_";
        private const int ClaimMinutes = 1;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 400;

        private int _claimWas = -1;
        private Vector3D _ground, _up, _side;

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                if (_claimWas >= 0) MySession.Static.Settings.NPCGridClaimTimeLimit = _claimWas;
            }
            finally { base.Cleanup(); }
        }

        private static readonly MethodInfo MergeNormal = typeof(MyShipMergeBlock).GetMethod("GetMergeNormalWorld", BindingFlags.Instance | BindingFlags.NonPublic);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(MergeNormal != null, "no MyShipMergeBlock.GetMergeNormalWorld");
            var factorum = MySession.Static.Factions.TryGetFactionByTag("FCTM");
            Check(factorum != null && MySession.Static.Players.IdentityIsNpc(factorum.FounderId), "no Factorum NPC to build the NPC grid");
            _claimWas = MySession.Static.Settings.NPCGridClaimTimeLimit;
            MySession.Static.Settings.NPCGridClaimTimeLimit = ClaimMinutes;
            Note($"MERGE | claim limit {_claimWas} min, {ClaimMinutes} for the run");

            var anchor = WorldApi.PlanetAnchor("drill_dock");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = anchor.Translation + Vector3D.Normalize(anchor.Translation - centre) * 50 + Vector3D.CalculatePerpendicularVector(anchor.Translation - centre) * 900;
            _ground = planet.GetClosestSurfacePointGlobal(ref probe);
            _up = Vector3D.Normalize(_ground - centre);
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            WorldApi.RevertTerrain(planet, _ground, 80);
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (_ground + _up * 3 - Vector3D.Cross(_up, _side) * 15, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(4, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var player = WorldApi.TestIdentityId();
            var failures = new List<string>();

            // the player's own ship
            var station = SpawnStation(0, player);
            var own = SpawnShip("own", player, player);
            var pressed = Press(station, own, "own");
            while (pressed.MoveNext()) yield return pressed.Current;
            var ownMerged = Until(() => Gone(own, station), 15);
            while (ownMerged.MoveNext()) yield return ownMerged.Current;
            var ownResult = Gone(own, station);
            Note("MERGE | the player's ship " + (ownResult ? "merged" : "did not merge: " + State(station)));
            if (!ownResult) failures.Add("the player's own ship did not merge");

            // the NPCs' ship
            var station2 = SpawnStation(1, player);
            // as the game makes one: all of it the NPCs' (the merge block nobody's), registered for its claim; then the
            // player takes the merge block over - the claim's timer starts on that (a block of a player's already on it when
            // it is registered does not start it: MyNPCGridClaimSessionComponent.RegisterGrid looks the grid up in a
            // CachingDictionary it has not applied yet)
            var npc = SpawnShip("npc", factorum.FounderId, 0, npcSpawned: true);
            var claims = MySession.Static.NPCGridClaimSystem;
            var registered = WaitForSeconds(3, "npc: registered for its claim");
            while (registered.MoveNext()) yield return registered.Current;
            Note("MERGE | the NPC ship registered: " + Registered(claims, npc));
            var npcMerge = npc.GetFatBlocks().OfType<MyShipMergeBlock>().First();
            npcMerge.ChangeOwner(player, MyOwnershipShareModeEnum.Faction);
            var pressed2 = Press(station2, npc, "npc");
            while (pressed2.MoveNext()) yield return pressed2.Current;
            var started = Until(() => claims.GetFramesElapsed(npc.EntityId).HasValue, 10);
            while (started.MoveNext()) yield return started.Current;
            Note($"MERGE | the NPC ship's claim timer: {claims.GetFramesElapsed(npc.EntityId)?.ToString() ?? "not started"}, {claims.GetTimeRemaining(npc.EntityId)?.ToString() ?? "-"} left; " +
                 $"npc-spawned {npc.IsNpcSpawnedGrid}, small owners [{string.Join(", ", npc.SmallOwners.Select(o => o + (MySession.Static.Players.IdentityIsNpc(o) ? " npc" : " player")))}], " +
                 $"the player an NPC: {MySession.Static.Players.IdentityIsNpc(player)}, the hull's builder an NPC: {MySession.Static.Players.IdentityIsNpc(factorum.FounderId)}");
            if (!claims.GetFramesElapsed(npc.EntityId).HasValue) failures.Add("the NPC ship's claim timer did not start (a block of the player's on it)");
            var early = WaitForSeconds(15, "npc: pressed to the station while unclaimed");
            while (early.MoveNext()) yield return early.Current;
            var earlyMerged = Gone(npc, station2);
            Note("MERGE | the NPC ship unclaimed: " + (earlyMerged ? "merged (it should not)" : "not merged, " + State(station2)));
            if (earlyMerged) failures.Add("the NPC ship merged before it was claimed");
            if (!earlyMerged)
            {
                var claimed = Until(() => Gone(npc, station2) || !npc.IsNpcSpawnedGrid, ClaimMinutes * 60 + 30);
                while (claimed.MoveNext()) yield return claimed.Current;
                Note($"MERGE | the NPC ship claimed: {!npc.IsNpcSpawnedGrid || npc.Closed}; its blocks now {(npc.Closed ? 0 : npc.BlocksCount)} (the NPCs' taken off)");
                if (!npc.Closed && !npc.MarkedForClose)
                {
                    var re = Press(station2, npc, "npc claimed");
                    while (re.MoveNext()) yield return re.Current;
                    var merged = Until(() => Gone(npc, station2), 20);
                    while (merged.MoveNext()) yield return merged.Current;
                }
                var late = Gone(npc, station2);
                Note("MERGE | the NPC ship once claimed: " + (late ? "merged" : "not merged: " + State(station2)));
                if (!late) failures.Add("the NPC ship did not merge once claimed: " + State(station2));
            }
            // the button "claim the NPC structure" in the terminal (MyBlockLimits.ClaimNPCGrid, as the player's client asks for
            // it): the NPCs' blocks become the player's, PCU and all, the grid stops being the NPCs' at once, nothing taken off
            var claimant = FakeClients.PlayerOf(0).Identity.IdentityId;
            var station3 = SpawnStation(2, claimant);
            var claimedShip = SpawnShip("button", factorum.FounderId, 0, npcSpawned: true);
            var registered3 = WaitForSeconds(3, "button: registered for its claim");
            while (registered3.MoveNext()) yield return registered3.Current;
            claimedShip.GetFatBlocks().OfType<MyShipMergeBlock>().First().ChangeOwner(claimant, MyOwnershipShareModeEnum.Faction);
            // pressed to the station first, as a player brings it: refused while unclaimed, then the button pressed
            var pressed3 = Press(station3, claimedShip, "button");
            while (pressed3.MoveNext()) yield return pressed3.Current;
            var refused = Until(() => Gone(claimedShip, station3), 5);
            while (refused.MoveNext()) yield return refused.Current;
            Note("MERGE | before the button: " + (Gone(claimedShip, station3) ? "merged (it should not)" : "not merged, " + State(station3)));
            if (Gone(claimedShip, station3)) failures.Add("merged before the claim button");
            var before = claimedShip.BlocksCount;
            var npcBuilt = claimedShip.GetBlocks().Count(b => b.BuiltBy == factorum.FounderId);
            FakeClients.AsClient(0, () => Sandbox.Game.World.MyBlockLimits.ClaimNPCGrid(claimedShip.EntityId));
            var claimedNow = Until(() => !claimedShip.IsNpcSpawnedGrid || Gone(claimedShip, station3), 5);
            while (claimedNow.MoveNext()) yield return claimedNow.Current;
            var byPlayer = claimedShip.GetBlocks().Count(b => b.BuiltBy == claimant);
            Note($"MERGE | the button: npc-spawned {claimedShip.IsNpcSpawnedGrid}, blocks {before} -> {claimedShip.BlocksCount}, built by the NPCs {npcBuilt} -> {claimedShip.GetBlocks().Count(b => b.BuiltBy == factorum.FounderId)}, by the player {byPlayer}");
            if (claimedShip.IsNpcSpawnedGrid) failures.Add("the claim button did not claim the grid");
            if (claimedShip.BlocksCount != before) failures.Add($"the claim button took blocks off ({before} -> {claimedShip.BlocksCount})");
            var merged3 = Until(() => Gone(claimedShip, station3), 15);
            while (merged3.MoveNext()) yield return merged3.Current;
            var ok = Gone(claimedShip, station3);
            Note("MERGE | claimed by the button: " + (ok ? "merged" : "not merged: " + State(station3)));
            if (!ok) failures.Add("claimed by the button, it did not merge: " + State(station3));
            Check(failures.Count == 0, string.Join("; ", failures));
        }

        /// <summary>Until the condition holds or the seconds are up, without failing (what came of it is looked at after).</summary>
        private static IEnumerator Until(Func<bool> condition, int seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < end) yield return null;
        }

        /// <summary>For the log: whether the claim component holds the grid and what it has for it, and its events on the grid.</summary>
        private static string Registered(object claims, MyCubeGrid grid)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var dict = claims.GetType().GetField("m_NPCGrids", any)?.GetValue(claims);
            var contains = dict?.GetType().GetMethod("ContainsKey")?.Invoke(dict, new object[] { grid.EntityId });
            var keys = dict?.GetType().GetProperty("Keys")?.GetValue(dict) as System.Collections.IEnumerable;
            var count = keys?.Cast<object>().Count();
            var ownershipEvent = typeof(MyCubeGrid).GetField("OnBlockOwnershipChanged", any)?.GetValue(grid) as Delegate;
            var addedEvent = typeof(MyCubeGrid).GetField("OnBlockAdded", any)?.GetValue(grid) as Delegate;
            string Subscribers(Delegate d) => d == null ? "none" : string.Join(",", d.GetInvocationList().Select(x => x.Method.DeclaringType?.Name + "." + x.Method.Name));
            return $"held {contains} ({count} grids held), ownership event: {Subscribers(ownershipEvent)}; block added: {Subscribers(addedEvent)}";
        }

        /// <summary>Merged: one of the two grids taken into the other.</summary>
        private static bool Gone(MyCubeGrid a, MyCubeGrid b) => a.Closed || a.MarkedForClose || b.Closed || b.MarkedForClose;

        private static string State(MyCubeGrid station)
        {
            var merge = station.GetFatBlocks().OfType<MyShipMergeBlock>().FirstOrDefault();
            if (merge == null) return "no merge block";
            merge.SetDetailedInfoDirty();
            return $"station merge block: locked {merge.IsLocked}, in constraint {merge.InConstraint}, info '{((Sandbox.ModAPI.IMyTerminalBlock)merge).DetailedInfo.Replace("\n", " / ")}'";
        }

        private Vector3D Origin(int slot) => _ground + _up * 1.5 + _side * (slot * 18);

        private MyCubeGrid SpawnStation(int slot, long player)
        {
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "station-" + slot, MyCubeSize.Large, true, Origin(slot), Enumerable.Empty<BlockSpec>(), (Vector3)_side, (Vector3)_up);
            // a 3x3 floor, the merge block on its middle facing up with a ring round it the ship's floor rests on
            for (var x = -1; x <= 1; x++)
            for (var z = -1; z <= 1; z++)
            {
                Add(ob, x == 1 && z == 1 ? "LargeBlockBatteryBlock" : "LargeBlockArmorBlock", x, 0, z, player, player);
                if (x != 0 || z != 0) Add(ob, "LargeBlockArmorBlock", x, 1, z, player, player);
            }
            Add(ob, "LargeShipMergeBlock", 0, 1, 0, player, player).BlockOrientation = MergeFacing(Base6Directions.Direction.Up);
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.ChargeBatteries(grid);
            return grid;
        }

        /// <summary>A ship of a block, a battery and a merge block: the hull built by <paramref name="hull"/>, the merge block by <paramref name="mergeBy"/>.</summary>
        private MyCubeGrid SpawnShip(string name, long hull, long mergeBy, bool npcSpawned = false)
        {
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "ship-" + name, MyCubeSize.Large, false, _ground + _up * 60 + _side * 200, Enumerable.Empty<BlockSpec>(), (Vector3)_side, (Vector3)_up);
            for (var x = -1; x <= 1; x++)
            for (var z = -1; z <= 1; z++)
                Add(ob, x == 1 && z == 1 ? "LargeBlockBatteryBlock" : "LargeBlockArmorBlock", x, 0, z, 0, hull);
            // the merge block under the hull, its merge side down
            Add(ob, "LargeShipMergeBlock", 0, -1, 0, mergeBy, mergeBy).BlockOrientation = MergeFacing(Base6Directions.Direction.Down);
            ob.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled;
            // (as the game spawns an encounter's grid: the flag in its builder, registered for its claim as it enters the scene)
            ob.IsNpcSpawnedGrid = npcSpawned;
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.ChargeBatteries(grid);
            return grid;
        }

        /// <summary>The ship let down onto the station: its merge block (facing down) on the station's (facing up), gravity holding them together.</summary>
        private IEnumerator Press(MyCubeGrid station, MyCubeGrid ship, string what)
        {
            var body = Wait(() => ship.Physics != null, what + ": the ship's body", 10);
            while (body.MoveNext()) yield return body.Current;
            var a = station.GetFatBlocks().OfType<MyShipMergeBlock>().First();
            var b = ship.GetFatBlocks().OfType<MyShipMergeBlock>().First();
            // upright (it may have come down and tipped over while it waited) and its merge block onto the station's, in one
            // move: turned upright where it lay first, it was driven into the ground and thrown over
            var m = MatrixD.CreateWorld(Vector3D.Zero, _side, _up);
            var mergeInShip = Vector3D.TransformNormal(b.PositionComp.LocalMatrixRef.Translation, m);
            m.Translation = a.PositionComp.GetPosition() + _up * (station.GridSize + 0.05) - mergeInShip;
            ship.Teleport(m);
            ship.Physics.LinearVelocity = Vector3.Zero;
            ship.Physics.AngularVelocity = Vector3.Zero;
            var settle = WaitForSeconds(2, what + ": on the station");
            while (settle.MoveNext()) yield return settle.Current;
            Note($"MERGE | {what}: station normal {Vector3D.Dot(Vector3D.Normalize((Vector3)MergeNormal.Invoke(a, null)), _up):0.00} up, ship normal {Vector3D.Dot(Vector3D.Normalize((Vector3)MergeNormal.Invoke(b, null)), _up):0.00} up, ship up {Vector3D.Dot(ship.WorldMatrix.Up, _up):0.00}, station up {Vector3D.Dot(station.WorldMatrix.Up, _up):0.00}, ship static {ship.IsStatic}");
            Note($"MERGE | {what}: merge blocks {Vector3D.Distance(a.PositionComp.GetPosition(), b.PositionComp.GetPosition()):0.00} m apart, normals {Vector3D.Dot(Vector3D.Normalize((Vector3)MergeNormal.Invoke(a, null)), Vector3D.Normalize((Vector3)MergeNormal.Invoke(b, null))):0.00}");
        }

        private static readonly FieldInfo MergeForward = typeof(MyShipMergeBlock).GetField("m_forward", BindingFlags.Instance | BindingFlags.NonPublic);
        private Base6Directions.Direction? _mergeLocal;

        /// <summary>A block orientation that turns the merge block's own merge side (read off a probe once) to <paramref name="to"/> in its grid.</summary>
        private SerializableBlockOrientation MergeFacing(Base6Directions.Direction to)
        {
            if (_mergeLocal == null)
            {
                var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "probe", MyCubeSize.Large, true, _ground + _up * 200, Enumerable.Empty<BlockSpec>(), (Vector3)_side, (Vector3)_up);
                Add(ob, "LargeShipMergeBlock", 0, 0, 0, 0, 0);
                var probe = WorldApi.SpawnGrid(ob);
                var block = probe.GetFatBlocks().OfType<MyShipMergeBlock>().First();
                // in the block's own axes (the game turns it by the block's orientation: MyShipMergeBlock.UpdateState)
                _mergeLocal = (Base6Directions.Direction)MergeForward.GetValue(block);
                probe.Close();
            }
            foreach (Base6Directions.Direction f in Enum.GetValues(typeof(Base6Directions.Direction)))
            foreach (Base6Directions.Direction u in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                if (Base6Directions.GetAxis(f) == Base6Directions.GetAxis(u)) continue;
                var o = new MyBlockOrientation(f, u);
                if (o.TransformDirection(_mergeLocal.Value) == to) return new SerializableBlockOrientation(f, u);
            }
            throw new ScenarioFailedException("no orientation turns the merge side to " + to);
        }

        private static MyObjectBuilder_CubeBlock Add(MyObjectBuilder_CubeGrid ob, string subtype, int x, int y, int z, long owner, long builtBy)
        {
            var block = WorldApi.MakeBlockOb(subtype);
            block.Min = new SerializableVector3I(x, y, z);
            block.Owner = owner;
            block.BuiltBy = builtBy;
            ob.CubeBlocks.Add(block);
            return block;
        }
    }
}
