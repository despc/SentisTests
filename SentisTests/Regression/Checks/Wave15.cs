using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// An algae farm in the sun makes algae on power: a farm each way it can be turned, the one facing the sun makes a
    /// batch (the time to it hurried: a batch takes minutes) into its own inventory.
    /// </summary>
    public sealed class AlgaeFarmCheck : RegressionCheck
    {
        public override string Name => Prefix + "algae_farm";

        private static readonly System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var sun = (Vector3D)MySector.DirectionToSunNormalized;
            Check(sun.LengthSquared() > 0.5, "the world has no sun");
            var side = Vector3D.CalculatePerpendicularVector(sun);
            var farms = new[] { (sun, side), (-sun, side), (side, sun), (side, -sun) }.Select((o, i) =>
            {
                var grid = Build("algae-farm-" + i, Site.Origin + Site.Right * (i * 15), true, new[]
                {
                    new Part("FunctionalBlock/LargeBlockAlgaeFarm", 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)),
                }, forward: o.Item1, up: o.Item2);
                return (MyCubeBlock)grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            }).ToList();
            var type = typeof(SpaceEngineers.Game.EntityComponents.Blocks.MySolarFoodGenerator);
            var generators = farms.Select(f => f.Components.Get<SpaceEngineers.Game.EntityComponents.Blocks.MySolarFoodGenerator>()).ToList();
            Check(generators.All(g => g != null), "an algae farm has no food generator");
            yield return Until(() => generators.Max(g => (double)g.ItemsPerMinute), v => v > 0, "an algae farm making algae in the sun", 30,
                () => string.Join(", ", generators.Select(g => F(g.ItemsPerMinute) + "/min")));
            // the batch at hand: a frame of production left on each
            foreach (var g in generators) type.GetField("m_itemProductionRemainingFrames", Any).SetValue(g, 1f);
            Func<double> algae = () => farms.Sum(f => f.InventoryCount > 0 ? Amount(Inventory(f), "PhysicalObject/Algae") : 0);
            yield return Until(algae, v => v > 0, "algae in the farm", 10, () => string.Join(", ", generators.Select(g => F(g.ItemsPerMinute) + "/min")));
            Result("algae farms in the sun at " + string.Join(", ", generators.Select(g => F(g.ItemsPerMinute) + "/min")) + "; a batch made " + F(algae()) + " algae");
        }
    }

    /// <summary>The text written on an LCD panel stays on it through a save (the grid written, read back and made again).</summary>
    public sealed class LcdTextCheck : RegressionCheck
    {
        public override string Name => Prefix + "lcd_text";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -8));
            var grid = Build("lcd", Site.Origin, true,
                new Part("TextPanel/LargeLCDPanel", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var panel = Block<Sandbox.Game.Entities.Blocks.MyTextPanel>(grid);
            var surface = (Sandbox.ModAPI.Ingame.IMyTextSurface)panel;
            const string text = "Regression 42\nsecond line";
            surface.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
            surface.WriteText(text);
            surface.FontSize = 2f;
            yield return Seconds(1);
            Check(surface.GetText() == text, "the panel shows '" + surface.GetText() + "'");
            var ob = (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder(true);
            var read = MyAPIGateway.Utilities.SerializeFromXML<MyObjectBuilder_CubeGrid>(MyAPIGateway.Utilities.SerializeToXML(ob));
            MyEntities.RemapObjectBuilder(read);
            read.Name = read.DisplayName = WorldApi.EntityPrefix + "lcd-copy";
            read.PositionAndOrientation = new MyPositionAndOrientation(Site.At(0, 0, 15), (Vector3)Site.Forward, (Vector3)Site.Up);
            var copy = WorldApi.SpawnGrid(read);
            Track(copy);
            yield return Seconds(1);
            var twin = (Sandbox.ModAPI.Ingame.IMyTextSurface)Block<Sandbox.Game.Entities.Blocks.MyTextPanel>(copy);
            Check(twin.GetText() == text, "after the save the panel shows '" + twin.GetText() + "'");
            Check(twin.ContentType == VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE, "after the save the panel is set to " + twin.ContentType);
            Check(Math.Abs(twin.FontSize - 2f) < 0.01, "after the save the panel's font is " + F(twin.FontSize));
            Result("text written on the LCD, kept through a save with its content type and font size");
        }
    }

    /// <summary>An armed warhead caught in another's blast goes off too.</summary>
    public sealed class WarheadChainCheck : RegressionCheck
    {
        public override string Name => Prefix + "warhead_chain";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 40, -40));
            var grid = Build("warhead-chain", Site.Origin, true,
                new Part("Warhead/LargeWarhead", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("Warhead/LargeWarhead", 2, 0, 0));
            var first = (MyWarhead)grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            var second = (MyWarhead)grid.GetCubeBlock(new Vector3I(2, 0, 0)).FatBlock;
            foreach (var w in new[] { first, second }) ((IMyTerminalBlock)w).SetValueBool("Safety", true);
            Request(first, "DetonateRequest");
            yield return Until(() => second.MarkedForClose || second.Closed ? 1 : 0, v => v > 0, "the second warhead set off by the first", 10,
                () => "first " + (first.MarkedForClose ? "gone" : "there") + ", second integrity " + F(grid.GetCubeBlock(new Vector3I(2, 0, 0))?.Integrity ?? 0));
            Result("the first warhead's blast set off the armed one 5 m off");
        }
    }

    /// <summary>A container full of rockets blown apart blows up its ammunition: the blocks round it are hurt.</summary>
    public sealed class AmmoExplosionCheck : RegressionCheck
    {
        public override string Name => Prefix + "ammo_explosion";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 40, -40));
            var around = new[] { new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 1, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1) };
            var parts = new List<Part> { new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0) };
            parts.AddRange(around.Select(c => new Part("CubeBlock/LargeHeavyBlockArmorBlock", c.X, c.Y, c.Z)));
            var grid = Build("ammo-blast", Site.Origin, true, parts);
            var box = grid.GetCubeBlock(Vector3I.Zero);
            Put(Inventory(box.FatBlock), "AmmoMagazine/Missile200mm", 40);
            yield return Seconds(1);
            Func<double> integrity = () => around.Sum(c => (double)(grid.GetCubeBlock(c)?.Integrity ?? 0));
            var whole = integrity();
            box.DoDamage(box.MaxIntegrity * 10, MyDamageType.Bullet, true, null, 0);
            yield return Until(() => whole - integrity(), v => v > 0, "the blocks round the container hurt by its rockets going off", 10);
            Result("the container of 40 rockets destroyed: its ammunition went off, " + F(whole - integrity()) + " integrity taken from the heavy armor round it");
        }
    }

    /// <summary>PCU: a block a player places counts its PCU to them; ground away, it is taken off again.</summary>
    public sealed class PcuAccountingCheck : RegressionCheck
    {
        public override string Name => Prefix + "pcu_accounting";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, 0));
            var floor = Build("pcu-floor", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var definition = Definition("CargoContainer/LargeBlockSmallContainer");
            var inventory = Inventory(Body);
            foreach (var component in definition.Components)
                inventory.AddItems(component.Count, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(component.Definition.Id));
            var limits = MySession.Static.Players.TryGetIdentity(Identity).BlockLimits;
            var pcu0 = limits.PCUBuilt;
            var cell = new Vector3I(0, 1, 0);
            StandAt(floor.GridIntegerToWorld(new Vector3I(0, 2, 2)), floor.GridIntegerToWorld(cell));
            var location = new MyCubeGrid.MyBlockLocation(definition.Id, cell, cell, cell, Quaternion.Identity, 0, Identity);
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", Placing.Any);
            AsMe(() => request.Invoke(floor, new object[] { Placing.Visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, Body.EntityId, false, Identity }));
            yield return Until(() => floor.GetCubeBlock(cell) != null ? 1 : 0, v => v > 0, "the container placed", 10);
            yield return Seconds(2);
            var frame = limits.PCUBuilt - pcu0;
            var block = floor.GetCubeBlock(cell);
            // welded whole from what the player carries
            foreach (var component in definition.Components)
                inventory.AddItems(component.Count, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(component.Definition.Id));
            block.MoveItemsToConstructionStockpile(inventory);
            block.IncreaseMountLevel(block.MaxIntegrity * 2, Identity, inventory, 1f, false);
            yield return Seconds(2);
            var placed = limits.PCUBuilt - pcu0;
            Note("PCU counted to the player: the frame " + frame + ", whole " + placed + " (full " + block.IsFullIntegrity + "), the block's " + definition.PCU);
            Check(placed == definition.PCU, "the container placed and welded counted " + placed + " PCU to the player, it has " + definition.PCU);
            // as a grinder does: down to nothing, then off the grid
            block.DecreaseMountLevel(block.MaxIntegrity * 2, Inventory(Body), true);
            if (block.IsFullyDismounted) floor.RazeBlock(cell);
            yield return Until(() => floor.GetCubeBlock(cell) == null ? 1 : 0, v => v > 0, "the container ground away", 10);
            yield return null;
            Check(limits.PCUBuilt == pcu0, "with the container ground away the player has " + (limits.PCUBuilt - pcu0) + " PCU of it still counted");
            Result("the container counted " + frame + " PCU to the player as a frame, " + placed + " welded whole; ground away it was taken off");
        }
    }
}
