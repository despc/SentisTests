using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisGameplayImprovements' forward offset of the ship welder's work sphere (WelderForwardOffset): with it the
    /// welder must not weld what is beside and behind it. A large welder on a static grid in space stands among armour
    /// frames - behind it, beside it and before it - and welds for a while; which frames grew is noted. Fresh grids: the
    /// offset 0 (the game's sphere: its centre 0.18 m before the welder's nose, radius 2.26 m - it welds the frames
    /// beside the nose), 0.5 m (reaches a cell further forward, still touches those beside the nose), 1.8 m (nothing
    /// beside), and 0 changed to 2 m while the welder works (live: the sphere moves at once, the welder's cache of its
    /// own grid's blocks starts anew). Also checked: the sphere moved exactly the offset along the welder, and
    /// SentisOptimisations' projection search moved with it.
    /// </summary>
    public sealed class ToolOffsetScenario : TestScenario
    {
        public const string ScenarioName = "tool_offset";
        private const string Prefix = "tool-offset-";
        private const float WeldSeconds = 8;

        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private Vector3D _spot;
        private int _site;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _gameplay.Set("WelderForwardOffset", 0f);
            _gameplay.Set("WelderRadiusMultiplier", 1f);

            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            for (var i = 0; i < 40; i++)
            {
                var probe = new Vector3D(-160000 - i * 20000, 70000, 140000 + i * 15000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() < 1e-6 &&
                    planets.All(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) > p.MaximumRadius + 5000))
                {
                    _spot = probe;
                    break;
                }
            }
            Check(_spot != Vector3D.Zero, "no place in space without gravity");
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (_spot + new Vector3D(0, 25, 0), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var none = new Result();
            var run = Site(0f, null, none);
            while (run.MoveNext()) yield return run.Current;
            var half = new Result();
            run = Site(0.5f, null, half);
            while (run.MoveNext()) yield return run.Current;
            var clear = new Result();
            run = Site(1.8f, null, clear);
            while (run.MoveNext()) yield return run.Current;
            var live = new Result();
            run = Site(0f, 2f, live);
            while (run.MoveNext()) yield return run.Current;

            Check(none.Behind.Count > 0, "with no offset the welder welded nothing behind it - nothing to take away");
            Check(half.Welded.Any(w => w.EndsWith(",-2)")), "with 0.5 m the welder does not reach further forward");
            Check(clear.Behind.Count == 0, "with 1.8 m the welder still welded beside or behind it: " + string.Join(", ", clear.Behind));
            Check(live.Behind.Count == 0, "after 2 m set live the welder still welded behind it: " + string.Join(", ", live.Behind));
            Check(clear.Welded.Count > 0 && live.Welded.Count > 0, "with the offset the welder welded nothing at all");
        }

        private sealed class Result
        {
            public readonly List<string> Welded = new List<string>();
            public readonly List<string> Behind = new List<string>();
        }

        /// <summary>
        /// A welder among frames, welding <see cref="WeldSeconds"/>; <paramref name="liveTo"/>: after the first half the
        /// offset is changed to it while the welder works, and only what grows after that counts.
        /// </summary>
        private IEnumerator Site(float offset, float? liveTo, Result result)
        {
            _gameplay.Set("WelderForwardOffset", offset);
            var label = liveTo == null ? $"offset {offset:0.0} m" : $"offset {offset:0.0} m, then {liveTo:0.0} m live";
            var welderType = WorldApi.FindSubtype(MyCubeSize.Large, "shipwelder");
            var batteryType = WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock");
            var armor = "LargeBlockArmorBlock";
            var size = MyDefinitionManager.Static.GetDefinitionsOfType<MyCubeBlockDefinition>().First(d => d.Id.SubtypeName == welderType && d is MyShipWelderDefinition).Size;
            // the welder at the origin facing grid forward (-Z): its front is z < 0; a frame at z >= 0 is beside or behind it
            var blocks = new List<BlockSpec> { new BlockSpec(welderType, Vector3I.Zero) };
            var frames = new List<Vector3I>();
            for (var z = -4; z <= size.Z + 2; z++)
            for (var x = -1; x <= 1; x++)
            {
                var cell = new Vector3I(x, 0, z);
                if (x >= 0 && x < size.X && z >= 0 && z < size.Z) continue;
                frames.Add(cell);
                blocks.Add(new BlockSpec(armor, cell) { BuildPercent = 0.1f });
            }
            for (var z = -4; z <= size.Z + 2; z++)
                blocks.Add(new BlockSpec(armor, new Vector3I(0, -1, z)));
            blocks.Add(new BlockSpec(batteryType, new Vector3I(0, -2, 0)));
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + _site, MyCubeSize.Large, true, _spot + new Vector3D(_site++ * 60, 0, 0), blocks);
            foreach (var block in ob.CubeBlocks.Where(b => b.BuildPercent < 1))
                block.IntegrityPercent = block.BuildPercent;     // a frame, not a whole block
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            yield return WaitForTicks(30);
            var welder = grid.GetFatBlocks().OfType<SpaceWelder>().FirstOrDefault();
            Check(welder != null, "no welder");
            Note($"{label}: welder {welderType} {size.X}x{size.Y}x{size.Z}, {frames.Count} frames around it, stocked: " +
                 WorldApi.StockComponents(welder.GetInventory(), new Dictionary<string, int> { { "SteelPlate", 400 }, { "InteriorPlate", 100 }, { "Construction", 100 } }));
            yield return Wait(() => { WorldApi.ChargeBatteries(grid); return welder.IsFunctional; }, "the welder is functional", 30);
            CheckSphere(welder, offset, label);

            var slims = frames.ToDictionary(c => c, c => grid.GetCubeBlock(c));
            Check(slims.Values.All(s => s != null), "a frame was not spawned");
            var before = slims.ToDictionary(p => p.Key, p => p.Value.Integrity);

            var steel0 = WorldApi.CountSteel(welder.GetInventory());
            welder.Enabled = true;
            yield return Wait(() => welder.IsWorking, "the welder works", 30);
            yield return WaitForTicks(10);
            if (!WorldApi.ToolIsActivated(welder)) WorldApi.ToolStartShooting(welder);

            if (liveTo != null)
            {
                var first = WaitForSeconds(WeldSeconds / 2, "welding with the offset " + offset);
                while (first.MoveNext()) yield return first.Current;
                var grewFirst = slims.Count(p => p.Value.Integrity > before[p.Key] + 0.01f);
                _gameplay.Set("WelderForwardOffset", liveTo.Value);
                var moved = welder.DetectorSphere.Center;
                yield return Wait(() => welder.DetectorSphere.Center != moved, "the sphere moves", 10);
                CheckSphere(welder, liveTo.Value, label);
                Note($"{label}: {grewFirst} frames grew before the change; the sphere moved {Vector3.Distance(moved, welder.DetectorSphere.Center):0.000} m");
                before = slims.ToDictionary(p => p.Key, p => p.Value.Integrity);
            }
            var weld = WaitForSeconds(liveTo == null ? WeldSeconds : WeldSeconds / 2, "welding");
            while (weld.MoveNext()) yield return weld.Current;
            Note($"{label}: welder working {welder.IsWorking}, activated {WorldApi.ToolIsActivated(welder)}, steel used {steel0 - WorldApi.CountSteel(welder.GetInventory())}, " +
                 $"grid {(grid.MarkedForClose ? "closed" : grid.BlocksCount + " blocks")}, frame (1,-1) integrity {slims[new Vector3I(1, 0, -1)].Integrity:0.0} of {slims[new Vector3I(1, 0, -1)].MaxIntegrity:0}");
            welder.Enabled = false;

            var front = new List<string>();
            foreach (var pair in slims.OrderBy(p => p.Key.Z).ThenBy(p => p.Key.X))
            {
                if (pair.Value.Integrity <= before[pair.Key] + 0.01f) continue;
                var name = $"({pair.Key.X},{pair.Key.Z})";
                result.Welded.Add(name);
                if (pair.Key.Z >= 0) result.Behind.Add(name);
                else if (pair.Key.Z < 0) front.Add(name);
            }
            var sphere = welder.DetectorSphere;
            Note($"TOOL OFFSET | {label}: sphere centre {(sphere.Center.Z / grid.GridSize):0.00} cells along the grid (welder z 0..{size.Z - 1}, " +
                 $"front at z < 0), radius {sphere.Radius:0.00} m; welded {result.Welded.Count} frames (x,z): before it {string.Join(" ", front)}; " +
                 $"BESIDE OR BEHIND {string.Join(" ", result.Behind)}");
        }

        /// <summary>The sphere stands the offset along the welder from where the game puts it, and SO's projection search follows.</summary>
        private void CheckSphere(SpaceWelder welder, float offset, string label)
        {
            var definition = (MyShipToolDefinition)welder.BlockDefinition;
            var model = VRage.Game.Models.MyModels.GetModelOnlyDummies(definition.Model);
            var dummy = model.Dummies.First(d => d.Key.ToLower().Contains("detector_shiptool")).Value.Matrix * welder.PositionComp.LocalMatrixRef;
            var game = dummy.Translation + dummy.Forward * definition.SensorOffset;
            var expected = game + Vector3.Normalize(dummy.Forward) * offset;
            var off = Vector3.Distance(expected, welder.DetectorSphere.Center);
            Check(off < 0.001f, $"{label}: the sphere is {off:0.000} m off where the offset puts it");

            var shiftOf = Integrations.TypeOf("SentisOptimisations", "SentisOptimisationsPlugin.ShipTool.ShipToolPatch")?.GetMethod("GetWelderSphereShift");
            if (shiftOf == null)
            {
                Note($"{label}: SentisOptimisations has no GetWelderSphereShift - the projection search is not checked");
                return;
            }
            var shift = (Vector3D)shiftOf.Invoke(null, new object[] { welder });
            var want = Vector3D.TransformNormal(Vector3.Normalize(dummy.Forward) * offset, welder.CubeGrid.WorldMatrix);
            Check(Vector3D.Distance(shift, want) < 0.001, $"{label}: SO's projection search is shifted {shift.Length():0.000} m, not {offset:0.000} m");
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _gameplay.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
