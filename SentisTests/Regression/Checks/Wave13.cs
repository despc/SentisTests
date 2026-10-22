using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>A drone: an AI flight block, a gyroscope, a battery and a small thruster each way, plus the blocks given.</summary>
    internal static class Drones
    {
        public static List<Part> Parts(params Part[] more)
        {
            var parts = new List<Part>
            {
                new Part("FlightMovementBlock/LargeFlightMovement", 0, 0, 0),
                new Part("Gyro/LargeBlockGyro", -1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)),
            };
            foreach (var (face, x, y, z) in new[]
                     {
                         (Base6Directions.Direction.Right, 2, 0, 0), (Base6Directions.Direction.Left, -3, 0, 0), (Base6Directions.Direction.Up, 0, 1, 0),
                         (Base6Directions.Direction.Down, 0, -2, 0), (Base6Directions.Direction.Backward, 0, 0, 1), (Base6Directions.Direction.Forward, 0, 0, -2),
                     })
                parts.Add(new Part("Thrust/LargeBlockSmallThrust", x, y, z).Facing(face, Base6Directions.GetPerpendicular(face)));
            parts.AddRange(more);
            return parts;
        }

        public static IEnumerator Activate(MyCubeBlock block)
        {
            var terminal = (Sandbox.ModAPI.IMyTerminalBlock)block;
            // an AI block raises its terminal controls as it comes up; under the suite's load that can lag a few frames
            for (var frame = 0; frame < 120 && terminal.GetProperty("ActivateBehavior") == null; frame++)
                yield return null;
            if (terminal.GetProperty("ActivateBehavior") == null)
                throw new Core.ScenarioFailedException(block.BlockDefinition.Id.SubtypeName + " has no \"ActivateBehavior\" in its terminal");
            terminal.SetValueBool("ActivateBehavior", true);
        }

        public static string Info(MyCubeBlock block) => ((Sandbox.ModAPI.IMyTerminalBlock)block).DetailedInfo.Trim().Replace("\n", " ");
    }

    /// <summary>
    /// An offensive combat block on a drone with an AI flight block finds an enemy station 400 m off (another player's)
    /// and flies the drone at it.
    /// </summary>
    public sealed class OffensiveCombatCheck : RegressionCheck
    {
        public override string Name => Prefix + "offensive_combat";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            var enemy = -1;
            var target = Site.At(400, 0, 0);
            yield return Join(target + Site.Up * 10 + Site.Forward * 10, i => enemy = i);
            var station = BuildFor(FakeClients.PlayerOf(enemy).Identity.IdentityId, "offensive-target", target,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var drone = Build("offensive-drone", Site.Origin, false, Drones.Parts(new Part("OffensiveCombatBlock/LargeOffensiveCombat", 1, 1, 0)));
            yield return null;
            var offensive = drone.GetFatBlocks().FirstOrDefault(b => b is Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock);
            Check(offensive != null, "no offensive combat block on the drone: " + string.Join(", ", drone.GetFatBlocks().Select(b => b.BlockDefinition.Id.ToString())));
            var ingame = (Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock)offensive;
            yield return Drones.Activate((MyCubeBlock)drone.GetCubeBlock(Vector3I.Zero).FatBlock);
            yield return Drones.Activate(offensive);
            Func<bool> onStation = () => ingame.SearchEnemyComponent?.FoundEnemyId is long id && MyEntities.GetEntityById(id)?.GetTopMostParent() == station;
            yield return Until(() => onStation() ? 1 : 0, v => v > 0, "the offensive block finding the enemy station", 30,
                () => "found " + ingame.SearchEnemyComponent?.FoundEnemyId + "; " + Drones.Info(offensive));
            Func<double> off = () => Vector3D.Distance(drone.PositionComp.WorldAABB.Center, station.PositionComp.WorldAABB.Center);
            var start = off();
            yield return Until(off, v => v < start - 100, "the drone flying at the enemy station, m off it", 60, () => F(Motion.Speed(drone)) + " m/s; " + Drones.Info(offensive));
            Result("the offensive combat block found the enemy station and flew the drone from " + F(start) + " m to " + F(off()) + " m off it");
        }
    }

    /// <summary>
    /// The world's block limit by tag (drills, per player): with the player's drills at the limit, one more is refused to
    /// them; with one of them gone, it is placed.
    /// </summary>
    public sealed class BlockLimitsCheck : RegressionCheck
    {
        public override string Name => Prefix + "block_limits";

        /// <summary>Up to 3 s of frames for the new grid to turn up.</summary>
        private static IEnumerator Look(Func<MyCubeGrid> find)
        {
            for (var f = Frames(3); f > 0 && find() == null; f--) yield return null;
        }

        protected override IEnumerator Check()
        {
            var settings = MySession.Static.Settings;
            SkipUnless(settings.BlockLimitsEnabled == MyBlockLimitsEnabledEnum.PER_PLAYER && settings.BlockTypeLimits?.Dictionary != null
                       && settings.BlockTypeLimits.Dictionary.ContainsKey("Drills"), "no per-player limit on drills in this world");
            var limit = settings.BlockTypeLimits.Dictionary["Drills"];
            SkipUnless(limit <= 200, "the drills' limit is " + limit + ", too many to build");
            yield return Arrive(Site.At(0, 6, -6));
            var drill = "Drill/SmallBlockDrill";
            var size = Definition(drill).Size;
            var parts = new List<Part>();
            for (var i = 0; i < limit - 1; i++)
                parts.Add(new Part(drill, i % 10 * size.X, 0, i / 10 * size.Z));
            var grid = Build("limit-drills", Site.Origin, true, parts, MyCubeSize.Small, ownedBy: Identity);
            yield return Seconds(1);
            var definition = Definition(drill);
            var inventory = Inventory(Body);
            foreach (var component in definition.Components)
                inventory.AddItems(component.Count * 2, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(component.Definition.Id));
            var identity = MySession.Static.Players.TryGetIdentity(Identity);
            Func<string> counted = () => string.Join(", ", identity.BlockLimits.BlockTypeBuilt.Select(t => t.Key + " " + t.Value.BlocksBuilt));
            // the drills put on armor by the back, as a player adds a block to a grid (MyCubeGrid.BuildBlocksRequest: the
            // server checks the limits there; a new grid in space, MyCubeBuilder.RequestGridSpawn, it does not - the client does)
            var mount = Build("limit-mount", Site.Origin + Site.Up * 10, true,
                new[] { new Part("CubeBlock/SmallBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/SmallBlockArmorBlock", 6, 0, 0) }, MyCubeSize.Small);
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", Placing.Any);
            Func<int, IEnumerator> place = x =>
            {
                var min = new Vector3I(x - 1, -1, -size.Z);
                var location = new MyCubeGrid.MyBlockLocation(definition.Id, min, min + size - Vector3I.One, min + definition.Center, Quaternion.Identity, 0, Identity);
                StandAt(mount.GridIntegerToWorld(min) + Site.Up * 3, mount.GridIntegerToWorld(min));
                AsMe(() => request.Invoke(mount, new object[] { Placing.Visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, Body.EntityId, false, Identity }));
                return Seconds(2);
            };
            Func<int, bool> placed = x => mount.GetCubeBlock(new Vector3I(x, 0, -1))?.BlockDefinition.Id == definition.Id;
            yield return place(0);
            Check(placed(0), "the player's drill number " + limit + " (the limit) was refused; counted: " + counted());
            yield return place(6);
            Check(!placed(6), "a drill past the limit of " + limit + " was placed; counted: " + counted());
            Result("drill number " + limit + " (the limit by tag) placed onto a grid, one more refused; counted: " + counted());
        }
    }

    /// <summary>
    /// The AI basic task "Follow player": a drone with it and an AI flight block, the player 120 m off pressing "Follow me",
    /// flies to the player.
    /// </summary>
    public sealed class BasicFollowPlayerCheck : RegressionCheck
    {
        public override string Name => Prefix + "basic_follow_player";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            var spot = Site.At(120, 0, 0);
            yield return Arrive(spot);
            Body.JetpackComp?.TurnOnJetpack(true);
            var drone = Build("follow-drone", Site.Origin, false, Drones.Parts(new Part("BasicMissionBlock/LargeBasicMission", 1, 1, 0)));
            yield return null;
            var block = drone.GetFatBlocks().FirstOrDefault(b => b is Sandbox.ModAPI.Ingame.IMyBasicMissionBlock);
            Check(block != null, "no basic task block on the drone: " + string.Join(", ", drone.GetFatBlocks().Select(b => b.BlockDefinition.Id.ToString())));
            var follow = block.Components.Get<SpaceEngineers.Game.EntityComponents.Blocks.MyBasicMissionFollowPlayer>();
            Check(follow != null, "the basic task block has no \"Follow player\" task");
            ((Sandbox.ModAPI.Ingame.IMyBasicMissionBlock)block).SelectedMissionId = follow.UniqueSelectionId;
            yield return Drones.Activate((MyCubeBlock)drone.GetCubeBlock(Vector3I.Zero).FatBlock);
            yield return Drones.Activate(block);
            yield return Seconds(1);
            Request(follow, "FollowPlayer");
            Func<double> off = () => Vector3D.Distance(drone.PositionComp.WorldAABB.Center, Body.PositionComp.GetPosition());
            var start = off();
            yield return Until(off, v => v < 50, "the drone following the player, m off them", 90, () => F(Motion.Speed(drone)) + " m/s; " + Drones.Info(block));
            Result("\"Follow me\" pressed 120 m off: the drone flew from " + F(start) + " m to " + F(off()) + " m of the player");
        }
    }

    /// <summary>
    /// The AI recorder: a waypoint 150 m off added to a drone's recorder, "Play" pressed: the drone flies there by its AI
    /// flight block.
    /// </summary>
    public sealed class PathRecorderCheck : RegressionCheck
    {
        public override string Name => Prefix + "path_recorder";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            var drone = Build("recorder-drone", Site.Origin, false, Drones.Parts(new Part("PathRecorderBlock/LargePathRecorderBlock", 1, 1, 0)));
            yield return null;
            var block = drone.GetFatBlocks().FirstOrDefault(b => b is Sandbox.ModAPI.Ingame.IMyPathRecorderBlock);
            Check(block != null, "no recorder on the drone: " + string.Join(", ", drone.GetFatBlocks().Select(b => b.BlockDefinition.Id.ToString())));
            var recorder = block.Components.Get<Sandbox.Game.EntityComponents.MyPathRecorderComponent>();
            Check(recorder != null, "the recorder block has no recorder component");
            var target = Site.At(150, 0, 0);
            // the waypoint as the recorder's own event puts it (the mod API's AddWaypoint raises it to the clients only)
            Request(recorder, "AddWaypointBroadcast", MatrixD.CreateWorld(target, Site.Forward, Site.Up));
            var points = new List<Sandbox.ModAPI.Ingame.IMyAutopilotWaypoint>();
            recorder.GetWaypoints(points);
            Check(points.Count == 1, "the recorder has " + points.Count + " waypoints after one was added");
            yield return Drones.Activate((MyCubeBlock)drone.GetCubeBlock(Vector3I.Zero).FatBlock);
            yield return Drones.Activate(block);
            recorder.Play();
            Func<double> off = () => Vector3D.Distance(drone.PositionComp.WorldAABB.Center, target);
            yield return Until(off, v => v < 20, "the drone flying the recorded path, m off its waypoint", 90, () => F(Motion.Speed(drone)) + " m/s; " + Drones.Info(block));
            Result("the recorder played its waypoint 150 m off: the drone flew there (" + F(off()) + " m from it)");
        }
    }
}
