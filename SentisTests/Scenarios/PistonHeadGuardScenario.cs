using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// "Add head" of a piston whose head check cannot run must not throw out of the request. On the server
    /// (11.10.2026) <c>MyPistonBase.CanPlaceTop</c> threw a NullReferenceException out of the player's "Add head" event
    /// and the player was dropped. The piston here has its third subpart taken away (what the check reads first), its
    /// head removed, and "Add head" asked from the client: with SentisOptimisations' PistonTopPlacement there is no
    /// throw, no head, and the refusal is counted; without it the request throws (the scenario fails).
    /// </summary>
    public sealed class PistonHeadGuardScenario : TestScenario
    {
        public const string ScenarioName = "piston_head_guard";
        private const string Prefix = "piston-guard-";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().OrderByDescending(x => x.MaximumRadius).FirstOrDefault();
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = centre + Vector3D.Normalize(new Vector3D(-0.4, 0.8, 0.45)) * planet.MaximumRadius;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var origin = ground + up * 60;

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (origin + up * 6, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var identity = FakeClients.PlayerOf(0).Identity.IdentityId;

            var plate = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
            plate.Owner = identity;
            var pistonOb = WorldApi.MakeBlockOb("LargePistonBase");
            pistonOb.Min = new SerializableVector3I(0, 1, 0);
            pistonOb.Owner = identity;
            pistonOb.BuiltBy = identity;
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "base", DisplayName = WorldApi.EntityPrefix + Prefix + "base",
                GridSizeEnum = MyCubeSize.Large, IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(origin, Vector3D.CalculatePerpendicularVector(up), up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = { plate, pistonOb },
            });
            Track(grid);
            yield return WaitForTicks(30);
            var piston = grid.GetFatBlocks().OfType<MyPistonBase>().FirstOrDefault();
            Check(piston != null, "no piston");
            if (piston.TopBlock?.CubeGrid is MyCubeGrid head)
            {
                typeof(MyMechanicalConnectionBlockBase).GetMethods(Any).First(m => m.Name == "Detach" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool))
                    .Invoke(piston, new object[] { true });
                head.Close();
                yield return WaitForTicks(20);
            }
            Check(piston.TopBlock == null, "the piston's head is still there");

            var guard = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("SentisOptimisationsPlugin.CrashFix.PistonTopPlacement");
            long Refused() => Convert.ToInt64(guard?.GetField("Refused")?.GetValue(null) ?? -1L);
            var before = Refused();

            piston.Subpart3 = null;       // what the check reads first
            var recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", Any);
            Check(recreate != null, "MyMechanicalConnectionBlockBase.DoRecreateTop not found");
            Exception thrown = null;
            try
            {
                FakeClients.AsClient(0, () => recreate.Invoke(piston, new object[] { identity, Enum.ToObject(recreate.GetParameters()[1].ParameterType, 0), false }));
            }
            catch (Exception e)
            {
                thrown = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            }
            yield return WaitForTicks(20);
            var strays = MyEntities.GetEntities().OfType<MyCubeGrid>().Count(g => !g.MarkedForClose && g.BlocksCount == 1 && g.CubeBlocks.First().FatBlock is MyPistonTop &&
                                                                                  Vector3D.Distance(g.PositionComp.GetPosition(), piston.PositionComp.GetPosition()) < 10);
            Note($"PISTON GUARD | 'Add head' without the third subpart: {(thrown == null ? "no throw" : "threw " + thrown.GetType().Name + ": " + thrown.Message)}; " +
                 $"head {(piston.TopBlock == null ? "not placed" : "placed")}; heads lying around {strays}; refusals counted {Refused() - before}");
            if (thrown != null) Note("PISTON GUARD | the throw: " + string.Join(" | ", (thrown.StackTrace ?? "").Split((char)10).Take(4)).Replace(((char)13).ToString(), ""));
            Check(thrown == null, $"'Add head' threw out of the request: {thrown?.GetType().Name}");
            Check(piston.TopBlock == null, "a head was placed with no check");
            Check(Refused() - before == 1, "the refusal was not counted");
            Check(strays == 0, "a head grid was left lying");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
