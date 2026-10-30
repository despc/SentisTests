using System.Collections;
using System.Reflection;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Game;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A defensive combat block's other way to flee (<c>FleeMode.AwayFromTarget</c>) on its other trigger
    /// (<c>FleeTrigger.WhenTakingDamage</c>): a drone with an AI flight block by an enemy station 150 m off sits still
    /// until it is hurt; hurt, it flees, away from the enemy.
    /// </summary>
    public sealed class DefensiveFleeDamageCheck : RegressionCheck
    {
        public override string Name => Prefix + "defensive_flee_damage";
        public override int TimeoutSeconds => 150;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            var enemy = -1;
            var target = Site.At(150, 0, 0);
            yield return Join(target + Site.Up * 10 + Site.Forward * 10, i => enemy = i);
            var enemyId = FakeClients.PlayerOf(enemy).Identity.IdentityId;
            var station = BuildFor(enemyId, "flee-damage-target", target,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var drone = Build("flee-damage-drone", Site.Origin, false, Drones.Parts(new Part("DefensiveCombatBlock/LargeDefensiveCombat", 1, 1, 0).With(b =>
            {
                b.GetType().GetField("FleeMode").SetValue(b, VRage.Game.ModAPI.Ingame.FleeMode.AwayFromTarget);
                b.GetType().GetField("FleeTrigger").SetValue(b, VRage.Game.ModAPI.Ingame.FleeTrigger.WhenTakingDamage);
            })));
            yield return null;
            var defensive = drone.GetFatBlocks().OfType<Sandbox.ModAPI.IMyDefensiveCombatBlock>().FirstOrDefault();
            Check(defensive != null, "no defensive combat block on the drone");
            var block = (MyCubeBlock)defensive;
            yield return Drones.Activate((MyCubeBlock)drone.GetCubeBlock(Vector3I.Zero).FatBlock);
            yield return Drones.Activate(block);
            var fleeing = block.GetType().GetProperty("IsFleeing");
            bool Fleeing() => fleeing != null && (bool)fleeing.GetValue(block);
            double Off() => Vector3D.Distance(drone.PositionComp.WorldAABB.Center, station.PositionComp.WorldAABB.Center);
            // unhurt it stays: no flight for 8 s
            yield return Seconds(8);
            Check(!Fleeing(), "the defensive block fled unhurt (" + Drones.Info(block) + ")");
            var still = Off();
            // hurt by the enemy (a hit on a block of the drone other than the defensive one, in the enemy's name)
            var armor = drone.CubeBlocks.First(s => s.FatBlock != block);
            armor.DoDamage(50f, MyDamageType.Bullet, true, null, station.EntityId);
            yield return Until(() => Fleeing() ? 1 : 0, v => v > 0, "the defensive block fleeing once hurt", 30, () => Drones.Info(block));
            yield return Until(Off, v => v > still + 30, "the drone fleeing away from the enemy, m off it", 60, () => F(Motion.Speed(drone)) + " m/s; " + Drones.Info(block));
            Result("unhurt it stayed (" + F(still) + " m off the enemy); hurt, it fled away from it to " + F(Off()) + " m");
        }
    }

    /// <summary>
    /// Two players at one assembler at once: both queue steel plates through the queue request a client sends
    /// (<c>OnAddQueueItemRequest</c>), one then takes items out of the output while the other keeps queueing. The
    /// assembler makes at least the sum queued, and both players' requests counted (the queue is the server's, not the
    /// client's to own).
    /// </summary>
    public sealed class SharedAssemblerCheck : RegressionCheck
    {
        public override string Name => Prefix + "shared_assembler";
        public override int TimeoutSeconds => 120;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var other = -1;
            yield return Join(Site.At(3, 8, -8), i => other = i);
            var grid = Build("shared-assembler", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("Assembler/LargeAssembler", 1, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All));
            var assembler = Block<MyAssembler>(grid);
            Put(assembler.InputInventory, "Ingot/Iron", 1000);
            var blueprint = Blueprints.Get("SteelPlate");
            // both players queue at the same moment, the server takes both
            Request(assembler, "OnAddQueueItemRequest", -1, (VRage.ObjectBuilders.SerializableDefinitionId)blueprint.Id, (VRage.MyFixedPoint)6);
            RequestAs(other, assembler, "OnAddQueueItemRequest", -1, (VRage.ObjectBuilders.SerializableDefinitionId)blueprint.Id, (VRage.MyFixedPoint)6);
            yield return Until(() => Amount(assembler.OutputInventory, "Component/SteelPlate"), v => v >= 12, "12 plates from the two players' queues", 90,
                () => "output " + F(Amount(assembler.OutputInventory, "Component/SteelPlate")));
            Check(Amount(assembler.OutputInventory, "Component/SteelPlate") >= 12, "the shared assembler made " + F(Amount(assembler.OutputInventory, "Component/SteelPlate")) + " plates of the 12 queued by two players");
            Result("two players queued 6 plates each in one shared assembler at once: " + F(Amount(assembler.OutputInventory, "Component/SteelPlate")) + " made");
        }
    }

    /// <summary>
    /// A player leaves the server while holding a programmable block's editor open (<c>MyProgrammableBlock</c>): the block
    /// lets the editor go when the client is removed (<c>ProgrammableBlock_ClientRemoved</c>), and another player can open
    /// it afterwards. (Before, a holder gone from the server left the editor shut to everyone until the block was reloaded.)
    /// </summary>
    public sealed class ProgrammableEditorQuitCheck : RegressionCheck
    {
        public override string Name => Prefix + "programmable_editor_quit";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo Open = typeof(Sandbox.Game.Entities.Blocks.MyProgrammableBlock).GetField("m_consoleOpen", Any);
        private static readonly PropertyInfo Holder = typeof(Sandbox.Game.Entities.Blocks.MyProgrammableBlock).GetProperty("UserId", Any);

        protected override IEnumerator Check()
        {
            Check(Open != null && Holder != null, "MyProgrammableBlock has no m_consoleOpen or UserId in this game");
            yield return Arrive(Site.At(0, 0, -4));
            var other = -1;
            yield return Join(Site.At(2, 0, -4), i => other = i);
            var grid = Build("pb-editor-quit", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", 1, 0, 0));
            var pb = Block<Sandbox.Game.Entities.Blocks.MyProgrammableBlock>(grid);
            ulong Steam(int client) => FakeClients.PlayerOf(client).Id.SteamId;
            RequestAs(other, pb, "OpenEditorRequest");
            Check((bool)Open.GetValue(pb) && (ulong)Holder.GetValue(pb) == Steam(other), "the editor not given to the first player (open " + Open.GetValue(pb) + ", holder " + Holder.GetValue(pb) + ")");
            FakeClients.Quit(other);
            yield return Seconds(1);
            Check(!(bool)Open.GetValue(pb), "the editor stayed open after its holder left the server (holder " + Holder.GetValue(pb) + ")");
            Request(pb, "OpenEditorRequest");
            Check((bool)Open.GetValue(pb) && (ulong)Holder.GetValue(pb) == Steam(Me), "another player could not open the editor after the holder left (open " + Open.GetValue(pb) + ", holder " + Holder.GetValue(pb) + ")");
            Request(pb, "CloseEditor");
            Result("the holder left the server: the editor was let go, and another player opened it");
        }
    }

    /// <summary>
    /// The server caps a jump at the drive's reach (<c>MyGridJumpDriveSystem.Jump</c>, <c>GetMaxJumpDistance</c>): a ship
    /// with one jump drive asked to jump far past its maximum jumps only its maximum, not where it was told. (The maximum
    /// is the drive's MaxJumpDistance for a light ship, less as the ship's mass nears MaxJumpMass.)
    /// </summary>
    public sealed class JumpDriveDistanceCheck : RegressionCheck
    {
        public override string Name => Prefix + "jump_drive_distance";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 8));
            var definition = (Sandbox.Definitions.MyJumpDriveDefinition)Definition("JumpDrive/LargeJumpDrive");
            var ship = Build("jump-distance", Site.Origin, false,
                new Part("JumpDrive/LargeJumpDrive", 0, 0, 0).With(b => ((MyObjectBuilder_JumpDrive)b).StoredPower = definition.PowerNeededForJump),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 3, 0, 0).With(b => Motion.Charged(b)),
                new Part("Cockpit/LargeBlockCockpitSeat", 3, 1, 0));
            var drive = Block<MyJumpDrive>(ship);
            yield return Until(() => drive.IsWorking ? 1 : 0, v => v > 0, "the jump drive working", 10, () => State(drive));
            var mass = ship.GetCurrentMass();
            var max = System.Math.Min(definition.MaxJumpDistance, definition.MaxJumpDistance * (definition.MaxJumpMass / mass));
            var from = ship.PositionComp.GetPosition();
            // ask to jump ten times the drive's reach straight up
            var target = from + Site.Up * (max * 10);
            Request(ship, "OnJumpRequested", target, Identity, 10f, (long?)null);
            yield return Until(() => ship.GridSystems.JumpSystem.IsJumping ? 1 : 0, v => v > 0, "the jump counting down", 5);
            yield return Until(() => Vector3D.Distance(ship.PositionComp.GetPosition(), from), v => v > max * 0.5, "the ship jumped, m", 30);
            yield return Seconds(1);
            var jumped = Vector3D.Distance(ship.PositionComp.GetPosition(), from);
            Check(jumped <= max * 1.05, "the ship jumped " + F(jumped / 1000) + " km, past the drive's reach " + F(max / 1000) + " km");
            Check(jumped > max * 0.5, "the ship jumped only " + F(jumped / 1000) + " km of the reach " + F(max / 1000) + " km");
            Result("asked to jump " + F(max * 10 / 1000) + " km, the server capped it at the drive's reach: jumped " + F(jumped / 1000) + " km of " + F(max / 1000) + " km max");
        }
    }

    /// <summary>
    /// A ship heavier than its jump drive's MaxJumpMass jumps at the drive's full reach while its mass grows a little in
    /// the countdown (cargo coming in), as a working ship's does (gas, ice): it jumps. The reach of such a ship is less
    /// as it is heavier, and the game, asking for it again at the countdown's end, aborted the jump without a word when
    /// it had shrunk by metres (the prod's "БурПрыг", 30.10.2026; SentisOptimisations' JumpReachDrift cuts it instead).
    /// </summary>
    public sealed class JumpDriveFullReachCheck : RegressionCheck
    {
        public override string Name => Prefix + "jump_drive_full_reach";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 8));
            var definition = (Sandbox.Definitions.MyJumpDriveDefinition)Definition("JumpDrive/LargeJumpDrive");
            var ship = Build("jump-full", Site.Origin, false,
                new Part("JumpDrive/LargeJumpDrive", 0, 0, 0).With(b => ((MyObjectBuilder_JumpDrive)b).StoredPower = definition.PowerNeededForJump),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 3, 0, 0).With(b => Motion.Charged(b)),
                new Part("Cockpit/LargeBlockCockpitSeat", 3, 1, 0),
                new Part("CargoContainer/LargeBlockLargeContainer", -3, 0, 0));
            var drive = Block<MyJumpDrive>(ship);
            var cargo = Inventory(Block<Sandbox.Game.Entities.MyCargoContainer>(ship));
            // heavier than the drive takes at full reach: the reach is then less as the mass grows
            var over = definition.MaxJumpMass * 1.5 - ship.GetCurrentMass();
            Put(cargo, "Ingot/Gold", over);
            yield return Until(() => drive.IsWorking ? 1 : 0, v => v > 0, "the jump drive working", 10, () => State(drive));
            yield return Seconds(1);
            var system = ship.GridSystems.JumpSystem;
            var reach = system.GetMaxJumpDistance(Identity);
            Check(reach < definition.MaxJumpDistance * 0.9, "the ship is not heavy enough to shorten the reach: " + F(reach / 1000) + " km of " + F(definition.MaxJumpDistance / 1000) + " km");
            var from = ship.PositionComp.GetPosition();
            Request(ship, "OnJumpRequested", from + Site.Up * reach, Identity, 10f, (long?)null);
            yield return Until(() => system.IsJumping ? 1 : 0, v => v > 0, "the jump counting down", 5);
            // a little cargo in every second of the countdown
            var added = 0.0;
            var within = Within(30);
            var frame = 0;
            while (within() && Vector3D.Distance(ship.PositionComp.GetPosition(), from) < reach * 0.5 && (system.IsJumping || frame < 30))
            {
                if (++frame % 60 == 0)
                {
                    Put(cargo, "Ingot/Gold", 20);
                    added += 20;
                }
                yield return null;
            }
            yield return Seconds(1);
            var jumped = Vector3D.Distance(ship.PositionComp.GetPosition(), from);
            var reachAfter = system.GetMaxJumpDistance(Identity);
            Check(jumped > reach * 0.9, "the ship at its full reach " + F(reach) + " m did not jump (moved " + F(jumped) + " m) after " + F(added) + " kg came into it in the countdown: the reach then " + F(reachAfter) + " m");
            Result("a ship of " + F(ship.GetCurrentMass() / 1000) + " t (the drive takes " + F(definition.MaxJumpMass / 1000) + " t at full reach) jumped " + F(jumped) + " m of its reach " + F(reach) +
                   " m, " + F(added) + " kg in in the countdown (the reach then " + F(reachAfter) + " m)");
        }
    }
}
