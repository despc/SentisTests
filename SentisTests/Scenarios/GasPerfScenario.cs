using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// <see cref="Generators"/> O2/H2 generators on one grid, standing on a conveyor plate with ice
    /// in small containers, hydrogen tanks and batteries. The tanks are emptied every
    /// <see cref="DrainSeconds"/> so the generators never idle on a full tank, which is the load a
    /// hydrogen farm puts on a server. Measured: <see cref="IdleSeconds"/> with the generators off,
    /// then <see cref="RunSeconds"/> of production; dotTrace attaches on PROFILE WINDOW START.
    /// </summary>
    public sealed class GasPerfScenario : TestScenario
    {
        public const string ScenarioName = "gas_perf";
        private const string Prefix = "gas-";
        private const int Generators = Side * Side;
        private const int Side = 20;
        private const int Containers = 40;
        private const int IcePerContainer = 200000;
        private const int Batteries = 80;
        private const int Tanks = 4;
        private const double OffsetM = 6000;
        private const double SettleSeconds = 10;
        private const double IdleSeconds = 10;
        private const double RunSeconds = 90;
        private const double ProfileAfterSeconds = 20;
        private const double LogEverySeconds = 20;
        private const double DrainSeconds = 2;
        private const int Players = 2;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private MyCubeGrid _station;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(RunSeconds + 300);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            _station = BuildStation(anchorM.Translation + up * OffsetM, up);
            var settle = WaitForSeconds(SettleSeconds, "station settles");
            while (settle.MoveNext()) yield return settle.Current;

            var generators = WorldApi.FindFunctionals<MyGasGenerator>(_station);
            var tanks = WorldApi.FindFunctionals<MyGasTank>(_station);
            Check(generators.Count > 0, "no gas generators on the station");
            Check(tanks.Count > 0, "no gas tanks on the station");
            var watch = _station.PositionComp.WorldAABB.Center + up * 80;
            FakeClients.Add(Players, Network, p => (watch + up * (10 * p), 0, 0), withCharacters: true);
            Note(generators.Count + " O2/H2 generators, " + tanks.Count + " hydrogen tanks, " + Containers + " containers with ice, " +
                 Batteries + " batteries: " + WorldApi.DescribePower(_station));
            // Gas is distributed inside conveyor groups: unless the generators reach the tanks
            // nothing asks them for hydrogen and they burn no ice at all.
            var connected = generators.Count(g => Reaches(g, tanks[0]));
            Note("generators connected to the first tank: " + connected + "/" + generators.Count);
            Check(connected == generators.Count, "generators are not on the tanks' conveyor network (" + connected + "/" + generators.Count + ")");

            foreach (var generator in generators) generator.Enabled = false;
            var off = WaitForSeconds(IdleSeconds, "generators off");
            while (off.MoveNext()) yield return off.Current;
            TickMetrics.Take();
            FrameProbe.Take();
            var idle = DateTime.UtcNow;
            while ((DateTime.UtcNow - idle).TotalSeconds < IdleSeconds) yield return null;
            var idleProbe = FrameProbe.Take();
            TickMetrics.Take();

            foreach (var generator in generators) generator.Enabled = true;
            var startIce = Ice();
            var started = DateTime.UtcNow;
            var lastLog = started;
            var lastDrain = DateTime.MinValue;
            var windowStarted = DateTime.MinValue;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                var now = DateTime.UtcNow;
                if ((now - lastDrain).TotalSeconds >= DrainSeconds)
                {
                    lastDrain = now;
                    foreach (var tank in tanks) Drain(tank);
                }
                var elapsed = (now - started).TotalSeconds;
                if (windowStarted == DateTime.MinValue && elapsed >= ProfileAfterSeconds)
                {
                    windowStarted = now;
                    TickMetrics.Take();
                    FrameProbe.Take();
                    Note("PROFILE WINDOW START: " + generators.Count(g => g.IsWorking) + " generators working");
                }
                if ((now - lastLog).TotalSeconds >= LogEverySeconds)
                {
                    lastLog = now;
                    Note("producing " + elapsed.ToString("F0") + "s: generators working " + generators.Count(g => g.IsWorking) +
                         ", producing " + generators.Count(g => g.IsProducing) +
                         ", ice used " + (startIce - Ice()).ToString("F0") + " kg, tanks " +
                         string.Join("/", tanks.Select(t => (t.FilledRatio * 100).ToString("F0"))) + "%");
                }
                yield return null;
            }
            foreach (var generator in generators) generator.Enabled = false;
            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            Note("GAS RESULT | " + generators.Count + " generators, ice used " + ((startIce - Ice()) / 1000).ToString("F0") +
                 " k | idle, generators off: " + Summary(idleProbe) + " | producing: " + Summary(probe) + " | " + metrics.Format() + " | " + probe);
            Check(startIce - Ice() > 0, "no ice was used");
        }

        private static bool Reaches(MyCubeBlock from, MyCubeBlock to) =>
            from is Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock a &&
            to is Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock b &&
            Sandbox.Game.GameSystems.MyGridConveyorSystem.Reachable(a.ConveyorEndpoint, b.ConveyorEndpoint);

        private double Ice() =>
            _station.GetFatBlocks().Where(b => b.HasInventory)
                .SelectMany(b => Enumerable.Range(0, b.InventoryCount).Select(i => b.GetInventory(i) as Sandbox.Game.MyInventory))
                .Where(inv => inv != null)
                .Sum(inv => (double)inv.GetItemAmount(new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Ice")));

        private static readonly MethodInfo ChangeFillRatio = typeof(MyGasTank)
            .GetMethod("ChangeFillRatioAmount", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Empties the tank, so the generators keep producing (the private setter vanilla uses).</summary>
        private static void Drain(MyGasTank tank) => ChangeFillRatio?.Invoke(tank, new object[] { 0.0 });

        private MyCubeGrid BuildStation(Vector3D position, Vector3D up)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            var owner = WorldApi.PlayerIdentityId();

            void Add(MyObjectBuilder_CubeBlock block, Vector3I min)
            {
                block.Min = new SerializableVector3I(min.X, min.Y, min.Z);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                blocks.Add(block);
            }

            // Conveyor plate under everything; containers with ice and batteries sit in it.
            // It reaches Side + 3 in x so the tanks stand on it too.
            var containers = 0;
            var batteries = 0;
            for (var x = 0; x < Side + 3; x++)
            for (var z = 0; z < Side + 3; z++)
            {
                if (x < Side && z < Side && containers < Containers && (x + z) % 3 == 0)
                {
                    // Filled with ice after the spawn, like the refinery bench does.
                    Add(WorldApi.MakeBlockOb("LargeBlockSmallContainer"), new Vector3I(x, 0, z));
                    containers++;
                    continue;
                }
                Add(WorldApi.MakeBlockOb("LargeBlockConveyor"), new Vector3I(x, 0, z));
            }

            // The generators, 1x2x1 each, standing on the plate: their bottom port meets it.
            for (var x = 0; x < Side; x++)
            for (var z = 0; z < Side; z++)
                Add(new Sandbox.Common.ObjectBuilders.MyObjectBuilder_OxygenGenerator { SubtypeName = "" }, new Vector3I(x, 1, z));

            // Hydrogen tanks (3x3x3) on top of the plate beside the generators. The tank's
            // conveyor port sits in the middle of its bottom face, so the tank has to stand on
            // the plate - set beside it at y = 0 nothing touches and the gas group stays split.
            for (var i = 0; i < Tanks; i++)
                Add(WorldApi.MakeBlockOb("LargeHydrogenTank"), new Vector3I(Side, 1, i * 3));

            // Batteries under the plate.
            for (var x = 0; x < Side && batteries < Batteries; x++)
            for (var z = 0; z < Side && batteries < Batteries; z++)
            {
                Add(WorldApi.MakeBlockOb("LargeBlockBatteryBlock"), new Vector3I(x, -1, z));
                batteries++;
            }

            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "station",
                DisplayName = WorldApi.EntityPrefix + Prefix + "station",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(position, Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up)), up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var station = WorldApi.SpawnGrid(ob);
            Track(station);
            WorldApi.ChargeBatteries(station);
            StockIce(station);
            return station;
        }

        /// <summary>
        /// Ice into every cargo container and into every generator: a generator only produces from
        /// the ice in its own inventory, and it pulls more through the conveyors as it runs low.
        /// </summary>
        private static void StockIce(MyCubeGrid station)
        {
            var ice = new MyObjectBuilder_Ore { SubtypeName = "Ice" };
            foreach (var block in station.GetFatBlocks())
            {
                if (!(block is MyCargoContainer) && !(block is MyGasGenerator)) continue;
                var inventory = block.GetInventory(0) as Sandbox.Game.MyInventory;
                if (inventory == null) continue;
                var fits = MyFixedPoint.Min((MyFixedPoint)IcePerContainer, inventory.ComputeAmountThatFits(ice.GetId()));
                if (fits > 0) inventory.AddItems(fits, ice);
            }
        }

        private static string Summary(string probe)
        {
            string Pick(string name)
            {
                var m = System.Text.RegularExpressions.Regex.Match(probe, System.Text.RegularExpressions.Regex.Escape(name) + @"=\d+ms/\d+calls\([\d.]+ms each, ([\d.]+)ms/frame");
                return m.Success ? m.Groups[1].Value : "?";
            }
            var sim = System.Text.RegularExpressions.Regex.Match(probe, @"sim-work frames=\d+ avg=([\d.]+)ms p50=[\d.]+ p95=[\d.]+ p99=([\d.]+)");
            return "frame avg " + (sim.Success ? sim.Groups[1].Value : "?") + " ms, p99 " + (sim.Success ? sim.Groups[2].Value : "?") +
                   ", gas.generator " + Pick("gas.generator") + ", gas.setCapacities " + Pick("gas.setCapacities") +
                   ", gas.checkProducing " + Pick("gas.checkProducing") + ", gas.tank " + Pick("gas.tank") +
                   ", gas.generator100 " + Pick("gas.generator100") + ", gas.tank100 " + Pick("gas.tank100") +
                   ", conveyor.pull " + Pick("conveyor.pull") + ", conveyor.pullRequest " + Pick("conveyor.pullRequest") +
                   ", inventory.transfer " + Pick("inventory.transfer") + ", power.distributor " + Pick("power.distributor");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
