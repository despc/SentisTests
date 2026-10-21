using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Every block of the game of one grid size, each a grid of its own: ground down to nothing it gives back all its
    /// components as the definition lists them (those it says break down - power cells, Prototech parts - as what they
    /// break down to), and welded up again from a set of its components it takes them all and is whole. The construction
    /// stockpile of every block, both ways.
    /// </summary>
    public sealed class AllBlocksBuildCheck : RegressionCheck
    {
        private readonly MyCubeSize _size;

        private MyInventory _stock;

        public AllBlocksBuildCheck(MyCubeSize size) { _size = size; }

        public override string Name => Prefix + "all_blocks_build_" + (_size == MyCubeSize.Large ? "large" : "small");
        public override int TimeoutSeconds => 400;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(-20, 20, -20));
            var definitions = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>()
                .Where(d => d.Public && d.CubeSize == _size && d.Components != null && d.Components.Length > 0)
                .OrderBy(d => d.Id.TypeId.ToString()).ThenBy(d => d.Id.SubtypeName).ToList();
            // what is ground off goes into a large container of the check's (an inventory needs an owner to take items)
            var stock = Build("build-stock", Site.Origin - Site.Forward * 40, true, new[] { new Part("CargoContainer/LargeBlockLargeContainer", 0, 0, 0) });
            _stock = Inventory(Block<MyCargoContainer>(stock));
            var wrong = new List<string>();
            var done = 0;
            const int batch = 40;
            for (var start = 0; start < definitions.Count; start += batch)
            {
                var placed = new List<(MyCubeBlockDefinition Definition, MyCubeGrid Grid)>();
                foreach (var (definition, i) in definitions.Skip(start).Take(batch).Select((d, i) => (d, i)))
                {
                    var at = Site.Origin + Site.Right * ((i % 8) * 25) + Site.Forward * ((i / 8) * 25) + Site.Up * (start / batch * 40);
                    try { placed.Add((definition, Build("build-block", at, true, new[] { new Part(definition.Id.TypeId + "/" + definition.Id.SubtypeName, 0, 0, 0) }, _size))); }
                    catch (Exception e) { wrong.Add(Short(definition) + ": not spawned (" + e.Message + ")"); }
                    // a few a frame: the other checks run alongside
                    if (i % 5 == 4) yield return null;
                }
                yield return null;
                foreach (var (definition, grid) in placed)
                {
                    try
                    {
                        var problem = GrindAndWeld(definition, grid);
                        if (problem != null) wrong.Add(Short(definition) + ": " + problem);
                    }
                    catch (Exception e) { wrong.Add(Short(definition) + ": " + e.GetType().Name + ": " + e.Message); }
                    grid.Close();
                    if (++done % 5 == 0) yield return null;
                }
                yield return null;
            }
            Check(wrong.Count == 0, wrong.Count + " of " + definitions.Count + " " + _size + " blocks off: " + string.Join("; ", wrong.Take(30)));
            Result((definitions.Count - wrong.Count) + " " + _size + " blocks ground down to nothing, all their components back (or what they break down to), and welded whole again from a set");
        }

        private string GrindAndWeld(MyCubeBlockDefinition definition, MyCubeGrid grid)
        {
            var slim = grid.CubeBlocks.FirstOrDefault();
            if (slim == null) return "no block on its grid";
            if (!slim.IsFullIntegrity) return "spawned at " + F(slim.Integrity / slim.MaxIntegrity * 100) + " %";
            var inventory = _stock;
            inventory.Clear();
            // what grinding gives: each component as itself, or as what it breaks down to (power cells to scrap)
            var listed = definition.Components.GroupBy(c => c.DeconstructItem?.Id ?? c.Definition.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
            var parts = definition.Components.GroupBy(c => c.Definition.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
            // ground as a hand grinder grinds (the parts taken off the block's stockpile into the inventory), down to nothing
            for (var i = 0; i < 100 && !slim.IsFullyDismounted; i++)
            {
                slim.DecreaseMountLevel(slim.MaxIntegrity, inventory, false, Identity);
                slim.MoveItemsFromConstructionStockpile(inventory);
            }
            if (!slim.IsFullyDismounted) return "not ground down in 100 grinds (" + F(slim.Integrity) + " left)";
            var back = listed.Where(c => (double)inventory.GetItemAmount(c.Key) != c.Value)
                .Select(c => c.Key.SubtypeName + " " + inventory.GetItemAmount(c.Key) + " of " + c.Value).ToList();
            if (back.Count > 0) return "ground down gave " + string.Join(", ", back);
            // welded up from a set of its components
            inventory.Clear();
            foreach (var part in parts)
                inventory.AddItems(part.Value, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(part.Key));
            for (var i = 0; i < 100 && !slim.IsFullIntegrity; i++)
            {
                slim.MoveItemsToConstructionStockpile(inventory);
                slim.IncreaseMountLevel(slim.MaxIntegrity, Identity, inventory, 1f, false);
            }
            if (!slim.IsFullIntegrity) return "not welded whole from its own components: " + F(slim.Integrity / slim.MaxIntegrity * 100) + " %, " +
                                             string.Join(", ", inventory.GetItems().Select(it => it.Content.SubtypeName + " " + it.Amount)) + " left over";
            var left = inventory.GetItems().Where(it => it.Amount > 0).Select(it => it.Content.SubtypeName + " " + it.Amount).ToList();
            if (left.Count > 0) return "welded whole with " + string.Join(", ", left) + " left over";
            return null;
        }

        private static string Short(MyCubeBlockDefinition d) => d.Id.SubtypeName.Length > 0 ? d.Id.SubtypeName : d.Id.TypeId.ToString().Replace("MyObjectBuilder_", "");
    }
}
