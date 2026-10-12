using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Pistons built from a projection, as a ship welder builds them (<c>MyProjectorBase.Build</c>, the call of
    /// SentisOptimisations' welder): from the server (11.10.2026) <c>MyPistonBase.CanPlaceTop</c> threw a
    /// NullReferenceException there, when the built piston made its head. A static platform with a projector; the
    /// blueprint is an armour block with pistons around it: one up into free space, one sideways, one whose head's place
    /// another blueprint block takes, one down into the platform, one on a pillar of the blueprint, one under the port of
    /// a connector on the platform (the connector's detector: a phantom body of an entity without Physics, which the
    /// check reads). Everything buildable
    /// is built, round after round; then for each piston: built, head made or not, and how many head checks threw
    /// (SentisOptimisations' PistonTopPlacement counts them and writes what was missing).
    /// </summary>
    public sealed class PistonProjectionScenario : TestScenario
    {
        public const string ScenarioName = "piston_projection";
        private const int Deck = 8;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            // every piston of the game: does its head have a collision shape at every stage (the check reads shape [0] of the
            // head as made - from a projection in survival a frame, not a whole block)
            foreach (var definition in Sandbox.Definitions.MyDefinitionManager.Static.GetAllDefinitions().OfType<Sandbox.Definitions.MyPistonBaseDefinition>())
            {
                var group = Sandbox.Definitions.MyDefinitionManager.Static.TryGetDefinitionGroup(definition.TopPart);
                var top = group?[definition.CubeSize];
                if (top == null) { Note($"PISTON MODELS | {definition.Id.SubtypeName}: no head '{definition.TopPart}'"); continue; }
                string Shapes(string model) => model == null ? "no model" :
                    VRage.Game.Models.MyModels.GetModelOnlyData(model)?.HavokCollisionShapes is var shapes && shapes != null ? shapes.Length + " shapes" : "NO SHAPES";
                Note($"PISTON MODELS | {definition.Id.SubtypeName} -> {top.Id.SubtypeName}: whole {Shapes(top.Model)}; stages " +
                     string.Join(", ", (top.BuildProgressModels ?? new Sandbox.Definitions.MyCubeBlockDefinition.BuildProgressModel[0]).Select(m => $"{m.BuildRatioUpperBound:0.00}: {Shapes(m.File)}")));
            }
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().OrderByDescending(x => x.MaximumRadius).FirstOrDefault();
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = centre + Vector3D.Normalize(new Vector3D(0.3, 0.85, -0.4)) * planet.MaximumRadius;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var origin = ground + up * 40;

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (origin + up * 15, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var owner = WorldApi.TestIdentityId();
            // the world from the server has research on: the owner must have the blocks, or the projector builds nothing
            Sandbox.Game.SessionComponents.MySessionComponentResearch.Static?.DebugUnlockAllResearch(owner);

            var armor = WorldApi.FindSubtype(MyCubeSize.Large, "blockarmorblock");
            var piston = "LargePistonBase";
            var projectorType = WorldApi.FindSubtype(MyCubeSize.Large, "projector");
            var batteryType = WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock");

            MyObjectBuilder_CubeBlock Block(string subtype, int x, int y, int z, Base6Directions.Direction forward = Base6Directions.Direction.Forward,
                Base6Directions.Direction pointing = Base6Directions.Direction.Up)
            {
                var b = WorldApi.MakeBlockOb(subtype);
                b.Min = new SerializableVector3I(x, y, z);
                b.BlockOrientation = new SerializableBlockOrientation(forward, pointing);
                b.Owner = owner;
                b.BuiltBy = owner;
                return b;
            }
            var blueprint = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + "piston-blueprint", GridSizeEnum = MyCubeSize.Large, IsStatic = false,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>
                {
                    Block(armor, 0, 0, 0),
                    Block(piston, 0, 1, 0),                                                                         // up, free
                    Block(piston, 1, 0, 0, Base6Directions.Direction.Forward, Base6Directions.Direction.Right),     // sideways
                    Block(piston, -1, 0, 0),                                                                        // its head's place taken
                    Block(armor, -1, 1, 0),
                    Block(piston, 0, 0, 1, Base6Directions.Direction.Forward, Base6Directions.Direction.Down),      // down into the platform
                    Block(armor, 0, 0, -1), Block(armor, 0, 1, -1), Block(armor, 0, 2, -1),
                    Block(piston, 0, 3, -1),                                                                        // on a pillar
                    Block(piston, 2, 0, 2),                                                                         // under a connector's port
                    // (no reskinned piston: a DLC block, the projector does not build it for a player without the DLC)
                },
            };
            var pistonCells = blueprint.CubeBlocks.Where(b => b.SubtypeName.Contains("PistonBase")).Select(b => (Vector3I)b.Min).ToList();

            var deck = new List<BlockSpec>();
            for (var x = 0; x < Deck; x++)
            for (var z = 0; z < Deck; z++)
                deck.Add(new BlockSpec(armor, new Vector3I(x, 0, z)));
            deck.Add(new BlockSpec(projectorType, new Vector3I(Deck - 1, 1, 0)));
            deck.Add(new BlockSpec(batteryType, new Vector3I(0, 1, Deck - 1)));
            // a connector on a pillar, its port down over the place of the head of the piston at blueprint (2, 0, 2) = deck (5, 1, 6):
            // its detector is a phantom body whose entity (the connector) has no Physics of its own
            for (var y = 1; y <= 3; y++) deck.Add(new BlockSpec(armor, new Vector3I(6, y, 6)));
            deck.Add(new BlockSpec("Connector", new Vector3I(5, 3, 6)));
            var platformOb = WorldApi.GridOb(WorldApi.EntityPrefix + "piston-platform", MyCubeSize.Large, true, origin, deck,
                Vector3D.CalculatePerpendicularVector(up), up);
            foreach (var connector in platformOb.CubeBlocks.Where(b => b.SubtypeName == "Connector"))
                connector.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Down, Base6Directions.Direction.Forward);
            foreach (var pb in platformOb.CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>())
            {
                pb.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { blueprint };
                pb.ProjectionOffset = new Vector3I(4, 0, -4);   // the armour at the blueprint origin stands on deck cell (3, 0, 4)
                pb.ProjectionsRemaining = 50;
                pb.Scale = 1;
            }
            var platform = WorldApi.SpawnGrid(platformOb);
            Track(platform);
            yield return WaitForTicks(60);
            var projector = WorldApi.FindFunctional<MyProjectorBase>(platform);
            Check(projector != null, "no projector");
            yield return Wait(() => { WorldApi.ChargeBatteries(platform); return projector.IsWorking && projector.ProjectedGrid != null; }, "the projection is up", 60);

            // what a head check's query (layer 15, as MyPistonBase.CanPlaceTop) sees at the connector's port
            var port = platform.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyShipConnector>().FirstOrDefault();
            if (port != null)
            {
                var hits = new List<Havok.HkBodyCollision>();
                var box = new Havok.HkBoxShape(new Vector3(0.6f));
                var at = port.PositionComp.GetPosition() + port.WorldMatrix.Forward * 1.25;
                var rotation = Quaternion.Identity;
                Sandbox.Engine.Physics.MyPhysics.GetPenetrationsShape(box, ref at, ref rotation, hits, 15);
                box.Base.RemoveReference();
                Note("PISTON PROJECTION | a layer-15 query at the connector's port touches: " + string.Join(", ", hits.Select(h =>
                {
                    var e = Sandbox.Engine.Physics.MyPhysicsExtensions.GetCollisionEntity(h);
                    return e == null ? "a body without an entity" : $"{e.GetType().Name}{(e.Physics == null ? " WITHOUT PHYSICS" : e.Physics.IsPhantom ? " (phantom)" : "")}";
                })));
            }

            var guard = Integrations.TypeOf("SentisOptimisations", "SentisOptimisationsPlugin.CrashFix.PistonTopPlacement");
            long Refused() => Convert.ToInt64(guard?.GetField("Refused")?.GetValue(null) ?? -1L);
            var refused0 = Refused();
            var thrown = new List<string>();
            var built = 0;
            for (var round = 0; round < 20; round++)
            {
                var preview = projector.ProjectedGrid;
                if (preview == null) break;
                var ready = preview.CubeBlocks.Where(b => projector.CanBuild(b, true) == BuildCheckResult.OK).ToList();
                if (round == 0)
                    Note($"projection: {preview.CubeBlocks.Count} blocks, {ready.Count} buildable now; preview cells " +
                         string.Join(" ", preview.CubeBlocks.Select(b => $"{b.BlockDefinition.Id.SubtypeName}@{b.Position}->{platform.WorldToGridInteger(preview.GridIntegerToWorld(b.Position))}:{projector.CanBuild(b, true)}")));
                if (ready.Count == 0) break;
                foreach (var block in ready)
                {
                    try
                    {
                        projector.Build(block, owner, projector.EntityId, true, owner);
                        built++;
                    }
                    catch (Exception e)
                    {
                        thrown.Add($"{block.BlockDefinition.Id.SubtypeName}@{block.Position}: {e.GetType().Name}");
                    }
                }
                yield return WaitForTicks(20);
            }

            var pistons = platform.GetFatBlocks().OfType<MyPistonBase>().ToList();
            foreach (var p in pistons)
                Note($"PISTON PROJECTION | piston at {p.Position} pointing {p.Orientation.Up}: head {(p.TopBlock == null ? "NONE" : "made")}, " +
                     $"third subpart {(p.Subpart3 == null ? "missing" : p.Subpart3.Model == null ? "without a model" : "there")}");
            Note($"PISTON PROJECTION | built {built} blocks, {pistons.Count} of {pistonCells.Count} pistons; head checks that threw {Refused() - refused0}; " +
                 $"throws out of Build {thrown.Count}{(thrown.Count == 0 ? "" : ": " + string.Join("; ", thrown))}");
            Check(pistons.Count == pistonCells.Count, "not every piston of the blueprint was built");
            Check(thrown.Count == 0, "Build threw");
            Check(Refused() - refused0 == 0, "a piston's head check threw");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
