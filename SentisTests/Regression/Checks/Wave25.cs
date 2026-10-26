using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineers.Game.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using Torch.API.Managers;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>The NPC trader faction's founder of the world: an NPC identity to own a grid.</summary>
    internal static class Npcs
    {
        public static long Trader()
        {
            var faction = MySession.Static.Factions.Select(f => f.Value).OfType<MyFaction>()
                .FirstOrDefault(f => f.FactionTypeString == "Trader" && MySession.Static.Players.IdentityIsNpc(f.FounderId));
            return faction?.FounderId ?? 0;
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' <c>DisableBuildBlockOnNPC</c> (on in memory): a player's block on an NPC's grid
    /// (<c>BuildBlocksRequest</c>, the build menu) is not placed; the same on the player's own grid is.
    /// </summary>
    public sealed class NpcBuildRefusedCheck : RegressionCheck
    {
        public override string Name => Prefix + "npc_build_refused";
        private Scenarios.ConfigOverride _gameplay;

        protected override IEnumerator Check()
        {
            var npc = Npcs.Trader();
            SkipUnless(npc != 0, "no NPC trader faction in this world");
            yield return Arrive(Site.At(0, 4, 0));
            var theirs = BuildFor(npc, "npc-build-theirs", Site.Origin,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0));
            var mine = Build("npc-build-mine", Site.At(0, 0, 10), true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0));
            Put(Inventory(Body), "Component/SteelPlate", 30);
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("DisableBuildBlockOnNPC", true);

            var definition = Definition("CubeBlock/LargeBlockArmorBlock");
            var cell = new Vector3I(0, 1, 0);
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", Placing.Any);
            void Place(MyCubeGrid grid)
            {
                var location = new MyCubeGrid.MyBlockLocation(definition.Id, cell, cell, cell, Quaternion.Identity, 0, Identity);
                StandAt(grid.GridIntegerToWorld(new Vector3I(0, 2, 2)), grid.GridIntegerToWorld(cell));
                AsMe(() => request.Invoke(grid, new object[] { Placing.Visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, Body.EntityId, false, Identity }));
            }

            Place(theirs);
            yield return Seconds(2);
            Check(theirs.GetCubeBlock(cell) == null, "the player's block was placed on the NPC's grid");
            Place(mine);
            yield return Until(() => mine.GetCubeBlock(cell) != null ? 1 : 0, v => v > 0, "the block placed on the player's own grid", 10);
            Result("on the NPC's grid the block was not placed; on the player's own it was");
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' <c>DisableLockLandingGearOnNPCShips</c> and <c>DisableLockLandingGearOnEnemyShips</c>
    /// (on in memory): a landing gear of the player's small ship over a floor locks onto the player's own floor, not onto
    /// an NPC's, not onto an enemy player's. The way the gear faces is found over the player's floor first.
    /// </summary>
    public sealed class LandingGearRefusedCheck : RegressionCheck
    {
        public override string Name => Prefix + "landing_gear_refused";
        public override SiteKind Kind => SiteKind.Planet;
        private Scenarios.ConfigOverride _gameplay;

        protected override IEnumerator Check()
        {
            var npc = Npcs.Trader();
            SkipUnless(npc != 0, "no NPC trader faction in this world");
            yield return Arrive(Site.At(0, 4, -10));
            var enemy = -1;
            yield return Join(Site.At(0, 10, 40), i => enemy = i);
            var enemyId = Sandbox.Game.World.MySession.Static.Players.TryGetIdentityId(FakeClients.PlayerOf(enemy).Id.SteamId);
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("DisableLockLandingGearOnNPCShips", true);
            _gameplay.Set("DisableLockLandingGearOnEnemyShips", true);

            // a floor of an owner: an owned block in it makes it theirs (armor has no owner)
            MyCubeGrid Floor(string name, Vector3D at, long owner) => Build(name, at, true, new[]
            {
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 1), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 1),
                new Part("CargoContainer/LargeBlockSmallContainer", 3, 0, 0),
            }, ownedBy: owner);
            var floors = new[]
            {
                // 6 m over the ground: the gear reaches no voxels by the floor
                ("the player's own", Floor("gear-floor-mine", Site.At(0, 6, 0), Identity)),
                ("an NPC's", Floor("gear-floor-npc", Site.At(30, 6, 0), npc)),
                ("an enemy player's", Floor("gear-floor-enemy", Site.At(-30, 6, 0), enemyId)),
            };

            Base6Directions.Direction? facing = null;
            var results = new List<string>();
            foreach (var (whose, floor) in floors)
            {
                var top = floor.GridIntegerToWorld(new Vector3I(0, 0, 0)) + Site.Up * (1.25 + 0.05);
                var faces = facing.HasValue ? new[] { facing.Value } : new[] { Base6Directions.Direction.Down, Base6Directions.Direction.Forward, Base6Directions.Direction.Backward, Base6Directions.Direction.Up, Base6Directions.Direction.Left, Base6Directions.Direction.Right };
                var locked = false;
                string lockedTo = null;
                foreach (var face in faces)
                {
                    // a container on the far side of the gear: a ship of a gear alone has no owner for the game
                    var gearPart = new Part("LandingGear/LargeBlockLandingGear", 0, 0, 0).Facing(face, Base6Directions.GetPerpendicular(face));
                    var gearCells = Cells(gearPart);
                    var back = -Base6Directions.GetIntVector(face);
                    var free = gearCells.Select(c => c + back).First(c => !gearCells.Contains(c));
                    var ship = Build("gear-" + whose.Split(' ').Last(), top, false, new[]
                    {
                        gearPart,
                        new Part("CargoContainer/LargeBlockSmallContainer", free.X, free.Y, free.Z),
                    });
                    var gear = (IMyLandingGear)Block<SpaceEngineers.Game.Entities.Blocks.MyLandingGear>(ship);
                    // the ship's owners are counted a frame or so after it is made (the plugin looks at them)
                    yield return Until(() => ship.BigOwners.Contains(Identity) ? 1 : 0, v => v > 0, "the ship the player's", 5,
                        () => "owners " + string.Join(",", ship.BigOwners) + " of " + ship.BlocksCount + " blocks (" + string.Join(", ", ship.GetFatBlocks().Select(b => b.BlockDefinition.Id.SubtypeName + " " + b.OwnerId)) + "), the player " + Identity);
                    var within = Within(4);
                    while (within() && !gear.IsLocked)
                    {
                        gear.Lock();
                        yield return null;
                    }
                    // locked onto that floor: on a planet site the gear can reach the ground by the floor too (it took the
                    // planet's voxels by the enemy's floor, and the check read that as the floor - 25.10.2026)
                    var held = ((SpaceEngineers.Game.ModAPI.IMyLandingGear)gear).GetAttachedEntity() as VRage.Game.Entity.MyEntity;
                    locked = gear.IsLocked && ReferenceEquals(held?.GetTopMostParent(), floor);
                    if (locked) lockedTo = AttachedTo(gear);
                    if (locked || facing.HasValue)
                    {
                        facing = face;
                        if (locked) gear.Unlock();
                        yield return CloseAndWait(ship);
                        break;
                    }
                    yield return CloseAndWait(ship);
                }
                Check(facing.HasValue, "the gear locked onto the player's own floor in no way it can face");
                results.Add(whose + (locked ? ": locked" : ": not locked"));
                if (whose == "the player's own") Check(locked, "the gear did not lock onto the player's own floor");
                else Check(!locked, "the gear locked onto " + whose + " floor (onto " + lockedTo + "; " + Relations(enemyId, floor) + ")");
            }
            Result(string.Join("; ", results));
        }

        private static string AttachedTo(IMyLandingGear gear)
        {
            var e = ((SpaceEngineers.Game.ModAPI.IMyLandingGear)gear).GetAttachedEntity() as VRage.Game.Entity.MyEntity;
            return e == null ? "nothing" : e.GetType().Name + " '" + e.DisplayName + "'";
        }

        /// <summary>Who the floor is whose, and what the player is to them: the plugin refuses an enemy's.</summary>
        private string Relations(long enemyId, MyCubeGrid floor)
        {
            var factions = Sandbox.Game.World.MySession.Static.Factions;
            string Faction(long id) => factions.TryGetPlayerFaction(id)?.Tag ?? "none";
            var floorOwner = floor.BigOwners.Count > 0 ? floor.BigOwners[0] : 0;
            return $"the floor's owner {floorOwner} (faction {Faction(floorOwner)}), the player {Identity} (faction {Faction(Identity)}), the enemy {enemyId}; " +
                   $"the player to the floor's owner: {Player.GetRelationTo(floorOwner)}";
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' <c>ConnectorDestroyInsteadThrow</c> (on in memory): a connector set to throw out
    /// destroys the ore and the ingots in it instead of throwing them; what else is in it (plates) it throws out. Not in
    /// the suite: the setting is the whole server's, and the connectors of the checks beside it would destroy their ore too.
    /// </summary>
    public sealed class ConnectorDestroyCheck : RegressionCheck
    {
        public override string Name => Prefix + "connector_destroy";
        private Scenarios.ConfigOverride _gameplay;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            var grid = Build("connector-destroy", Site.Origin, true,
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 3).With(b => Motion.Charged(b)));
            var connector = Block<MyShipConnector>(grid);
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("ConnectorDestroyInsteadThrow", true);
            var inventory = Inventory(connector);
            Put(inventory, "Ore/Stone", 100);
            Put(inventory, "Ingot/Iron", 50);
            Put(inventory, "Component/SteelPlate", 20);
            var near = connector.PositionComp.GetPosition();
            Func<string, double> thrown = subtype => MyEntities.GetEntities().OfType<MyFloatingObject>()
                .Where(o => !o.MarkedForClose && Vector3D.Distance(o.PositionComp.GetPosition(), near) < 40 && o.Item.Content.SubtypeName == subtype)
                .Sum(o => (double)o.Item.Amount);
            ((Sandbox.ModAPI.IMyTerminalBlock)connector).SetValueBool("ThrowOut", true);
            yield return Until(() => thrown("SteelPlate"), v => v > 0, "plates thrown out of the connector", 30, () => State(connector));
            yield return Until(() => Amount(inventory, "Ore/Stone") + Amount(inventory, "Ingot/Iron"), v => v < 0.01, "the ore and the ingots gone from the connector, kg", 30);
            foreach (var floating in MyEntities.GetEntities().OfType<MyFloatingObject>().Where(o => Vector3D.Distance(o.PositionComp.GetPosition(), near) < 40).ToList())
                Track(floating);
            Check(thrown("Stone") < 0.01, "stone was thrown out (" + F(thrown("Stone")) + " kg), not destroyed");
            Check(thrown("Iron") < 0.01, "iron ingots were thrown out (" + F(thrown("Iron")) + " kg), not destroyed");
            Result("the connector destroyed 100 kg of stone and 50 kg of iron ingots and threw " + F(thrown("SteelPlate")) + " plates out");
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' <c>RaycastLimit</c> (60 m in memory, for a camera made then): the camera's scan of 100
    /// m finds nothing of the block 80 m ahead; the block 40 m ahead it finds. The game's own limit of the camera's
    /// definition is put back after. Not in the suite: the limit is the camera definition's, every camera of it has it.
    /// </summary>
    public sealed class RaycastLimitCheck : RegressionCheck
    {
        public override string Name => Prefix + "raycast_limit";
        private Scenarios.ConfigOverride _gameplay;
        private Sandbox.Definitions.MyCameraBlockDefinition _definition;
        private double _limit;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            _definition = (Sandbox.Definitions.MyCameraBlockDefinition)Definition("CameraBlock/LargeCameraBlock");
            _limit = _definition.RaycastDistanceLimit;
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("RaycastLimit", 60);
            var grid = Build("raycast-limit", Site.Origin, true,
                new Part("CameraBlock/LargeCameraBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)));
            var camera = (Sandbox.ModAPI.Ingame.IMyCameraBlock)Block<MyCameraBlock>(grid);
            var ahead = ((MyCameraBlock)camera).WorldMatrix.Forward;
            var from = ((MyCameraBlock)camera).PositionComp.GetPosition();
            var far = Build("raycast-far", from + ahead * 80, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            camera.EnableRaycast = true;
            yield return Until(() => camera.AvailableScanRange, v => v >= 60, "the camera's scan range charging, m", 15, () => State((MyCameraBlock)camera));
            var hit = camera.Raycast(100, 0, 0);
            Check(hit.IsEmpty() || hit.EntityId != far.EntityId, "the camera found the block 80 m ahead with a limit of 60 m");
            Check(camera.RaycastDistanceLimit <= 60.01, "the camera's limit is " + F(camera.RaycastDistanceLimit) + " m, not 60");
            var near = Build("raycast-near", from + ahead * 40, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            yield return Until(() => camera.AvailableScanRange, v => v >= 50, "the camera's scan range charging again, m", 15);
            hit = camera.Raycast(50, 0, 0);
            Check(!hit.IsEmpty() && hit.EntityId == near.EntityId, "the camera did not find the block 40 m ahead");
            Result("with a limit of 60 m the camera's scan found nothing at 80 m and the block at 40 m");
        }

        public override void Cleanup()
        {
            if (_definition != null) _definition.RaycastDistanceLimit = _limit;
            _gameplay?.Restore();
            base.Cleanup();
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' <c>EnableCheckBeacon</c> (on in memory): a block placed on a grid with no beacon
    /// (<c>BuildBlocksRequest</c>) puts a warning on the screens of the players by it, two seconds later; the same on a
    /// grid with a beacon puts none.
    /// </summary>
    public sealed class BeaconWarningCheck : RegressionCheck
    {
        public override string Name => Prefix + "beacon_warning";
        private Scenarios.ConfigOverride _gameplay;
        private static Torch.Managers.PatchManager.PatchContext _context;
        private static readonly ConcurrentQueue<KeyValuePair<long, string>> Shown = new ConcurrentQueue<KeyValuePair<long, string>>();

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, 0));
            Watch();
            var bare = Build("beacon-bare", Site.Origin, true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0));
            var lit = Build("beacon-lit", Site.At(0, 0, 12), true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("Beacon/LargeBlockBeacon", 1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 3, 0, 0).With(b => Motion.Charged(b)));
            Put(Inventory(Body), "Component/SteelPlate", 30);
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("EnableCheckBeacon", true);
            var definition = Definition("CubeBlock/LargeBlockArmorBlock");
            var cell = new Vector3I(0, 1, 0);
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", Placing.Any);
            IEnumerator Place(MyCubeGrid grid)
            {
                while (Shown.TryDequeue(out _)) { }
                var location = new MyCubeGrid.MyBlockLocation(definition.Id, cell, cell, cell, Quaternion.Identity, 0, Identity);
                StandAt(grid.GridIntegerToWorld(new Vector3I(0, 2, 2)), grid.GridIntegerToWorld(cell));
                AsMe(() => request.Invoke(grid, new object[] { Placing.Visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, Body.EntityId, false, Identity }));
                yield return Until(() => grid.GetCubeBlock(cell) != null ? 1 : 0, v => v > 0, "the block placed on " + grid.DisplayName, 10);
                // the plugin looks two seconds after the request
                yield return Seconds(4);
            }

            yield return Place(bare);
            var warned = Shown.Where(s => s.Key == Identity && s.Value.Contains(bare.DisplayName)).Select(s => s.Value).ToList();
            Check(warned.Count > 0, "no warning on the player's screen for a block on a grid with no beacon");
            yield return Place(lit);
            var wrongly = Shown.Where(s => s.Key == Identity && s.Value.Contains(lit.DisplayName)).Select(s => s.Value).ToList();
            Check(wrongly.Count == 0, "a warning for a block on a grid with a beacon: " + string.Join(" / ", wrongly));
            Result("no beacon: \"" + warned[0] + "\"; with a beacon: no warning");
        }

        // the lines on the players' screens (by a Torch prefix: the translator has one there too)
        private static void Watch()
        {
            if (_context != null) return;
            var patches = SentisTestsPlugin.TorchInstance.Managers.GetManager<Torch.Managers.PatchManager.PatchManager>();
            var show = typeof(Sandbox.Game.MyVisualScriptLogicProvider).GetMethod("ShowNotification", new[] { typeof(string), typeof(int), typeof(string), typeof(long) });
            _context = patches.AcquireContext();
            _context.GetPattern(show).Prefixes.Add(typeof(BeaconWarningCheck).GetMethod(nameof(ShowPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            patches.Commit();
        }

        private static void ShowPrefix(string message, long playerId)
        {
            if (message != null) Shown.Enqueue(new KeyValuePair<long, string>(playerId, message));
            while (Shown.Count > 200 && Shown.TryDequeue(out _)) { }
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }
}
