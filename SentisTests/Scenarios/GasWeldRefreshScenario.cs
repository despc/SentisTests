using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A large grid with an air vent being welded, and the worker time its airtightness takes (from the server,
    /// 14.10.2026: a worker spent 44 % of its time in <c>MyGridGasSystem.RefreshRoomData</c> while players welded large
    /// ships, nearly all of it flooding the outside air of the grid's bounding box anew after every batch of blocks).
    /// A static frame, the edges of a box of 100 x 50 x 200 large blocks, with a closed armor room and an air vent
    /// in it, one wall block left out. For <see cref="WeldSeconds"/> s a block every <see cref="PlaceEveryFrames"/>
    /// frames is placed on the bottom unfinished (one plate of 25 mounted, the rest in its stockpile, as a welder places a
    /// projected block) and welded up <see cref="WeldAfterFrames"/> frames later. Noted: how many refreshes the
    /// gas system ran, the share of the time it was busy on a worker, its longest run. Then the wall block is put in,
    /// and once the system has settled: the vent's room must be airtight, and the outside's block count must be what
    /// the game's own flood gives. Fails on those two; the times are for comparing builds (SO GasRoomRefresh).
    /// </summary>
    public sealed class GasWeldRefreshScenario : TestScenario
    {
        public const string ScenarioName = "gas_weld_refresh";
        private const string Prefix = "GasWeldRefresh_";
        private const int SizeX = 100, SizeY = 50, SizeZ = 200;
        private const int WeldSeconds = 40;
        private const int PlaceEveryFrames = 4;
        private const int WeldAfterFrames = 30;
        private const int Plates = 25;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ProcessingField = typeof(MyGridGasSystem).GetField("m_isProcessingData", Any);
        private static readonly FieldInfo RoomsField = typeof(MyGridGasSystem).GetField("m_rooms", Any);
        private static readonly FieldInfo AddedField = typeof(MyGridGasSystem).GetField("m_addedBlocks", Any);
        private static readonly FieldInfo StoredMinField = typeof(MyGridGasSystem).GetField("m_storedGridMin", Any);
        private static readonly MethodInfo RoomBlocksMethod = typeof(MyGridGasSystem).GetMethod("GetRoomBlocks", Any);
        private static readonly MethodInfo AddBlockMethod = typeof(MyCubeGrid).GetMethod("AddBlock", Any, null,
            new[] { typeof(MyObjectBuilder_CubeBlock), typeof(bool) }, null);

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            SkipUnless(MySession.Static.Settings.EnableOxygenPressurization, "oxygen pressurization is off in this world");
            Check(ProcessingField != null && RoomsField != null && AddedField != null && StoredMinField != null && RoomBlocksMethod != null && AddBlockMethod != null,
                "MyGridGasSystem or MyCubeGrid members not found");
            var vent = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>()
                .FirstOrDefault(d => d.Id.TypeId.ToString() == "MyObjectBuilder_AirVent" && d.CubeSize == MyCubeSize.Large && d.Size == Vector3I.One && d.Public);
            Check(vent != null, "no large air vent");

            var planet = MyEntities.GetEntities().OfType<MyPlanet>().OrderByDescending(x => x.MaximumRadius).FirstOrDefault();
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = centre + Vector3D.Normalize(new Vector3D(0.5, -0.3, 0.8)) * planet.MaximumRadius;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var origin = ground + up * 700;

            var owner = WorldApi.TestIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            var taken = new HashSet<Vector3I>();
            void Put(MyObjectBuilder_CubeBlock b, Vector3I at)
            {
                if (!taken.Add(at)) return;
                b.Min = at;
                b.Owner = owner;
                b.BuiltBy = owner;
                blocks.Add(b);
            }
            // the box's edges
            for (var x = 0; x <= SizeX; x++)
            foreach (var (y, z) in new[] { (0, 0), (SizeY, 0), (0, SizeZ), (SizeY, SizeZ) })
                Put(Armor(), new Vector3I(x, y, z));
            for (var y = 0; y <= SizeY; y++)
            foreach (var (x, z) in new[] { (0, 0), (SizeX, 0), (0, SizeZ), (SizeX, SizeZ) })
                Put(Armor(), new Vector3I(x, y, z));
            for (var z = 0; z <= SizeZ; z++)
            foreach (var (x, y) in new[] { (0, 0), (SizeX, 0), (0, SizeY), (SizeX, SizeY) })
                Put(Armor(), new Vector3I(x, y, z));
            // the room: an armor shell 5 x 5 x 5 at (10..14, 10..14, 10..14), the vent in the middle, (14, 12, 12) left out
            var gap = new Vector3I(14, 12, 12);
            var ventAt = new Vector3I(12, 12, 12);
            for (var x = 10; x <= 14; x++)
            for (var y = 10; y <= 14; y++)
            for (var z = 10; z <= 14; z++)
            {
                var p = new Vector3I(x, y, z);
                if (p == gap || (x > 10 && x < 14 && y > 10 && y < 14 && z > 10 && z < 14)) continue;
                Put(Armor(), p);
            }
            var ventOb = WorldApi.MakeBlockOb(vent.Id.SubtypeName);
            Put(ventOb, ventAt);

            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "frame", DisplayName = WorldApi.EntityPrefix + Prefix + "frame",
                GridSizeEnum = MyCubeSize.Large, IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(origin, Vector3D.CalculatePerpendicularVector(up), up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            Note($"GAS | frame {SizeX} x {SizeY} x {SizeZ} spawned: {grid.BlocksCount} blocks, the bounding box {(grid.Max - grid.Min + Vector3I.One).Volume()} cells");
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, i => (grid.PositionComp.WorldAABB.Center + up * 60, 0, 0), withCharacters: true);

            var gas = grid.GridSystems.GasSystem;
            Check(gas != null, "the grid has no gas system");
            bool Busy() => (bool)ProcessingField.GetValue(gas);
            var initial = Wait(() => RoomsField.GetValue(gas) != null && !Busy(), "the gas system has made its rooms", 120);
            while (initial.MoveNext()) yield return initial.Current;
            Note($"GAS | rooms made: {Rooms(gas).Count}, the outside {Rooms(gas)[0].BlockCount} cells");

            // welding: a block every PlaceEveryFrames frames along the bottom (y = 0, rows of z from 1), each welded up later
            var placed = 0;
            var pending = new Queue<(MySlimBlock Block, int At)>();
            var frame = 0;
            long busyTicks = 0, longestBusy = 0, busySince = 0;
            var refreshes = 0;
            gas.OnProcessingDataComplete += () => refreshes++;
            var wasBusy = Busy();
            var watch = Stopwatch.StartNew();
            var last = watch.ElapsedTicks;
            var x0 = 1;
            var z0 = 1;
            while (watch.Elapsed.TotalSeconds < WeldSeconds)
            {
                if (frame % PlaceEveryFrames == 0)
                {
                    var slim = AddBlock(grid, UnfinishedArmor(owner), new Vector3I(x0, 0, z0));
                    if (slim != null)
                    {
                        placed++;
                        pending.Enqueue((slim, frame));
                    }
                    if (++x0 >= SizeX) { x0 = 1; z0++; }
                }
                while (pending.Count > 0 && frame - pending.Peek().At >= WeldAfterFrames)
                {
                    var b = pending.Dequeue().Block;
                    if (!b.IsFullIntegrity) b.IncreaseMountLevel(b.MaxIntegrity * 10, owner, null, 1f, false, MyOwnershipShareModeEnum.None);
                }
                frame++;
                yield return null;
                var now = watch.ElapsedTicks;
                var busy = Busy();
                if (busy && !wasBusy) busySince = now;
                if (busy) busyTicks += now - last;
                if (!busy && wasBusy) longestBusy = Math.Max(longestBusy, now - busySince);
                wasBusy = busy;
                last = now;
            }
            while (pending.Count > 0)
            {
                var b = pending.Dequeue().Block;
                if (!b.IsFullIntegrity) b.IncreaseMountLevel(b.MaxIntegrity * 10, owner, null, 1f, false, MyOwnershipShareModeEnum.None);
            }
            var finished = 0;
            foreach (var b in grid.CubeBlocks) if (b.Position.Y == 0 && b.Position.X > 0 && b.Position.X < SizeX && b.Position.Z > 0 && b.Position.Z < SizeZ && b.IsFullIntegrity) finished++;
            var seconds = watch.Elapsed.TotalSeconds;
            var runs = refreshes;
            if (wasBusy) longestBusy = Math.Max(longestBusy, last - busySince);
            Note($"GAS | welding {seconds:0.0} s: {placed} blocks placed and {finished} welded up; the gas system busy {busyTicks * 100.0 / watch.ElapsedTicks:0.0} % of the time " +
                 $"({busyTicks * 1000.0 / Stopwatch.Frequency:0} ms), {runs} runs over, {busyTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, runs):0} ms a run; " +
                 $"the longest stretch busy without a frame free {longestBusy * 1000.0 / Stopwatch.Frequency:0} ms");

            // the wall block, and the system settled
            AddBlock(grid, Armor(owner), gap);
            var settle = Wait(() => !Busy() && ((System.Collections.ICollection)AddedField.GetValue(gas)).Count == 0, "the gas system has taken in every block", 60);
            while (settle.MoveNext()) yield return settle.Current;
            var calm = WaitForSeconds(3, "nothing more comes in");
            while (calm.MoveNext()) yield return calm.Current;
            settle = Wait(() => !Busy() && ((System.Collections.ICollection)AddedField.GetValue(gas)).Count == 0, "the gas system has taken in every block", 60);
            while (settle.MoveNext()) yield return settle.Current;

            var ventCell = ventAt;
            var room = gas.GetOxygenRoomForCubeGridPosition(ref ventCell);
            var outside = Rooms(gas)[0];
            var flood = Stopwatch.StartNew();
            var gameFlood = ((HashSet<Vector3I>)RoomBlocksMethod.Invoke(gas, new object[] { (Vector3I)StoredMinField.GetValue(gas), null })).Count;
            flood.Stop();
            Note($"GAS | wall closed: the vent's room {(room == null ? "none" : room.IsAirtight ? $"airtight, {room.BlockCount} cells" : $"not airtight ({(room == outside ? "the outside" : "room " + room.Index)})")}; " +
                 $"the outside {outside.BlockCount} cells, the game's flood of it {gameFlood} ({flood.ElapsedMilliseconds} ms on the game thread); {Rooms(gas).Count} rooms");
            Check(room != null && room.IsAirtight, "the vent's room did not become airtight when its wall was closed");
            Check(outside.BlockCount == gameFlood, $"the outside has {outside.BlockCount} cells, the game's flood finds {gameFlood}");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        private static MyConcurrentList<MyOxygenRoom> Rooms(MyGridGasSystem gas) => (MyConcurrentList<MyOxygenRoom>)RoomsField.GetValue(gas);

        private static MyObjectBuilder_CubeBlock Armor(long owner = 0) =>
            new MyObjectBuilder_CubeBlock { SubtypeName = "LargeBlockArmorBlock", Owner = owner, BuiltBy = owner };

        // (one plate mounted, the other plates in the block's stockpile: what a welder's first touch leaves)
        private static MyObjectBuilder_CubeBlock UnfinishedArmor(long owner)
        {
            var b = Armor(owner);
            b.BuildPercent = 1f / Plates;
            b.IntegrityPercent = 1f / Plates;
            b.ConstructionStockpile = new MyObjectBuilder_ConstructionStockpile
            {
                Items = new[]
                {
                    new MyObjectBuilder_StockpileItem { Amount = Plates - 1, PhysicalContent = new MyObjectBuilder_Component { SubtypeName = "SteelPlate" } },
                },
            };
            return b;
        }

        private static MySlimBlock AddBlock(MyCubeGrid grid, MyObjectBuilder_CubeBlock ob, Vector3I at)
        {
            ob.Min = at;
            return AddBlockMethod.Invoke(grid, new object[] { ob, false }) as MySlimBlock;
        }
    }
}
