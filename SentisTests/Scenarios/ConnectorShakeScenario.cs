using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Two dynamic structures locked together by connectors in space shake, a little or a lot, and one let go of its
    /// connector was thrown a kilometre and a half (reported from the server, 11.10.2026: "Бур" on "Опорник Бурмалда").
    /// A probe of a world copied from the server - it moves nothing: a player beside the pair (where nobody is, the
    /// physics is not stepped and nothing shows), then for <see cref="WatchSeconds"/> every frame: each grid of the group
    /// - its speed, its turning, how much both changed from the frame before - and each locked pair of connectors - how far
    /// apart, how fast against each other, whether the lock is a constraint or a weld. The frames where something jumped
    /// are written as they come, the rest as a summary.
    /// </summary>
    public sealed class ConnectorShakeScenario : TestScenario
    {
        public const string ScenarioName = "connector_shake";
        private const double WatchSeconds = 30;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static string GridName = "Бур";

        /// <summary>The second half of the watch with the grid's dampeners off (put back after).</summary>
        public static bool DampenersTest = false;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)WatchSeconds + 60;

        public override IEnumerator Run()
        {
            var grid = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g.DisplayName == GridName)
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            Check(grid != null, $"no grid '{GridName}'");
            var group = MyCubeGridGroups.Static.Physical.GetGroup(grid)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { grid };
            var logical = MyCubeGridGroups.Static.Logical.GetGroup(grid)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { grid };
            Note($"'{grid.DisplayName}': physical group {string.Join(", ", group.Select(g => $"'{g.DisplayName}' ({g.BlocksCount} blocks, {(g.IsStatic ? "static" : "dynamic")}, mass {g.Physics?.Mass ?? 0:0} kg)"))}; " +
                 $"logical {logical.Count} grids");

            var connectors = group.SelectMany(g => g.GetFatBlocks().OfType<MyShipConnector>()).ToList();
            foreach (var c in connectors)
                Note($"connector '{c.CustomName}' on '{c.CubeGrid.DisplayName}': connected {c.Connected}, other '{c.Other?.CubeGrid.DisplayName}', " +
                     $"welded {GetBool(c, "m_welded") ?? GetBool(c, "IsWelded")}, constraint {(Get(c, "m_constraint") != null ? "yes" : "no")}, strength {c.Strength:0.###}");

            var at = grid.PositionComp.GetPosition() + grid.PositionComp.WorldAABB.HalfExtents.Length() * Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(grid.WorldMatrix.Up)) * 1.5;
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (at, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            // do the grids of the group overlap, and does the physics let them push each other? Locked grids are one
            // physical group, and the game keeps their collisions off by the system group of the collision filter
            for (var i = 0; i < group.Count; i++)
            for (var j = i + 1; j < group.Count; j++)
            {
                var a = group[i].Physics?.RigidBody;
                var b = group[j].Physics?.RigidBody;
                if (a == null || b == null) continue;
                var pa = group[i].Physics.ClusterToWorld(a.Position);
                var ra = Quaternion.CreateFromRotationMatrix(a.GetRigidBodyMatrix());
                var pb = group[j].Physics.ClusterToWorld(b.Position);
                var rb = Quaternion.CreateFromRotationMatrix(b.GetRigidBodyMatrix());
                var overlap = Sandbox.Engine.Physics.MyPhysics.IsPenetratingShapeShape(a.GetShape(), ref pa, ref ra, b.GetShape(), ref pb, ref rb);
                var world = group[i].Physics.HavokWorld;
                var collide = world != null && world.IsCollisionEnabled(a.GetCollisionFilterInfo(), b.GetCollisionFilterInfo());
                Note($"'{group[i].DisplayName}' and '{group[j].DisplayName}': shapes overlap {overlap}; collisions between them {(collide ? "ON" : "off")}; " +
                     $"layers {a.Layer}/{b.Layer}, system groups {Havok.HkGroupFilter.GetSystemGroupFromFilterInfo(a.GetCollisionFilterInfo())}/{Havok.HkGroupFilter.GetSystemGroupFromFilterInfo(b.GetCollisionFilterInfo())}; " +
                     $"same world {group[i].Physics.HavokWorld == group[j].Physics.HavokWorld}");
            }

            // what the drill touches: every contact point of its body, by the entity on the other side
            var contacts = new Dictionary<string, (int Count, double Speed)>();
            var body = grid.Physics?.RigidBody;
            var wasEnabled = body != null && body.ContactPointCallbackEnabled;
            Havok.HkContactPointEventHandler handler = (ref Havok.HkContactPointEvent e) =>
            {
                var other = Sandbox.Engine.Physics.MyPhysicsExtensions.GetOtherEntity(e, grid);
                var name = other == null ? "?" : other is MyCubeGrid g ? "grid '" + g.DisplayName + "'" : other.GetType().Name;
                lock (contacts)
                {
                    contacts.TryGetValue(name, out var was);
                    contacts[name] = (was.Count + 1, Math.Max(was.Speed, Math.Abs(e.SeparatingVelocity)));
                }
            };
            if (body != null)
            {
                body.ContactPointCallbackEnabled = true;
                body.ContactPointCallback += handler;
            }

            // what pushes the grids by themselves: thrusters with the dampeners, gyroscopes
            var thrustMax = new Dictionary<MyCubeGrid, double>();
            var gyroMax = new Dictionary<MyCubeGrid, double>();
            foreach (var g in group)
            {
                var thrust = g.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>();
                var gyro = g.GridSystems.GyroSystem;
                Note($"'{g.DisplayName}': thrusters {g.GetFatBlocks().OfType<Sandbox.Game.Entities.MyThrust>().Count()} (dampeners {thrust?.DampenersEnabled}, enabled {thrust?.Enabled}), " +
                     $"gyroscopes {gyro?.GyroCount}, wheels {g.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyMotorSuspension>().Count()}, " +
                     $"landing gears {g.GetFatBlocks().OfType<SpaceEngineers.Game.Entities.Blocks.MyLandingGear>().Count()}, drills {g.GetFatBlocks().OfType<Sandbox.Game.Weapons.MyShipDrill>().Count()}, " +
                     $"active {g.Physics?.IsActive}, frozen by SO {RuntimePluginControls.IsGridFrozen(g.EntityId)}");
            }

            var lastV = new Dictionary<MyCubeGrid, Vector3>();
            var lastW = new Dictionary<MyCubeGrid, Vector3>();
            var maxDv = new Dictionary<MyCubeGrid, double>();
            var maxDw = new Dictionary<MyCubeGrid, double>();
            var maxV = new Dictionary<MyCubeGrid, double>();
            var sumDv = new Dictionary<MyCubeGrid, double>();
            var pairs = connectors.Where(c => c.Connected && c.Other != null && c.EntityId < c.Other.EntityId).ToList();
            var maxGap = pairs.ToDictionary(p => p, p => 0.0);
            var maxRel = pairs.ToDictionary(p => p, p => 0.0);
            var frames = 0;
            var started = DateTime.UtcNow;
            var jumps = 0;
            var drillThrust = grid.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>();
            var dampenersWere = drillThrust?.DampenersEnabled ?? false;
            var halfV = 0.0; var halfDv = 0.0; var halfFrames = 0; var secondHalf = false;
            while ((DateTime.UtcNow - started).TotalSeconds < WatchSeconds)
            {
                var seconds = (DateTime.UtcNow - started).TotalSeconds;
                if (!secondHalf && seconds >= WatchSeconds / 2 && DampenersTest && drillThrust != null)
                {
                    secondHalf = true;
                    Note($"SHAKE | first half: '{grid.DisplayName}' speed up to {halfV:0.0000} m/s, change a frame on average {(halfFrames == 0 ? 0 : halfDv / halfFrames):0.00000}; now its dampeners off");
                    drillThrust.DampenersEnabled = false;
                    halfV = 0; halfDv = 0; halfFrames = 0;
                }
                if (grid.Physics != null && lastV.TryGetValue(grid, out var hv))
                {
                    halfV = Math.Max(halfV, grid.Physics.LinearVelocity.Length());
                    halfDv += (grid.Physics.LinearVelocity - hv).Length();
                    halfFrames++;
                }
                frames++;
                foreach (var g in group)
                {
                    var p = g.Physics;
                    if (p == null) continue;
                    var tc = g.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>();
                    if (tc != null) thrustMax[g] = Math.Max(thrustMax.TryGetValue(g, out var t0) ? t0 : 0, tc.FinalThrust.Length());
                    var gs = g.GridSystems.GyroSystem;
                    if (gs != null) gyroMax[g] = Math.Max(gyroMax.TryGetValue(g, out var y0) ? y0 : 0, gs.Torque.Length());
                    var v = p.LinearVelocity;
                    var w = p.AngularVelocity;
                    if (lastV.TryGetValue(g, out var v0))
                    {
                        var dv = (v - v0).Length();
                        var dw = (w - lastW[g]).Length();
                        maxDv[g] = Math.Max(maxDv.TryGetValue(g, out var a) ? a : 0, dv);
                        maxDw[g] = Math.Max(maxDw.TryGetValue(g, out var b) ? b : 0, dw);
                        sumDv[g] = (sumDv.TryGetValue(g, out var s) ? s : 0) + dv;
                        if ((dv > 0.5 || dw > 0.5) && jumps++ < 40)
                            Note($"{seconds:0.00} s: JUMP '{g.DisplayName}': speed {v0.Length():0.00} -> {v.Length():0.00} m/s (by {dv:0.00}), turning by {dw:0.00} rad/s, active {p.IsActive}");
                    }
                    maxV[g] = Math.Max(maxV.TryGetValue(g, out var m) ? m : 0, v.Length());
                    lastV[g] = v;
                    lastW[g] = w;
                }
                foreach (var c in pairs)
                {
                    if (c.Other == null) continue;
                    var gap = Vector3D.Distance(c.PositionComp.GetPosition(), c.Other.PositionComp.GetPosition());
                    var rel = ((c.CubeGrid.Physics?.GetVelocityAtPoint(c.PositionComp.GetPosition()) ?? Vector3.Zero) -
                               (c.Other.CubeGrid.Physics?.GetVelocityAtPoint(c.Other.PositionComp.GetPosition()) ?? Vector3.Zero)).Length();
                    maxGap[c] = Math.Max(maxGap[c], gap);
                    maxRel[c] = Math.Max(maxRel[c], rel);
                }
                yield return null;
            }

            if (secondHalf)
            {
                Note($"SHAKE | second half, the dampeners off: '{grid.DisplayName}' speed up to {halfV:0.0000} m/s, change a frame on average {(halfFrames == 0 ? 0 : halfDv / halfFrames):0.00000}");
                drillThrust.DampenersEnabled = dampenersWere;
            }
            if (body != null)
            {
                body.ContactPointCallback -= handler;
                body.ContactPointCallbackEnabled = wasEnabled;
            }
            lock (contacts)
                foreach (var c in contacts.OrderByDescending(x => x.Value.Count))
                    Note($"SHAKE | '{grid.DisplayName}' touched {c.Key}: {c.Value.Count} contact points, separating at up to {c.Value.Speed:0.000} m/s");
            foreach (var g in group)
                Note($"SHAKE | '{g.DisplayName}': thrust up to {(thrustMax.TryGetValue(g, out var tm) ? tm : 0):0} N, gyroscope torque up to {(gyroMax.TryGetValue(g, out var gm) ? gm : 0):0} N·m");
            foreach (var g in group)
                Note($"SHAKE | '{g.DisplayName}' ({(g.IsStatic ? "static" : "dynamic")}): speed up to {(maxV.TryGetValue(g, out var a) ? a : 0):0.000} m/s; change of speed a frame up to " +
                     $"{(maxDv.TryGetValue(g, out var b) ? b : 0):0.000} m/s, on average {(sumDv.TryGetValue(g, out var s) ? s / frames : 0):0.0000} m/s; change of turning up to {(maxDw.TryGetValue(g, out var c) ? c : 0):0.000} rad/s");
            foreach (var c in pairs)
                Note($"SHAKE | connectors '{c.CustomName}' on '{c.CubeGrid.DisplayName}' and '{c.Other?.CustomName}' on '{c.Other?.CubeGrid.DisplayName}': up to {maxGap[c]:0.000} m apart, " +
                     $"up to {maxRel[c]:0.000} m/s against each other");
            Note($"SHAKE | {frames} frames, {jumps} jumps over 0.5 a frame");
        }

        private static object Get(object o, string name) => o.GetType().GetField(name, Any)?.GetValue(o) ?? o.GetType().GetProperty(name, Any)?.GetValue(o);
        private static bool? GetBool(object o, string name) => Get(o, name) as bool?;

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
