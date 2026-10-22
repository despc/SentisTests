using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The world's limit of floating objects (<c>MaxFloatingObjects</c>): <see cref="Over"/> more loose items than the
    /// limit thrown into space at once (separate stacks, apart from each other), the game removes the extra ones
    /// (<c>MyFloatingObjects.ReduceFloatingObjects</c>, or SentisGameplayImprovements' own cleanup when it is on): within
    /// <see cref="Seconds"/> no more than the limit are in the world. Run alone: other tests' loose items count too and
    /// go first (the oldest ones are removed).
    /// </summary>
    public sealed class FloatingObjectsLimitScenario : TestScenario
    {
        public const string ScenarioName = "floating_objects_limit";
        private const int Over = 30;
        private const int Seconds = 15;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            var limit = MySession.Static.MaxFloatingObjects;
            SkipUnless(limit > 0 && limit <= 1000, "the world's limit of floating objects is " + limit);
            var before = MyEntities.GetEntities().OfType<MyFloatingObject>().Count();
            var center = new Vector3D(320000, 320000, 320000);
            var count = limit + Over;
            var plate = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate"));
            for (var i = 0; i < count; i++)
            {
                var at = center + new Vector3D(i % 20 * 3, i / 20 % 20 * 3, i / 400 * 3);
                MyFloatingObjects.Spawn(new VRage.Game.Entity.MyPhysicalInventoryItem((MyFixedPoint)1, plate), at, Vector3D.Forward, Vector3D.Up);
            }
            Note("thrown " + count + " plates (the limit " + limit + ", " + before + " loose items in the world before)");
            var spawned = Wait(() => MyEntities.GetEntities().OfType<MyFloatingObject>().Count() >= limit / 2, "the plates in the world", 10);
            while (spawned.MoveNext()) yield return spawned.Current;
            var reduced = Wait(() => MyEntities.GetEntities().OfType<MyFloatingObject>().Count(f => !f.MarkedForClose) <= limit, "no more loose items than the limit", Seconds);
            while (reduced.MoveNext()) yield return reduced.Current;
            var now = MyEntities.GetEntities().OfType<MyFloatingObject>().Count(f => !f.MarkedForClose);
            Note("FLOATING OBJECTS LIMIT RESULT | " + count + " thrown over a limit of " + limit + ": " + now + " left in the world");
        }

        public override void Cleanup()
        {
            try
            {
                foreach (var loose in MyEntities.GetEntities().OfType<MyFloatingObject>().Where(f => Vector3D.Distance(f.PositionComp.GetPosition(), new Vector3D(320000, 320000, 320000)) < 200).ToList())
                    loose.Close();
            }
            finally { base.Cleanup(); }
        }
    }
}
