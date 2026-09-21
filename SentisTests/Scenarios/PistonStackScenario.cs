using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// A stack of fourteen pistons, copied from the operator's world, put up in the atmosphere and
    /// put through what a stack goes through - and checked to hold together at every step.
    ///
    /// The stack is <see cref="ResourceName"/>: a static base and fourteen large pistons standing
    /// on each other, fully extended. It is spawned a kilometre above the ground, the base fixed in
    /// the air and the stack pointing away from the planet, and then:
    ///
    ///  1. it settles: every grid there, every piston attached, the tip where fourteen extended
    ///     pistons put it and nothing leaning over;
    ///  2. it is knocked sideways, the tip hardest, and has to calm down again;
    ///  3. it retracts all the way, and extends all the way back, and at both ends it has to be one
    ///     straight stack of the right height.
    /// </summary>
    public sealed class PistonStackScenario : TestScenario
    {
        public const string ScenarioName = "piston_stack";
        public const string ResourceName = "SentisTests.Resources.PistonStack.xml";
        private const string Prefix = "pstack-";
        private const int ExpectedGrids = 15;
        private const int ExpectedPistons = 14;
        private const double AltitudeM = 1000;
        private const double SettleSeconds = 10;
        private const double HeightToleranceM = 1.5;
        private const double LeanToleranceM = 1.5;
        private const float CalmSpeed = 0.05f;
        private const float KnockSpeed = 2.0f;
        private const int CalmWithinSeconds = 10;
        private const float TravelSpeed = 1.0f;
        private const int TravelSeconds = 40;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _config = new ConfigOverride();
        private List<MyCubeGrid> _grids = new List<MyCubeGrid>();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            // ------------------------------------------------------------- where
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var basePos = anchorM.Translation + up * AltitudeM;
            Check(planet.GetAirDensity(basePos) > 0.1f, "the spawn point is not in the atmosphere");

            // A player next to it first: Havok only simulates a cluster somebody is in, and a stack
            // spawned into an idle cluster falls apart before anyone looks at it.
            FakeClients.RemoveAll();
            var watchFrom = basePos + up * 100 + Vector3D.CalculatePerpendicularVector(up) * 30;
            FakeClients.Add(1, Network, p => (watchFrom, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            Check(FakeClients.Character(0) != null, "the fake player has no character");

            // ------------------------------------------------------------- the stack
            var group = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + Prefix + "stack");
            Check(group.Count == ExpectedGrids, "the stack resource holds " + group.Count + " grids, not " + ExpectedGrids);
            var base0 = group[0].PositionAndOrientation.Value.GetMatrix();
            var tip0 = group.Select(g => g.PositionAndOrientation.Value.Position).Select(p => (Vector3D)p)
                .OrderByDescending(p => Vector3D.DistanceSquared(p, base0.Translation)).First();
            var axis0 = Vector3D.Normalize(tip0 - base0.Translation);
            // the whole stack turned so that it stands up from the planet, and moved over there
            var turn = MatrixD.CreateFromQuaternion(QuaternionD.CreateFromTwoVectors(axis0, up));
            var move = MatrixD.CreateTranslation(-base0.Translation) * turn * MatrixD.CreateTranslation(basePos);
            foreach (var ob in group)
                ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
            group[0].IsStatic = true;

            _grids = group.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in _grids) Track(grid);
            var blocks = _grids.Sum(g => g.BlocksCount);
            Note("spawned the stack " + AltitudeM.ToString("F0") + " m up: " + _grids.Count + " grids, " + blocks + " blocks");

            var settle = WaitForSeconds(SettleSeconds, "the stack settles");
            while (settle.MoveNext()) yield return settle.Current;

            var baseGrid = _grids[0];
            var pistons = Pistons();
            Check(pistons.Count == ExpectedPistons, "the stack has " + pistons.Count + " pistons, not " + ExpectedPistons);
            var extended = pistons.Sum(p => (double)p.MaxLimit);
            var retracted = pistons.Sum(p => (double)p.MinLimit);
            var extendedHeight = Height(baseGrid, up);
            var shape0 = Shape(baseGrid, up);
            Note("settled: " + shape0 + " | " + State());
            CheckWhole(blocks, "after settling");
            CheckCalm("after settling");
            Check(Lean(baseGrid, up) < LeanToleranceM, "the stack leans after settling: " + shape0);

            // ------------------------------------------------------------- knocked
            var chain = Chain();
            var side = (Vector3)Vector3D.CalculatePerpendicularVector(up);
            for (var i = 1; i < chain.Count; i++)
                chain[i].Physics.LinearVelocity += side * (KnockSpeed * i / (chain.Count - 1));
            var knocked = DateTime.UtcNow;
            var calm = Wait(() => Calm(), "the knocked stack calms down", CalmWithinSeconds);
            while (calm.MoveNext()) yield return calm.Current;
            var calmIn = (DateTime.UtcNow - knocked).TotalSeconds;
            Note("knocked at " + KnockSpeed + " m/s: calm in " + calmIn.ToString("F1") + " s, " + Shape(baseGrid, up));
            CheckWhole(blocks, "after the knock");
            Check(Math.Abs(Height(baseGrid, up) - extendedHeight) < HeightToleranceM, "the knock changed the height: " + Shape(baseGrid, up));
            Check(Lean(baseGrid, up) < LeanToleranceM, "the stack leans after the knock: " + Shape(baseGrid, up));

            // ------------------------------------------------------------- down and up again
            foreach (var p in pistons) ((Sandbox.ModAPI.IMyPistonBase)p).Velocity = -TravelSpeed;
            var down = Wait(() => pistons.All(p => ((Sandbox.ModAPI.IMyPistonBase)p).CurrentPosition <= p.MinLimit + 0.01f),
                "every piston retracted (" + State() + ")", TravelSeconds);
            while (down.MoveNext()) yield return down.Current;
            var rest = WaitForSeconds(3, "the retracted stack comes to rest");
            while (rest.MoveNext()) yield return rest.Current;
            var shapeDown = Shape(baseGrid, up);
            Note("retracted: " + shapeDown);
            CheckWhole(blocks, "retracted");
            Check(Math.Abs(extendedHeight - Height(baseGrid, up) - (extended - retracted)) < HeightToleranceM,
                "retracting did not shorten the stack by " + (extended - retracted).ToString("F1") + " m: " + shapeDown);
            Check(Lean(baseGrid, up) < LeanToleranceM, "the retracted stack leans: " + shapeDown);

            foreach (var p in pistons) ((Sandbox.ModAPI.IMyPistonBase)p).Velocity = TravelSpeed;
            var upAgain = Wait(() => pistons.All(p => ((Sandbox.ModAPI.IMyPistonBase)p).CurrentPosition >= p.MaxLimit - 0.01f),
                "every piston extended again", TravelSeconds);
            while (upAgain.MoveNext()) yield return upAgain.Current;
            rest = WaitForSeconds(3, "the extended stack comes to rest");
            while (rest.MoveNext()) yield return rest.Current;
            var shapeUp = Shape(baseGrid, up);
            CheckWhole(blocks, "extended again");
            Check(Math.Abs(Height(baseGrid, up) - extendedHeight) < HeightToleranceM,
                "extending did not bring the stack back to its height: " + shapeUp);
            Check(Lean(baseGrid, up) < LeanToleranceM, "the extended stack leans: " + shapeUp);
            CheckCalm("extended again");

            Note("PISTON STACK RESULT | " + _grids.Count + " grids, " + pistons.Count + " pistons held together: settled " +
                 shape0 + " | knocked, calm in " + calmIn.ToString("F1") + " s | retracted " + shapeDown +
                 " | extended again " + shapeUp);
        }

        // ------------------------------------------------------------------ the stack

        private string State()
        {
            var pistons = Pistons();
            return "pistons working " + pistons.Count(p => p.IsWorking) + "/" + pistons.Count +
                   ", functional " + pistons.Count(p => p.IsFunctional) + ", enabled " + pistons.Count(p => p.Enabled) +
                   ", positions " + string.Join(",", pistons.Select(p => ((Sandbox.ModAPI.IMyPistonBase)p).CurrentPosition.ToString("F1"))) +
                   ", velocities " + string.Join(",", pistons.Select(p => ((float)p.Velocity).ToString("F1"))) +
                   " | static grids " + _grids.Count(g => g.IsStatic) + "/" + _grids.Count +
                   ", physics on " + _grids.Count(g => g.Physics != null && g.Physics.Enabled) +
                   ", awake " + _grids.Count(g => g.Physics?.RigidBody != null && g.Physics.RigidBody.IsActive) +
                   ", frozen " + _grids.Count(g => RuntimePluginControls.IsGridFrozen(g.EntityId)) +
                   ", in scene " + _grids.Count(g => g.InScene) +
                   ", gravity " + (_grids.Count > 1 && _grids[1].Physics != null ? _grids[1].Physics.Gravity.Length().ToString("F1") : "-") +
                   " | power " + WorldApi.DescribePower(_grids[0]) +
                   " | base blocks " + string.Join(",", _grids[0].GetFatBlocks().Select(b => b.BlockDefinition.Id.SubtypeName));
        }

        private List<MyPistonBase> Pistons() =>
            _grids.SelectMany(g => g.GetFatBlocks().OfType<MyPistonBase>()).ToList();

        /// <summary>The grids from the base up, following the pistons.</summary>
        private List<MyCubeGrid> Chain()
        {
            var chain = new List<MyCubeGrid> { _grids[0] };
            var byGrid = Pistons().Where(p => p.TopGrid != null).GroupBy(p => p.CubeGrid).ToDictionary(g => g.Key, g => g.First());
            MyPistonBase piston;
            while (chain.Count <= ExpectedGrids && byGrid.TryGetValue(chain[chain.Count - 1], out piston) && !chain.Contains(piston.TopGrid))
                chain.Add(piston.TopGrid);
            return chain;
        }

        private Vector3D Tip() => Chain().Last().PositionComp.WorldAABB.Center;

        /// <summary>
        /// The stack's own axis: the way the bottom piston extends, from where it stands. The line
        /// between grid origins is off it by a degree or two, which over the 140 m a stack travels
        /// is metres.
        /// </summary>
        private MyPistonBase BottomPiston(MyCubeGrid baseGrid) =>
            baseGrid.GetFatBlocks().OfType<MyPistonBase>().First();

        private double Height(MyCubeGrid baseGrid, Vector3D up)
        {
            var piston = BottomPiston(baseGrid);
            return Vector3D.Dot(Tip() - piston.PositionComp.GetPosition(), piston.WorldMatrix.Up);
        }

        private double Lean(MyCubeGrid baseGrid, Vector3D up)
        {
            var piston = BottomPiston(baseGrid);
            var axis = piston.WorldMatrix.Up;
            var d = Tip() - piston.PositionComp.GetPosition();
            return (d - Vector3D.Dot(d, axis) * axis).Length();
        }

        private string Shape(MyCubeGrid baseGrid, Vector3D up) =>
            "tip " + Height(baseGrid, up).ToString("F1") + " m up, " + Lean(baseGrid, up).ToString("F2") + " m off the axis";

        private bool Calm() =>
            _grids.Skip(1).All(g => g.Physics == null || g.Physics.LinearVelocity.Length() < CalmSpeed);

        private void CheckCalm(string when)
        {
            var fastest = _grids.Skip(1).Max(g => g.Physics?.LinearVelocity.Length() ?? 0f);
            Check(fastest < CalmSpeed, "the stack is still moving " + when + ": " + fastest.ToString("F3") + " m/s");
        }

        /// <summary>Every grid and block still there, and every piston holding the grid above it.</summary>
        private void CheckWhole(int blocks, string when)
        {
            var gone = _grids.Count(g => g.MarkedForClose || g.Closed);
            Check(gone == 0, gone + " grids of the stack are gone " + when);
            var now = _grids.Sum(g => g.BlocksCount);
            Check(now == blocks, "the stack lost blocks " + when + ": " + now + " of " + blocks);
            var pistons = Pistons();
            var detached = pistons.Count(p => !((Sandbox.ModAPI.IMyMechanicalConnectionBlock)p).IsAttached || p.TopGrid == null);
            Check(detached == 0, detached + " pistons came off " + when);
            var chain = Chain();
            Check(chain.Count == ExpectedGrids, "the stack is not one chain " + when + ": " + chain.Count + " of " + ExpectedGrids + " grids");
            var group = MyCubeGridGroups.Static.Mechanical.GetGroup(_grids[0]);
            Check(group != null && group.Nodes.Count == ExpectedGrids,
                "the stack is not one mechanical group " + when + ": " + (group?.Nodes.Count ?? 0) + " grids");
            var loose = _grids.Where(g => g.MarkedForClose == false).SelectMany(g => g.GetFatBlocks())
                .Count(b => b.SlimBlock != null && b.SlimBlock.IsDestroyed);
            Check(loose == 0, loose + " blocks destroyed " + when);
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
