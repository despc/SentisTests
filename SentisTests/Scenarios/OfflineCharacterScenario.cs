using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A character nobody controls does not keep its physics cluster stepped (SentisOptimisations
    /// SelectivePhysicsBodies).
    ///
    /// The game steps a cluster with any character body in it, and a player who quits leaves the character where
    /// it stood (so do the SentisAi bots switched off): on the old server two such bodies kept two clusters stepped
    /// for nobody. Here, in open space far from everything: a plate and a fake player next to it - the cluster is
    /// stepped; the player quits the way a real one does, the character stays - the cluster must stop being stepped
    /// while the character is still there; a player comes again - stepped again.
    /// </summary>
    public sealed class OfflineCharacterScenario : TestScenario
    {
        public const string ScenarioName = "offline_character";
        private const string Prefix = "offc-";
        private const int WaitSeconds = 15;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo IsClusterActive =
            typeof(MyPhysics).GetMethod("IsClusterActive", Any, null, new[] { typeof(int), typeof(int) }, null)
            ?? throw new MissingMethodException("MyPhysics", "IsClusterActive");

        private readonly List<MyCharacter> _left = new List<MyCharacter>();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            // far out in space, away from the stand's planet, its bots and every other scenario
            var site = anchor + Vector3D.Normalize(new Vector3D(0.3, 1, 0.2)) * 400000;
            var plate = Plate(site);
            yield return null;

            FakeClients.Add(1, Network, i => (site + new Vector3D(6, 0, 0), 0, 0), withCharacters: true);
            var stepped = Wait(() => Stepped(plate) == true, "the plate's cluster stepped with the player there", WaitSeconds);
            while (stepped.MoveNext()) yield return stepped.Current;
            // stepped for real: a push moves it (on a server where nothing else is stepped the step itself starts again).
            // The freezer may have taken the plate before the player came: first it is thawed.
            var thawed = Wait(() => ((uint)plate.Flags & 4) == 0 && plate.Physics != null && !plate.Physics.IsStatic, "the plate thawed with the player there", WaitSeconds);
            while (thawed.MoveNext()) yield return thawed.Current;
            var from = plate.PositionComp.GetPosition();
            plate.Physics.ForceActivate();   // a body at rest sleeps, and a sleeping body ignores a new speed
            plate.Physics.SetSpeeds(new Vector3(1, 0, 0), Vector3.Zero);
            Note("pushed: speed " + plate.Physics.LinearVelocity.Length().ToString("F2") + ", active " + plate.Physics.IsActive + ", static " + plate.Physics.IsStatic +
                 ", in world " + plate.Physics.IsInWorld + ", frozen flag " + (((uint)plate.Flags & 4) != 0));
            yield return WaitForTicks(5);
            Note("5 ticks later: speed " + plate.Physics.LinearVelocity.Length().ToString("F2") + ", moved " + Vector3D.Distance(plate.PositionComp.GetPosition(), from).ToString("F3") + " m, stepped " + Stepped(plate));
            var moving = WaitForSeconds(2, "the plate drifts at 1 m/s");
            while (moving.MoveNext()) yield return moving.Current;
            var moved = Vector3D.Distance(plate.PositionComp.GetPosition(), from);
            Check(moved > 1, "the plate did not move with the player there: " + moved.ToString("F2") + " m in 2 s");
            plate.Physics.SetSpeeds(Vector3.Zero, Vector3.Zero);

            // the player quits as a real one does: the character stays
            _left.AddRange(FakeClients.QuitKeepingCharacters());
            Check(_left.Count == 1, "the quitting player left " + _left.Count + " characters, not 1");
            var character = _left[0];
            yield return WaitForTicks(5);
            Check(!character.MarkedForClose && character.Physics?.HavokWorld != null, "the character left behind is not in the world");
            Note("the character left behind: controlled " + (character.ControllerInfo?.Controller != null) + ", in the plate's cluster " +
                 (character.Physics.HavokWorld == plate.Physics?.HavokWorld));
            var alone = Wait(() => Stepped(plate) == false, "the plate's cluster not stepped with only the left character there", WaitSeconds);
            while (alone.MoveNext()) yield return alone.Current;
            Check(!character.MarkedForClose, "the character left behind was closed");

            // a player comes back
            FakeClients.Add(1, Network, i => (site + new Vector3D(-6, 0, 0), 0, 0), withCharacters: true);
            var back = Wait(() => Stepped(plate) == true, "the plate's cluster stepped again with a player back", WaitSeconds);
            while (back.MoveNext()) yield return back.Current;
            Note("OFFLINE CHARACTER RESULT | stepped (and moving) with a player, not stepped with only the character left behind, stepped again with a player back");
        }

        private static bool? Stepped(MyCubeGrid grid)
        {
            var world = grid.Physics?.HavokWorld;
            if (world == null) return null;
            var cluster = MyPhysics.Clusters.GetClusters().FirstOrDefault(c => c.UserData == world);
            if (cluster == null) return null;
            return (bool)IsClusterActive.Invoke(Sandbox.Game.World.MySession.Static.GetComponent<MyPhysics>(),
                new object[] { cluster.ClusterId, world.CharacterRigidBodies.Count });
        }

        private MyCubeGrid Plate(Vector3D at)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
            for (var z = 0; z < 3; z++)
            {
                var block = WorldApi.MakeBlockOb("SmallBlockArmorBlock");
                block.Min = new SerializableVector3I(x, 0, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                blocks.Add(block);
            }
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "plate",
                DisplayName = WorldApi.EntityPrefix + Prefix + "plate",
                GridSizeEnum = MyCubeSize.Small,
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at)),
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
                foreach (var character in _left)
                    if (character != null && !character.MarkedForClose) character.Close();
                _left.Clear();
            }
            finally { base.Cleanup(); }
        }
    }
}
