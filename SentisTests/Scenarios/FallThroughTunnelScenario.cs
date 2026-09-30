using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The fall-through recovery of SentisGameplayImprovements (AutoRestoreFromVoxel) against grids that are under
    /// the ground on purpose.
    ///
    /// A drill head down its own shaft was pulled out of the ground on every load of the old server: rock stood over
    /// its centre, and that was all the detector asked. Three small plates on the planet, a fake player next to them:
    /// one in a cave cut into the rock, under a shelf of rock (rock over it, air where its blocks are) - it must stay;
    /// one buried in solid rock - it must be brought out over the ground; one on the surface - left alone. And a rotor
    /// on a static grid deep in the rock with its head buried: held by the static grid it cannot have fallen, and
    /// neither the head nor the static grid may be moved (the old server's drill rig took its bot's base along).
    /// </summary>
    public sealed class FallThroughTunnelScenario : TestScenario
    {
        public const string ScenarioName = "fall_through_tunnel";
        private const string Prefix = "ftt-";
        /// <summary>The cave: this much rock over it, this big; the shelf over the plate: from this high over its centre, this thick, this wide.</summary>
        private const double CaveDepthM = 6, CaveRadiusM = 7, ShelfThickM = 3, ShelfHalfWidthM = 3.5;
        private static readonly double[] ShelfFromM = { 0.75, 0.6 };
        private const double WatchSeconds = 12;

        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        // The cave and the shelf filled into it are put back as generated in the cleanup.
        private MyPlanet _planet;
        private Vector3D? _caveCentre;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            _planet = planet;
            Check(planet != null, "no planet");
            var ground = new PlanetGround(planet);
            var up0 = ground.Up(anchorM.Translation);
            var east = Vector3D.Normalize(anchorM.Forward - up0 * Vector3D.Dot(anchorM.Forward, up0));
            var site = ground.Ground(anchorM.Translation + east * 900);
            var up = ground.Up(site);
            var side = Vector3D.Normalize(Vector3D.Cross(up, east));
            var forward = Vector3D.Normalize(Vector3D.Cross(side, up));

            _gameplay.Set("AutoRestoreFromVoxel", true);

            // the cave: cut deep in the rock; a shelf of rock is filled back in over the plate once it has come to rest
            var caveCentre = site - up * (CaveDepthM + CaveRadiusM);
            _caveCentre = caveCentre;
            string Column(Vector3D at) => string.Join(" ", Enumerable.Range(0, 15).Select(k => 3 - k * 0.5).Select(h => h.ToString("F1") + ":" + ground.ContentAt(at + up * h)));
            MyVoxelGenerator.CutOutShapeWithProperties(planet, new MyShapeSphere { Center = caveCentre, Radius = (float)CaveRadiusM }, out var cut, out _, null, updateSync: true);
            var floor = caveCentre;
            for (var h = 0.0; h > -CaveRadiusM - 3; h -= 0.25)
                if (ground.ContentAt(caveCentre + up * h) >= 128) { floor = caveCentre + up * (h + 0.25); break; }
            yield return WaitForTicks(5);
            Note("cave cut " + cut + ", floor " + (site - floor).Length().ToString("F1") + " m under the ground");

            var tunnel = Plate("tunnel", floor + up * 0.45, forward, up);
            var buried = Plate("buried", site - up * 4 + side * 20, forward, up);
            var surface = Plate("surface", ground.Ground(site + side * 40) + up * 0.6, forward, up);
            // a rotor on a static grid deep in the rock, its head (dynamic) buried too: held by the static grid, it can
            // not have fallen - and moving it would move the static grid along
            var rigBase = RotorBase(site - side * 25 - up * 9, forward, up);
            var stator = rigBase.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyMotorStator>().First();
            CreateTopPart.Invoke(stator, new[] { (object)WorldApi.PlayerIdentityId(), NormalTopSize, true });
            yield return WaitForTicks(3);
            var head = stator.TopGrid;
            Check(head != null, "the rotor got no head");
            Track(head);
            var start = new Dictionary<MyCubeGrid, Vector3D>();
            // the buried ones from where they were put: the check may act while the others settle
            start[buried] = buried.PositionComp.GetPosition();
            start[rigBase] = rigBase.PositionComp.GetPosition();
            start[head] = head.PositionComp.GetPosition();
            Check(ground.SmoothContentAt(head.PositionComp.WorldAABB.Center) >= 128, "the rotor head is not in rock");
            Check(ground.ContentAt(buried.PositionComp.WorldAABB.Center) >= 128, "the buried plate is not in rock");

            FakeClients.Add(1, Network, i => (site + side * 10 + up * 2, 0, 0), withCharacters: true);
            var settle = WaitForSeconds(5, "the plates settle");
            while (settle.MoveNext()) yield return settle.Current;

            // the shelf: rock filled back in over the plate where it came to rest, from just over its top (a thin cut
            // or fill comes out smoothed over half a metre either way)
            var tunnelCentre = tunnel.PositionComp.WorldAABB.Center;
            Note("plate in the cave came to rest with rock content " + ground.SmoothContentAt(tunnelCentre).ToString("F0") + " at its centre");
            var rockAt = site - up * 3;
            var rock = planet.GetMaterialAt(ref rockAt);
            Check(rock != null, "no voxel material in the rock");
            // down in steps until the old check reads rock 1.5 m over the plate (where the plate lies in the voxel grid
            // decides how far the fill reaches)
            foreach (var from in ShelfFromM)
            {
                MyVoxelGenerator.FillInShape(planet, new MyShapeBox
                {
                    Boundaries = new BoundingBoxD(new Vector3D(-ShelfHalfWidthM, from, -ShelfHalfWidthM), new Vector3D(ShelfHalfWidthM, from + ShelfThickM, ShelfHalfWidthM)),
                    Transformation = MatrixD.CreateWorld(tunnelCentre, forward, up),
                }, rock.Index);
                yield return WaitForTicks(5);
                Note("shelf of " + rock.Id.SubtypeName + " from " + from + " m over the plate's centre; column from its centre (height : content): " + Column(tunnelCentre));
                // done when the old check reads rock over it - or before the shelf comes down on the plate itself
                if (ground.ContentAt(tunnelCentre + up * 1.5) >= 128 || ground.SmoothContentAt(tunnelCentre) >= 110) break;
            }
            start[tunnel] = tunnel.PositionComp.GetPosition();
            start[surface] = surface.PositionComp.GetPosition();

            // what the old check saw: rock 1.5 m over the plate in the cave - and air where its blocks are
            tunnelCentre = tunnel.PositionComp.WorldAABB.Center;
            var overPlate = ground.ContentAt(tunnelCentre + up * 1.5);
            var atPlate = ground.SmoothContentAt(tunnelCentre);
            Note("plate in the cave: rock content 1.5 m over its centre " + overPlate + " (what the old check read), at its centre " + atPlate.ToString("F0") +
                 "; plate on the surface: " + ground.SmoothContentAt(surface.PositionComp.WorldAABB.Center).ToString("F0"));
            Check(atPlate < 128, "the plate in the cave stands in rock (" + atPlate.ToString("F0") + "): the shelf came down on it");

            var watch = WaitForSeconds(WatchSeconds, "the fall-through check looks at them");
            while (watch.MoveNext()) yield return watch.Current;

            double Moved(MyCubeGrid g) => Vector3D.Distance(g.PositionComp.GetPosition(), start[g]);
            // brought out: lifted to the surface from 4 m down (the ground read from the voxels is good to half a metre)
            var buriedOut = Vector3D.Dot(buried.PositionComp.GetPosition() - start[buried], up) > 2.5;
            Note("FALL THROUGH RESULT | plate in the cave (the old check " + (overPlate >= 128 ? "would have taken it" : "would not have fired this time") + ") moved " + Moved(tunnel).ToString("F2") + " m | buried plate moved " + Moved(buried).ToString("F1") +
                 " m, brought out " + buriedOut + " | surface plate moved " + Moved(surface).ToString("F2") + " m | buried rotor head on a static grid moved " +
                 Moved(head).ToString("F2") + " m, the static grid " + Moved(rigBase).ToString("F2") + " m, head attached " + (stator.TopGrid == head));
            Check(Moved(tunnel) < 1, "the plate in the cave was moved " + Moved(tunnel).ToString("F1") + " m: taken for a fall-through");
            Check(buriedOut, "the buried plate was not brought out of the rock");
            Check(Moved(surface) < 1, "the plate on the surface was moved " + Moved(surface).ToString("F1") + " m");
            Check(Moved(rigBase) < 0.01, "the static grid was moved " + Moved(rigBase).ToString("F2") + " m");
            Check(Moved(head) < 1, "the rotor head held by the static grid was moved " + Moved(head).ToString("F1") + " m: taken for a fall-through");
        }

        private static readonly System.Reflection.MethodInfo CreateTopPart = typeof(Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase)
            .GetMethod("CreateTopPartAndAttach", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly object NormalTopSize = CreateTopPart == null ? null
            : Enum.Parse(CreateTopPart.GetParameters()[1].ParameterType, "Normal");

        /// <summary>A static grid of one armour block with a large rotor on top of it (the rotor pointing up).</summary>
        private MyCubeGrid RotorBase(Vector3D at, Vector3D forward, Vector3D up)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            foreach (var (subtype, y) in new[] { ("LargeBlockArmorBlock", 0), ("LargeStator", 1) })
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(0, y, 0);
                block.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
                block.Owner = owner;
                block.BuiltBy = owner;
                blocks.Add(block);
            }
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "rotor-base",
                DisplayName = WorldApi.EntityPrefix + Prefix + "rotor-base",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, forward, up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            WorldApi.ChargeBatteries(grid);
            return grid;
        }

        /// <summary>A 5x5 plate of small armour blocks, one block thick, its centre at the point.</summary>
        private MyCubeGrid Plate(string name, Vector3D centre, Vector3D forward, Vector3D up)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 5; x++)
            for (var z = 0; z < 5; z++)
            {
                var block = WorldApi.MakeBlockOb("SmallBlockArmorBlock");
                block.Min = new SerializableVector3I(x, 0, z);
                block.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
                block.Owner = owner;
                block.BuiltBy = owner;
                blocks.Add(block);
            }
            // the grid's origin is the centre of block (0,0,0): the plate's centre is 2 blocks of 0.5 m in along x and z
            var right = Vector3D.Cross(forward, up);
            var origin = centre - right * 1.0 + forward * 1.0;
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Small,
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(origin, forward, up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _gameplay.Restore();
                if (_caveCentre.HasValue) WorldApi.RevertTerrain(_planet, _caveCentre.Value, CaveRadiusM + ShelfHalfWidthM + ShelfThickM);
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                _gameplay.Restore();
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
