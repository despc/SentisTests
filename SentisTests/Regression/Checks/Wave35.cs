using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A character in a cryo chamber is kept (<c>MySessionComponentTrash.RemoveCharacter</c> does not take one sitting in
    /// a cryo chamber): a player who logged off in their cryo chamber 100 hours ago, with "remove characters after" 60
    /// minutes - the character stays in the chamber. One who logged off standing beside it is removed.
    /// </summary>
    public sealed class CryoOfflineCheck : RegressionCheck
    {
        public override string Name => Prefix + "cryo_offline";

        private readonly SessionSettingsOverride _settings = new SessionSettingsOverride();

        protected override IEnumerator Check()
        {
            var trash = MySession.Static.GetComponent<MySessionComponentTrash>();
            Check(trash != null, "no trash removal component in this session");
            var checkIdentity = typeof(MySessionComponentTrash).GetMethod("CheckIdentity", BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? throw new Core.ScenarioFailedException("MySessionComponentTrash.CheckIdentity is not in this game");
            var list = typeof(MySessionComponentTrash).GetField("m_allIdentities", BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new Core.ScenarioFailedException("MySessionComponentTrash.m_allIdentities is not in this game");
            yield return Arrive(Site.At(0, 4, -6));
            var sleeper = Me;
            var standing = -1;
            yield return Join(Site.At(3, 4, -6), i => standing = i);
            var sleeperIdentity = Player.Identity;
            var standingIdentity = FakeClients.PlayerOf(standing).Identity;
            var grid = Build("cryo-offline", Site.Origin, true, new[]
            {
                new Part("CryoChamber/LargeBlockCryoChamber", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)),
            }, ownedBy: sleeperIdentity.IdentityId);
            // the standing player has a block too: the identity stays, only the character is up for removal
            Build("cryo-offline-other", Site.At(0, 0, 12), true, new[] { new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0) }, ownedBy: standingIdentity.IdentityId);
            var cryo = Block<Sandbox.Game.Entities.Blocks.MyCryoChamber>(grid);
            var asleep = Body;
            var awake = FakeClients.Character(standing);
            yield return Until(() => cryo.IsWorking ? 1 : 0, v => v > 0, "the cryo chamber working", 10, () => State(cryo));
            yield return Sit(cryo);
            FakeClients.Quit(sleeper);
            FakeClients.Quit(standing);
            yield return Seconds(1);
            Check(cryo.Pilot == asleep && !asleep.MarkedForClose, "the character left the cryo chamber when its player logged off");
            sleeperIdentity.LastLogoutTime = DateTime.Now - TimeSpan.FromHours(100);
            standingIdentity.LastLogoutTime = DateTime.Now - TimeSpan.FromHours(100);
            _settings.Set("PlayerCharacterRemovalThreshold", 60);
            try
            {
                foreach (var identity in new[] { sleeperIdentity, standingIdentity })
                {
                    var identities = (List<MyIdentity>)list.GetValue(trash);
                    if (identities.IndexOf(identity) < 0) list.SetValue(trash, MySession.Static.Players.GetAllIdentities().ToList());
                    var index = ((List<MyIdentity>)list.GetValue(trash)).IndexOf(identity);
                    Check(index >= 0, identity.DisplayName + " is not among the identities the trash removal looks at");
                    checkIdentity.Invoke(trash, new object[] { index });
                }
            }
            finally { _settings.Restore(); }
            yield return Seconds(1);
            Check(!asleep.MarkedForClose && cryo.Pilot == asleep, "the character in the cryo chamber was removed");
            Check(awake.MarkedForClose, "the character standing in the world was not removed (characters go after 60 min)");
            Result("100 h offline: the character in its cryo chamber kept, the one standing outside removed");
        }

        public override void Cleanup()
        {
            try { _settings.Restore(); }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A ship flown from afar by its remote control (<c>MyRemoteControl.RequestUseMessage</c>, <c>CanControl</c>: the
    /// player's suit radio and the ship's antenna must reach each other, <c>MyAntennaSystem.CheckConnection</c>, mutual):
    /// a player 150 m off, inside the suit radio's 200 m, with the ship's antenna on takes control and the ship moves on
    /// their keys; with the antenna off, and from 300 m with it on, the request is refused.
    /// </summary>
    public sealed class RemoteControlCheck : RegressionCheck
    {
        public override string Name => Prefix + "remote_control";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -150));
            var ship = Build("remote-control", Site.Origin, false,
                new Part("RemoteControl/LargeBlockRemoteControl", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, -1, 1),
                new Part("RadioAntenna/LargeBlockRadioAntenna", 0, 1, 0),
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Base6Directions.Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Base6Directions.Direction.Forward));
            var remote = Block<MyRemoteControl>(ship);
            var antenna = Block<MyRadioAntenna>(ship);
            ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueFloat("Radius", 2000f);
            ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueBool("EnableBroadCast", true);
            Body.EnableBroadcastingPlayerToggle(true);
            Request(Body, "EnableBroadcasting", true);
            // the suit's radio is on while the suit's battery gives power (counted every 100 frames) and the link is
            // worked out again every ~100 frames
            yield return Seconds(4);
            var body = Body;

            // the antenna off: no link, refused
            Switch(antenna, false);
            yield return Seconds(4);
            Request(remote, "RequestUseMessage", VRage.Game.Entity.UseObject.UseActionEnum.Manipulate, body.EntityId);
            yield return Seconds(1);
            Check(Player.Controller.ControlledEntity != remote, "the player took the remote control 150 m off with the ship's antenna off");

            // the antenna on, the player out of the suit radio's reach
            Switch(antenna, true);
            FakeClients.MoveTo(Me, Site.At(0, 0, -300));
            yield return Seconds(4);
            Request(remote, "RequestUseMessage", VRage.Game.Entity.UseObject.UseActionEnum.Manipulate, body.EntityId);
            yield return Seconds(1);
            Check(Player.Controller.ControlledEntity != remote, "the player took the remote control 300 m off, out of the suit radio's 200 m");
            FakeClients.MoveTo(Me, Site.At(0, 0, -150));
            yield return Seconds(4);
            yield return Until(() =>
                {
                    if (Player.Controller.ControlledEntity == remote) return 1;
                    Request(remote, "RequestUseMessage", VRage.Game.Entity.UseObject.UseActionEnum.Manipulate, body.EntityId);
                    return 0;
                }, v => v > 0, "the player controlling the ship through its antenna", 20,
                () => "the suit's radio " + (body.RadioBroadcaster.Enabled ? "on" : "off") + " (" + F(body.RadioBroadcaster.BroadcastRadius) + " m), the antenna " + State(antenna) +
                      ", the remote " + State(remote) + "; can control " + remote.CanControl(body) + ", linked " +
                      Sandbox.Game.GameSystems.MyAntennaSystem.Static.CheckConnection(body, ship, Player) + ", can use " +
                      remote.GetType().GetMethod("CanUse", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?
                          .Invoke(remote, new object[] { VRage.Game.Entity.UseObject.UseActionEnum.Manipulate, body }) + ", controlled " + (Player.Controller.ControlledEntity as VRage.Game.Entity.MyEntity)?.DisplayName);
            var from = ship.PositionComp.GetPosition();
            yield return HoldShip(3, remote, new Vector3(0, 0, -1));
            var gone = Vector3D.Dot(ship.PositionComp.GetPosition() - from, remote.WorldMatrix.Forward);
            Check(gone > 1, "3 s of forward on the remote control took the ship " + F(gone) + " m");
            remote.ForceReleaseControl();
            Result("antenna off: refused; from 300 m: refused; from 150 m with the antenna on the player took the remote control and flew the ship " + F(gone) + " m");
        }
    }

    /// <summary>
    /// The consumable kits (<c>MyInventory.InventoryConsumeItem_Implementation</c>, what using one from the inventory
    /// sends; their stats act over time, and another kit is not taken while one acts): a medkit heals, a power kit charges
    /// the suit, a radiation kit takes the radiation down - each by its definition's stat.
    /// </summary>
    public sealed class ConsumableKitsCheck : RegressionCheck
    {
        public override string Name => Prefix + "consumable_kits";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var body = Body;
            var suit = Inventory(body);
            void Use(string kit)
            {
                Put(suit, "ConsumableItem/" + kit, 1);
                Request(suit, "InventoryConsumeItem_Implementation", (VRage.MyFixedPoint)1, (SerializableDefinitionId)Item("ConsumableItem/" + kit), body.EntityId);
                Check(Amount(suit, "ConsumableItem/" + kit) == 0, "the " + kit + " was not used up");
            }

            var health = body.StatComp.Health;
            health.Value = health.MaxValue * 0.4f;
            var hurt = health.Value;
            Use("Medkit");
            yield return Until(() => health.Value - hurt, v => v >= 5, "health up after the medkit", 30);
            var healed = health.Value;
            health.Value = health.MaxValue;
            // a kit is not taken while another one still acts (the medkit's stat goes on 10 s)
            yield return Seconds(11);

            var source = body.SuitBattery.ResourceSource;
            source.SetRemainingCapacityByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId, 1E-05f * 0.1f);
            var low = body.SuitEnergyLevel;
            Use("Powerkit");
            yield return Until(() => body.SuitEnergyLevel - low, v => v >= 0.05, "the suit charged by the power kit", 30, () => "energy " + F(body.SuitEnergyLevel * 100) + " %");
            var charged = body.SuitEnergyLevel;
            yield return Seconds(11);

            string radiationNote = "no radiation stat here";
            if (body.StatComp.TryGetStat(VRage.Utils.MyStringHash.GetOrCompute("Radiation"), out var radiation))
            {
                radiation.Value = radiation.MaxValue * 0.5f;
                var dose = radiation.Value;
                Use("RadiationKit");
                yield return Until(() => dose - radiation.Value, v => v > 0, "the radiation down after the radiation kit", 30);
                radiationNote = "radiation " + F(dose) + " -> " + F(radiation.Value);
                radiation.Value = radiation.MinValue;
            }
            Result("medkit: health " + F(hurt) + " -> " + F(healed) + "; power kit: suit " + F(low * 100) + " -> " + F(charged * 100) + " %; radiation kit: " + radiationNote);
        }
    }

    /// <summary>
    /// Inertial dampeners off (<c>MyShipController.SwitchDamping</c>, the pilot's Z): a ship pushed forward and let go
    /// keeps its speed instead of stopping; the save has them off.
    /// </summary>
    public sealed class DampenersOffCheck : RegressionCheck
    {
        public override string Name => Prefix + "dampeners_off";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 6));
            var ship = Build("dampeners-off", Site.Origin, false,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, 1, 0),
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Base6Directions.Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Base6Directions.Direction.Forward));
            var seat = Block<MyCockpit>(ship);
            yield return Sit(seat);
            var thrust = ship.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>();
            Check(thrust != null && thrust.DampenersEnabled, "the ship's dampeners are not on from the start");
            seat.SwitchDamping();
            yield return WaitForTicks(2);
            Check(!thrust.DampenersEnabled, "the dampeners did not switch off");
            Check(!((MyObjectBuilder_CubeGrid)ship.GetObjectBuilder()).DampenersEnabled, "the grid's save has the dampeners on");
            yield return HoldShip(2, seat, new Vector3(0, 0, -1));
            var speed = Motion.Speed(ship);
            Check(speed > 1, "2 s of forward thrust gave the ship " + F(speed) + " m/s");
            yield return HoldShip(3, seat, Vector3.Zero);
            var after = Motion.Speed(ship);
            Check(after > speed * 0.8, "with the dampeners off the let-go ship slowed from " + F(speed) + " to " + F(after) + " m/s");
            seat.SwitchDamping();
            Result("dampeners off (saved off): let go at " + F(speed) + " m/s, the ship drifted on at " + F(after) + " m/s");
        }
    }

    /// <summary>
    /// A rotor's displacement (terminal "Displacement"): the head moves along the rotor's axis by what the setting changes.
    /// </summary>
    public sealed class RotorDisplacementCheck : RegressionCheck
    {
        public override string Name => Prefix + "rotor_displacement";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 4, 0));
            var grid = Build("rotor-displacement", Site.Origin, true,
                new Part("MotorStator/LargeStator", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var stator = Block<MyMotorStator>(grid);
            yield return AddHead(stator);
            var terminal = (Sandbox.ModAPI.IMyTerminalBlock)stator;
            terminal.SetValueFloat("Velocity", 0f);
            var axis = stator.WorldMatrix.Up;
            double Along() => Vector3D.Dot(stator.TopBlock.PositionComp.GetPosition() - stator.PositionComp.GetPosition(), axis);
            terminal.SetValueFloat("Displacement", -0.3f);
            yield return Seconds(2);
            var low = Along();
            terminal.SetValueFloat("Displacement", 0.2f);
            yield return Until(() => Along() - low, v => v > 0.4, "the head moved out along the axis", 10, () => "moved " + F(Along() - low) + " m");
            Result("displacement -0.3 -> 0.2: the head moved " + F(Along() - low) + " m along the rotor's axis");
        }
    }

    /// <summary>
    /// Where a respawn is refused (<c>MySpaceRespawnComponent.FindRespawnById</c>): at the player's own medical room with no
    /// power, and at another player's medical room shared with nobody, the dead player gets no new character; at their own
    /// medical room powered, they do.
    /// </summary>
    public sealed class RespawnRefusedCheck : RegressionCheck
    {
        public override string Name => Prefix + "respawn_refused";
        public override int TimeoutSeconds => 120;
        protected override bool NeedsDamage => true;

        private bool Ask(MyCubeBlock at)
        {
            var message = new Sandbox.Game.Multiplayer.MyPlayerCollection.RespawnMsg
            {
                JoinGame = false, NewIdentity = false, RespawnEntityId = at.EntityId, RespawnShipId = null, PlayerSerialId = 0, Color = Color.Red,
            };
            Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "OnRespawnRequest", message);
            return true;
        }

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            var other = -1;
            yield return Join(Site.At(10, 6, -10), i => other = i);
            var stranger = FakeClients.PlayerOf(other).Identity.IdentityId;
            var dark = Build("respawn-dark", Site.At(-8, 0, 0), true, new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0));
            var foreign = Build("respawn-foreign", Site.At(0, 0, 0), true, new[]
            {
                new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.None),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)),
            }, ownedBy: stranger);
            var own = Build("respawn-own", Site.At(8, 0, 0), true,
                new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var darkRoom = dark.GetCubeBlock(Vector3I.Zero).FatBlock;
            var foreignRoom = foreign.GetCubeBlock(Vector3I.Zero).FatBlock;
            var ownRoom = own.GetCubeBlock(Vector3I.Zero).FatBlock;
            yield return Until(() => foreignRoom.IsWorking && ownRoom.IsWorking ? 1 : 0, v => v > 0, "the powered medical rooms working", 10);
            Check(!darkRoom.IsWorking, "the medical room with no power works");
            var body = Body;
            body.DoDamage(1000f, MyDamageType.Suicide, true, attackerId: 0);
            yield return Until(() => body.IsDead ? 1 : 0, v => v > 0, "the player dead", 10);
            bool Revived() => Player.Character != null && Player.Character != body && !Player.Character.IsDead;
            foreach (var refused in new[] { darkRoom, foreignRoom })
            {
                Ask(refused);
                yield return Seconds(3);
                Check(!Revived(), "the player respawned at " + (refused == darkRoom ? "their medical room with no power" : "another player's medical room shared with nobody"));
            }
            Ask(ownRoom);
            yield return Until(() => Revived() ? 1 : 0, v => v > 0, "the player respawned at their own powered medical room", 20);
            Track(Body);
            var off = Vector3D.Distance(Body.PositionComp.GetPosition(), ownRoom.PositionComp.GetPosition());
            Check(off < 10, "the new astronaut is " + F(off) + " m from the player's medical room");
            Result("respawn refused at an unpowered medical room and at another player's unshared one; done at the player's own, " + F(off) + " m from it");
        }
    }
}
