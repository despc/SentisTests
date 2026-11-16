using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using VRage.Game.ObjectBuilders.Components;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisWatcher tells which admins' safe zone (one of no block) went and why, read from the stack of its closing:
    /// - one made here and deleted the admin menu's way (MySessionComponentSafeZones.DeleteSafeZone_Implementation): "deleted
    ///   from the admin menu";
    /// - one made here and closed by a plugin (this scenario): "closed by SentisTests...";
    /// - an economy station of the stand's world whose safe zone is closed: Watcher's station check at the load tells it
    ///   has none (called here), and SentisGameplayImprovements makes the zone again the frame after (StationSafeZones),
    ///   a new one where the station is; without SGI the scenario makes it again the game's way (MyFactionStation.CreateSafeZone).
    /// Each told as a warning in Torch's log (read back here).
    /// </summary>
    public class SafeZoneWatchScenario : TestScenario
    {
        public const string ScenarioName = "safezone_watch";

        private MyFactionStation _station;
        private Sandbox.Game.World.MyFaction _stationFaction;
        private bool _restored = true;
        private MySafeZone _edited;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;
        public override string[] Requires => new[] { Integrations.Watcher };

        public override IEnumerator Run()
        {
            var watch = RequireType(Integrations.TypeOf(Integrations.Watcher, "SentisWatcher.Recording.SafeZoneWatch"), "SentisWatcher.Recording.SafeZoneWatch");
            var log = TorchLog();
            var start = new FileInfo(log).Length;
            var anchor = MySession.Static.Players.GetOnlinePlayers().FirstOrDefault()?.GetPosition() ?? new Vector3D(300000, 300000, 300000);
            var site = anchor + new Vector3D(50000, 50000, 50000);

            // the admin menu's way
            var menu = Zone(site, "Scenario admin menu zone");
            MySessionComponentSafeZones.DeleteSafeZone_Implementation(menu.EntityId);
            // a plugin's
            var plugin = Zone(site + new Vector3D(1000, 0, 0), "Scenario plugin zone");
            plugin.Close();
            // one edited from the admin menu: switched off, made smaller, everything allowed in it
            _edited = Zone(site + new Vector3D(2000, 0, 0), "Scenario edited zone");
            yield return null; // Watcher looks at a new zone the frame after
            yield return null;
            var ob = (MyObjectBuilder_SafeZone)_edited.GetObjectBuilder();
            ob.Enabled = false;
            ob.Radius = 20;
            ob.AllowedActions = (MySafeZoneAction)0x7fffffff;
            // the admin menu's handler called here, not through the network: its call site (what names the admin menu) is not on the stack
            MySessionComponentSafeZones.UpdateSafeZone_Implementation(ob);

            // a station without its safe zone
            foreach (var faction in MySession.Static.Factions.Factions.Values)
            foreach (var station in faction.Stations.OfType<MyFactionStation>())
                if (_station == null && station.StationEntityId != 0 && MyEntities.TryGetEntityById(station.StationEntityId, out MyCubeGrid _) &&
                    MyEntities.TryGetEntityById(station.SafeZoneEntityId, out MySafeZone _))
                {
                    _station = station;
                    _stationFaction = (Sandbox.Game.World.MyFaction)faction;
                }
            long stationGrid = 0, oldZone = 0;
            if (_station != null)
            {
                stationGrid = _station.StationEntityId;
                oldZone = _station.SafeZoneEntityId;
                MyEntities.TryGetEntityById(oldZone, out MySafeZone zone);
                _restored = false;
                zone.Close();
                watch.GetMethod("CheckStations").Invoke(null, null);
            }
            else Note("no economy station with its safe zone in the stand's world: the station check not tried");

            var sgi = Integrations.TypeOf(Integrations.Gameplay, "SentisGameplayImprovements.StationSafeZones") != null;
            if (_station != null && sgi)
            {
                var back = Wait(() => _station.SafeZoneEntityId != oldZone && MyEntities.TryGetEntityById(_station.SafeZoneEntityId, out MySafeZone _), "SGI making the station's safe zone again", 10);
                while (back.MoveNext()) yield return back.Current;
                _restored = true;
            }
            else Restore();
            yield return Seconds(3); // NLog writes in the background
            var text = Tail(log, start);
            bool Told(long id, string reason) => text.Split('\n').Any(l => l.Contains("WARN") && l.Contains(id.ToString()) && l.Contains(reason));
            Note($"the admin menu's: {Told(menu.EntityId, "deleted from the admin menu")}, the plugin's: {Told(plugin.EntityId, "closed by SentisTests")}" +
                 (_station != null ? $", station {stationGrid} without its safe zone {oldZone}: {Told(stationGrid, "has no safe zone")} (now {_station.SafeZoneEntityId})" : ""));
            Check(Told(menu.EntityId, "deleted from the admin menu"), "the admin menu's deleting is not told");
            Check(Told(plugin.EntityId, "closed by SentisTests.Scenarios.SafeZoneWatchScenario"), "the plugin's closing is not told with its frames");
            var edit = text.Split('\n').FirstOrDefault(l => l.Contains("WARN") && l.Contains("safe zone changed") && l.Contains(_edited.EntityId.ToString()));
            Note("the admin menu's edit: " + (edit ?? "not told"));
            Check(edit != null && edit.Contains("on on -> off") && edit.Contains("radius 50 -> 20") && edit.Contains("allowed"),
                "the admin menu's edit is not told with what changed");
            if (_station != null)
            {
                Check(Told(oldZone, "closed by SentisTests"), "the station's safe zone closed is not told");
                Check(Told(stationGrid, "has no safe zone"), "the station without its safe zone is not told");
                Check(MyEntities.TryGetEntityById(_station.SafeZoneEntityId, out MySafeZone again) && again.Radius > 0 &&
                      Vector3D.Distance(again.PositionComp.GetPosition(), _station.Position) < 1, "the station's safe zone was not made again where the station is");
                if (sgi) Check(Told(stationGrid, "made again"), "SGI did not tell it made the station's safe zone again");
            }
            Note("PASS: the admins' safe zones gone are told with the reason (the admin menu, a plugin's frames), an admin menu's edit with what changed" + (_station != null ? "; a station without its safe zone is told" + (sgi ? " and SGI made it again" : "") : ""));
        }

        private static MySafeZone Zone(Vector3D at, string name) =>
            (MySafeZone)MySessionComponentSafeZones.CrateSafeZone(MatrixD.CreateTranslation(at), MySafeZoneShape.Sphere, MySafeZoneAccess.Blacklist, null, null, 50f, true, name: name);

        /// <summary>The station's safe zone made again as the game makes it with the station.</summary>
        private void Restore()
        {
            if (_restored || _station == null) return;
            var create = typeof(MyFactionStation).GetMethod("CreateSafeZone", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var zone = (MySafeZone)create.Invoke(_station, new object[] { _stationFaction });
            zone.AccessTypeGrids = MySafeZoneAccess.Blacklist;
            zone.AccessTypeFloatingObjects = MySafeZoneAccess.Blacklist;
            zone.AccessTypePlayers = MySafeZoneAccess.Blacklist;
            zone.AccessTypeFactions = MySafeZoneAccess.Blacklist;
            _restored = true;
        }

        private static string TorchLog() =>
            Directory.GetFiles(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"), "Torch-*.log").OrderByDescending(File.GetLastWriteTimeUtc).First();

        private static string Tail(string path, long from)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Math.Min(from, stream.Length), SeekOrigin.Begin);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        private static IEnumerator Seconds(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until) yield return null;
        }

        public override void Cleanup()
        {
            try
            {
                Restore();
            }
            catch (Exception e)
            {
                Log.Error(e, "safezone_watch: the station's safe zone was not made again");
            }
            try { _edited?.Close(); }
            catch { }
            base.Cleanup();
        }
    }
}
