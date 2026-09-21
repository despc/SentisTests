using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What the pistons already standing in the world cost the server.
    ///
    /// Nothing is spawned: the operator builds the piston rigs in the world, and the bench takes
    /// them as they are - every piston of every grid, whatever state it was left in. With the
    /// freezer off, so every rig runs as it would with a player next to it, it counts what the
    /// pistons are doing (moving, resting at a limit, standing still in between) and how many of
    /// their grids Havok keeps awake, then measures the frames for <see cref="MeasureSeconds"/>.
    ///
    /// The server's physics guard turns grids that load physics heavily into stations, and a
    /// stack of pistons is exactly that: a rig found converted is turned back into what was built -
    /// every grid a piston carries is dynamic again, only the base the first piston stands on stays
    /// fixed to the voxels - and the guard is off while the bench runs, so it does not happen again
    /// mid-measurement.
    ///
    /// Next every stack is knocked sideways, the tip hardest - enough to set the stack swaying,
    /// not enough to tear it apart - and the bench waits for each stack to calm down: how long that
    /// takes, and what the swaying costs meanwhile.
    ///
    /// Then every piston is sent the other way for <see cref="MoveSeconds"/>, slowly enough that
    /// none of them reaches a limit, and the frames are measured again: a piston at rest and a
    /// piston on the move take very different paths through the game. At the end each piston is
    /// given its own velocity back and travels home by itself.
    /// </summary>
    public sealed class PistonPerfScenario : TestScenario
    {
        public const string ScenarioName = "piston_perf";
        private const double SettleSeconds = 10;
        private const double MeasureSeconds = 30;
        private const double MoveSeconds = 20;
        private const float KnockSpeed = 2.0f;
        private const double CalmSeconds = 60;
        private const float CalmSpeed = 0.05f;
        /// <summary>A swaying stack stops for a moment at each end of its swing: calm has to last.</summary>
        private const double CalmHoldSeconds = 2;

        private static readonly FieldInfo CurrentPos = typeof(MyPistonBase).GetField("m_currentPos",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            var pistons = Pistons();
            Check(pistons.Count > 0, "there are no pistons in the world to measure");

            // Measuring frozen rigs measures nothing: wait until the freezer has let every one go.
            var thawed = Wait(() => Frozen(pistons) == 0, "every piston rig thawed", 120);
            while (thawed.MoveNext()) yield return thawed.Current;
            var converted = MakeDynamic(pistons);
            Note("turned " + converted + " piston-carried grids back into dynamic ones");
            var settle = WaitForSeconds(SettleSeconds, "the rigs settle");
            while (settle.MoveNext()) yield return settle.Current;
            Note("before: " + Census(pistons));

            TickMetrics.Take();
            FrameProbe.Take();
            var measuring = WaitForSeconds(MeasureSeconds, "measuring");
            while (measuring.MoveNext()) yield return measuring.Current;
            var metrics = TickMetrics.Take();
            var work = FrameProbe.Take();

            Note("PISTON RESULT RESTING | " + pistons.Count + " pistons: " + Census(Pistons()) + " | " +
                 metrics.Format() + " | " + work);

            // ------------------------------------------------------------- knocked
            var stacks = Stacks(pistons);
            // the stack's own sway: every carried grid pushed sideways in proportion to its height,
            // KnockSpeed at the tip - a knock on the tip alone is soaked up by the rest at once
            foreach (var stack in stacks)
            {
                var side = (Vector3)stack.TipPiston.WorldMatrix.Right;
                var n = stack.Grids.Count - 1;
                for (var i = 1; i <= n; i++)
                    stack.Grids[i].Physics.LinearVelocity += side * (KnockSpeed * i / n);
            }
            TickMetrics.Take();
            FrameProbe.Take();
            var knocked = DateTime.UtcNow;
            var calmAt = new double?[stacks.Count];
            var calmSince = new double?[stacks.Count];
            while ((DateTime.UtcNow - knocked).TotalSeconds < CalmSeconds + CalmHoldSeconds && calmAt.Any(t => t == null))
            {
                var now = (DateTime.UtcNow - knocked).TotalSeconds;
                for (var i = 0; i < stacks.Count; i++)
                {
                    if (calmAt[i] != null) continue;
                    var asleep = Asleep(stacks[i]);
                    if (!asleep && !Calm(stacks[i])) { calmSince[i] = null; continue; }
                    if (calmSince[i] == null) calmSince[i] = now;
                    if (asleep || now - calmSince[i].Value >= CalmHoldSeconds) calmAt[i] = calmSince[i];
                }
                yield return WaitForTicks(10);
            }
            metrics = TickMetrics.Take();
            work = FrameProbe.Take();
            var settled = calmAt.Where(t => t != null).Select(t => t.Value).OrderBy(t => t).ToList();
            Note("PISTON RESULT KNOCKED | " + stacks.Count + " stacks knocked at " + KnockSpeed + " m/s: " +
                 settled.Count + " calm within " + CalmSeconds.ToString("F0") + " s" +
                 (settled.Count > 0
                     ? ", median " + settled[settled.Count / 2].ToString("F1") + " s, slowest " + settled[settled.Count - 1].ToString("F1") + " s"
                     : "") +
                 " | " + Census(Pistons()) + " | " + metrics.Format() + " | " + work);

            // ------------------------------------------------------------- on the move
            _velocities.Clear();
            foreach (var p in pistons)
            {
                var piston = (Sandbox.ModAPI.IMyPistonBase)p;
                _velocities[p] = piston.Velocity;
                var pos = CurrentPos == null ? 0f : (float)CurrentPos.GetValue(p);
                var room = piston.Velocity > 0 ? pos - p.MinLimit : p.MaxLimit - pos;
                var speed = Math.Min(Math.Abs(piston.Velocity), (float)(room / (MoveSeconds + 5)));
                if (speed <= 0.001f) speed = Math.Abs(piston.Velocity);
                piston.Velocity = piston.Velocity > 0 ? -speed : speed;
            }
            yield return WaitForTicks(30);

            TickMetrics.Take();
            FrameProbe.Take();
            var moving = WaitForSeconds(MoveSeconds, "measuring the pistons on the move");
            while (moving.MoveNext()) yield return moving.Current;
            metrics = TickMetrics.Take();
            work = FrameProbe.Take();
            var census = Census(Pistons());
            RestoreVelocities();
            Check(Frozen(pistons) == 0, "piston rigs froze while being measured: " + census);

            Note("PISTON RESULT MOVING | " + pistons.Count + " pistons: " + census + " | " +
                 metrics.Format() + " | " + work);
        }

        private readonly Dictionary<MyPistonBase, float> _velocities = new Dictionary<MyPistonBase, float>();

        private void RestoreVelocities()
        {
            foreach (var pair in _velocities)
                if (!pair.Key.MarkedForClose)
                    ((Sandbox.ModAPI.IMyPistonBase)pair.Key).Velocity = pair.Value;
            _velocities.Clear();
        }

        private static List<MyPistonBase> Pistons() =>
            MyEntities.GetEntities().OfType<MyCubeGrid>()
                .Where(g => !g.MarkedForClose)
                .SelectMany(g => g.GetFatBlocks().OfType<MyPistonBase>())
                .ToList();

        /// <summary>
        /// Every grid a piston carries is made dynamic again; the grid the first piston of a stack
        /// stands on is not carried by any piston, and stays as it is.
        /// </summary>
        private static int MakeDynamic(List<MyPistonBase> pistons)
        {
            var converted = 0;
            foreach (var top in pistons.Select(p => p.TopGrid).Where(g => g != null).Distinct())
            {
                if (!top.IsStatic || top.MarkedForClose || top.Physics == null) continue;
                top.OnConvertToDynamic();
                Sandbox.Engine.Multiplayer.MyMultiplayer.RaiseEvent<MyCubeGrid>(top,
                    x => new Action(x.OnConvertToDynamic), default(VRage.Network.EndpointId));
                converted++;
            }
            return converted;
        }

        private sealed class Stack
        {
            public readonly List<MyCubeGrid> Grids = new List<MyCubeGrid>();   // base first, tip last
            public MyPistonBase TipPiston;
        }

        /// <summary>Each stack from the grid it stands on to the grid at its tip.</summary>
        private static List<Stack> Stacks(List<MyPistonBase> pistons)
        {
            var carried = new HashSet<MyCubeGrid>(pistons.Where(p => p.TopGrid != null).Select(p => p.TopGrid));
            var byGrid = pistons.Where(p => p.TopGrid != null).GroupBy(p => p.CubeGrid).ToDictionary(g => g.Key, g => g.First());
            var stacks = new List<Stack>();
            foreach (var root in byGrid.Keys.Where(g => !carried.Contains(g)))
            {
                var stack = new Stack();
                var grid = root;
                stack.Grids.Add(grid);
                MyPistonBase piston;
                while (byGrid.TryGetValue(grid, out piston) && !stack.Grids.Contains(piston.TopGrid))
                {
                    stack.TipPiston = piston;
                    grid = piston.TopGrid;
                    stack.Grids.Add(grid);
                }
                if (stack.TipPiston != null) stacks.Add(stack);
            }
            return stacks;
        }

        private static bool Asleep(Stack stack)
        {
            for (var i = 1; i < stack.Grids.Count; i++)
            {
                var physics = stack.Grids[i].Physics;
                if (physics?.RigidBody != null && physics.RigidBody.IsActive) return false;
            }
            return true;
        }

        /// <summary>Every carried grid asleep in Havok, or all but still.</summary>
        private static bool Calm(Stack stack)
        {
            for (var i = 1; i < stack.Grids.Count; i++)
            {
                var physics = stack.Grids[i].Physics;
                if (physics?.RigidBody == null || !physics.RigidBody.IsActive) continue;
                if (physics.LinearVelocity.Length() > CalmSpeed) return false;
            }
            return true;
        }

        private static int Frozen(List<MyPistonBase> pistons) =>
            pistons.Select(p => p.CubeGrid).Concat(pistons.Where(p => p.TopGrid != null).Select(p => p.TopGrid))
                .Distinct().Count(g => RuntimePluginControls.IsGridFrozen(g.EntityId));

        private static string Census(List<MyPistonBase> pistons)
        {
            int off = 0, detached = 0, atMin = 0, atMax = 0, still = 0, moving = 0;
            foreach (var p in pistons)
            {
                if (!p.IsWorking) { off++; continue; }
                if (p.TopGrid == null) { detached++; continue; }
                var pos = CurrentPos == null ? float.NaN : (float)CurrentPos.GetValue(p);
                float velocity = p.Velocity;
                if (velocity == 0f) { still++; continue; }
                if (velocity < 0 && pos <= p.MinLimit) atMin++;
                else if (velocity > 0 && pos >= p.MaxLimit) atMax++;
                else moving++;
            }

            // grids the pistons hold together, and how many of them Havok is simulating
            var grids = new HashSet<MyCubeGrid>();
            foreach (var p in pistons)
            {
                grids.Add(p.CubeGrid);
                if (p.TopGrid != null) grids.Add(p.TopGrid);
            }
            var awakeGrids = grids.Where(g => g.Physics?.RigidBody != null && g.Physics.RigidBody.IsActive).ToList();
            var speeds = awakeGrids.Select(g => (double)g.Physics.LinearVelocity.Length()).OrderBy(v => v).ToList();
            var spins = awakeGrids.Select(g => (double)g.Physics.AngularVelocity.Length()).OrderBy(v => v).ToList();
            string Stats(List<double> v) => v.Count == 0 ? "-" :
                "median " + v[v.Count / 2].ToString("0.#####") + ", max " + v[v.Count - 1].ToString("0.#####");

            return "moving " + moving + ", resting at min " + atMin + ", at max " + atMax +
                   ", velocity 0 " + still + ", no top " + detached + ", off " + off +
                   " | grids " + grids.Count + ", frozen " + Frozen(pistons) +
                   ", static carried " + pistons.Where(p => p.TopGrid != null).Select(p => p.TopGrid).Distinct().Count(g => g.IsStatic) +
                   ", awake in Havok " + awakeGrids.Count +
                   " (static " + awakeGrids.Count(g => g.IsStatic) + "), speed m/s " + Stats(speeds) +
                   ", spin rad/s " + Stats(spins);
        }

        public override void Cleanup()
        {
            try
            {
                RestoreVelocities();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
