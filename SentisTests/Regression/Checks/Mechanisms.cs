using System;
using System.Linq;
using System.Collections;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;
using GearMode = SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode;

namespace SentisTests.Regression.Checks
{
    /// <summary>A piston on a station, its head added, extends out and retracts back at the speed set in its terminal.</summary>
    public sealed class PistonCheck : RegressionCheck
    {
        public override string Name => Prefix + "piston";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 4, 0));
            var grid = Build("piston", Site.Origin, true,
                new Part("ExtendedPistonBase/LargePistonBase", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var piston = Block<MyPistonBase>(grid);
            yield return AddHead(piston);
            var ingame = (Sandbox.ModAPI.Ingame.IMyPistonBase)piston;
            ((Sandbox.ModAPI.IMyTerminalBlock)piston).SetValueFloat("Velocity", 2f);
            yield return Until(() => ingame.CurrentPosition, v => v > 3, "the piston extending, m", 15, () => State(piston));
            var head = Vector3D.Distance(piston.TopGrid.PositionComp.GetPosition(), piston.PositionComp.GetPosition());
            ((Sandbox.ModAPI.IMyTerminalBlock)piston).SetValueFloat("Velocity", -2f);
            yield return Until(() => ingame.CurrentPosition, v => v < 0.5, "the piston retracting, m", 15);
            Result("piston extended past 3 m (head " + F(head) + " m off) and retracted");
        }
    }

    /// <summary>
    /// A connector on a piston's head, pushed by the piston to a connector of a station above it, gets in reach of it
    /// ("Connectable") and connects. Its detector (<c>MyShipConnector.m_connectorDummy</c>, a phantom body held to the head's
    /// grid by a fixed constraint) is what finds the other grid: it must go with the head as the piston moves (live server,
    /// 16.11.2026: a connector on a piston sometimes never connected). Told on the way: where the detector is from the
    /// connector, what it has found.
    /// </summary>
    public sealed class ConnectorOnPistonCheck : RegressionCheck
    {
        public override string Name => Prefix + "connector_on_piston";

        private static readonly System.Reflection.FieldInfo Dummy = typeof(MyShipConnector).GetField("m_connectorDummy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 4, 0));
            var grid = Build("connector-piston", Site.Origin, true,
                new Part("ExtendedPistonBase/LargePistonBase", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var piston = Block<MyPistonBase>(grid);
            yield return AddHead(piston);
            var top = piston.TopGrid;
            var up = piston.WorldMatrix.Up;
            // the connector on the head, its port along the piston
            var topBlock = piston.TopBlock.SlimBlock;
            var localUp = Base6Directions.GetIntVector(top.WorldMatrix.GetClosestDirection(up));
            var place = topBlock.Position + localUp;
            // the block turned so that its port faces along the piston: the game's ConnectDirection (in the block's own
            // Left/Up/Forward) points into the block, the lock point is on the other side
            var definition = (Sandbox.Definitions.MyShipConnectorDefinition)Sandbox.Definitions.MyDefinitionManager.Static.GetCubeBlockDefinition(
                new VRage.Game.MyDefinitionId(typeof(Sandbox.Common.ObjectBuilders.MyObjectBuilder_ShipConnector), "Connector"));
            var want = -(Vector3)localUp;
            Direction forward = Direction.Forward, side = Direction.Up;
            foreach (var f in Base6Directions.EnumDirections)
            foreach (var u in Base6Directions.EnumDirections)
            {
                if (Base6Directions.GetAxis(f) == Base6Directions.GetAxis(u)) continue;
                new MyBlockOrientation(f, u).GetMatrix(out Matrix m);
                var port = definition.ConnectDirection.X * m.Left + definition.ConnectDirection.Y * m.Up + definition.ConnectDirection.Z * m.Forward;
                if (Vector3.Dot(port, want) > 0.9f) { forward = f; side = u; }
            }
            var ob = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_ShipConnector)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(
                typeof(Sandbox.Common.ObjectBuilders.MyObjectBuilder_ShipConnector), "Connector");
            ob.Min = place;
            ob.BlockOrientation = new VRage.Game.SerializableBlockOrientation(forward, side);
            ob.Owner = Identity;
            ob.BuiltBy = Identity;
            ob.ShareMode = VRage.Game.MyOwnershipShareModeEnum.Faction;
            ((VRage.Game.ModAPI.IMyCubeGrid)top).AddBlock(ob, false);
            yield return Seconds(1); // the connector reads its port from its model in its first update
            var connector = top.GetCubeBlock(place)?.FatBlock as MyShipConnector;
            Check(connector != null, "the connector was not put on the piston's head");
            Vector3D Port(MyShipConnector c) => c.BlockDefinition.ConnectDirection.X * c.WorldMatrix.Left + c.BlockDefinition.ConnectDirection.Y * c.WorldMatrix.Up +
                                                c.BlockDefinition.ConnectDirection.Z * c.WorldMatrix.Forward;
            Check(Vector3D.Dot(Port(connector), up) < -0.9, "the head's connector does not face along the piston");

            // the station's connector above, facing down, met when the piston is out 2 m
            const double Meet = 2;
            var at = connector.WorldMatrix.Translation + up * (top.GridSize + Meet);
            var station = Build("connector-piston-dock", at, true, new[]
            {
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 2).With(b => Motion.Charged(b)),
            }, forward: -up, up: Vector3D.CalculatePerpendicularVector(up));
            var dock = (MyShipConnector)station.GetCubeBlock(Vector3I.Zero).FatBlock;
            yield return Seconds(1);
            // the station's connector's point where the head's is to come: moved by what is between them
            var gap = Vector3D.Dot(dock.ConstraintPositionWorld() - connector.ConstraintPositionWorld(), up);
            Check(gap > 0.5 && gap < 9, "the dock's point " + F(gap) + " m along the piston from the head's connector's (" + F(Meet) + " m meant)");

            var dummy = Dummy.GetValue(connector) as Sandbox.Engine.Physics.MyPhysicsBody;
            Check(dummy != null, "the head's connector has no detector");
            // where Havok has the detector (not the block's matrix: the body's own place in its world)
            Vector3D DummyAt() => dummy.RigidBody != null ? dummy.ClusterToWorld(dummy.RigidBody.Position) : dummy.GetWorldMatrix().Translation;
            var offset = DummyAt() - connector.PositionComp.GetPosition();
            string Filter(Havok.HkRigidBody body)
            {
                if (body == null) return "none";
                var info = body.GetCollisionFilterInfo();
                return "layer " + (info & 31) + " system " + (info >> 16) + " sub " + ((info >> 5) & 31) + " not with " + ((info >> 10) & 31);
            }
            string Filters() => "filters: detector " + Filter(dummy.RigidBody) + "; head " + Filter(top.Physics?.RigidBody) + "; dock " + Filter(station.Physics?.RigidBody) +
                                "; piston's grid " + Filter(grid.Physics?.RigidBody);
            string Seen() => "detector " + F(Vector3D.Distance(DummyAt() - connector.PositionComp.GetPosition(), offset)) + " m off where it was on the connector" +
                             (dummy.Enabled ? "" : ", off") + (dummy.IsInWorld ? "" : ", not in world") + (dummy.RigidBody?.IsActive == true ? ", active" : ", asleep") +
                             "; grids found " + connector.DetectedGridCount + "; status " + ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Status +
                             "; to the dock's point " + F(Vector3D.Dot(dock.ConstraintPositionWorld() - connector.ConstraintPositionWorld(), up)) + " m along, " +
                             F(Vector3D.Reject(dock.ConstraintPositionWorld() - connector.ConstraintPositionWorld(), up).Length()) + " m aside" +
                             "; blocks " + connector.Min + ".." + connector.Max + " and " + dock.Min + ".." + dock.Max + "; piston at " +
                             F(((Sandbox.ModAPI.Ingame.IMyPistonBase)piston).CurrentPosition) + " m";

            Log.Info(Name + ": start: gap " + F(gap) + " m; " + Seen() + "; points from their blocks: head " + F(Vector3D.Dot(connector.ConstraintPositionWorld() - connector.WorldMatrix.Translation, up)) +
                     ", dock " + F(Vector3D.Dot(dock.ConstraintPositionWorld() - dock.WorldMatrix.Translation, up)) + " m up; ports head " + F(Vector3D.Dot(Port(connector), up)) +
                     ", dock " + F(Vector3D.Dot(Port(dock), up)) + "; ConnectDirection " + connector.BlockDefinition.ConnectDirection + "; head grid " + (connector.CubeGrid == top) + ", piston up " + up + ", top " + F(Vector3D.Dot(top.PositionComp.GetPosition() - piston.PositionComp.GetPosition(), up)) + " m up the piston");
            var terminal = (Sandbox.ModAPI.IMyTerminalBlock)piston;
            terminal.SetValueFloat("UpperLimit", (float)gap);
            var worst = 0.0;
            var rounds = new System.Collections.Generic.List<string>();
            // out and back at the speeds a player sets, each time to lock and let go (the second time round, after the
            // detector saw the dock leave, as much as the first)
            foreach (var speed in new[] { 0.5f, 0.5f, 2f, 2f })
            {
                var started = top.PositionComp.GetPosition();
                var frames = 0;
                Log.Info(Name + ": before " + speed + " m/s: " + Filters());
                terminal.SetValueFloat("Velocity", speed);
                yield return Until(() =>
                {
                    frames++;
                    worst = Math.Max(worst, Vector3D.Distance(DummyAt() - connector.PositionComp.GetPosition(), offset));
                    return ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connectable ? 1 : 0;
                }, v => v > 0, "the head's connector in reach of the dock at " + speed + " m/s", 30,
                    () => Seen() + "; the head moved " + F(Vector3D.Dot(top.PositionComp.GetPosition() - started, up)) + " m up");
                Log.Info(Name + ": " + speed + " m/s, in reach: " + Seen());
                Log.Info(Name + ": " + speed + " m/s, " + Filters());
                Request(connector, "TryConnect");
                yield return Until(() => connector.Connected ? 1 : 0, v => v > 0, "the connectors connected at " + speed + " m/s", 10, Seen);
                Request(connector, "TryDisconnect");
                yield return Until(() => connector.Connected ? 0 : 1, v => v > 0, "the connectors let go", 10, Seen);
                terminal.SetValueFloat("Velocity", -5f);
                yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyPistonBase)piston).CurrentPosition, v => v < 0.05, "the piston back in", 15, Seen);
                yield return Until(() => connector.DetectedGridCount, v => v == 0, "the detector seeing the dock leave", 10, Seen);
                rounds.Add(speed + " m/s");
            }
            Result("a connector on a piston's head in reach of a station's and connected at " + string.Join(", ", rounds) + "; its detector at most " + F(worst) + " m off it on the way");
        }
    }

    /// <summary>A hinge on a station, its head added, swings one way and back within its limits.</summary>
    public sealed class HingeCheck : RegressionCheck
    {
        public override string Name => Prefix + "hinge";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 4, 0));
            var grid = Build("hinge", Site.Origin, true,
                new Part("MotorAdvancedStator/LargeHinge", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var hinge = Block<MyMotorAdvancedStator>(grid);
            yield return AddHead(hinge);
            var ingame = (Sandbox.ModAPI.Ingame.IMyMotorStator)hinge;
            ((Sandbox.ModAPI.IMyTerminalBlock)hinge).SetValueFloat("Velocity", 5f);
            yield return Until(() => MathHelper.ToDegrees(ingame.Angle), v => v > 30, "the hinge swinging, degrees", 15, () => State(hinge));
            ((Sandbox.ModAPI.IMyTerminalBlock)hinge).SetValueFloat("Velocity", -5f);
            yield return Until(() => MathHelper.ToDegrees(ingame.Angle), v => v < -30, "the hinge swinging back, degrees", 20);
            Result("hinge swung past 30 degrees both ways");
        }
    }

    /// <summary>
    /// A landing gear under a small ship over a floor on a planet locks onto the floor as the player locks it: the ship
    /// holds there; unlocked, it lets go.
    /// </summary>
    public sealed class LandingGearCheck : RegressionCheck
    {
        public override string Name => Prefix + "landing_gear";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            var pad = Pad("gear-pad", Site.Origin, 3);
            var floor = pad.PositionComp.GetPosition() + Site.Up * 1.25;
            yield return Arrive(floor + Site.Up * 2 + Site.Forward * 8);
            // the gear's locking face is in its model: each way it can face is tried, the gear's cells just over the floor
            foreach (var face in new[] { Direction.Down, Direction.Forward, Direction.Backward, Direction.Up, Direction.Left, Direction.Right })
            {
                var up = Base6Directions.GetPerpendicular(face);
                // just over the floor: in the suite every grid takes kinetic damage, and a drop of 0.3 m hurt the gear past its red line
                var at = floor + Site.Up * (1.25 + 0.05);
                var ship = Build("gear", at, false, new[] { new Part("LandingGear/LargeBlockLandingGear", 0, 0, 0).Facing(face, up) });
                var gear = Block<SpaceEngineers.Game.Entities.Blocks.MyLandingGear>(ship);
                var ingame = (SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear)gear;
                var within = Within(4);
                while (within() && ingame.LockMode == GearMode.Unlocked)
                    yield return null;
                if (ingame.LockMode == GearMode.Unlocked)
                {
                    yield return CloseAndWait(ship);
                    continue;
                }
                ingame.Lock();
                yield return Until(() => ingame.IsLocked ? 1 : 0, v => v > 0, "the gear locked", 5);
                var held = ship.PositionComp.GetPosition();
                yield return Seconds(2);
                var moved = Vector3D.Distance(held, ship.PositionComp.GetPosition());
                Check(moved < 0.1, "a locked gear let the ship go " + F(moved) + " m");
                // pressed again each second, as a player does when the first press does not take
                var pressed = 0;
                yield return Until(() =>
                {
                    if (!ingame.IsLocked) return 1;
                    if (pressed++ % 60 == 0) ingame.Unlock();
                    return 0;
                }, v => v > 0, "the gear unlocked", 5, () => State(gear) + ", mode " + ingame.LockMode + ", autolock " + ingame.AutoLock + ", pressed " + (pressed + 59) / 60 + " times, integrity "
                    + F(gear.SlimBlock.Integrity / gear.SlimBlock.MaxIntegrity * 100) + " %; grids about: " + string.Join(", ", Sandbox.Game.Entities.MyEntities.GetEntities().OfType<Sandbox.Game.Entities.MyCubeGrid>()
                        .Where(g => g != ship && g != pad && Vector3D.Distance(g.PositionComp.GetPosition(), gear.PositionComp.GetPosition()) < 150)
                        .Select(g => g.DisplayName + " " + F(Vector3D.Distance(g.PositionComp.GetPosition(), gear.PositionComp.GetPosition())) + " m " + F(g.Physics?.LinearVelocity.Length() ?? 0) + " m/s")));
                Result("gear facing " + face + " locked on the floor (moved " + F(moved) + " m in 2 s) and unlocked");
                yield break;
            }
            Check(false, "the landing gear came ready to lock on the floor no way it faced");
        }
    }

    /// <summary>
    /// A gravity generator on a station in open space makes gravity in its field (9.81 m/s2 as set), none far outside it;
    /// an astronaut in it with the jetpack off falls.
    /// </summary>
    public sealed class GravityGeneratorCheck : RegressionCheck
    {
        public override string Name => Prefix + "gravity_generator";

        protected override IEnumerator Check()
        {
            // the astronaut well off the generator's block, with room under them to fall
            var inside = Site.At(20, 30, 20);
            yield return Arrive(inside);
            var grid = Build("gravity", Site.Origin, true,
                new Part("GravityGenerator/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var generator = Block<SpaceEngineers.Game.Entities.Blocks.MyGravityGenerator>(grid);
            yield return Until(() => MyGravityProviderSystem.CalculateArtificialGravityInPoint(inside).Length(), v => v > 9, "gravity in the field, m/s2", 10,
                () => State(generator));
            var g = MyGravityProviderSystem.CalculateArtificialGravityInPoint(inside);
            var outside = MyGravityProviderSystem.CalculateArtificialGravityInPoint(Site.At(0, 500, 0)).Length();
            Check(outside < 0.01, "gravity 500 m off the generator: " + F(outside));
            FakeClients.SetFree(Me, true);
            var fall = 0.0;
            // 5 s of game time: under load the game runs slower than the clock
            for (var f = Frames(5); f > 0 && fall <= 3; f--)
            {
                Keys(Vector3.Zero, jetpack: false);
                fall = Math.Max(fall, Vector3D.Dot(Body.Physics.LinearVelocity, Vector3D.Normalize(g)));
                yield return null;
            }
            Check(fall > 3, "an astronaut in the field with the jetpack off falls at most " + F(fall) + " m/s in 5 s (" + Body.GetCurrentMovementState() + ", jetpack " + Body.JetpackComp?.TurnedOn + ")");
            Result("gravity " + F(g.Length()) + " m/s2 in the field, none outside; the astronaut fell at " + F(fall) + " m/s");
        }
    }
}
