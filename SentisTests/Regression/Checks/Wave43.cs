using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A jump drive carries a ship out of its cluster of physics and back: up 7 km to open space where no cluster is
    /// (SentisClusters' clusters are 5 km wide), then back onto its station. After every jump the ship has to answer the
    /// pilot as before - its new cluster's thread took it over: it moves on thrust, and its body's cluster id is its own
    /// up there and the station's again when back.
    /// </summary>
    public sealed class JumpAcrossClustersCheck : RegressionCheck
    {
        public override string Name => Prefix + "jump_across_clusters";
        public override bool NeedsOwnCluster => true;
        public override int TimeoutSeconds => 240;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 8));
            var definition = (MyJumpDriveDefinition)Definition("JumpDrive/LargeJumpDrive");
            var station = Build("jump-station", Site.At(30, 0, 0), true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)));
            // reg_ship_flight's ship with the drive by its side: the drive is 3x3x2, cells 2..4 across, clear of the
            // seat and the thrusters (a block over another one is left out of the grid)
            var ship = Build("jump-clusters", Site.Origin, false,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, -1, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, 1, 0),
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Direction.Forward),
                new Part("JumpDrive/LargeJumpDrive", 2, 0, 0).With(b => ((MyObjectBuilder_JumpDrive)b).StoredPower = definition.PowerNeededForJump));
            Check(Blocks<MyThrust>(ship).Count() == 2 && ship.CubeBlocks.Count == 7, "the ship came out with " + ship.CubeBlocks.Count + " of its 7 blocks");
            var drive = Block<MyJumpDrive>(ship);
            yield return Until(() => drive.IsWorking ? 1 : 0, v => v > 0, "the jump drive working", 10, () => State(drive));
            var seat = Block<MyCockpit>(ship);
            yield return Sit(seat);
            var stationId = ClusterId(station);
            var before = ClusterId(ship);

            // out: 7 km up, past the edge of every cluster round the site (a cluster is 5 km wide, its box half of it
            // on every side) - the ship lands where no cluster was
            var from = ship.PositionComp.GetPosition();
            var target = from + Site.Up * 7000;
            Request(ship, "OnJumpRequested", target, Identity, 10f, (long?)null);
            yield return Until(() => ship.GridSystems.JumpSystem.IsJumping ? 1 : 0, v => v > 0, "the jump counting down", 5);
            yield return Until(() => Vector3D.Distance(ship.PositionComp.GetPosition(), from), v => v > 6500, "the ship jumped up, m", 30);
            yield return Seconds(1);
            var off = ship.PositionComp.GetPosition() - from;
            var upMiss = Math.Abs(Vector3D.Dot(off, Site.Up) - 7000);
            var sideMiss = (off - Vector3D.Dot(off, Site.Up) * Site.Up).Length();
            Check(upMiss < 500 && sideMiss < 500, "the ship jumped to " + F(off.Length() / 1000) + " km up, off the asked spot by " +
                  F(sideMiss) + " m sideways and " + F(upMiss) + " m in height");
            var far = ClusterId(ship);
            Check(far >= 0 && far != stationId, "the ship 7 km up is in cluster " + far + ", the station's is " + stationId + ": no new cluster");
            // the new cluster's thread took the ship over: it answers the pilot
            yield return HoldShip(3, seat, new Vector3(0, 0, -1));
            var speed = Motion.Speed(ship);
            Check(speed > 0.5, "the ship sits dead in its new cluster: " + F(speed) + " m/s after 3 s of thrust (" +
                  PhysicsState(ship) + ")");
            yield return HoldShip(2, seat, Vector3.Zero);

            // back: charged again by hand (the ride back is the point, not the charging), onto the station
            Check(Recharge(drive, definition), "MyJumpDrive.CurrentStoredPower is not in this game: the drive cannot be charged for the way back");
            yield return Until(() => drive.CanJumpAndHasAccess(Identity) ? 1 : 0, v => v > 0, "the drive ready to jump back", 10, () => State(drive));
            Request(ship, "OnJumpRequested", station.PositionComp.GetPosition() + Site.Up * 10, Identity, 10f, (long?)null);
            yield return Until(() => ship.GridSystems.JumpSystem.IsJumping ? 1 : 0, v => v > 0, "the jump back counting down", 5);
            // the game ends a jump short of another grid in the way (about 1 km off the station): well within its cluster
            yield return Until(() => Vector3D.Distance(ship.PositionComp.GetPosition(), station.PositionComp.GetPosition()), v => v < 1500,
                "the ship jumped back to the station, m off it", 30);
            yield return Until(() => ClusterId(ship) == ClusterId(station) ? 1 : 0, v => v > 0, "the ship back in the station's cluster", 10,
                () => "the ship in cluster " + ClusterId(ship) + ", the station in " + ClusterId(station));
            var back = ClusterId(ship);
            Result("jumped 7 km up out of the station's cluster and back: cluster " + stationId + " (station), " + before + " (at the site), " +
                   far + " (up there, " + F(speed) + " m/s on thrust there), " + back + " (back at the station)");
        }

        /// <summary>Charges the drive full, as the check charged it at the start: the drive's own (private) stored power.</summary>
        private static bool Recharge(MyJumpDrive drive, MyJumpDriveDefinition definition)
        {
            var power = typeof(MyJumpDrive).GetProperty("CurrentStoredPower", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (power == null || !power.CanWrite) return false;
            power.SetValue(drive, definition.PowerNeededForJump);
            return true;
        }
    }

    /// <summary>
    /// One player respawned twice, once per station 7 km apart: dead by the station of the site, respawned at it; dead
    /// again, respawned at the station 7 km up - a cluster away, where no cluster of theirs was. The new astronaut stands
    /// at the far station and flies on the jetpack as any fresh one does: its character updates in the new cluster.
    /// </summary>
    public sealed class RespawnAcrossClustersCheck : RegressionCheck
    {
        public override string Name => Prefix + "respawn_across_clusters";
        public override bool NeedsOwnCluster => true;
        public override int TimeoutSeconds => 240;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            var here = Build("respawn-here", Site.Origin, true,
                new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var far = Build("respawn-far", Site.At(0, 7000, 0), true,
                new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var hereRoom = Block<MyMedicalRoom>(here);
            var farRoom = Block<MyMedicalRoom>(far);
            yield return Until(() => hereRoom.IsWorking && farRoom.IsWorking ? 1 : 0, v => v > 0, "both medical rooms working", 15,
                () => State(hereRoom) + "; " + State(farRoom));

            // died and respawned at the near station (the plain respawn check's path, kept short)
            var first = RespawnAt(hereRoom);
            yield return first;
            var nearOff = Vector3D.Distance(Body.PositionComp.GetPosition(), hereRoom.PositionComp.GetPosition());
            Check(nearOff < 10, "the astronaut respawned " + F(nearOff) + " m from the near medical room");

            // died again, asked for the far one: a cluster away
            yield return RespawnAt(farRoom);
            var farOff = Vector3D.Distance(Body.PositionComp.GetPosition(), farRoom.PositionComp.GetPosition());
            Check(farOff < 10, "the astronaut respawned " + F(farOff) + " m from the far medical room 7 km up");
            // a static grid makes no cluster: the one up there is the new astronaut's, and the far station is in it
            yield return Until(() => ClusterId(Body) >= 0 && ClusterId(Body) == ClusterId(far) ? 1 : 0, v => v > 0, "the astronaut and the far station in one cluster", 10,
                () => "the astronaut in cluster " + ClusterId(Body) + ", the far station in " + ClusterId(far) + ", the near one in " + ClusterId(here));
            Check(ClusterId(here) != ClusterId(Body), "the near station 7 km down is in the astronaut's cluster " + ClusterId(Body));

            // who controls what, now and then (the astronaut uncontrolled: SentisOptimisations leaves its cluster unstepped)
            Note("after the far respawn: " + Control());
            // the new astronaut flies there as a fresh one does: the jetpack answers in the new cluster
            FakeClients.SetFree(Me, true);
            yield return Hold(1, Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
            Check(Body.JetpackComp?.TurnedOn == true, "the jetpack did not come on at the far station");
            var from = Body.PositionComp.GetPosition();
            yield return Hold(3, new Vector3(0, 0, -1), jetpack: true, dampeners: true, face: Site.Forward);
            var gone = Vector3D.Dot(Body.PositionComp.GetPosition() - from, Site.Forward);
            Note("after the flight: " + Control());
            Check(gone > 3, "3 s on the jetpack at the far station took the astronaut " + F(gone) + " m forward (speed " + F(Motion.Speed(Body)) + " m/s); " + CharacterState(Body));
            Result("respawned twice across a cluster border: " + F(nearOff) + " m at the near station, " + F(farOff) + " m at the far one 7 km up, " +
                   "flew " + F(gone) + " m on the jetpack there; clusters " + ClusterId(here) + " and " + ClusterId(far));
        }

        private string Control()
        {
            var controlled = Player?.Controller?.ControlledEntity as VRage.Game.Entity.MyEntity;
            return "the player controls " + (controlled == null ? "nothing" : controlled.GetType().Name + " " + controlled.EntityId) + ", their character " + Body?.EntityId +
                   " controlled by " + (Body?.ControllerInfo?.Controller?.Player?.DisplayName ?? "nobody") + ", the player's character " + Player?.Character?.EntityId;
        }

        /// <summary>Kills the player and asks to respawn at the block, waiting for the new astronaut.</summary>
        private IEnumerator RespawnAt(MyMedicalRoom at)
        {
            var body = Body;
            body.DoDamage(1000f, MyDamageType.Suicide, true, attackerId: 0);
            yield return Until(() => body.IsDead ? 1 : 0, v => v > 0, "the player dead", 10);
            var message = new Sandbox.Game.Multiplayer.MyPlayerCollection.RespawnMsg
            {
                JoinGame = false,
                NewIdentity = false,
                RespawnEntityId = at.EntityId,
                RespawnShipId = null,
                PlayerSerialId = 0,
                Color = Color.Red,
            };
            Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "OnRespawnRequest", message);
            yield return Until(() => Body != null && Body != body && !Body.IsDead ? 1 : 0, v => v > 0, "a new astronaut at " + at.DisplayName, 20,
                () => "the player's character " + (Player.Character == null ? "none" : Player.Character == body ? "the dead one" : Player.Character.EntityId + (Player.Character.IsDead ? " dead" : "")));
            Track(Body);
            Note("respawned at " + at.DisplayName + ": " + Control());
        }
    }

    /// <summary>
    /// A ship flown from its station past the edge of the station's cluster and back by the same pilot: out 5.5 km (a
    /// cluster is 5 km wide), back to the station. Through the border both ways the ship keeps answering the controls -
    /// one thread updating it hands it to another and the handover loses nothing; its body's cluster id tells where the
    /// game put it on every leg.
    /// </summary>
    public sealed class FlightClusterBorderCheck : RegressionCheck
    {
        public override string Name => Prefix + "flight_cluster_border";
        public override bool NeedsOwnCluster => true;
        public override int TimeoutSeconds => 360;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 4));
            var station = Build("border-station", Site.At(40, 0, 0), true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)));
            // the ship faces up: up over the site is empty (the next site is 8 km off sideways, a cluster's reach from
            // a ship 5.5 km out that way)
            var ship = Build("border-flight", Site.Origin, false, new[] {
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, -1, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", -1, -1, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, 1, 0),
                // small thrusters (1x1x2) side by side, two each way: a large one (3x2x4) would stand over the seat
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", 2, 0, 0).Facing(Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Direction.Forward),
                new Part("Thrust/LargeBlockSmallThrust", -2, 0, 0).Facing(Direction.Forward),
            }, forward: Site.Up, up: -Site.Forward);
            Check(Blocks<MyThrust>(ship).Count() == 4 && ship.CubeBlocks.Count == 9, "the ship came out with " + ship.CubeBlocks.Count + " of its 9 blocks");
            var seat = Block<MyCockpit>(ship);
            yield return Sit(seat);
            Func<double> off = () => Vector3D.Distance(ship.PositionComp.GetPosition(), station.PositionComp.GetPosition());
            yield return Seconds(1);
            var startOff = off();
            var atHome = ClusterId(ship);

            // out: full thrust away from the station, 7 km up: the game parts the ship's cluster from the station's
            // once the boxes round them (2.5 km on every side) leave a gap and the whole is over two clusters long
            var leg = Within(150);
            while (off() < 7000 && leg())
            {
                ShipKeys(seat, new Vector3(0, 0, -1));
                yield return null;
            }
            Check(off() >= 7000, "the ship reached only " + F(off()) + " m off the station in 150 s (" + F(Motion.Speed(ship)) + " m/s)");
            yield return Until(() => ClusterId(ship) >= 0 && ClusterId(ship) != ClusterId(station) ? 1 : 0, v => v > 0, "the ship in a cluster of its own", 10,
                () => "the ship in cluster " + ClusterId(ship) + ", the station in " + ClusterId(station) + ", " + F(off()) + " m apart");
            var away = ClusterId(ship);
            var speedOut = Motion.Speed(ship);
            Check(speedOut > 5, "the ship's speed died past the cluster's edge: " + F(speedOut) + " m/s at " + F(off()) + " m (" + PhysicsState(ship) + ")");

            // back: the same keys the other way; the ship slows, turns around its own speed and comes home
            leg = Within(150);
            while (off() > 1200 && leg())
            {
                ShipKeys(seat, new Vector3(0, 0, 1));
                yield return null;
            }
            Check(off() <= 1200, "the ship came back only to " + F(off()) + " m of the station in 150 s (" + F(Motion.Speed(ship)) + " m/s)");
            yield return Until(() => ClusterId(ship) == ClusterId(station) ? 1 : 0, v => v > 0, "the ship back in the station's cluster", 10,
                () => "the ship in cluster " + ClusterId(ship) + ", the station in " + ClusterId(station));
            var home = ClusterId(ship);
            // and it still flies: 2 s of forward thrust near the station answer again
            yield return HoldShip(2, seat, new Vector3(0, 0, -1));
            var backSpeed = Motion.Speed(ship);
            Check(backSpeed > 0.5, "the ship stopped answering the pilot after the round trip: " + F(backSpeed) + " m/s after 2 s of thrust (" + PhysicsState(ship) + ")");
            Result("flew out and back, " + F(off()) + " m off the station at the end (start " + F(startOff) + "): cluster " + atHome + " at home, " + away + " out there, " + home + " back" +
                   " (station " + ClusterId(station) + "); " + F(speedOut) + " m/s out there, " + F(backSpeed) + " m/s after the trip");
        }
    }
}
