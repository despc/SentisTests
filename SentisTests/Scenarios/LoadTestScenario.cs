using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A load test of the whole server: <see cref="_ships"/> copies of Spitfire (2757 blocks, dynamic, out in space,
    /// <see cref="Spacing"/> m apart) and <see cref="Players"/> fake players with characters by some of them; the
    /// freezer off. Then where the frame goes:
    /// <list type="bullet">
    /// <item>a clean window of <see cref="CleanSeconds"/> s: FrameProbe (the simulation work of a frame and its
    /// sections) and TickMetrics;</item>
    /// <item>a window of <see cref="ProfileSeconds"/> s with the Profiler plugin's profilers - the parts of the frame,
    /// entities by type, blocks by type and by definition, session components, physics. They cost time
    /// themselves, which is why the clean window is separate.</item>
    /// </list>
    /// The ships come in batches and stop coming when the server's memory reaches <see cref="MaxServerGb"/> GB or the
    /// machine has less than <see cref="MinFreeGb"/> GB left; the report says how many there are.
    /// </summary>
    internal sealed class LoadTestScenario : TestScenario
    {
        private const string Prefix = "load-";
        private const string Spitfire = "SentisTests.Resources.Spitfire.xml";
        private const int Players = 64;
        private const double Spacing = 250;
        // ships being built by the game's workers at once
        private const int InFlight = 8;
        private const double MaxServerGb = 22;
        private const double MinFreeGb = 3;
        private const int WarmupSeconds = 45;
        private const int CleanSeconds = 60;
        private const int ProfileSeconds = 30;
        private const int Top = 15;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly string _name;
        private readonly int _ships;
        private readonly List<MyCubeGrid> _spawned = new List<MyCubeGrid>();
        private uint? _autoSave;

        // A world save during the run wrote hundreds of test ships into the world (a 435 MB file), and the next
        // start removed them all in one frame - over the watchdog's minute. The world is not saved while the
        // ships are here, and they go a few a frame at the end.
        private const int ClosePerFrame = 10;

        public LoadTestScenario(string name, int ships)
        {
            _name = name;
            _ships = ships;
        }

        public override string Name => _name;
        public override int TimeoutSeconds => 1500;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _autoSave = Sandbox.Game.World.MySession.Static.Settings.AutoSaveInMinutes;
            Sandbox.Game.World.MySession.Static.Settings.AutoSaveInMinutes = 0;
            _config.Set("FreezerEnabled", false);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var forward = Vector3D.Cross(side, up);
            Vector3D origin = default;
            for (var d = 150000.0; d <= 1000000; d += 50000)
            {
                origin = anchor + up * d + side * 60000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(origin).Length() < 0.001f) break;
            }

            // ------------------------------------------------------------- the ships, in a block 10 x 10 x n
            // The blueprint is read once and copied for every ship; the game builds each grid on its worker
            // threads (CreateFromObjectBuilderParallel, as it does for pastes), a few at a time, and it goes into
            // the world here, on the game thread. Reading the XML and building the grid on the game thread for
            // every ship took 1.8 s a ship.
            var memoryBefore = ServerGb();
            var positions = new List<Vector3D>();
            var ships = new List<MyCubeGrid>();
            var spawnWatch = Stopwatch.StartNew();
            var template = WorldApi.LoadGridTemplate(Spitfire, WorldApi.EntityPrefix + Prefix + "template", origin, forward, up);
            var built = new System.Collections.Concurrent.ConcurrentQueue<MyCubeGrid>();
            var inFlight = 0;
            var copyTicks = 0L;
            var addTicks = 0L;
            string stopped = null;
            var next = 0;
            var lastProgress = Stopwatch.StartNew();
            while (ships.Count < next || next < _ships && stopped == null)
            {
                // into the world: what the workers have built
                while (built.TryDequeue(out var ship))
                {
                    var at = Stopwatch.GetTimestamp();
                    if (ship != null)
                    {
                        MyEntities.Add(ship);
                        Track(ship);
                        ships.Add(ship);
                        _spawned.Add(ship);
                    }
                    else ships.Add(null);
                    addTicks += Stopwatch.GetTimestamp() - at;
                    Interlocked.Decrement(ref inFlight);
                    lastProgress.Restart();
                }
                // more to the workers
                while (next < _ships && stopped == null && Volatile.Read(ref inFlight) < InFlight)
                {
                    var at = Stopwatch.GetTimestamp();
                    var position = origin + side * (Spacing * (next % 10)) + forward * (Spacing * (next / 10 % 10)) + up * (Spacing * (next / 100));
                    var ob = (VRage.Game.MyObjectBuilder_CubeGrid)template.Clone();
                    ob.Name = WorldApi.EntityPrefix + Prefix + next;
                    ob.DisplayName = ob.Name;
                    ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(position, forward, up);
                    MyAPIGateway.Entities.RemapObjectBuilder(ob);
                    copyTicks += Stopwatch.GetTimestamp() - at;
                    positions.Add(position);
                    Interlocked.Increment(ref inFlight);
                    MyAPIGateway.Entities.CreateFromObjectBuilderParallel(ob, false, entity => built.Enqueue(entity as MyCubeGrid));
                    next++;
                    if (next % 20 == 0)
                    {
                        var server = ServerGb();
                        var free = FreeGb();
                        if (server >= MaxServerGb || free < MinFreeGb)
                            stopped = "stopped at " + next + " ships: the server " + server.ToString("F1") + " GB, " + free.ToString("F1") + " GB free";
                    }
                }
                if (lastProgress.Elapsed.TotalSeconds > 60)
                {
                    stopped = (stopped == null ? "" : stopped + "; ") + (next - ships.Count) + " ships never came from the workers";
                    break;
                }
                yield return null;
            }
            ships.RemoveAll(ship => ship == null);
            Note("spawning: the game thread spent " + (copyTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, next)).ToString("F1") +
                 " ms a ship copying the blueprint and " + (addTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, ships.Count)).ToString("F1") +
                 " ms adding it to the world; " + (spawnWatch.Elapsed.TotalMilliseconds / Math.Max(1, ships.Count)).ToString("F0") + " ms a ship in all");
            var memoryAfter = ServerGb();
            var perShipMb = (memoryAfter - memoryBefore) * 1024 / Math.Max(1, ships.Count);
            var blocks = ships.Sum(s => s.BlocksCount);
            Note(ships.Count + " ships, " + blocks + " blocks, spawned in " + spawnWatch.Elapsed.TotalSeconds.ToString("F0") + " s; server memory " +
                 memoryBefore.ToString("F1") + " -> " + memoryAfter.ToString("F1") + " GB (~" + perShipMb.ToString("F0") + " MB a ship), " +
                 FreeGb().ToString("F1") + " GB free" + (stopped != null ? "; " + stopped : ""));

            // ------------------------------------------------------------- the players, two by each of some ships
            var near = Math.Min(ships.Count, Players / 2);
            var step = Math.Max(1, ships.Count / Math.Max(1, near));
            FakeClients.RemoveAll();
            FakeClients.Add(Players, Network, p =>
            {
                var ship = positions[Math.Min(positions.Count - 1, (p / 2) * step)];
                return (ship + up * 80 + side * (p % 2 == 0 ? 60 : -60), 0, 0);
            }, withCharacters: true);
            var warmup = WaitForSeconds(WarmupSeconds, "the players arrive and the ships stream to them");
            while (warmup.MoveNext()) yield return warmup.Current;
            Note("players " + FakeClients.Count + " by " + near + " ships; replicables still to send " + FakeClients.PendingReplicables() +
                 "; grids in the world " + MyEntities.GetEntities().OfType<MyCubeGrid>().Count() +
                 "; ships a player sees " + ships.Count(s => s.PlayerPresenceTier == VRage.Game.ModAPI.MyUpdateTiersPlayerPresence.Normal) +
                 " of " + ships.Count);

            // ------------------------------------------------------------- the clean window
            TickMetrics.Take();
            FrameProbe.Take();
            FakeClients.Take(1);
            var watcher = Watcher();
            if (watcher != null)
            {
                // the heaviest part of its work goes into the window: a pass over every inventory
                Invoke(watcher.GetType().GetProperty("Cost").GetValue(watcher), "Reset");
                Invoke(watcher.GetType().GetProperty("Sweep").GetValue(watcher), "StartNow");
            }
            var clean = Stopwatch.StartNew();
            var window = WaitForSeconds(CleanSeconds, "the clean window");
            while (window.MoveNext()) yield return window.Current;
            var cleanResult = "LOAD CLEAN WINDOW (" + CleanSeconds + " s) | " + TickMetrics.Take().Format() + " | " + FrameProbe.Take() +
                              " | net " + FakeClients.Take(clean.Elapsed.TotalSeconds);
            Note(cleanResult);
            if (watcher != null)
                Note("WATCHER (clean window): " + watcher.GetType().GetProperty("Cost").GetValue(watcher).GetType().GetMethod("Describe")
                         .Invoke(watcher.GetType().GetProperty("Cost").GetValue(watcher), null) + "; " +
                     watcher.GetType().GetMethod("Describe").Invoke(watcher, null));

            // ------------------------------------------------------------- the profiled window
            var profile = Profile();
            while (profile.MoveNext()) yield return profile.Current;

            // ------------------------------------------------------------- the suspects, method by method
            var methods = Methods();
            while (methods.MoveNext()) yield return methods.Current;

            Note("LOAD TEST RESULT: " + ships.Count + " ships, " + blocks + " blocks, " + FakeClients.Count + " players; see LOAD CLEAN WINDOW and PROFILE lines");

            FakeClients.RemoveAll();
            var close = CloseShips();
            while (close.MoveNext()) yield return close.Current;
        }

        private IEnumerator CloseShips()
        {
            for (var i = 0; i < _spawned.Count; i++)
            {
                var ship = _spawned[i];
                if (ship != null && !ship.MarkedForClose) ship.Close();
                if (i % ClosePerFrame == ClosePerFrame - 1) yield return null;
            }
            _spawned.Clear();
        }

        private IEnumerator Profile()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Profiler");
            if (asm == null)
            {
                Note("PROFILE: the Profiler plugin is not loaded");
                yield break;
            }
            var queue = asm.GetType("Profiler.Core.ProfilerResultQueue").GetMethod("Profile", BindingFlags.Static | BindingFlags.Public);
            var maskType = asm.GetType("Profiler.Basics.GameEntityMask");
            var mask = maskType.GetField("Empty", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            var profilers = new List<(string Name, object Profiler)>
            {
                ("frame", Activator.CreateInstance(asm.GetType("Profiler.Basics.GameLoopProfiler"))),
                ("entities by type", Activator.CreateInstance(asm.GetType("Profiler.Basics.EntityTypeProfiler"))),
                ("blocks by type", Activator.CreateInstance(asm.GetType("Profiler.Basics.BlockTypeProfiler"), mask)),
                ("blocks by definition", Activator.CreateInstance(asm.GetType("Profiler.Basics.BlockDefinitionProfiler"), mask)),
                ("session components", Activator.CreateInstance(asm.GetType("Profiler.Basics.SessionComponentsProfiler"))),
                ("physics clusters", Activator.CreateInstance(asm.GetType("Profiler.Basics.PhysicsProfiler"))),
                ("methods", Activator.CreateInstance(asm.GetType("Profiler.Basics.MethodNameProfiler"))),
            };
            var subscriptions = new List<IDisposable>();
            try
            {
                foreach (var (_, profiler) in profilers)
                {
                    subscriptions.Add((IDisposable)queue.Invoke(null, new[] { profiler }));
                    profiler.GetType().GetMethod("MarkStart").Invoke(profiler, null);
                }
                var window = WaitForSeconds(ProfileSeconds, "the profiled window");
                while (window.MoveNext()) yield return window.Current;
                foreach (var (name, profiler) in profilers)
                {
                    profiler.GetType().GetMethod("MarkEnd").Invoke(profiler, null);
                    var result = profiler.GetType().GetMethod("GetResult").Invoke(profiler, null);
                    Note("PROFILE " + name + ": " + Format(result));
                }
            }
            finally
            {
                foreach (var subscription in subscriptions) subscription.Dispose();
                foreach (var (_, profiler) in profilers) (profiler as IDisposable)?.Dispose();
            }
        }

        private const int MethodSeconds = 20;

        /// <summary>The block updates the profilers point at, each timed on its own, and how often they run.</summary>
        private IEnumerator Methods()
        {
            Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => t.Name == name && ((t.Namespace ?? "").StartsWith("Sandbox") || (t.Namespace ?? "").StartsWith("SpaceEngineers")));
            var thrust = Find("MyThrust");
            var antenna = Find("MyRadioAntenna");
            var turret = Find("MyLargeTurretBase");
            var conveyorTurret = Find("MyLargeConveyorTurretBase");
            var gun = Find("MyUserControllableGun");
            var functional = Find("MyFunctionalBlock");
            var cubeBlock = Find("MyCubeBlock");
            var thrustComponent = Find("MyEntityThrustComponent");
            var targeting = Find("MyLargeTurretTargetingSystem");
            var gridTargeting = Find("MyGridTargeting");
            var gunBase = Find("MyGunBase");
            var targets = new List<(Type, string)>
            {
                (thrust, "UpdateAfterSimulation100"), (thrust, "CheckIsWorking"), (cubeBlock, "UpdateIsWorking"),
                (functional, "UpdateAfterSimulation100"), (thrust, "UpdateAfterSimulation10"),
                (antenna, "UpdateAfterSimulation10"),
                (turret, "DoUpdateTimerTick"), (turret, "UpdateAfterSimulation"),
                (targeting, "CheckAndSelectNearTargetsParallel"), (gridTargeting, "UpdateGridConnections"),
                (gridTargeting, "RefreshGridConnections"), (gunBase, "HasEnoughAmmunition"),
                (gun, "UpdateAfterSimulation"), (conveyorTurret, "UpdateBeforeSimulation100"),
                (thrustComponent, "UpdateBeforeSimulation"),
            };
            var missing = MethodTimerProbe.Start(targets);
            if (missing.Length > 0) Note("METHODS " + missing);
            var from = Sandbox.MySandboxGame.Static.SimulationFrameCounter;
            try
            {
                var window = WaitForSeconds(MethodSeconds, "the methods window");
                while (window.MoveNext()) yield return window.Current;
                Note("METHODS (" + MethodSeconds + " s): " + MethodTimerProbe.Format((long)(Sandbox.MySandboxGame.Static.SimulationFrameCounter - from)));
            }
            finally
            {
                MethodTimerProbe.Stop();
            }
        }

        /// <summary>The top entries of a profiler result: main thread ms a frame, off it, and the total.</summary>
        private static string Format(object result)
        {
            var type = result.GetType();
            var frames = Convert.ToDouble(type.GetProperty("TotalFrameCount").GetValue(result));
            var total = (double)type.GetProperty("TotalTime").GetValue(result);
            var top = (IEnumerable)type.GetMethod("GetTopEntities").Invoke(result, new object[] { (int?)Top });
            var parts = new List<string>();
            foreach (var keyed in top)
            {
                var key = keyed.GetType().GetField("Key").GetValue(keyed);
                var entry = keyed.GetType().GetField("Entity").GetValue(keyed);
                var main = (double)entry.GetType().GetProperty("MainThreadTime").GetValue(entry);
                var off = (double)entry.GetType().GetProperty("OffThreadTime").GetValue(entry);
                parts.Add(KeyName(key) + " " + (main / frames).ToString("F3") + (off > 0 ? "+" + (off / frames).ToString("F3") + " off" : "") + " ms/f");
            }
            return frames + " frames, " + total.ToString("F0") + " ms | " + string.Join("; ", parts);
        }

        private static string KeyName(object key)
        {
            switch (key)
            {
                case null: return "null";
                case Type t: return t.Name;
                case Sandbox.Definitions.MyCubeBlockDefinition d: return d.Id.SubtypeName + " (" + d.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") + ")";
                case string s: return s;
                default:
                    var name = key.ToString();
                    return name.StartsWith("Sandbox.") || name.StartsWith("SpaceEngineers.") ? key.GetType().Name : name;
            }
        }

        private static double ServerGb() => Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024 * 1024);

        private static double FreeGb()
        {
            try
            {
                using (var counter = new PerformanceCounter("Memory", "Available MBytes"))
                    return counter.NextValue() / 1024.0;
            }
            catch
            {
                return double.MaxValue;
            }
        }

        /// <summary>The SentisWatcher plugin, when it is loaded and recording.</summary>
        private static object Watcher()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisWatcher")
                ?.GetType("SentisWatcher.SentisWatcherPlugin");
            var plugin = type?.GetProperty("Instance")?.GetValue(null);
            return plugin != null && type.GetProperty("Store")?.GetValue(plugin) != null ? plugin : null;
        }

        private static void Invoke(object target, string method) => target?.GetType().GetMethod(method)?.Invoke(target, null);

        public override void Cleanup()
        {
            try
            {
                MethodTimerProbe.Stop();
                if (_autoSave.HasValue) Sandbox.Game.World.MySession.Static.Settings.AutoSaveInMinutes = _autoSave.Value;
                _config.Restore();
                FakeClients.RemoveAll();
            }
            catch (Exception e)
            {
                Log.Warn("cleaning up after the test failed: " + e.Message);
            }
            finally { base.Cleanup(); }
        }
    }
}
