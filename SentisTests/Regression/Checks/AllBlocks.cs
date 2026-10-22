using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Every functional block of the game, of one grid size, each on a grid of its own with a charged battery: it is
    /// built whole and functional; switched on in its terminal it works; switched off it stops. The blocks that need
    /// more than power to work (fuel, the sun, wind, a target) are listed with what they need, and only built and
    /// switched for them.
    /// </summary>
    public sealed class AllBlocksCheck : RegressionCheck
    {
        private readonly MyCubeSize _size;

        public AllBlocksCheck(MyCubeSize size) { _size = size; }

        public override string Name => Prefix + "all_blocks_" + (_size == MyCubeSize.Large ? "large" : "small");
        public override int TimeoutSeconds => 400;

        /// <summary>Block types that do not work on power alone, and what they need.</summary>
        private static readonly Dictionary<string, string> NeedMore = new Dictionary<string, string>
        {
            { "MyObjectBuilder_Reactor", "uranium" },
            { "MyObjectBuilder_HydrogenEngine", "hydrogen" },
            { "MyObjectBuilder_SolarPanel", "the sun on its face" },
            { "MyObjectBuilder_WindTurbine", "wind" },
            { "MyObjectBuilder_DefensiveCombatBlock", "its AI behaviour switched on" },
            { "MyObjectBuilder_OffensiveCombatBlock", "its AI behaviour switched on" },
        };

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(-20, 20, -20));
            var definitions = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>()
                .Where(d => d.Public && d.CubeSize == _size && typeof(MyObjectBuilder_FunctionalBlock).IsAssignableFrom(d.Id.TypeId))
                .OrderBy(d => d.Id.TypeId.ToString()).ThenBy(d => d.Id.SubtypeName).ToList();
            var battery = _size == MyCubeSize.Large ? "BatteryBlock/LargeBlockBatteryBlock" : "BatteryBlock/SmallBlockBatteryBlock";
            var batterySize = Definition(battery).Size;
            var cell = _size == MyCubeSize.Large ? 2.5 : 0.5;
            Note(definitions.Count + " functional " + _size + " blocks");

            var broken = new List<string>();
            var needing = new List<string>();
            var working = 0;
            const int batch = 24;
            for (var start = 0; start < definitions.Count; start += batch)
            {
                var placed = new List<(MyCubeBlockDefinition Definition, MyCubeGrid Grid, MyCubeBlock Block)>();
                foreach (var (definition, i) in definitions.Skip(start).Take(batch).Select((d, i) => (d, i)))
                {
                    // each batch a layer of its own: the last batch's grids are closed but may not be gone yet (deletes wait
                    // their turn under load), and a wheel is not put down where something stands
                    var at = Site.Origin + Site.Right * ((i % 6) * 30) + Site.Forward * ((i / 6) * 30) + Site.Up * (start / batch * 40);
                    MyCubeGrid grid;
                    try
                    {
                        // stations, but a gyroscope on a ship (it works only there): ships floating side by side drift
                        // into each other, pulled by the gravity generators among them
                        var ship = definition.Id.TypeId.ToString() == "MyObjectBuilder_Gyro";
                        grid = Build("block", at, !ship, new[]
                        {
                            new Part(definition.Id.TypeId.ToString() + "/" + definition.Id.SubtypeName, 0, 0, 0),
                            // the battery a few cells off: a wheel or a head must not run into it
                            new Part(battery, -batterySize.X - (_size == MyCubeSize.Large ? 2 : 8), 0, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 3f),
                        }, _size);
                    }
                    catch (Exception e)
                    {
                        broken.Add(Short(definition) + ": not spawned (" + e.GetType().Name + ": " + e.Message + ")");
                        continue;
                    }
                    var block = grid.GetCubeBlock(Vector3I.Zero)?.FatBlock;
                    if (block == null)
                    {
                        broken.Add(Short(definition) + ": no block after the spawn");
                        continue;
                    }
                    if (!block.IsFunctional)
                    {
                        broken.Add(Short(definition) + ": not functional right after it was built (integrity " + (block.SlimBlock.Integrity / block.SlimBlock.MaxIntegrity * 100).ToString("F0") +
                                   " %, built " + (block.SlimBlock.BuildLevelRatio * 100).ToString("F0") + " %)");
                        grid.Close();
                        continue;
                    }
                    placed.Add((definition, grid, block));
                }
                yield return Seconds(1);
                // rotors, hinges, pistons, suspensions get their heads and wheels as a player adds them
                foreach (var p in placed)
                    if (p.Block is Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase mechanical && mechanical.TopGrid == null)
                        // made on the server, built whole
                        typeof(Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            .Invoke(mechanical, new object[] { Identity, Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal, true });
                // the heads are made as grids of their own a while later (longer under load): waited for, each
                for (var f = Frames(15); f > 0 && placed.Any(p => p.Block is Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase m && m.TopGrid == null); f--)
                    yield return null;
                foreach (var p in placed)
                {
                    if (p.Block is Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase mechanical && mechanical.TopGrid != null) Track(mechanical.TopGrid);
                    if (p.Block is MyFunctionalBlock) Switch(p.Block, true);
                }
                yield return Seconds(2);
                foreach (var p in placed)
                {
                    if (!p.Block.IsFunctional)
                        broken.Add(Short(p.Definition) + ": functional when built, not any more (integrity " + (p.Block.SlimBlock.Integrity / p.Block.SlimBlock.MaxIntegrity * 100).ToString("F0") +
                                   " %, built " + (p.Block.SlimBlock.BuildLevelRatio * 100).ToString("F0") + " %, damage " + F(p.Block.SlimBlock.CurrentDamage) + ")");
                    else if (!p.Block.IsWorking)
                    {
                        var what = NeedMore.TryGetValue(p.Definition.Id.TypeId.ToString(), out var need) ? need
                            : p.Block is MyThrust && p.Definition.Id.SubtypeName.Contains("Hydrogen") ? "hydrogen (reg_hydrogen_thrust flies on it)" : null;
                        if (what != null) needing.Add(Short(p.Definition) + " (" + what + ")");
                        else broken.Add(Short(p.Definition) + ": on and powered and not working (" + State(p.Block) + Around(p.Block) + ")");
                    }
                    else working++;
                }
                foreach (var p in placed)
                    if (p.Block is MyFunctionalBlock && p.Block.IsWorking) Switch(p.Block, false);
                yield return Seconds(1);
                foreach (var p in placed)
                    if (p.Block is MyFunctionalBlock && ((MyFunctionalBlock)p.Block).Enabled == false && p.Block.IsWorking)
                        broken.Add(Short(p.Definition) + ": switched off and still working");
                foreach (var p in placed)
                {
                    if (p.Block is Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase mechanical) mechanical.TopGrid?.Close();
                    p.Grid.Close();
                }
                yield return null;
            }
            Note("blocks that need more than power: " + string.Join(", ", needing));
            Check(broken.Count == 0, broken.Count + " of " + definitions.Count + " blocks do not do what they should: " + string.Join(" | ", broken));
            Result(definitions.Count + " " + _size + " functional blocks: " + working + " work on power and stop when off, " + needing.Count + " need more than power");
        }

        /// <summary>For a mechanical block left without its head: what else is within 5 m of it (a head the game would not put down there).</summary>
        private static string Around(MyCubeBlock block)
        {
            if (!(block is Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase m) || m.TopGrid != null) return "";
            var at = block.PositionComp.GetPosition();
            var sphere = new VRageMath.BoundingSphereD(at, 5);
            var near = Sandbox.Game.Entities.MyEntities.GetTopMostEntitiesInSphere(ref sphere).Where(e => e != block.CubeGrid).ToList();
            return "; within 5 m: " + (near.Count == 0 ? "nothing" : string.Join(", ", near.Take(5).Select(e => e.DisplayName ?? e.GetType().Name)));
        }

        private static string Short(MyCubeBlockDefinition definition) => definition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + definition.Id.SubtypeName;
    }
}
