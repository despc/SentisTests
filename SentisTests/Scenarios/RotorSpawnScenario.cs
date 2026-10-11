using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A player places a rotor on its own, as a new grid (the build menu with nothing under the cross: MyCubeBuilder
    /// .RequestGridSpawn from the client) - the frame of the stator, and the head the game makes for it once the grid is
    /// made (AfterGridBuild, OnBuildSuccess). Reported from a server: such a head of an advanced rotor is not held - it
    /// falls on its side, jerks, throws a player standing by, once blew up - not without SentisOptimisations.
    ///
    /// Advanced rotors and a plain one, each as a grid of its own: on the ground (the game makes it static: it touches
    /// the voxels) and in the air (a dynamic grid; it falls and lands). Every frame for <see cref="WatchSeconds"/>: the
    /// head there and attached, the hinge in the world, how far the hinge's two pivots are apart, the head's axis against
    /// the stator's and how fast it turns against it.
    /// </summary>
    public sealed class RotorSpawnScenario : TestScenario
    {
        public const string ScenarioName = "rotor_spawn";
        private const double WatchSeconds = 12;
        private const double MaxTiltDegrees = 3, MaxPivotGapM = 0.1, MaxTurnRadS = 1;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly (string Subtype, bool OnGround)[] Cases =
        {
            ("LargeAdvancedStator", true), ("LargeAdvancedStator", true), ("LargeStator", true),
            ("LargeAdvancedStator", false), ("LargeAdvancedStator", false), ("LargeStator", false),
        };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        private sealed class Watched
        {
            public string Label;
            public MyMechanicalConnectionBlockBase Stator;
            public double Tilt, Gap, Turn, FirstTilt = -1;
            public int NoHead, Detached, NoHinge, Frames;
            public readonly List<string> Seen = new List<string>();
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().OrderByDescending(x => x.MaximumRadius).FirstOrDefault();
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var direction = Vector3D.Normalize(new Vector3D(0.31, 0.83, -0.47));
            var probe = centre + direction * planet.MaximumRadius;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var forward = Vector3D.Cross(up, side);

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (ground + up * 3, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var player = FakeClients.PlayerOf(0);
            var character = player?.Character;
            Check(character != null, "the player has no character");

            // a world with research (the server's): the player has what a player who builds rotors has researched
            Sandbox.Game.SessionComponents.MySessionComponentResearch.Static?.DebugUnlockAllResearch(player.Identity.IdentityId);
            var builder = typeof(MyCubeBuilder);
            var request = builder.GetMethod("RequestGridSpawn", Any);
            var dataType = builder.GetNestedType("GridSpawnRequestData", BindingFlags.NonPublic);
            var authorType = builder.GetNestedType("Author", BindingFlags.NonPublic);
            var buildDataType = builder.GetNestedType("BuildData", BindingFlags.NonPublic);
            Check(request != null && dataType != null && authorType != null && buildDataType != null, "MyCubeBuilder.RequestGridSpawn and its data not found");

            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            for (var i = 0; i < Cases.Length; i++)
            {
                var (subtype, onGround) = Cases[i];
                var definition = MyDefinitionManager.Static.GetDefinitionsOfType<MyCubeBlockDefinition>().First(d => d.Id.SubtypeName == subtype && d.CubeSize == MyCubeSize.Large);
                // in a row along the ground, 6 m apart; in the air 8 m up
                var spot = ground + side * (i * 6 - 15) + forward * 8;
                var hereProbe = spot;
                spot = planet.GetClosestSurfacePointGlobal(ref hereProbe) + up * (onGround ? 1.0 : 8);
                foreach (var component in definition.Components)
                    character.GetInventory().AddItems(component.Count, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(component.Definition.Id));
                var author = Activator.CreateInstance(authorType, character.EntityId, player.Identity.IdentityId);
                var position = Activator.CreateInstance(buildDataType);
                buildDataType.GetField("Position").SetValue(position, spot);
                buildDataType.GetField("Forward").SetValue(position, (Vector3)forward);
                buildDataType.GetField("Up").SetValue(position, (Vector3)up);
                buildDataType.GetField("AbsolutePosition").SetValue(position, true);
                var visuals = new MyCubeGrid.MyBlockVisuals(new Vector3(0, -1, 0).PackHSVToUint(), MyStringHash.NullOrEmpty);
                var data = Activator.CreateInstance(dataType, author, (DefinitionIdBlit)definition.Id, position, false, false, visuals);
                FakeClients.AsClient(0, () => request.Invoke(null, new[] { data }));
                yield return WaitForTicks(20);
            }
            yield return WaitForTicks(20);

            var stators = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !before.Contains(g.EntityId) && !g.MarkedForClose)
                .SelectMany(g => g.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>()).ToList();
            foreach (var g in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !before.Contains(g.EntityId))) Track(g);
            Check(stators.Count == Cases.Length, $"{stators.Count} rotors made of {Cases.Length}");

            var constraint = typeof(MyMechanicalConnectionBlockBase).GetProperty("Constraint", Any);
            var watched = stators.Select((s, i) => new Watched
            {
                Label = $"{s.BlockDefinition.Id.SubtypeName} #{i + 1} ({(s.CubeGrid.IsStatic ? "static" : "dynamic")}, built {s.SlimBlock.BuildLevelRatio:0.00})",
                Stator = s,
            }).ToList();
            var started = DateTime.UtcNow;
            var nextNote = 0.0;
            while ((DateTime.UtcNow - started).TotalSeconds < WatchSeconds)
            {
                var seconds = (DateTime.UtcNow - started).TotalSeconds;
                foreach (var w in watched)
                {
                    w.Frames++;
                    var top = w.Stator.TopBlock;
                    var hk = constraint.GetValue(w.Stator) as HkConstraint;
                    var attached = ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)w.Stator).IsAttached;
                    string state;
                    if (top == null) { w.NoHead++; state = "no head"; }
                    else
                    {
                        if (!attached) w.Detached++;
                        if (hk == null || !hk.InWorld) w.NoHinge++;
                        var tilt = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(top.WorldMatrix.Up, w.Stator.WorldMatrix.Up), -1, 1)));
                        // turning across the axis only: about it the head of a rotor that is not working turns freely
                        var relative = (Vector3D)((top.CubeGrid.Physics?.AngularVelocity ?? Vector3.Zero) - (w.Stator.CubeGrid.Physics?.AngularVelocity ?? Vector3.Zero));
                        var axis = w.Stator.WorldMatrix.Up;
                        var turn = (relative - axis * Vector3D.Dot(relative, axis)).Length();
                        var gap = 0.0;
                        if (hk != null && hk.InWorld)
                        {
                            hk.GetPivotsInWorld(out var a, out var b);
                            gap = (a - b).Length();
                        }
                        if (w.FirstTilt < 0) w.FirstTilt = tilt;
                        w.Tilt = Math.Max(w.Tilt, tilt);
                        w.Turn = Math.Max(w.Turn, turn);
                        w.Gap = Math.Max(w.Gap, gap);
                        state = $"{(attached ? "attached" : "DETACHED")}, hinge {(hk == null ? "NONE" : hk.InWorld ? "in world" : "OUT")}, tilted {tilt:0.0}°, pivots {gap:0.000} m apart, " +
                                $"turning across the axis {turn:0.00} rad/s, stator grid {w.Stator.CubeGrid.Physics?.LinearVelocity.Length() ?? 0:0.0} m/s";
                    }
                    var head = state.Split(',')[0];
                    if (w.Seen.Count == 0 || w.Seen[w.Seen.Count - 1] != head) w.Seen.Add($"{head}@{seconds:0.0}s");
                    if (seconds >= nextNote) Note($"{seconds:0} s: {w.Label}: {state}");
                }
                if (seconds >= nextNote) nextNote += 3;
                yield return null;
            }

            var failures = new List<string>();
            foreach (var w in watched)
            {
                Note($"ROTOR SPAWN | {w.Label}: frames {w.Frames}, without a head {w.NoHead}, detached {w.Detached}, without the hinge {w.NoHinge}; tilted {w.FirstTilt:0.0}° at first, " +
                     $"up to {w.Tilt:0.0}°; pivots up to {w.Gap:0.000} m apart; turning up to {w.Turn:0.00} rad/s; {string.Join(" > ", w.Seen)}");
                if (w.NoHead > 0 || w.Detached > 0 || w.NoHinge > 0 || w.Tilt > MaxTiltDegrees || w.Gap > MaxPivotGapM || w.Turn > MaxTurnRadS)
                    failures.Add($"{w.Label}: no head {w.NoHead}, detached {w.Detached}, no hinge {w.NoHinge} frames, tilted {w.Tilt:0.0}°, pivots {w.Gap:0.00} m, turning {w.Turn:0.0} rad/s");
                if (w.Stator.TopBlock?.CubeGrid is MyCubeGrid h) Track(h);
            }
            Check(failures.Count == 0, "heads not held: " + string.Join("; ", failures));
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
