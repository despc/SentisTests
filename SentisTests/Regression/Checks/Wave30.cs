using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The cold of space (<c>MyCharacter.UpdateBeforeSimulation100</c>): a player whose suit has no energy left takes
    /// damage of the temperature (5 every 100 frames in the extreme cold, which open space is); with energy in the suit
    /// they take none.
    /// </summary>
    public sealed class TemperatureDamageCheck : RegressionCheck
    {
        public override string Name => Prefix + "temperature_damage";
        protected override bool NeedsDamage => true;

        private static readonly List<(long Entity, float Amount)> Taken = new List<(long, float)>();
        private static bool _registered;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var body = Body;
            Check(Sandbox.Game.SessionComponents.MySectorWeatherComponent.TemperatureToLevel(Sandbox.Game.SessionComponents.MySectorWeatherComponent.GetTemperatureInPoint(body.PositionComp.GetPosition())) == MyTemperatureLevel.ExtremeFreeze,
                "open space here is not of the extreme cold (temperature " + F(Sandbox.Game.SessionComponents.MySectorWeatherComponent.GetTemperatureInPoint(body.PositionComp.GetPosition())) + ")");
            if (!_registered)
            {
                _registered = true;
                Sandbox.Game.GameSystems.MyDamageSystem.Static.RegisterBeforeDamageHandler(0, (object target, ref MyDamageInformation info) =>
                {
                    if (info.Type == MyDamageType.Temperature && target is VRage.Game.Entity.MyEntity e)
                        lock (Taken) Taken.Add((e.EntityId, info.Amount));
                });
            }
            float Cold() { lock (Taken) return Taken.Where(t => t.Entity == body.EntityId).Sum(t => t.Amount); }

            body.StatComp.Health.Value = body.StatComp.Health.MaxValue;
            var source = body.SuitBattery.ResourceSource;
            // a full suit: no cold
            source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, 1E-05f);
            var before = Cold();
            yield return Seconds(5);
            Check(Cold() - before == 0, "with energy in the suit the cold took " + F(Cold() - before));

            // the suit spent: the cold bites
            source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, 0f);
            before = Cold();
            var health = body.StatComp.Health.Value;
            yield return Until(() => Cold() - before, v => v >= 10, "the cold's damage to the player with an empty suit", 30,
                () => "suit energy " + F(body.SuitEnergyLevel * 100) + " %, health " + F(body.StatComp.Health.Value) + ", outside " + F(body.GetOutsideTemperature()));
            var lost = health - body.StatComp.Health.Value;
            Check(lost > 0, "the cold's damage did not lower the player's health");
            source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, 1E-05f);
            body.StatComp.Health.Value = body.StatComp.Health.MaxValue;
            Result("full suit: no cold; empty suit in open space: " + F(Cold() - before) + " of the cold's damage, health " + F(health) + " -> " + F(health - lost));
        }
    }

    /// <summary>
    /// The power grid short of power (<c>MyResourceDistributorComponent.RecomputeResourceDistributionPartial</c>): the
    /// consumers go by the priority of their group. A group that does not fit in what is left is not powered at all, and
    /// what is left goes on to the groups after it; an adaptive group (a jump drive charging) takes whatever is left. One
    /// battery of 12 MW: 21 artificial masses (Utility, 0.6 MW each, 12.6 in all) get nothing and the gyroscope after them
    /// is powered; two masses off, the 19 fit and are powered, and the jump drive charges on the rest.
    /// </summary>
    public sealed class PowerPriorityCheck : RegressionCheck
    {
        public override string Name => Prefix + "power_priority";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -6));
            var parts = new List<Part> { new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)) };
            for (var i = 0; i < 21; i++) parts.Add(new Part("VirtualMass/VirtualMassLarge", 1 + i % 7, 0, i / 7));
            parts.Add(new Part("Gyro/LargeBlockGyro", 0, 0, 1));
            parts.Add(new Part("JumpDrive/LargeJumpDrive", -3, 0, 1));
            // a ship: a station's gyroscopes ask for nothing
            var grid = Build("power-priority", Site.Origin, false, parts);
            var battery = Block<Sandbox.Game.Entities.MyBatteryBlock>(grid);
            var masses = Blocks<SpaceEngineers.Game.Entities.Blocks.MyVirtualMass>(grid);
            var gyro = Block<Sandbox.Game.Entities.MyGyro>(grid);
            var jump = Block<Sandbox.Game.Entities.MyJumpDrive>(grid);
            Check(battery != null && masses.Count == 21 && gyro != null && jump != null, "the grid did not come up whole: " + masses.Count + " masses");
            var power = MyResourceDistributorComponent.ElectricityId;
            // the gyroscopes of a grid draw through one sink, the grid's gyroscope system's
            MyResourceSinkComponent Sink(MyCubeBlock b) => b is Sandbox.Game.Entities.MyGyro ? grid.GridSystems.GyroSystem.ResourceSink
                : (b as MyFunctionalBlock)?.ResourceSink ?? b.Components.Get<MyResourceSinkComponent>();
            float In(MyCubeBlock b) => Sink(b)?.CurrentInputByType(power) ?? -1;
            var group = typeof(MyResourceSinkComponent).GetField("Group", BindingFlags.Instance | BindingFlags.NonPublic);
            string Group(MyCubeBlock b) { var sink = Sink(b); return sink == null ? "-" : group?.GetValue(sink)?.ToString() ?? "?"; }
            Check(masses.All(m => Group(m) == "Utility") && Group(gyro) == "Gyro" && Group(jump) == "Charging",
                "the groups are not as the game's definitions say: masses " + Group(masses[0]) + ", gyroscope " + Group(gyro) + ", jump drive " + Group(jump));
            var max = battery.SourceComp.MaxOutputByType(power);
            string State() => "battery " + F(battery.SourceComp.CurrentOutputByType(power)) + " of " + F(max) + " MW; masses " + F(masses.Sum(m => In(m))) + " MW (" + masses.Count(m => In(m) > 0) + " powered); gyroscope "
                              + F(In(gyro) * 1000) + " kW (asks " + F((Sink(gyro)?.RequiredInputByType(power) ?? -1) * 1000) + ", working " + gyro.IsWorking + "); jump drive " + F(In(jump)) + " MW";

            // 21 x 0.6 = 12.6 MW of Utility over 12: none of them, the gyroscope after them powered, the jump drive the rest
            yield return Until(() => masses.Count(m => In(m) > 0) == 0 && In(gyro) > 0 && In(jump) > 0 ? 1 : 0, v => v > 0,
                "the masses unpowered, the gyroscope and the jump drive powered", 20, State);
            var first = State();
            var restBefore = In(jump);
            Check(Math.Abs(battery.SourceComp.CurrentOutputByType(power) - max) < max * 0.02, "the battery does not give all it can: " + first);

            // two off: 11.4 MW of Utility fits; the jump drive gets less
            masses[0].Enabled = false;
            masses[1].Enabled = false;
            yield return Until(() => masses.Skip(2).All(m => In(m) > 0) ? 1 : 0, v => v > 0, "the 19 masses powered", 20, State);
            yield return Seconds(1);
            Check(In(gyro) > 0, "the gyroscope lost power once the masses fit: " + State());
            Check(In(jump) > 0 && In(jump) < restBefore - 5, "the jump drive did not give way to the masses: " + State() + " (it had " + F(restBefore) + " MW)");
            Result("21 masses over the battery: " + first + " | 19: " + State());
        }
    }

    /// <summary>
    /// A player's GPS marks, as the client asks for them (<c>MyGpsCollection.AddGpsOnServer</c>, <c>ModifyGpsOnServer</c>,
    /// <c>DeleteGpsOnServer</c>): added, it is the player's (not the identity the request names), renamed and moved it
    /// keeps one entry, it goes into the save, and deleted it is gone; another player's marks are not touched.
    /// </summary>
    public sealed class GpsCheck : RegressionCheck
    {
        public override string Name => Prefix + "gps";

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<(long Identity, int Hash)> _made = new List<(long, int)>();

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -9));
            var other = -1;
            yield return Join(Site.At(6, 6, -9), i => other = i);
            var me = Identity;
            var them = FakeClients.PlayerOf(other).Identity.IdentityId;
            var gpss = MySession.Static.Gpss;
            var type = typeof(Sandbox.Game.Multiplayer.MyGpsCollection);
            Dictionary<int, MyGps> Of(long identity) => gpss.ExistsForPlayer(identity) ? gpss[identity] : new Dictionary<int, MyGps>();
            MyGps Named(long identity, string name) => Of(identity).Values.FirstOrDefault(g => g.Name == name);
            var theirsBefore = Of(them).Count;

            // added by the player, in a request that names the other's identity: the game takes the sender's
            var add = type.GetNestedType("AddMsg", Any);
            var msg = Activator.CreateInstance(add);
            void Set(object m, string field, object value) => m.GetType().GetField(field, Any).SetValue(m, value);
            var at = Site.At(100, 0, 0);
            Set(msg, "IdentityId", them);
            Set(msg, "Name", "reg-gps mark");
            Set(msg, "DisplayName", "reg-gps mark");
            Set(msg, "Description", "");
            Set(msg, "Coords", at);
            Set(msg, "ShowOnHud", true);
            Set(msg, "IsFinal", true);
            Set(msg, "GPSColor", Color.Yellow);
            FakeClients.AsClient(Me, () => type.GetMethod("AddGpsOnServer", Any).Invoke(null, new[] { msg }));
            var mark = Named(me, "reg-gps mark");
            Check(mark != null, "the player's mark is not in their list (" + Of(me).Count + " marks)");
            _made.Add((me, mark.Hash));
            Check(Vector3D.Distance(mark.Coords, at) < 0.01, "the mark is at " + mark.Coords + ", not where it was asked for");
            Check(Named(them, "reg-gps mark") == null && Of(them).Count == theirsBefore, "the mark went to the identity the request named, not the sender");

            // renamed and moved: one entry, under its new hash
            var modify = type.GetNestedType("ModifyMsg", Any);
            var change = Activator.CreateInstance(modify);
            var moved = Site.At(200, 0, 0);
            Set(change, "Hash", mark.Hash);
            Set(change, "Name", "reg-gps moved");
            Set(change, "Description", "moved");
            Set(change, "Coords", moved);
            Set(change, "GPSColor", Color.Red);
            FakeClients.AsClient(Me, () => type.GetMethod("ModifyGpsOnServer", Any).Invoke(null, new[] { change }));
            var renamed = Named(me, "reg-gps moved");
            Check(renamed != null && Named(me, "reg-gps mark") == null, "the renamed mark: " + string.Join(", ", Of(me).Values.Where(g => g.Name.StartsWith("reg-gps")).Select(g => g.Name)));
            _made.Add((me, renamed.Hash));
            Check(Vector3D.Distance(renamed.Coords, moved) < 0.01 && Of(me).ContainsKey(renamed.Hash), "the moved mark is at " + renamed.Coords + " (in the list under its hash: " + Of(me).ContainsKey(renamed.Hash) + ")");

            // into the save
            var checkpoint = new MyObjectBuilder_Checkpoint();
            gpss.SaveGpss(checkpoint);
            var inSave = checkpoint.Gps?.Dictionary.TryGetValue(me, out var entries) == true ? entries.Entries.Where(e => e.name == "reg-gps moved").ToList() : new List<MyObjectBuilder_Gps.Entry>();
            Check(inSave.Count == 1, "the mark is " + inSave.Count + " times in the save's GPS of the player");
            var saved = inSave[0];
            Check(Vector3D.Distance(saved.coords, moved) < 0.01, "the saved mark is at " + saved.coords);

            // the other's delete by its hash takes nothing of the player's; the player's own does
            FakeClients.AsClient(other, () => type.GetMethod("DeleteGpsOnServer", Any).Invoke(null, new object[] { renamed.Hash }));
            Check(Of(me).ContainsKey(renamed.Hash), "the other player's delete took the player's mark");
            FakeClients.AsClient(Me, () => type.GetMethod("DeleteGpsOnServer", Any).Invoke(null, new object[] { renamed.Hash }));
            Check(!Of(me).ContainsKey(renamed.Hash) && Named(me, "reg-gps moved") == null, "the player's delete left the mark");
            yield return null;
            Result("added (in the sender's list, not the identity named), renamed and moved (one entry), saved, the other's delete refused, deleted");
        }

        public override void Cleanup()
        {
            try
            {
                foreach (var (identity, hash) in _made)
                    if (MySession.Static.Gpss.ExistsForPlayer(identity) && MySession.Static.Gpss[identity].ContainsKey(hash))
                        MySession.Static.Gpss.SendDeleteGpsRequest(identity, hash);
            }
            catch (Exception e) { Log.Warn("cleanup of {0}: {1}", Name, e.Message); }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A connector in trading mode (<c>MyShipConnector.TradingEnabled</c>): a ship docks at a station's trading connector,
    /// and the conveyors do not go over it from cargo to cargo (<c>MyGridConveyorSystem.IsAccessAllowed</c> stops at a
    /// trading connector; a player's own hands in the terminal are not the conveyors); trading off, they do. Undocked from a trading connector, the pair is held from locking a
    /// while (<c>IsProtectedFromLockingByTrading</c>).
    /// </summary>
    public sealed class ConnectorTradingCheck : RegressionCheck
    {
        public override string Name => Prefix + "connector_trading";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 6, 0));
            var station = Build("trading-station", Site.Origin, true,
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 1),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 1).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f));
            var dock = (MyShipConnector)station.GetCubeBlock(Vector3I.Zero).FatBlock;
            var shipAt = station.GridIntegerToWorld(Vector3I.Zero) + Site.Forward * 2.5;
            var ship = Build("trading-ship", shipAt, false, new[]
            {
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 1),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 1).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f),
            }, forward: -Site.Forward, up: Site.Up);
            var shipConnector = (MyShipConnector)ship.GetCubeBlock(Vector3I.Zero).FatBlock;
            var stationCargo = Inventory(station.GetCubeBlock(new Vector3I(0, 0, 1)).FatBlock);
            var shipCargoBlock = ship.GetCubeBlock(new Vector3I(0, 0, 1)).FatBlock;
            var shipCargo = Inventory(shipCargoBlock);
            dock.TradingEnabled.Value = true;
            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyShipConnector)shipConnector).Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connectable ? 1 : 0,
                v => v > 0, "the connectors in reach of each other", 20, () => "status " + ((Sandbox.ModAPI.Ingame.IMyShipConnector)shipConnector).Status);
            Request(shipConnector, "TryConnect");
            yield return Until(() => shipConnector.Connected ? 1 : 0, v => v > 0, "the ship docked at the trading connector", 10);
            yield return Seconds(1);

            // by the conveyors (a script's or a sorter's transfer, MyInventory.CanTransferTo), not the player's hands
            var from = (VRage.Game.ModAPI.Ingame.IMyInventory)stationCargo;
            var to = (VRage.Game.ModAPI.Ingame.IMyInventory)shipCargo;
            Put(stationCargo, "Component/SteelPlate", 20);
            yield return null;
            Check(!from.IsConnectedTo(to), "the ship's cargo is on the station's conveyors over the trading connector");
            from.TransferItemTo(to, 0, null, true, (VRage.MyFixedPoint)20);
            Check(Amount(shipCargo, "Component/SteelPlate") == 0 && Amount(stationCargo, "Component/SteelPlate") == 20,
                "the plates went over the trading connector: the ship has " + Amount(shipCargo, "Component/SteelPlate"));

            dock.TradingEnabled.Value = false;
            yield return Seconds(1);
            Check(from.IsConnectedTo(to), "trading off, the ship's cargo is not on the station's conveyors");
            from.TransferItemTo(to, 0, null, true, (VRage.MyFixedPoint)20);
            Check(Amount(shipCargo, "Component/SteelPlate") == 20, "trading off, the plates did not go over the connection: the ship has " + Amount(shipCargo, "Component/SteelPlate"));

            dock.TradingEnabled.Value = true;
            yield return Seconds(1);
            Request(shipConnector, "TryDisconnect");
            yield return Until(() => shipConnector.Connected ? 0 : 1, v => v > 0, "the ship undocked", 10);
            Check(dock.IsProtectedFromLockingByTrading() && shipConnector.IsProtectedFromLockingByTrading(),
                "undocked from a trading connector, the pair is not held from locking (station " + dock.IsProtectedFromLockingByTrading() + ", ship " + shipConnector.IsProtectedFromLockingByTrading() + ")");
            var held = dock.GetProtectionFromLockingTime();
            Result("over the trading connector the conveyors took no plates, trading off they did; undocked, held from locking (" + held + ")");
        }
    }

    /// <summary>
    /// The transponder (<c>MyTransponderBlock</c>, <c>MySignalSenderEntityComponent</c>): a signal sent on a channel goes by
    /// the antennas to the transponders in reach; the one on that channel of the same owner is triggered, the one on
    /// another channel hears it and is not, and a stranger's one is triggered only when it takes signals from everyone
    /// (by default only from its owner and their faction).
    /// </summary>
    public sealed class TransponderSignalCheck : RegressionCheck
    {
        public override string Name => Prefix + "transponder_signal";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, -10));
            var stranger = -1;
            yield return Join(Site.At(0, 10, 10), i => stranger = i);
            var strangerId = FakeClients.PlayerOf(stranger).Identity.IdentityId;
            MyCubeGrid Station(string name, Vector3D at, long owner) => Build(name, at, true, new[]
            {
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("RadioAntenna/LargeBlockRadioAntenna", 1, 0, 0),
                new Part("TransponderBlock/LargeBlockTransponder", -1, 0, 0),
            }, ownedBy: owner);
            var grids = new[]
            {
                Station("signal-from", Site.Origin, Identity), Station("signal-same", Site.At(120, 0, 0), Identity),
                Station("signal-other-channel", Site.At(-120, 0, 0), Identity), Station("signal-stranger", Site.At(0, 0, 120), strangerId),
            };
            var t = grids.Select(g => Block<SpaceEngineers.Game.Entities.Blocks.MyTransponderBlock>(g)).ToList();
            Check(t.All(x => x != null), "a transponder did not come up: " + string.Join(", ", t.Select(x => x != null)));
            var antennas = grids.Select(g => Block<MyRadioAntenna>(g)).ToList();
            foreach (var antenna in antennas)
            {
                ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueFloat("Radius", 500f);
                ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueBool("EnableBroadCast", true);
            }
            var channels = new[] { 7, 7, 8, 7 };
            for (var i = 0; i < 4; i++) ((SpaceEngineers.Game.ModAPI.Ingame.IMyTransponder)t[i]).Channel = channels[i];
            var heard = new int[4];
            var triggered = new int[4];
            for (var i = 1; i < 4; i++)
            {
                var k = i;
                ((SpaceEngineers.Game.ModAPI.IMyTransponder)t[i]).SignalReceived += c => heard[k]++;
                ((SpaceEngineers.Game.ModAPI.IMyTransponder)t[i]).TriggeredBySignal += c => triggered[k]++;
            }
            yield return Until(() => t.All(x => x.IsWorking) && antennas.All(a => a.IsWorking) ? 1 : 0, v => v > 0, "the transponders and antennas working", 20,
                () => string.Join(", ", t.Select(x => x.IsWorking)) + " / " + string.Join(", ", antennas.Select(a => a.IsWorking)));
            string State() => "heard " + string.Join("/", heard.Skip(1)) + ", triggered " + string.Join("/", triggered.Skip(1));
            // the antennas find who is in their reach every 100 frames or so: sent again every 2 s until it comes
            var last = DateTime.MinValue;
            Func<double> sendUntil(Func<bool> done) => () =>
            {
                if (done()) return 1;
                if ((DateTime.UtcNow - last).TotalSeconds >= 2) { last = DateTime.UtcNow; t[0].SendSignal(); }
                return 0;
            };
            yield return Until(sendUntil(() => triggered[1] > 0 && heard[2] > 0 && heard[3] > 0), v => v > 0, "the signal at the transponders in reach", 30, State);
            Check(triggered[2] == 0, "the transponder on another channel was triggered: " + State());
            Check(triggered[3] == 0, "the stranger's transponder (signals from the faction) was triggered: " + State());

            t[3].AllowSignalsFrom = VRage.Game.ModAPI.Ingame.MyTransponderRelationFilter.Everyone;
            var before = triggered[3];
            last = DateTime.MinValue;
            yield return Until(sendUntil(() => triggered[3] > before), v => v > 0, "the stranger's transponder taking signals from everyone triggered", 20, State);
            Result("channel 7 from the owner: the same owner's on 7 triggered, the one on 8 heard it only, the stranger's only once it took signals from everyone (" + State() + ")");
        }
    }

    /// <summary>
    /// The world's auto healing (<c>MySpaceStatEffect.Update10</c>): a hurt player in air of oxygen enough (over 0.75: a
    /// planet's air here) heals by itself, slowly, up to 70 % of their health and no further.
    /// </summary>
    public sealed class AutoHealingCheck : RegressionCheck
    {
        public override string Name => Prefix + "auto_healing";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.AutoHealing && MySession.Static.Settings.EnableOxygen, "auto healing (or oxygen) is off in this world");
            // the planet sites may be on a planet without air (on the stand: Mars): the check goes to one with it
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().FirstOrDefault(p => p.GetOxygenForPosition(p.PositionComp.GetPosition() + Vector3D.Up * (p.AverageRadius + 50)) > 0.75);
            SkipUnless(planet != null, "no planet with air of oxygen enough in this world");
            var guess = planet.PositionComp.GetPosition() + Vector3D.Normalize(Site.Origin - planet.PositionComp.GetPosition()) * planet.MaximumRadius * 1.1;
            var ground = planet.GetClosestSurfacePointGlobal(ref guess);
            var up = Vector3D.Normalize(ground - planet.PositionComp.GetPosition());
            yield return Arrive(ground + up * 3);
            var body = Body;
            body.JetpackComp?.TurnOnJetpack(true);
            // the air at the player is counted every 100 frames: the value the character came with goes first
            yield return Seconds(3);
            yield return Until(() => body.EnvironmentOxygenLevel, v => v > 0.75, "the planet's air at the player", 20);
            var health = body.StatComp.Health;
            health.Value = 30;
            yield return Until(() => health.Value, v => v >= 32, "the player healing by themselves from 30", 40,
                () => "health " + F(health.Value) + ", air " + F(body.EnvironmentOxygenLevel));
            var rate = health.Value - 30;
            // just under the cap: up to it and no further
            health.Value = 69.5f;
            yield return Until(() => health.Value, v => v >= 69.99, "the healing up to 70", 20, () => "health " + F(health.Value));
            yield return Seconds(5);
            Check(health.Value <= 70.05, "the auto healing went past 70 %: " + F(health.Value));
            var top = health.Value;
            health.Value = health.MaxValue;
            Result("hurt to 30 in the planet's air, healed by itself (+" + F(rate) + "), and up to " + F(top) + ", no further");
        }
    }

    /// <summary>
    /// The world's remote block removal (<c>EnableRemoteBlockRemoval</c>, the X by a grid in the terminal's Info tab:
    /// <c>MyBlockLimits.RemoveBlocksBuiltByID</c>): a player takes off, from far away, the blocks they built on a grid,
    /// and only theirs - those someone else built stay. SentisGameplayImprovements refuses it while an enemy player is
    /// within 10 km of the grid (<c>RemovePluginPatch</c>): with one there (the suite's players of the sites nearby) the
    /// check expects that refusal instead.
    /// </summary>
    public sealed class RemoteBlockRemovalCheck : RegressionCheck
    {
        public override string Name => Prefix + "remote_block_removal";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableRemoteBlockRemoval, "remote block removal is off in this world");
            yield return Arrive(Site.At(0, 6, -400));
            var someone = WorldApi.TestIdentityId();
            var parts = new List<Part>();
            for (var x = 0; x < 6; x++)
                parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, 0, 0).With(b => { if (b.Min.X >= 3) b.BuiltBy = someone; }));
            var grid = Build("remote-removal", Site.Origin, true, parts);
            int ByMe() => grid.GetBlocks().Count(b => b.BuiltBy == Identity);
            int BySomeone() => grid.GetBlocks().Count(b => b.BuiltBy == someone);
            Check(ByMe() == 3 && BySomeone() == 3, "the grid's blocks are not 3 and 3 of the two builders: " + ByMe() + " and " + BySomeone());
            var far = Vector3D.Distance(Body.PositionComp.GetPosition(), grid.PositionComp.GetPosition());
            var enemies = MySession.Static.Players.GetOnlinePlayers().Where(p => p.Character != null && p.Identity.IdentityId != Identity
                && ((IMyPlayer)p).GetRelationTo(Identity) == MyRelationsBetweenPlayerAndBlock.Enemies
                && Vector3D.Distance(p.Character.PositionComp.GetPosition(), grid.PositionComp.GetPosition()) <= 10000).ToList();
            var guarded = enemies.Count > 0 && Core.Integrations.IsLoaded(Scenarios.ConfigOverride.Gameplay);
            FakeClients.AsClient(Me, () => typeof(MyBlockLimits).GetMethod("RemoveBlocksBuiltByID", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { grid.EntityId }));
            if (guarded)
            {
                yield return Seconds(2);
                Check(ByMe() == 3 && BySomeone() == 3, "with " + enemies.Count + " enemy players within 10 km SGI let the removal through: " + ByMe() + " and " + BySomeone() + " left");
                Result("from " + F(far) + " m, with " + enemies.Count + " enemy players within 10 km: SGI refused the removal, all 6 blocks stayed");
                yield break;
            }
            yield return Until(() => ByMe(), v => v == 0, "the player's blocks taken off", 10, () => ByMe() + " of the player's left, " + BySomeone() + " of the other's");
            Check(!grid.Closed && BySomeone() == 3, "the other builder's blocks went too: " + BySomeone() + " left" + (grid.Closed ? ", the grid closed" : ""));
            Result("from " + F(far) + " m the player took off the 3 blocks they built; the other builder's 3 stayed");
        }
    }

    /// <summary>
    /// A grid's power switch (the cockpit's or terminal's "switch power", <c>MyCubeGrid.OnPowerProducerStateRequest</c> ->
    /// <c>MyResourceDistributorComponent.ChangeSourcesState</c>): off, it turns off the producers the player may use - their
    /// own and those shared with all - and not a stranger's battery on the same grid shared with nobody; on, back.
    /// </summary>
    public sealed class GridPowerSwitchCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_power_switch";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -5));
            var stranger = -1;
            yield return Join(Site.At(8, 3, -5), i => stranger = i);
            var them = FakeClients.PlayerOf(stranger).Identity.IdentityId;
            var grid = Build("power-switch", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => { Motion.Charged(b); b.Owner = them; b.ShareMode = MyOwnershipShareModeEnum.None; }),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => { Motion.Charged(b); b.Owner = them; b.ShareMode = MyOwnershipShareModeEnum.All; }),
                new Part("InteriorLight/SmallLight", 3, 0, 0));
            MyFunctionalBlock At(int x) => (MyFunctionalBlock)grid.GetCubeBlock(new Vector3I(x, 0, 0)).FatBlock;
            var mine = At(0);
            var theirs = At(1);
            var shared = At(2);
            Check(mine.OwnerId == Identity && theirs.OwnerId == them && shared.OwnerId == them, "the batteries' owners are not as built");
            string State() => "own " + mine.Enabled + ", stranger's unshared " + theirs.Enabled + ", stranger's shared with all " + shared.Enabled;
            Request(grid, "OnPowerProducerStateRequest", VRage.MyMultipleEnabledEnum.AllDisabled, false);
            yield return Until(() => mine.Enabled ? 0 : 1, v => v > 0, "the player's own battery off", 10, State);
            Check(!shared.Enabled, "the stranger's battery shared with all stayed on: " + State());
            Check(theirs.Enabled, "the stranger's battery shared with nobody went off: " + State());
            var off = State();
            Request(grid, "OnPowerProducerStateRequest", VRage.MyMultipleEnabledEnum.AllEnabled, false);
            yield return Until(() => mine.Enabled && shared.Enabled ? 1 : 0, v => v > 0, "the batteries back on", 10, State);
            Result("off: " + off + "; on: " + State());
        }
    }

    /// <summary>
    /// A sensor's filters by relation (<c>MySensorBlock</c>: "Detect Owner", "Detect Enemy" with "Detect Players"): the
    /// player and a stranger both in its field; owner only, it sees the player and not the stranger; enemy only, the
    /// stranger and not the player; neither, nobody.
    /// </summary>
    public sealed class SensorRelationsCheck : RegressionCheck
    {
        public override string Name => Prefix + "sensor_relations";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, 0));
            var stranger = -1;
            yield return Join(Site.At(2, 3, 0), i => stranger = i);
            var grid = Build("sensor-relations", Site.Origin, true,
                new Part("SensorBlock/LargeBlockSensor", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var block = Block<Sandbox.Game.Entities.Blocks.MySensorBlock>(grid);
            var sensor = (Sandbox.ModAPI.Ingame.IMySensorBlock)block;
            var me = Body.EntityId;
            var them = FakeClients.Character(stranger).EntityId;
            StandAt(Site.At(0, 3, 0), Site.Origin);
            sensor.DetectPlayers = true;
            sensor.DetectFriendly = false;
            sensor.DetectNeutral = false;
            var seen = new List<Sandbox.ModAPI.Ingame.MyDetectedEntityInfo>();
            string Seen() { seen.Clear(); sensor.DetectedEntities(seen); return string.Join(",", seen.Select(e => e.EntityId == me ? "player" : e.EntityId == them ? "stranger" : e.Name)); }
            bool Sees(long id) { seen.Clear(); sensor.DetectedEntities(seen); return seen.Any(e => e.EntityId == id); }

            sensor.DetectOwner = true;
            sensor.DetectEnemy = false;
            yield return Until(() => Sees(me) ? 1 : 0, v => v > 0, "the sensor of owner only seeing the player", 10, Seen);
            Check(!Sees(them), "owner only, the sensor sees the stranger too: " + Seen());
            var owner = Seen();

            sensor.DetectOwner = false;
            sensor.DetectEnemy = true;
            yield return Until(() => Sees(them) && !Sees(me) ? 1 : 0, v => v > 0, "the sensor of enemies only seeing the stranger and not the player", 10, Seen);
            var enemy = Seen();

            sensor.DetectEnemy = false;
            yield return Until(() => Sees(them) || Sees(me) ? 0 : 1, v => v > 0, "the sensor of neither seeing nobody", 10, Seen);
            Result("owner only: " + owner + "; enemies only: " + enemy + "; neither: nobody");
        }
    }

    /// <summary>
    /// The tiers of production (the blueprint classes of a block, <c>MyProductionBlock.CanUseBlueprint</c>): the basic
    /// assembler takes into its queue a blueprint of its classes and refuses one only the large assembler has, asked as the
    /// client asks (<c>OnAddQueueItemRequest</c>); the blast furnace refines an ore of its classes and leaves one it has no
    /// blueprint for in its input, untouched.
    /// </summary>
    public sealed class ProductionTiersCheck : RegressionCheck
    {
        public override string Name => Prefix + "production_tiers";
        public override int TimeoutSeconds => 240;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var basicDefinition = (Sandbox.Definitions.MyProductionBlockDefinition)Definition("Assembler/BasicAssembler");
            var largeDefinition = (Sandbox.Definitions.MyProductionBlockDefinition)Definition("Assembler/LargeAssembler");
            bool In(Sandbox.Definitions.MyProductionBlockDefinition d, Sandbox.Definitions.MyBlueprintDefinitionBase b) => d.BlueprintClasses.Any(c => c.ContainsBlueprint(b));
            // a component made of ingots (not the blocks' own blueprints of the build planner)
            bool Component(Sandbox.Definitions.MyBlueprintDefinitionBase b) => !b.Id.SubtypeName.Contains("/") && b.Results.Length == 1 && b.Results[0].Id.TypeId == typeof(MyObjectBuilder_Component)
                                                                                 && b.Prerequisites.Length > 0 && b.Prerequisites.All(p => p.Id.TypeId == typeof(MyObjectBuilder_Ingot));
            var largeOnly = largeDefinition.BlueprintClasses.SelectMany(c => c).FirstOrDefault(b => !In(basicDefinition, b) && Component(b));
            var basic = basicDefinition.BlueprintClasses.SelectMany(c => c).FirstOrDefault(Component);
            SkipUnless(largeOnly != null && basic != null, "no component blueprint of the large assembler alone and one of the basic");
            var grid = Build("production-tiers", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("Assembler/BasicAssembler", 2, 0, 0),
                new Part("Refinery/Blast Furnace", -3, 0, 0));
            Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
            var assembler = Block<MyAssembler>(grid);
            var furnace = Block<MyRefinery>(grid);
            Check(assembler != null && furnace != null, "the basic assembler or the blast furnace did not come up");

            // the client's queue requests
            Request(assembler, "OnAddQueueItemRequest", -1, (VRage.ObjectBuilders.SerializableDefinitionId)largeOnly.Id, (VRage.MyFixedPoint)1);
            Request(assembler, "OnAddQueueItemRequest", -1, (VRage.ObjectBuilders.SerializableDefinitionId)basic.Id, (VRage.MyFixedPoint)1);
            yield return null;
            var queued = assembler.Queue.Select(q => q.Blueprint.Id.SubtypeName).ToList();
            Check(!queued.Contains(largeOnly.Id.SubtypeName), "the basic assembler took the large assembler's " + largeOnly.Id.SubtypeName + " into its queue: " + string.Join(", ", queued));
            Check(queued.Contains(basic.Id.SubtypeName), "the basic assembler did not take its own " + basic.Id.SubtypeName + ": " + string.Join(", ", queued));
            assembler.ClearQueue();

            // an ore of the furnace's blueprints and one it has none for
            var furnaceDefinition = (Sandbox.Definitions.MyRefineryDefinition)furnace.BlockDefinition;
            var oreBlueprints = furnaceDefinition.BlueprintClasses.SelectMany(c => c).Where(b => b.Prerequisites.Length == 1 && b.Prerequisites[0].Id.TypeId == typeof(MyObjectBuilder_Ore)).ToList();
            var refined = oreBlueprints.Select(b => b.Prerequisites[0].Id.SubtypeName).ToList();
            var own = refined.Contains("Iron");
            var foreign = new[] { "Platinum", "Uranium", "Gold", "Magnesium", "Silver" }.FirstOrDefault(o => !refined.Contains(o));
            SkipUnless(own && foreign != null, "the blast furnace refines " + string.Join(", ", refined) + ": no ore of it and one it cannot");
            Put(furnace.InputInventory, "Ore/" + foreign, 100);
            Put(furnace.InputInventory, "Ore/Iron", 100);
            yield return Until(() => Amount(furnace.OutputInventory, "Ingot/Iron"), v => v > 1, "the blast furnace refining iron", 60,
                () => "input: " + string.Join(", ", furnace.InputInventory.GetItems().Select(i => i.Content.SubtypeName + " " + i.Amount)) + "; working " + furnace.IsWorking);
            yield return Seconds(3);
            Check(Amount(furnace.InputInventory, "Ore/" + foreign) == 100 && Amount(furnace.OutputInventory, "Ingot/" + foreign) == 0,
                "the blast furnace touched the " + foreign + " ore it has no blueprint for: " + Amount(furnace.InputInventory, "Ore/" + foreign) + " kg left, " + Amount(furnace.OutputInventory, "Ingot/" + foreign) + " kg of ingots");
            Result("the basic assembler refused " + largeOnly.Id.SubtypeName + " and queued " + basic.Id.SubtypeName + "; the blast furnace refined iron (" + F(Amount(furnace.OutputInventory, "Ingot/Iron")) + " kg) and left " + foreign + " ore whole");
        }
    }

    /// <summary>
    /// A refill station (<c>MedicalRoom/LargeRefillStation</c>: refuelling allowed, healing not): a player with a flat suit
    /// and hurt, asking it for help, has the suit charged and is not healed.
    /// </summary>
    public sealed class RefillStationCheck : RegressionCheck
    {
        public override string Name => Prefix + "refill_station";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("refill-station", Site.Origin, true,
                new Part("MedicalRoom/LargeRefillStation", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var station = Block<SpaceEngineers.Game.Entities.Blocks.MyMedicalRoom>(grid);
            Check(station != null, "the refill station did not come up");
            yield return Until(() => station.IsWorking ? 1 : 0, v => v > 0, "the refill station working", 10, () => State(station));
            StandAt(station.PositionComp.GetPosition() + Site.Up * 0.2, station.PositionComp.GetPosition() + Site.Forward);
            // the suite's players come from other sites: the air the game counted at the last one (a planet's) goes first,
            // or the world's auto healing heals them here
            yield return Until(() => Body.EnvironmentOxygenLevel, v => v < 0.5, "the open space's air counted at the player", 10,
                () => "the air in this point now " + F(Sandbox.Game.GameSystems.MyOxygenProviderSystem.GetOxygenInPoint(Body.PositionComp.GetPosition())) + ", the character's oxygen component " +
                      (Body.OxygenComponent == null ? "none" : "there") + ", updates " + Body.NeedsUpdate + ", in scene " + Body.InScene);
            Body.SuitBattery.ResourceSource.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, 1E-05f * 0.05f);
            var health = Body.StatComp.Health;
            health.Value = health.MaxValue * 0.4f;
            var hurt = health.Value;
            var low = Body.SuitEnergyLevel;
            yield return Until(() =>
                {
                    Request(station, "RequestSupport", Body.EntityId);
                    return Body.SuitEnergyLevel - low;
                }, v => v > 0.1, "the suit charging at the refill station", 20, () => "energy " + F(Body.SuitEnergyLevel * 100) + " %");
            for (var i = 0; i < 60; i++) { Request(station, "RequestSupport", Body.EntityId); yield return null; }
            Check(health.Value <= hurt + 0.5f, "the refill station healed the player: " + F(hurt) + " -> " + F(health.Value));
            var energy = Body.SuitEnergyLevel;
            health.Value = health.MaxValue;
            Result("at the refill station the suit charged " + F(low * 100) + " -> " + F(energy * 100) + " %, health stayed at " + F(hurt));
        }
    }

    /// <summary>
    /// A ship bought at an NPC station's store (<c>MyStoreBlock.BuyFromStation</c> -> <c>BuyPrefabInternal</c>): a grid
    /// offer of the smallest of the trader faction's ships for sale, bought by the player as the client asks: the ship
    /// comes near the station, the player's, the player paid its price, the offer one fewer.
    /// After, the station is taken off the faction and the ship removed.
    /// </summary>
    public sealed class StoreBuyShipCheck : RegressionCheck
    {
        public override string Name => Prefix + "store_buy_ship";
        public override int TimeoutSeconds => 120;

        private MyFaction _faction;
        private MyFactionStation _station;

        private static readonly FieldInfo Stations = typeof(MyFaction).GetField("m_stations", BindingFlags.Instance | BindingFlags.NonPublic);

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var economy = MySession.Static.GetComponent<Sandbox.Game.SessionComponents.MySessionComponentEconomy>();
            // a trader other than the first one when there is one: npc_station counts the first one's money to the credit,
            // and a ship's price coming in on the same frame broke its count
            var traders = MySession.Static.Factions.Select(f => f.Value).OfType<MyFaction>()
                .Where(f => f.FactionTypeString == "Trader" && f.Stations.Count > 0 && MySession.Static.Players.IdentityIsNpc(f.FounderId)).ToList();
            _faction = traders.Skip(1).FirstOrDefault() ?? traders.FirstOrDefault();
            SkipUnless(_faction != null, "no NPC trader faction in this world");
            var types = Sandbox.Definitions.MyDefinitionManager.Static.GetAllDefinitions<Sandbox.Definitions.MyFactionTypeDefinition>().Where(d => d.GridsForSale?.Length > 0).ToList();
            var type = types.FirstOrDefault(d => d.Id.SubtypeName == _faction.FactionTypeString) ?? types.FirstOrDefault();
            var prefab = type?.GridsForSale?.Select(n => Sandbox.Definitions.MyDefinitionManager.Static.GetPrefabDefinition(n)).Where(d => d?.CubeGrids != null)
                .OrderBy(d => d.CubeGrids.Sum(g => g.CubeBlocks.Count)).FirstOrDefault();
            SkipUnless(prefab != null, "the traders sell no ships in this game (" + types.Count + " faction types with ships for sale, of " + (type?.GridsForSale?.Length ?? 0) + " ships of "
                                       + type?.Id.SubtypeName + " " + (type?.GridsForSale?.Count(n => Sandbox.Definitions.MyDefinitionManager.Static.GetPrefabDefinition(n) != null) ?? 0) + " found)");
            yield return Arrive(Site.At(0, 0, -3));
            Sandbox.Game.GameSystems.BankingAndCurrency.MyBankingSystem.ChangeBalance(Identity, 100000000);
            var grid = BuildFor(_faction.FounderId, "ship-store", Site.Origin,
                new Part("StoreBlock/StoreBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var store = Block<Sandbox.Game.Entities.Blocks.MyStoreBlock>(grid);
            yield return Until(() => store.IsWorking ? 1 : 0, v => v > 0, "the store working", 30, () => State(store));
            var blocks = prefab.CubeGrids.Sum(g => g.CubeBlocks.Count);
            const int price = 50000;
            _station = new MyFactionStation(VRage.MyEntityIdentifier.AllocateId(VRage.MyEntityIdentifier.ID_OBJECT_TYPE.STATION), "Regression ship store", grid.PositionComp.GetPosition(),
                MyStationTypeEnum.SpaceStation, _faction, "", null) { StationEntityId = grid.EntityId };
            var offer = new Sandbox.Game.Entities.Blocks.MyStoreItem(VRage.MyEntityIdentifier.AllocateId(VRage.MyEntityIdentifier.ID_OBJECT_TYPE.STORE_ITEM), prefab.Id.SubtypeName, 2, price, blocks, VRage.Game.ObjectBuilders.Definitions.StoreItemTypes.Offer);
            _station.StoreItems.Add(offer);
            _faction.AddStation(_station);
            var tick = (double)typeof(Sandbox.Game.SessionComponents.MySessionComponentEconomy).GetProperty("LastEconomyTick", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(economy);
            var money = Sandbox.Game.GameSystems.BankingAndCurrency.MyBankingSystem.GetBalance(Identity);
            var factionMoney = Sandbox.Game.GameSystems.BankingAndCurrency.MyBankingSystem.GetBalance(_faction.FactionId);
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            Request(store, "BuyItem", offer.Id, 1, Body.EntityId, tick);
            // the new grids of the player's and their subgrids: sites are 1000 m apart, and a grid another check makes near
            // by (a rotor's head let go) is not the ship
            List<MyCubeGrid> Bought() => MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !before.Contains(g.EntityId) && !g.MarkedForClose
                && Vector3D.Distance(g.PositionComp.GetPosition(), grid.PositionComp.GetPosition()) < 2000 && g.BigOwners.Contains(Identity))
                .SelectMany(g => MyCubeGridGroups.Static.Mechanical.GetGroupNodes(g)).Distinct().ToList();
            yield return Until(() => Bought().Count, v => v > 0, "the bought ship " + prefab.Id.SubtypeName + " by the station", 30,
                () => "offer left " + offer.Amount + ", paid " + (money - Sandbox.Game.GameSystems.BankingAndCurrency.MyBankingSystem.GetBalance(Identity)));
            yield return Seconds(1);
            var ships = Bought();
            foreach (var g in ships) Track(g);
            var ship = ships.OrderByDescending(g => g.BlocksCount).First();
            Check(ship.BigOwners.Contains(Identity), "the bought ship is not the player's (owners " + string.Join(", ", ship.BigOwners) + ")");
            var paid = money - Sandbox.Game.GameSystems.BankingAndCurrency.MyBankingSystem.GetBalance(Identity);
            var got = Sandbox.Game.GameSystems.BankingAndCurrency.MyBankingSystem.GetBalance(_faction.FactionId) - factionMoney;
            // the faction's own account moves with the other checks of its stations in the suite: only the player's is exact here
            Check(paid == price, "the player paid " + paid + " for a ship of " + price);
            Check(offer.Amount == 1, "the offer has " + offer.Amount + " ships left of 2");
            var off = Vector3D.Distance(ship.PositionComp.GetPosition(), grid.PositionComp.GetPosition());
            Result("bought " + prefab.Id.SubtypeName + " (" + ship.BlocksCount + " blocks) for " + paid + ": " + F(off) + " m from the station, the player's, the faction's account +" + got + ", 1 left");
        }

        public override void Cleanup()
        {
            try
            {
                if (_station != null && Stations?.GetValue(_faction) is Dictionary<long, IMyFactionStation> stations) stations.Remove(_station.Id);
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A programmable block's script out of bounds (<c>MyProgrammableBlock.ExecuteCode</c>): a run that loops past the
    /// instruction limit is cut off ("too complex": the script is stopped, <c>InstructionOverflow</c>), one that throws is
    /// stopped too (<c>RuntimeException</c>), and the server goes on; recompiled, the script runs again.
    /// </summary>
    public sealed class ProgrammableLimitsCheck : RegressionCheck
    {
        public override string Name => Prefix + "programmable_limits";

        private const string Script = @"
public void Main(string argument)
{
    if (argument == ""loop"") { long i = 0; while (true) i++; }
    if (argument == ""throw"") throw new System.InvalidOperationException(""regression throw"");
    Me.CustomData = ""ran "" + argument;
}";

        private static readonly FieldInfo Reason = typeof(Sandbox.Game.Entities.Blocks.MyProgrammableBlock).GetField("m_terminationReason", BindingFlags.Instance | BindingFlags.NonPublic);

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.EnableIngameScripts, "in-game scripts are off in this world");
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("programmable-limits", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", 1, 0, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_MyProgrammableBlock)b).Program = Script));
            var pb = Block<Sandbox.Game.Entities.Blocks.MyProgrammableBlock>(grid);
            yield return Until(() => Scripts.Ready(pb) ? 1 : 0, v => v > 0, "the script compiled", 30, () => State(pb) + "; " + pb.DetailedInfo);
            string Why() => Reason?.GetValue(pb)?.ToString() ?? "?";
            void Run(string argument) => Request(pb, "RunProgramRequest", Scripts.Argument(argument), Sandbox.ModAPI.Ingame.UpdateType.Terminal);
            void Recompile() => typeof(Sandbox.Game.Entities.Blocks.MyProgrammableBlock).GetMethod("Recompile", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pb, new object[] { true });

            Run("ok");
            yield return Until(() => pb.CustomData == "ran ok" ? 1 : 0, v => v > 0, "the script's normal run", 10, () => pb.DetailedInfo.ToString());

            var frame = MySession.Static.GameplayFrameCounter;
            Run("loop");
            yield return null;
            Check(MySession.Static.GameplayFrameCounter > frame, "the game did not go on after the endless loop");
            Check(Why() == "InstructionOverflow" && !Scripts.Ready(pb), "the endless loop was not cut off as too complex: " + Why() + "; " + pb.DetailedInfo.ToString().Trim());
            var tooComplex = pb.DetailedInfo.ToString().Trim().Replace("\n", " ");

            Recompile();
            yield return Until(() => Scripts.Ready(pb) ? 1 : 0, v => v > 0, "the script recompiled", 30, () => pb.DetailedInfo.ToString());
            Run("throw");
            yield return null;
            Check(Why() == "RuntimeException" && !Scripts.Ready(pb), "the throwing script was not stopped: " + Why() + "; " + pb.DetailedInfo.ToString().Trim());

            Recompile();
            yield return Until(() => Scripts.Ready(pb) ? 1 : 0, v => v > 0, "the script recompiled again", 30, () => pb.DetailedInfo.ToString());
            Run("again");
            yield return Until(() => pb.CustomData == "ran again" ? 1 : 0, v => v > 0, "the recompiled script running", 10, () => pb.DetailedInfo.ToString());
            Result("the endless loop cut off (" + tooComplex + "), the throw stopped the script, recompiled it ran again");
        }
    }

    /// <summary>
    /// The pilot's weight on a ship (<c>MyCubeGrid.GetCurrentMass</c>, the mass the ship's controls and thrusters go by):
    /// seated in its cockpit, the player adds their own mass and what they carry; out of it, the ship is as it was.
    /// </summary>
    public sealed class PilotMassCheck : RegressionCheck
    {
        public override string Name => Prefix + "pilot_mass";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -4));
            var ship = Build("pilot-mass", Site.Origin, false,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var seat = Block<MyCockpit>(ship);
            yield return Seconds(1);
            var empty = ship.GetCurrentMass();
            var dirtyField = typeof(MyCubeGrid).GetField("m_inventoryMassDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var adjusted = typeof(Sandbox.Game.Entities.Cube.MyGridShape).GetField("m_massAdjustedForItemsAndPilot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var before = "before sitting: shape mass " + F(ship.Physics?.Shape?.MassProperties?.Mass ?? -1) + ", dirty " + dirtyField?.GetValue(ship) + ", adjusted " + adjusted?.GetValue(ship.Physics?.Shape);
            Put(Inventory(Body), "Ore/Iron", 500);
            var carried = (double)Inventory(Body).CurrentMass;
            var own = Body.BaseMass;
            yield return Sit(seat);
            yield return Until(() => ship.GetCurrentMass() - empty, v => Math.Abs(v - (own + carried)) < 1, "the ship's mass up by the pilot and what they carry (" + F(own) + " + " + F(carried) + " kg)", 10,
                () => "the ship " + F(empty) + " -> " + F(ship.GetCurrentMass()) + " kg, seated " + (Body.Parent == seat));
            var seated = ship.GetCurrentMass();
            before += "; seated: shape mass " + F(ship.Physics?.Shape?.MassProperties?.Mass ?? -1) + ", adjusted " + adjusted?.GetValue(ship.Physics?.Shape);
            seat.RemovePilot();
            yield return Until(() => Math.Abs(ship.GetCurrentMass() - empty), v => v < 1, "the ship's mass back once the pilot is out", 10, () => F(ship.GetCurrentMass()) + " kg; pilot " +
                (seat.Pilot?.DisplayName ?? "none") + ", occupied blocks " + ship.OccupiedBlocks.Count + ", shape mass " + F(ship.Physics?.Shape?.MassProperties?.Mass ?? -1) +
                ", inventory mass dirty " + typeof(MyCubeGrid).GetField("m_inventoryMassDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(ship) +
                ", player's body " + (Body == null ? "none" : Body.Parent == null ? "free" : "in " + Body.Parent.GetType().Name) + "; " + before);
            Result("the ship " + F(empty) + " kg; with the pilot (" + F(own) + " kg) carrying " + F(carried) + " kg of ore: " + F(seated) + " kg; out: " + F(ship.GetCurrentMass()) + " kg; " + before);
        }
    }

    /// <summary>
    /// Parking (the pilot's P, <c>MyCubeGrid.ToggleParkStateRequest</c>: the landing gears and connectors of the ship
    /// lock, its wheels brake): a ship whose gear is over a station's floor, parked, has the gear locked on the floor;
    /// released, unlocked.
    /// </summary>
    public sealed class ParkingCheck : RegressionCheck
    {
        public override string Name => Prefix + "parking";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -12));
            var floor = new List<Part>();
            for (var x = -1; x <= 1; x++)
            for (var z = -1; z <= 1; z++)
                floor.Add(new Part("CubeBlock/LargeHeavyBlockArmorBlock", x, 0, z));
            var station = Build("parking-floor", Site.Origin - Site.Up * 1.25, true, floor);
            // which face of the gear grips: the one that comes ready to lock over the floor
            foreach (var face in new[] { Base6Directions.Direction.Down, Base6Directions.Direction.Forward, Base6Directions.Direction.Backward,
                         Base6Directions.Direction.Up, Base6Directions.Direction.Left, Base6Directions.Direction.Right })
            {
                var up = Base6Directions.GetPerpendicular(face);
                var ship = Build("parking-ship", Site.Origin + Site.Up * (1.25 + 0.3), false, new[]
                {
                    new Part("LandingGear/LargeBlockLandingGear", 0, 0, 0).Facing(face, up),
                });
                var gear = (SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear)Block<SpaceEngineers.Game.Entities.Blocks.MyLandingGear>(ship);
                for (var f = Frames(3); f > 0 && gear != null && gear.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Unlocked; f--)
                    yield return null;
                if (gear == null || gear.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Unlocked)
                {
                    yield return CloseAndWait(ship);
                    continue;
                }
                // the request as the pilot's P sends it (a ship of the gear alone: no seat to fit about it in every turn)
                Request(ship, "ToggleParkStateRequest");
                yield return Until(() => gear.IsLocked && ship.IsParked ? 1 : 0, v => v > 0, "the ship parked, its gear locked on the floor", 10,
                    () => "gear " + gear.LockMode + ", parked " + ship.IsParked);
                Request(ship, "ToggleParkStateRequest");
                yield return Until(() => !gear.IsLocked && !ship.IsParked ? 1 : 0, v => v > 0, "the ship released, its gear unlocked", 10,
                    () => "gear " + gear.LockMode + ", parked " + ship.IsParked);
                Result("parked the ship (gear facing " + face + " locked on the station's floor) and released it");
                yield break;
            }
            Check(false, "no face of the gear came ready to lock over the floor");
        }
    }
}
