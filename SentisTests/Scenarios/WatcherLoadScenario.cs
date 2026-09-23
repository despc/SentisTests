using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
    /// SentisWatcher under load: 64 Spitfires in space, a fake player in the cockpit of each, the ships flying
    /// back and forth; two of them jump.
    ///
    ///  - The cost: windows with the recording on and off alternate under the same load; the game thread's
    ///    work is compared between them, and SentisWatcher's own time is read (each "on" window starts a pass
    ///    over every inventory, the heaviest part of its work).
    ///  - The data: every ship and every player has positions over the run, the players their taking of the
    ///    controls, the ships their inventories; the jumps are there, for the ship and for its pilot; nothing
    ///    was dropped.
    /// </summary>
    public sealed class WatcherLoadScenario : TestScenario
    {
        public const string ScenarioName = "watcher_load";
        private const string Prefix = "wload-";
        private const string Spitfire = "SentisTests.Resources.Spitfire.xml";
        private const int Ships = 64;
        private const double Spacing = 400;
        private const int InFlight = 8;
        private const float Speed = 40;
        private const double FlipSeconds = 6;
        private const int WarmupSeconds = 40;
        private const int WindowSeconds = 30;
        private const int Windows = 6;               // on, off, on, off, on, off
        private const double JumpM = 8000;
        private const int ClosePerFrame = 10;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private static readonly Regex SimWork = new Regex(@"sim-work frames=\d+ avg=([\d.]+)ms p50=[\d.]+ p95=[\d.]+ p99=([\d.]+)");

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<MyCubeGrid> _ships = new List<MyCubeGrid>();
        private uint? _autoSave;
        private PropertyInfo _current;
        private object _recorder;
        private Vector3D _side;
        private double _flightStarted;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 1200;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            // ------------------------------------------------------------- SentisWatcher
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisWatcher");
            Check(assembly != null, "SentisWatcher is not loaded");
            var plugin = assembly.GetType("SentisWatcher.SentisWatcherPlugin").GetProperty("Instance").GetValue(null);
            var store = plugin.GetType().GetProperty("Store").GetValue(plugin);
            Check(store != null, "SentisWatcher records nothing");
            var sweep = plugin.GetType().GetProperty("Sweep").GetValue(plugin);
            var cost = plugin.GetType().GetProperty("Cost").GetValue(plugin);
            _current = assembly.GetType("SentisWatcher.Recording.Recorder").GetProperty("Current");
            _recorder = _current.GetValue(null);
            var web = Activator.CreateInstance(assembly.GetType("SentisWatcher.Storage.WebData"), store);
            var clock = assembly.GetType("SentisWatcher.Storage.Clock");
            long Now() => (long)clock.GetProperty("Now").GetValue(null);
            long Dropped() => (long)store.GetType().GetProperty("Dropped").GetValue(store);
            var droppedBefore = Dropped();

            _autoSave = Sandbox.Game.World.MySession.Static.Settings.AutoSaveInMinutes;
            Sandbox.Game.World.MySession.Static.Settings.AutoSaveInMinutes = 0;
            _config.Set("FreezerEnabled", false);

            // ------------------------------------------------------------- where: in space, above the stand
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var forward = Vector3D.Cross(_side, up);
            Vector3D origin = default;
            for (var d = 150000.0; d <= 1000000; d += 50000)
            {
                origin = anchor + up * d - _side * 80000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(origin).Length() < 0.001f) break;
            }

            // ------------------------------------------------------------- the ships (as the load test spawns them)
            var template = WorldApi.LoadGridTemplate(Spitfire, WorldApi.EntityPrefix + Prefix + "template", origin, forward, up);
            var built = new System.Collections.Concurrent.ConcurrentQueue<MyCubeGrid>();
            var positions = new List<Vector3D>();
            var inFlight = 0;
            var next = 0;
            var arrived = 0;
            var spawn = Stopwatch.StartNew();
            while (arrived < next || next < Ships)
            {
                while (built.TryDequeue(out var ship))
                {
                    arrived++;
                    Interlocked.Decrement(ref inFlight);
                    if (ship == null) continue;
                    MyEntities.Add(ship);
                    Track(ship);
                    _ships.Add(ship);
                }
                while (next < Ships && Volatile.Read(ref inFlight) < InFlight)
                {
                    var position = origin + _side * (Spacing * (next % 8)) + forward * (Spacing * (next / 8));
                    var ob = (VRage.Game.MyObjectBuilder_CubeGrid)template.Clone();
                    ob.Name = WorldApi.EntityPrefix + Prefix + next;
                    ob.DisplayName = ob.Name;
                    ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(position, forward, up);
                    MyAPIGateway.Entities.RemapObjectBuilder(ob);
                    positions.Add(position);
                    Interlocked.Increment(ref inFlight);
                    MyAPIGateway.Entities.CreateFromObjectBuilderParallel(ob, false, entity => built.Enqueue(entity as MyCubeGrid));
                    next++;
                }
                Check(spawn.Elapsed.TotalSeconds < 300, "the ships did not come from the workers in 5 minutes");
                yield return null;
            }
            Note(_ships.Count + " ships spawned in " + spawn.Elapsed.TotalSeconds.ToString("F0") + " s");
            var from = Now();

            // ------------------------------------------------------------- a player in the cockpit of each
            FakeClients.RemoveAll();
            FakeClients.Add(_ships.Count, Network, p => (positions[p] + up * 30, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(10, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            var seated = 0;
            var pilots = new long[_ships.Count];
            for (var i = 0; i < _ships.Count; i++)
            {
                pilots[i] = FakeClients.Character(i)?.GetPlayerIdentityId() ?? 0;
                var cockpit = _ships[i].GetFatBlocks().OfType<MyCockpit>().FirstOrDefault();
                if (cockpit != null && FakeClients.TakeControl(i, cockpit)) seated++;
            }
            Note("players " + FakeClients.Count + ", in a cockpit " + seated);

            // ------------------------------------------------------------- warm-up: flying, replication settles
            _flightStarted = Stopwatch.GetTimestamp();
            var warm = Fly(WarmupSeconds, "the ships fly back and forth, the players are sent everything");
            while (warm.MoveNext()) yield return warm.Current;

            // two jumps, 8 km along the row
            var jumper = new[] { _ships[0], _ships[1] };
            var performJump = typeof(MyGridJumpDriveSystem).GetMethod("PerformJump", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var ship in jumper)
                performJump.Invoke(ship.GridSystems.JumpSystem, new object[] { ship.PositionComp.GetPosition() + _side * JumpM });
            Note("jumped: " + string.Join(", ", jumper.Select(s => s.DisplayName)));

            // ------------------------------------------------------------- windows: the recording on and off
            var on = new List<(double Avg, double P99)>();
            var off = new List<(double Avg, double P99)>();
            var costs = new List<string>();
            for (var w = 0; w < Windows; w++)
            {
                var recording = w % 2 == 0;
                _current.SetValue(null, recording ? _recorder : null);
                if (recording)
                {
                    cost.GetType().GetMethod("Reset").Invoke(cost, null);
                    sweep.GetType().GetMethod("StartNow").Invoke(sweep, null);
                }
                TickMetrics.Take();
                FrameProbe.Take();
                var window = Fly(WindowSeconds, "window " + (w + 1) + "/" + Windows + ", recording " + (recording ? "on" : "off"));
                while (window.MoveNext()) yield return window.Current;
                var frames = TickMetrics.Take();
                var work = FrameProbe.Take();
                var m = SimWork.Match(work);
                var sim = m.Success
                    ? (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                    : (frames.AvgFrameMs, frames.P99FrameMs);
                (recording ? on : off).Add(sim);
                var line = "window " + (w + 1) + " recording " + (recording ? "ON " : "OFF") + ": " + frames.Format() + " | " + work;
                if (recording)
                {
                    var own = (string)cost.GetType().GetMethod("Describe").Invoke(cost, null);
                    costs.Add(own);
                    line += " | WATCHER " + own;
                }
                Note(line);
            }
            _current.SetValue(null, _recorder);
            var flush = Fly(4, "the last records are written");
            while (flush.MoveNext()) yield return flush.Current;

            var onAvg = on.Average(x => x.Avg);
            var offAvg = off.Average(x => x.Avg);
            var onP99 = on.Average(x => x.P99);
            var offP99 = off.Average(x => x.P99);
            Note($"WATCHER LOAD: game thread work {onAvg:F2} ms a frame with the recording on, {offAvg:F2} ms off ({onAvg - offAvg:+0.00;-0.00} ms); " +
                 $"p99 {onP99:F2} vs {offP99:F2} ms; SentisWatcher itself: {string.Join(" / ", costs)}");

            // ------------------------------------------------------------- the data
            var to = Now();
            object Call(string method, params object[] args) =>
                web.GetType().GetMethods().First(x => x.Name == method && x.GetParameters().Length == args.Length).Invoke(web, args);
            IDictionary<string, object> Dict(object o) => (IDictionary<string, object>)o;
            int Total(object track) => (int)Dict(track)["total"];
            IEnumerable<string> Kinds(object events) => ((IEnumerable)Dict(events)["events"]).Cast<IDictionary<string, object>>().Select(e => (string)e["kind"]);

            var shipsTracked = _ships.Count(s => Total(Call("Track", "grid", s.EntityId, from, to)) >= 5);
            var shipsWithInventory = _ships.Count(s => ((ICollection)Dict(Call("Inventory", "grid", s.EntityId, to))["inventories"]).Count > 0);
            var playersTracked = pilots.Count(id => id != 0 && Total(Call("Track", "player", id, from, to)) >= 5);
            var playersControl = pilots.Count(id => id != 0 && Kinds(Call("Events", id, from, to)).Contains("control"));
            var jumps = jumper.Count(s => Kinds(Call("Events", s.EntityId, from, to)).Contains("jump"));
            var passengers = jumper.Select(s => pilots[_ships.IndexOf(s)]).Count(id => id != 0 && Kinds(Call("Events", id, from, to)).Contains("jump_passenger"));
            var dropped = Dropped() - droppedBefore;
            Note($"WATCHER DATA: ships with positions {shipsTracked}/{_ships.Count}, with inventories {shipsWithInventory}/{_ships.Count}; " +
                 $"players with positions {playersTracked}/{pilots.Length}, taking the controls {playersControl}/{seated}; " +
                 $"jumps {jumps}/{jumper.Length}, their pilots {passengers}/{jumper.Length}; rows dropped {dropped}");

            Check(dropped == 0, "the writer dropped " + dropped + " rows");
            Check(shipsTracked >= _ships.Count - 2, "only " + shipsTracked + " of " + _ships.Count + " ships have positions");
            Check(shipsWithInventory >= _ships.Count - 2, "only " + shipsWithInventory + " of " + _ships.Count + " ships have inventories recorded");
            Check(playersTracked >= pilots.Length - 2, "only " + playersTracked + " of " + pilots.Length + " players have positions");
            Check(playersControl >= seated - 2, "only " + playersControl + " of " + seated + " seated players have their taking of the controls");
            Check(jumps == jumper.Length, "only " + jumps + " of " + jumper.Length + " jumps were recorded");
            Check(seated == 0 || passengers == jumper.Length, "only " + passengers + " of the " + jumper.Length + " jumping pilots have their jump");
            Check(onAvg - offAvg < 1.0, $"the recording costs the game thread {onAvg - offAvg:F2} ms a frame");

            FakeClients.RemoveAll();
            var close = CloseShips();
            while (close.MoveNext()) yield return close.Current;
        }

        /// <summary>Flies the ships back and forth along the row for a while: the velocity turns every few seconds.</summary>
        private IEnumerator Fly(double seconds, string what)
        {
            var watch = Stopwatch.StartNew();
            var frame = 0;
            var lastNote = -1;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                if (frame++ % 30 == 0)
                {
                    var flown = (Stopwatch.GetTimestamp() - _flightStarted) / (double)Stopwatch.Frequency;
                    var direction = (int)(flown / FlipSeconds) % 2 == 0 ? 1 : -1;
                    var velocity = (Vector3)(_side * (Speed * direction));
                    foreach (var ship in _ships)
                        if (ship.Physics != null && !ship.MarkedForClose) ship.Physics.LinearVelocity = velocity;
                }
                var tens = (int)(watch.Elapsed.TotalSeconds / 10);
                if (tens != lastNote)
                {
                    lastNote = tens;
                    Note(what + " (" + (tens * 10) + "/" + seconds.ToString("F0") + "s)");
                }
                yield return null;
            }
        }

        private IEnumerator CloseShips()
        {
            for (var i = 0; i < _ships.Count; i++)
            {
                if (_ships[i] != null && !_ships[i].MarkedForClose) _ships[i].Close();
                if (i % ClosePerFrame == ClosePerFrame - 1) yield return null;
            }
            _ships.Clear();
        }

        public override void Cleanup()
        {
            try
            {
                if (_current != null && _recorder != null) _current.SetValue(null, _recorder);   // never leave the recording off
                if (_autoSave.HasValue) Sandbox.Game.World.MySession.Static.Settings.AutoSaveInMinutes = _autoSave.Value;
                _config.Restore();
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
