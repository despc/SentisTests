using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;
using IngameInventory = VRage.Game.ModAPI.Ingame.IMyInventory;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The main cockpit (<c>MyShipController.IsMainCockpit</c>): a ship with two seats, the second one set as main - the
    /// first one's keys move nothing (<c>MoveAndRotate</c> gives way to the main cockpit), the main one's fly the ship.
    /// </summary>
    public sealed class MainCockpitCheck : RegressionCheck
    {
        public override string Name => Prefix + "main_cockpit";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 6));
            var ship = Build("main-cockpit", Site.Origin, false,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 2),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, -1, 1),
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Direction.Forward));
            var seats = Blocks<MyCockpit>(ship).OrderBy(s => s.Position.Z).ToList();
            var other = seats[0];
            var main = seats[1];
            ((Sandbox.ModAPI.IMyTerminalBlock)main).SetValueBool("MainCockpit", true);
            Check(main.IsMainCockpit && !other.IsMainCockpit, "the second seat is not the main cockpit");
            var thrusters = Blocks<MyThrust>(ship);

            IEnumerator Forward(MyCockpit seat, Action<float, double> done)
            {
                var from = ship.PositionComp.GetPosition();
                var strongest = 0f;
                for (var f = Frames(2); f > 0; f--)
                {
                    ShipKeys(seat, new Vector3(0, 0, -1));
                    strongest = Math.Max(strongest, thrusters.Max(t => t.CurrentStrength));
                    yield return null;
                }
                done(strongest, Vector3D.Dot(ship.PositionComp.GetPosition() - from, seat.WorldMatrix.Forward));
                for (var f = Frames(3); f > 0; f--)
                {
                    ShipKeys(seat, Vector3.Zero);
                    yield return null;
                }
            }

            yield return Sit(other);
            float otherThrust = 0, mainThrust = 0;
            double otherGone = 0, mainGone = 0;
            yield return Forward(other, (t, g) => { otherThrust = t; otherGone = g; });
            other.RemovePilot();
            yield return WaitForTicks(5);
            yield return Sit(main);
            yield return Forward(main, (t, g) => { mainThrust = t; mainGone = g; });
            Check(otherThrust < 0.05f && otherGone < 0.2, "forward from the seat that is not main fired the thrusters (" + F(otherThrust) + ") and took the ship " + F(otherGone) + " m");
            Check(mainThrust > 0.2f && mainGone > 0.3, "forward from the main cockpit fired the thrusters " + F(mainThrust) + " and took the ship " + F(mainGone) + " m");
            Result("with the main cockpit set, the other seat's forward did nothing (" + F(otherGone) + " m); the main one's flew the ship " + F(mainGone) + " m");
        }
    }

    /// <summary>
    /// A landing gear's auto lock (<c>MyLandingGear.AutoLock</c>): a small ship of one gear with auto lock on, let down onto
    /// a floor on a planet, locks itself as it touches - no lock asked for.
    /// </summary>
    public sealed class LandingGearAutoLockCheck : RegressionCheck
    {
        public override string Name => Prefix + "landing_gear_autolock";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            var pad = Pad("autolock-pad", Site.Origin, 3);
            var floor = pad.PositionComp.GetPosition() + Site.Up * 1.25;
            yield return Arrive(floor + Site.Up * 2 + Site.Forward * 8);
            // the gear's locking face is in its model: each way it can face is tried, just over the floor
            foreach (var face in new[] { Direction.Down, Direction.Forward, Direction.Backward, Direction.Up, Direction.Left, Direction.Right })
            {
                var up = Base6Directions.GetPerpendicular(face);
                var ship = Build("autolock-gear", floor + Site.Up * (1.25 + 0.05), false,
                    new[] { new Part("LandingGear/LargeBlockLandingGear", 0, 0, 0).Facing(face, up).With(b => ((MyObjectBuilder_LandingGear)b).AutoLock = true) });
                var gear = Block<SpaceEngineers.Game.Entities.Blocks.MyLandingGear>(ship);
                var ingame = (SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear)gear;
                Check(ingame.AutoLock, "the gear has no auto lock on");
                var within = Within(4);
                while (within() && !ingame.IsLocked && ingame.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Unlocked)
                    yield return null;
                if (ingame.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Unlocked)
                {
                    yield return CloseAndWait(ship);
                    continue;
                }
                yield return Until(() => ingame.IsLocked ? 1 : 0, v => v > 0, "the gear locking itself on the floor", 5, () => "mode " + ingame.LockMode);
                Result("gear facing " + face + " with auto lock locked itself on the floor");
                yield break;
            }
            Check(false, "the landing gear came to the floor no way it faced");
        }
    }

    /// <summary>
    /// Power through a connector (the connected grids are one power network): a ship with no power source of its own,
    /// a light on it - docked to a station with a battery, the light works; undocked, it is off.
    /// </summary>
    public sealed class ConnectorPowerCheck : RegressionCheck
    {
        public override string Name => Prefix + "connector_power";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 6, 0));
            var station = Build("power-station", Site.Origin, true,
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)));
            var dock = (MyShipConnector)station.GetCubeBlock(Vector3I.Zero).FatBlock;
            var ship = Build("power-ship", station.GridIntegerToWorld(Vector3I.Zero) + Site.Forward * 2.5, false, new[]
            {
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 1),
                new Part("InteriorLight/SmallLight", 0, 1, 1),
            }, forward: -Site.Forward, up: Site.Up);
            var shipConnector = (MyShipConnector)ship.GetCubeBlock(Vector3I.Zero).FatBlock;
            var light = Block<MyLightingBlock>(ship);
            yield return Seconds(1);
            Check(!light.IsWorking, "the ship's light works with no power on the ship");
            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyShipConnector)shipConnector).Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connectable ? 1 : 0,
                v => v > 0, "the connectors in reach of each other", 20, () => "status " + ((Sandbox.ModAPI.Ingame.IMyShipConnector)shipConnector).Status);
            Request(shipConnector, "TryConnect");
            yield return Until(() => shipConnector.Connected ? 1 : 0, v => v > 0, "the connectors connected", 10);
            yield return Until(() => light.IsWorking ? 1 : 0, v => v > 0, "the ship's light powered by the station's battery", 10, () => State(light));
            Request(shipConnector, "TryDisconnect");
            yield return Until(() => shipConnector.Connected ? 0 : 1, v => v > 0, "the connectors disconnected", 10);
            yield return Until(() => light.IsWorking ? 0 : 1, v => v > 0, "the ship's light off undocked", 10, () => State(light));
            Result("the ship's light dark, powered by the station's battery while docked, dark again undocked");
        }
    }

    /// <summary>
    /// A station split in two (<c>MyCubeGrid.CreateSplits</c>): with the world's station voxel support on, the parts stay
    /// stations; off, a part that touches no ground is set to be tested for becoming a ship (<c>TestDynamic</c>). The
    /// world's own setting is checked as it is: it is not changed for the check (every split station of the world would
    /// fall with it).
    /// </summary>
    public sealed class StationSplitCheck : RegressionCheck
    {
        public override string Name => Prefix + "station_split";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -6));
            var grid = Build("station-split", Site.Origin, true,
                new Part("CubeBlock/LargeBlockArmorBlock", -1, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var support = MySession.Static.Settings.StationVoxelSupport;
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            grid.RazeBlock(Vector3I.Zero);
            List<MyCubeGrid> Parts() => MyEntities.GetEntities().OfType<MyCubeGrid>()
                .Where(g => !g.MarkedForClose && (g == grid || !before.Contains(g.EntityId)) && Vector3D.Distance(g.PositionComp.GetPosition(), Site.Origin) < 10).ToList();
            yield return Until(() => Parts().Count, v => v >= 2, "the station split in two", 10);
            foreach (var part in Parts()) Track(part);
            yield return Seconds(3);
            var parts = Parts();
            if (support)
                Check(parts.All(p => p.IsStatic), "with station voxel support on, a part of the split station became a ship: " + string.Join(", ", parts.Select(p => p.IsStatic ? "station" : "ship")));
            else
                Check(parts.Any(p => !p.IsStatic), "with station voxel support off, the parts of the split station in open space stayed stations");
            Result("station voxel support " + (support ? "on" : "off") + ": the split station's parts are " + string.Join(", ", parts.Select(p => p.IsStatic ? "a station" : "a ship")));
        }
    }

    /// <summary>
    /// The suit's radio (<c>MyCharacter.EnableBroadcasting</c>, the player's antenna toggle): allowed by the server, the
    /// player switches it off and on, and the save has it; the server not allowing the toggle, the request changes nothing.
    /// What the player chose is <c>WantsToBeEnabled</c>; the radio is on while it is and the suit's battery gives power.
    /// </summary>
    public sealed class CharacterBroadcastCheck : RegressionCheck
    {
        public override string Name => Prefix + "character_broadcast";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var body = Body;
            Check(body.RadioBroadcaster != null, "the character has no radio");
            body.EnableBroadcastingPlayerToggle(true);
            yield return WaitForTicks(2);
            Request(body, "EnableBroadcasting", false);
            yield return WaitForTicks(2);
            Check(!body.RadioBroadcaster.WantsToBeEnabled && !body.RadioBroadcaster.Enabled, "the radio is on after the player switched it off");
            Check(!((MyObjectBuilder_Character)body.GetObjectBuilder()).EnableBroadcasting, "the save has the radio on");
            Request(body, "EnableBroadcasting", true);
            yield return WaitForTicks(2);
            Check(body.RadioBroadcaster.WantsToBeEnabled, "the radio is off after the player switched it on");
            body.EnableBroadcastingPlayerToggle(false);
            yield return WaitForTicks(2);
            Request(body, "EnableBroadcasting", false);
            yield return WaitForTicks(2);
            Check(body.RadioBroadcaster.WantsToBeEnabled, "the player switched the radio off with the server not allowing it");
            body.EnableBroadcastingPlayerToggle(true);
            Result("the radio switched off and on by the player (saved); not allowed by the server, the switch changed nothing");
        }
    }

    /// <summary>
    /// A medical room refuels the suit (<c>RefuelAllowed</c>: the suit's gas sink takes the room's gas from the tanks on
    /// its conveyors while it is used): oxygen and hydrogen low in the suit, the player at the room - both fill up.
    /// </summary>
    public sealed class MedicalRefuelCheck : RegressionCheck
    {
        public override string Name => Prefix + "medical_refuel";

        protected override IEnumerator Check()
        {
            SkipUnless(((Sandbox.Definitions.MyMedicalRoomDefinition)Definition("MedicalRoom/LargeMedicalRoom")).RefuelAllowed, "the medical room does not refuel in this game");
            yield return Arrive(Site.At(0, 6, -6));
            var room = new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0);
            Part oxygen = null, hydrogen = null;
            yield return ConveyorNeighbour(room, "OxygenTank/", null, p => oxygen = p);
            var taken = Cells(room).Concat(Cells(oxygen)).ToList();
            yield return ConveyorNeighbour(room, "OxygenTank/LargeHydrogenTankSmall", p => !Cells(p).Overlaps(taken), p => hydrogen = p);
            taken.AddRange(Cells(hydrogen));
            var free = Cells(room).SelectMany(c => Base6Directions.IntDirections.Select(d => c + (Vector3I)d)).First(c => !taken.Contains(c));
            var grid = Build("medical-refuel", Site.Origin, true, room,
                oxygen.With(b => ((MyObjectBuilder_OxygenTank)b).FilledRatio = 1f),
                hydrogen.With(b => ((MyObjectBuilder_OxygenTank)b).FilledRatio = 1f),
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var medical = Block<SpaceEngineers.Game.Entities.Blocks.MyMedicalRoom>(grid);
            yield return Until(() => medical.IsWorking ? 1 : 0, v => v > 0, "the medical room working", 10, () => State(medical));
            StandAt(medical.PositionComp.GetPosition() + Site.Up * 0.2, medical.PositionComp.GetPosition() + Site.Forward);
            var gas = Body.OxygenComponent;
            Check(gas != null, "the character has no gas component");
            var o2 = MyCharacterOxygenComponent.OxygenId;
            var h2 = MyCharacterOxygenComponent.HydrogenId;
            gas.UpdateStoredGasLevel(ref o2, 0.1f);
            gas.UpdateStoredGasLevel(ref h2, 0.1f);
            yield return Until(() =>
                {
                    Request(medical, "RequestSupport", Body.EntityId);
                    return Math.Min(gas.GetGasFillLevel(o2), gas.GetGasFillLevel(h2));
                }, v => v > 0.3, "the suit's oxygen and hydrogen filling at the medical room", 30,
                () => "oxygen " + F(gas.GetGasFillLevel(o2) * 100) + " %, hydrogen " + F(gas.GetGasFillLevel(h2) * 100) + " %");
            Result("at the medical room the suit filled: oxygen " + F(gas.GetGasFillLevel(o2) * 100) + " %, hydrogen " + F(gas.GetGasFillLevel(h2) * 100) + " % (from 10)");
        }
    }

    /// <summary>
    /// A respawn ship replaced (<c>MySpaceRespawnComponent</c>: a new respawn ship closes the player's last one,
    /// <c>MySessionComponentTrash.CloseRespawnShip</c>): a player respawns in a respawn ship twice - the first ship is gone.
    /// </summary>
    public sealed class RespawnShipReplacedCheck : RegressionCheck
    {
        public override string Name => Prefix + "respawn_ship_replaced";
        public override int TimeoutSeconds => 200;
        protected override bool NeedsDamage => true;

        private const string Ship = "RespawnSpace";

        private IEnumerator Respawn(Action<MyCubeGrid> spawned)
        {
            var body = Body;
            body.DoDamage(1000f, MyDamageType.Suicide, true, attackerId: 0);
            yield return Until(() => body.IsDead ? 1 : 0, v => v > 0, "the player dead", 10);
            FakeClients.ClearRespawnCooldowns(Player.Id.SteamId);
            var message = new Sandbox.Game.Multiplayer.MyPlayerCollection.RespawnMsg
            {
                JoinGame = false, NewIdentity = false, RespawnEntityId = 0, RespawnShipId = Ship, PlayerSerialId = 0, Color = Color.Red,
            };
            Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "OnRespawnRequest", message);
            yield return Until(() => Body != null && Body != body && !Body.IsDead && Body.Parent is MyCockpit ? 1 : 0, v => v > 0, "the player in a new respawn ship", 60);
            var pod = ((MyCockpit)Body.Parent).CubeGrid;
            Track(Body);
            Track(pod);
            spawned(pod);
        }

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableRespawnShips, "respawn ships are off in this world");
            SkipUnless(Sandbox.Definitions.MyDefinitionManager.Static.GetRespawnShipDefinition(Ship) != null, "no respawn ship " + Ship + " in this game");
            yield return Arrive(Site.At(0, 6, -10));
            MyCubeGrid first = null, second = null;
            yield return Respawn(p => first = p);
            Check(first.IsRespawnGrid, "the first ship is not a respawn grid");
            yield return Respawn(p => second = p);
            yield return Until(() => first.MarkedForClose ? 1 : 0, v => v > 0, "the first respawn ship closed with the second one out", 10);
            Check(!second.MarkedForClose, "the second respawn ship is gone too");
            Result("respawned in " + Ship + " twice: the first ship (" + first.DisplayName + ") closed when the second came");
        }
    }

    /// <summary>
    /// An air vent in a planet's breathable air (<c>MyAirVent</c>: outside, depressurizing, it is a source of the air
    /// around it): a vent on a station on the ground, an empty oxygen tank on its conveyors - the tank fills.
    /// </summary>
    public sealed class VentPlanetAirCheck : RegressionCheck
    {
        public override string Name => Prefix + "vent_planet_air";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableOxygen && MySession.Static.Settings.EnableOxygenPressurization, "oxygen is off in this world");
            // the planet sites may be on a planet without air (on the stand: Mars): the check goes to one with it
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().FirstOrDefault(p => p.GetOxygenForPosition(p.PositionComp.GetPosition() + Vector3D.Up * (p.AverageRadius + 50)) > 0.75);
            SkipUnless(planet != null, "no planet with air in this world");
            var guess = planet.PositionComp.GetPosition() + Vector3D.Normalize(Site.Origin - planet.PositionComp.GetPosition()) * planet.MaximumRadius * 1.1
                        + Vector3D.CalculatePerpendicularVector(Vector3D.Normalize(Site.Origin - planet.PositionComp.GetPosition())) * 400;
            var ground = planet.GetClosestSurfacePointGlobal(ref guess);
            var up = Vector3D.Normalize(ground - planet.PositionComp.GetPosition());
            var forward = Vector3D.CalculatePerpendicularVector(up);
            yield return Arrive(ground + up * 3 + forward * 8);
            var vent = new Part("AirVent/", 0, 0, 0).Facing(Direction.Up, Direction.Backward);
            Part tank = null;
            // where the tank meets the vent's port is found on probes in space (on the ground the probes stand in the
            // planet's terrain), then the pair is built on the planet
            var planetSite = Site;
            Site = Sites.Get(SiteKind.Space, planetSite.Index);
            try { yield return ConveyorNeighbour(vent, "OxygenTank/", p => Cells(p).All(c => c.Y < 0 || Math.Abs(c.X) > 1 || Math.Abs(c.Z) > 1), p => tank = p); }
            finally { Site = planetSite; }
            var taken = Cells(vent).Concat(Cells(tank)).ToList();
            var free = Cells(vent).SelectMany(c => Base6Directions.IntDirections.Select(d => c + (Vector3I)d)).First(c => !taken.Contains(c));
            var grid = Build("vent-planet-air", ground + up * 6, true, new[]
            {
                vent.With(b => ((MyObjectBuilder_AirVent)b).IsDepressurizing = true),
                tank.With(b => ((MyObjectBuilder_OxygenTank)b).FilledRatio = 0f),
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)),
            }, forward: forward, up: up);
            var oxygenTank = Block<MyGasTank>(grid);
            var airVent = Block<SpaceEngineers.Game.Entities.Blocks.MyAirVent>(grid);
            Check(Sandbox.Game.GameSystems.MyOxygenProviderSystem.GetOxygenInPoint(airVent.PositionComp.GetPosition()) > 0.5, "no air at the vent");
            yield return Until(() => oxygenTank.FilledRatio, v => v > 0.001, "the tank filling with the planet's air through the vent", 30,
                () => State(airVent) + ", depressurizing " + airVent.IsDepressurizing + ", tank " + State(oxygenTank));
            Result("a depressurizing vent in " + planet.StorageName + "'s air filled the tank to " + F(oxygenTank.FilledRatio * 100) + " %");
        }
    }
}
