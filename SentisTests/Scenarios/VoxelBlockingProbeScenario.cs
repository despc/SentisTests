using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Havok;
using Sandbox;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using SentisTests.Core;
using SentisTests.Game;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.Voxels;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A probe, not a check: who makes Havok ask for a voxel collision mesh in the middle of its step.
    ///
    /// The game makes the meshes of voxel cells ahead, on workers, for what it sees coming: grids and floating objects
    /// near the voxel map, over the box they are predicted to be in (MyVoxelPhysicsBody.UpdateAfterSimulation10). A
    /// cell Havok needs and has no mesh for it asks for at once (RequestShapeBatchBlockingInternal), and the step waits
    /// while the mesh is made: frames of 12-17 ms led by physics on a world with no player (a test world, 06.10.2026).
    ///
    /// For <see cref="ListenSeconds"/> with the freezer off (those frames are not there with it on) every such request
    /// is written down: the voxel map, the cells and where they are, how long it took, the managed stack it came by,
    /// and what moves near the cells - with whether the voxel body had it among the entities it makes meshes ahead for.
    /// The first run showed them to come from the rays blocks queue (MyPhysics.CastRayParallel: solar panels, wind
    /// turbines, turrets...), cast in the physics step: every queued ray is noted with who queued it, and a request is
    /// laid to the rays that cross its cells. The first <see cref="SettleSeconds"/> after the freezer goes off (every
    /// grid of the world wakes at once) are counted apart.
    /// </summary>
    public sealed class VoxelBlockingProbeScenario : TestScenario
    {
        public const string ScenarioName = "voxel_blocking_probe";
        private const int ListenSeconds = 300;
        private const double NearM = 40;
        private const int MaxWritten = 40;
        private const int SettleSeconds = 30;
        private const double RayMemorySeconds = 3;

        private struct Ray
        {
            public long At;
            public Vector3D From, To;
            public string By;
        }

        // a queued query of the physics step (a ray, a chain of rays, a shape's penetrations) that took long by itself
        private const double SlowQueryMs = 1;
        private static readonly ConcurrentQueue<(DateTime At, double Ms, string What)> SlowQueries = new ConcurrentQueue<(DateTime, double, string)>();
        [ThreadStatic] private static long _queryStarted;
        // voxel meshes Havok asked for inside the query this thread is running: their time and cells
        [ThreadStatic] private static double _meshMsInQuery;
        [ThreadStatic] private static int _meshCellsInQuery;
        private static int _queriesRunning, _queriesInStep;

        private static readonly ConcurrentQueue<Ray> Rays = new ConcurrentQueue<Ray>();
        private static readonly ConcurrentDictionary<string, int> RaysBy = new ConcurrentDictionary<string, int>();

        private sealed class Request
        {
            public DateTime At;
            public MyVoxelPhysicsBody Body;
            public Vector3I[] Cells;
            public bool Lod1, GameThread;
            public double Ms;
            public string Stack;
        }

        private static readonly ConcurrentQueue<Request> Requests = new ConcurrentQueue<Request>();
        private static volatile bool _listening;
        private static bool _probeInstalled;
        [ThreadStatic] private static Request _current;
        [ThreadStatic] private static long _started;

        private static readonly FieldInfo NearbyField = typeof(MyVoxelPhysicsBody).GetField("m_nearbyEntities", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => ListenSeconds + 120;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var torch = SentisTestsPlugin.TorchInstance;
            Check(torch != null, "Torch instance is not available to the test plugin");
            Check(NearbyField != null, "MyVoxelPhysicsBody.m_nearbyEntities not found");
            InstallProbe((PatchManager)torch.Managers.GetManager(typeof(PatchManager)));
            _optimisations.Set("FreezerEnabled", false);
            while (Requests.TryDequeue(out _)) { }
            while (Rays.TryDequeue(out _)) { }
            RaysBy.Clear();
            _listening = true;
            var recent = new List<Ray>();
            var byRay = new Dictionary<string, (int Count, double Ms)>();
            var frames = new Dictionary<long, (double Ms, int Count, string By)>();
            var settling = 0;
            var settlingMs = 0.0;
            while (SlowQueries.TryDequeue(out _)) { }
            var slow = new Dictionary<string, (int Count, double Ms, double Worst)>();
            var slowWritten = 0;

            var watch = Stopwatch.StartNew();
            var written = 0;
            var total = 0;
            var totalMs = 0.0;
            var byStack = new Dictionary<string, (int Count, double Ms)>();
            var byMover = new Dictionary<string, (int Count, double Ms)>();
            var near = new List<MyEntity>();
            while (watch.Elapsed.TotalSeconds < ListenSeconds)
            {
                while (SlowQueries.TryDequeue(out var query))
                {
                    if (watch.Elapsed.TotalSeconds < SettleSeconds) continue;
                    var key = query.What.Split('|')[0].Trim();
                    slow[key] = slow.TryGetValue(key, out var sq) ? (sq.Count + 1, sq.Ms + query.Ms, Math.Max(sq.Worst, query.Ms)) : (1, query.Ms, query.Ms);
                    if (query.Ms >= 5 && slowWritten++ < 30)
                        Note("SLOW PHYSICS QUERY | " + query.At.ToLocalTime().ToString("HH:mm:ss.fff") + " " + query.Ms.ToString("F1") + " ms " + query.What);
                }
                while (Rays.TryDequeue(out var ray)) recent.Add(ray);
                var oldest = Stopwatch.GetTimestamp() - (long)(RayMemorySeconds * Stopwatch.Frequency);
                recent.RemoveAll(r => r.At < oldest);
                while (Requests.TryDequeue(out var request))
                {
                    if (watch.Elapsed.TotalSeconds < SettleSeconds)
                    {
                        settling++;
                        settlingMs += request.Ms;
                        continue;
                    }
                    total++;
                    totalMs += request.Ms;
                    var voxel = request.Body.m_voxelMap;
                    var cell = new MyCellCoord(request.Lod1 ? 1 : 0, request.Cells.Length > 0 ? request.Cells[0] : Vector3I.Zero);
                    MyVoxelCoordSystems.GeometryCellCoordToWorldAABB(voxel.PositionLeftBottomCorner, ref cell, out var box);
                    foreach (var other in request.Cells.Skip(1))
                    {
                        var c = new MyCellCoord(cell.Lod, other);
                        MyVoxelCoordSystems.GeometryCellCoordToWorldAABB(voxel.PositionLeftBottomCorner, ref c, out var b);
                        box.Include(ref b);
                    }
                    var ahead = NearbyField.GetValue(request.Body) as List<MyEntity>;
                    var query = box;
                    query.Inflate(NearM);
                    near.Clear();
                    MyGamePruningStructure.GetTopMostEntitiesInBox(ref query, near);
                    var movers = near.Where(e => !(e is MyVoxelBase) && e.Physics != null)
                        .Select(e => (Entity: e, Distance: box.Distance(e.PositionComp.GetPosition())))
                        .OrderBy(e => e.Distance).Take(6).ToList();
                    var described = movers.Select(m => Describe(m.Entity, m.Distance, ahead)).ToList();
                    var mover = movers.Count == 0 ? "nothing with a body within " + NearM + " m" : Kind(movers[0].Entity, ahead);
                    byMover[mover] = byMover.TryGetValue(mover, out var bm) ? (bm.Count + 1, bm.Ms + request.Ms) : (1, request.Ms);
                    byStack[request.Stack] = byStack.TryGetValue(request.Stack, out var bs) ? (bs.Count + 1, bs.Ms + request.Ms) : (1, request.Ms);
                    // whose ray crosses these cells
                    var crossed = box;
                    crossed.Inflate(2);
                    var owners = new HashSet<string>();
                    foreach (var r in recent)
                    {
                        var along = r.To - r.From;
                        var length = along.Length();
                        if (length < 0.01) continue;
                        var hit = crossed.Intersects(new RayD(r.From, along / length));
                        if (crossed.Contains(r.From) != ContainmentType.Disjoint || (hit.HasValue && hit.Value <= length)) owners.Add(r.By);
                    }
                    var rayBy = owners.Count == 0 ? "no ray noted" : string.Join(" + ", owners.OrderBy(o => o));
                    byRay[rayBy] = byRay.TryGetValue(rayBy, out var br) ? (br.Count + 1, br.Ms + request.Ms) : (1, request.Ms);
                    var frame = request.At.Ticks / (TimeSpan.TicksPerMillisecond * 17);
                    frames[frame] = frames.TryGetValue(frame, out var fr) ? (fr.Ms + request.Ms, fr.Count + 1, fr.By.Contains(rayBy) ? fr.By : fr.By + ", " + rayBy) : (request.Ms, 1, rayBy);
                    if (written++ < MaxWritten)
                        Note("VOXEL BLOCKING | " + request.At.ToLocalTime().ToString("HH:mm:ss.fff") + " " + request.Ms.ToString("F1") + " ms, " + request.Cells.Length +
                             " cell(s) of " + voxel.StorageName + (request.Lod1 ? " lod1" : "") + " at " + Round(box.Center) +
                             (request.GameThread ? ", game thread" : ", a worker") + ", the body makes ahead for " + (ahead?.Count ?? 0) + " entities | near: " +
                             (described.Count == 0 ? "nothing" : string.Join("; ", described.Take(3))) + " | ray of: " + rayBy);
                }
                yield return null;
            }
            _listening = false;

            Note("VOXEL BLOCKING RESULT | " + total + " requests in " + (ListenSeconds - SettleSeconds) + " s, " + totalMs.ToString("F0") + " ms in all (and " + settling + ", " +
                 settlingMs.ToString("F0") + " ms, in the first " + SettleSeconds + " s after the freezer went off) | whose ray crossed the cells: " +
                 string.Join("; ", byRay.OrderByDescending(p => p.Value.Ms).Take(8).Select(p => p.Key + " x" + p.Value.Count + " " + p.Value.Ms.ToString("F0") + " ms")) +
                 " | the worst frames (the requests' time summed over the threads): " +
                 string.Join("; ", frames.Values.OrderByDescending(f => f.Ms).Take(8).Select(f => f.Ms.ToString("F0") + " ms in " + f.Count + " (" + f.By + ")")) +
                 " | queued queries over " + SlowQueryMs + " ms each: " +
                 (slow.Count == 0 ? "none" : string.Join("; ", slow.OrderByDescending(p => p.Value.Ms).Take(8).Select(p => p.Key + " x" + p.Value.Count + " " + p.Value.Ms.ToString("F0") + " ms, worst " + p.Value.Worst.ToString("F1")))) +
                 " | rays queued, per second: " + string.Join(", ", RaysBy.OrderByDescending(p => p.Value).Take(10).Select(p => p.Key + " " + (p.Value / (double)ListenSeconds).ToString("F1"))) +
                 " | nearest thing with a body: " +
                 string.Join("; ", byMover.OrderByDescending(p => p.Value.Ms).Select(p => p.Key + " x" + p.Value.Count + " " + p.Value.Ms.ToString("F0") + " ms")) +
                 " | came by: " + string.Join(" || ", byStack.OrderByDescending(p => p.Value.Ms).Take(5).Select(p => "x" + p.Value.Count + " " + p.Value.Ms.ToString("F0") + " ms: " + p.Key)));
        }

        private static string Kind(MyEntity entity, List<MyEntity> ahead)
        {
            var body = entity.Physics?.RigidBody;
            var what = entity is MyCubeGrid grid ? (grid.IsStatic ? "static grid" : "grid")
                : entity is MyCharacter character ? (character.IsBot ? "bot character" : "character")
                : entity.GetType().Name;
            return what + (body == null ? " (no rigid body)" : " layer " + body.Layer + (body.IsFixedOrKeyframed ? " fixed" : "")) +
                   (ahead != null && ahead.Contains(entity) ? ", made ahead for" : ", NOT made ahead for");
        }

        private static string Describe(MyEntity entity, double distance, List<MyEntity> ahead)
        {
            var speed = entity.Physics?.LinearVelocity.Length() ?? 0;
            return Kind(entity, ahead) + " \"" + (entity.DisplayName ?? entity.Name ?? "?") + "\" " + distance.ToString("F0") + " m off, " + speed.ToString("F1") + " m/s";
        }

        private static string Round(Vector3D v) => "(" + v.X.ToString("F0") + ", " + v.Y.ToString("F0") + ", " + v.Z.ToString("F0") + ")";

        private static void InstallProbe(PatchManager patchManager)
        {
            if (_probeInstalled) return;
            var ctx = patchManager.AcquireContext();
            var target = typeof(MyVoxelPhysicsBody).GetMethod("RequestShapeBatchBlockingInternal", BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new MissingMethodException("MyVoxelPhysicsBody", "RequestShapeBatchBlockingInternal");
            ctx.GetPattern(target).Prefixes.Add(typeof(VoxelBlockingProbeScenario).GetMethod(nameof(RequestPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(target).Suffixes.Add(typeof(VoxelBlockingProbeScenario).GetMethod(nameof(RequestSuffix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var cast in typeof(Sandbox.Engine.Physics.MyPhysics).GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name == "CastRayParallel"))
                ctx.GetPattern(cast).Prefixes.Add(typeof(VoxelBlockingProbeScenario).GetMethod(nameof(RayPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var step = typeof(Sandbox.Engine.Physics.MyPhysics).GetMethod("ExecuteParallelRayCasts", BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new MissingMethodException("MyPhysics", "ExecuteParallelRayCasts");
            ctx.GetPattern(step).Prefixes.Add(typeof(VoxelBlockingProbeScenario).GetMethod(nameof(StepPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var query = typeof(Sandbox.Engine.Physics.MyPhysics).GetNestedType("ParallelRayCastQuery", BindingFlags.NonPublic)?.GetMethod("ExecuteRayCast", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? throw new MissingMethodException("MyPhysics.ParallelRayCastQuery", "ExecuteRayCast");
            ctx.GetPattern(query).Prefixes.Add(typeof(VoxelBlockingProbeScenario).GetMethod(nameof(QueryPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(query).Suffixes.Add(typeof(VoxelBlockingProbeScenario).GetMethod(nameof(QuerySuffix), BindingFlags.Static | BindingFlags.NonPublic));
            patchManager.Commit();
            _probeInstalled = true;
        }

        // Havok calls this from its step, on the game thread or one of its workers: only what is safe anywhere is read here
        private static void RequestPrefix(MyVoxelPhysicsBody __instance, HkShapeBatch info, bool lod1physics)
        {
            _current = null;
            if (!_listening) return;
            try
            {
                var count = info.Count;
                var cells = new Vector3I[Math.Min(count, 64)];
                for (var i = 0; i < cells.Length; i++) info.GetInfo(i, out cells[i]);
                var frames = new StackTrace(1, false).GetFrames() ?? new StackFrame[0];
                var stack = string.Join(" < ", frames.Select(f => f.GetMethod()).Where(m => m != null)
                    .Select(m => (m.DeclaringType?.Name ?? "?") + "." + m.Name)
                    .Where(n => !n.StartsWith("VoxelBlockingProbeScenario") && !n.Contains("LoadSampler") && !n.StartsWith("ExecutionContext") && !n.StartsWith("ThreadHelper"))
                    .Take(14));
                _current = new Request
                {
                    At = DateTime.UtcNow, Body = __instance, Cells = cells, Lod1 = lod1physics,
                    GameThread = MySandboxGame.Static?.UpdateThread == Thread.CurrentThread, Stack = stack,
                };
                _started = Stopwatch.GetTimestamp();
            }
            catch
            {
                _current = null;
            }
        }

        // a ray queued for the physics step: who queued it (the first frame over MyPhysics)
        private static void RayPrefix(ref Vector3D from, ref Vector3D to)
        {
            if (!_listening) return;
            try
            {
                var by = "?";
                var frames = new StackTrace(1, false).GetFrames();
                if (frames != null)
                    foreach (var f in frames)
                    {
                        var type = f.GetMethod()?.DeclaringType;
                        if (type == null || type == typeof(Sandbox.Engine.Physics.MyPhysics) || type == typeof(VoxelBlockingProbeScenario) || type.Name.Contains("LoadSampler")) continue;
                        var name = f.GetMethod().Name;
                        if (name.StartsWith("Patched_")) continue;
                        by = (type.DeclaringType ?? type).Name;
                        break;
                    }
                Rays.Enqueue(new Ray { At = Stopwatch.GetTimestamp(), From = from, To = to, By = by });
                RaysBy.AddOrUpdate(by, 1, (k, n) => n + 1);
            }
            catch
            {
                // a probe
            }
        }

        private static void QueryPrefix()
        {
            _queryStarted = _listening ? Stopwatch.GetTimestamp() : 0;
            _meshMsInQuery = 0;
            _meshCellsInQuery = 0;
            Interlocked.Increment(ref _queriesRunning);
            Interlocked.Increment(ref _queriesInStep);
        }

        private static void StepPrefix()
        {
            Interlocked.Exchange(ref _queriesInStep, 0);
        }

        private static void QuerySuffix(object __instance)
        {
            var beside = Interlocked.Decrement(ref _queriesRunning);
            var started = _queryStarted;
            if (started == 0) return;
            _queryStarted = 0;
            var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (ms < SlowQueryMs) return;
            try
            {
                var type = __instance.GetType();
                var kind = type.GetField("Kind").GetValue(__instance).ToString();
                string what;
                if (kind.StartsWith("Raycast") && kind != "RaycastChained")
                {
                    var data = type.GetField("RayCastData").GetValue(__instance);
                    var from = (Vector3D)data.GetType().GetField("From").GetValue(data);
                    var to = (Vector3D)data.GetType().GetField("To").GetValue(data);
                    what = kind + " of " + Owner(data.GetType().GetField("Callback").GetValue(data)) + " | " + (to - from).Length().ToString("F0") + " m, layer " +
                           data.GetType().GetField("RayCastFilterLayer").GetValue(data) + ", from " + Round(from);
                }
                else if (kind == "RaycastChained")
                    what = kind + " of " + Owner(type.GetField("Raycasts").GetValue(__instance));
                else
                {
                    var data = type.GetField("QueryBoxData").GetValue(__instance);
                    var callback = data.GetType().GetField("Callback").GetValue(data) ?? data.GetType().GetField("CallbackBodies").GetValue(data);
                    what = kind + " of " + Owner(callback) + " | at " + Round((Vector3D)data.GetType().GetField("Translation").GetValue(data));
                }
                SlowQueries.Enqueue((DateTime.UtcNow, ms, what + (MySandboxGame.Static?.UpdateThread == Thread.CurrentThread ? ", game thread" : ", a worker") +
                                                          ", voxel meshes made inside " + _meshMsInQuery.ToString("F1") + " ms (" + _meshCellsInQuery + " cells), " +
                                                          Volatile.Read(ref _queriesInStep) + " queries in the step, " + beside + " still running"));
            }
            catch (Exception e)
            {
                SlowQueries.Enqueue((DateTime.UtcNow, ms, "? (" + e.GetType().Name + ")"));
            }
        }

        /// <summary>Whose callback it is: the type the delegate (or the closure it sits in) belongs to, an entity's by its type.</summary>
        private static string Owner(object callback)
        {
            var target = callback is Delegate d ? d.Target ?? (object)d.Method.DeclaringType : callback;
            for (var depth = 0; depth < 4 && target != null; depth++)
            {
                if (target is Type t) return t.Name;
                if (target is MyEntity || target is VRage.Game.Components.MyComponentBase) return target.GetType().Name;
                var outer = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(f => f.Name.Contains("this"));
                if (outer == null) return (target.GetType().DeclaringType ?? target.GetType()).Name;
                target = outer.GetValue(target);
            }
            return target?.GetType().Name ?? "?";
        }

        private static void RequestSuffix()
        {
            var request = _current;
            if (request == null) return;
            _current = null;
            request.Ms = (Stopwatch.GetTimestamp() - _started) * 1000.0 / Stopwatch.Frequency;
            _meshMsInQuery += request.Ms;
            _meshCellsInQuery += request.Cells.Length;
            Requests.Enqueue(request);
        }

        public override void Cleanup()
        {
            _listening = false;
            try { _optimisations.Restore(); }
            finally { base.Cleanup(); }
        }
    }
}
