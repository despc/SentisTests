using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisWatcher's GarageWatch and GridSpawnWatch against VirtualGarage: a ship put into the garage
    /// (<c>VirtualGarageSave.Store</c>) and made again from its garage file with the ids it had (as a rollback or a
    /// bug would leave it), then taken out (<c>VirtualGarageLoad.Load</c>): told as a grid twice in the world - what the
    /// file was made of is still there; the file put back (as the garage does after a crash) and taken out again while the
    /// first copy is still there: told again; each take-out told as a grid spawned from the garage.
    /// </summary>
    public sealed class GarageDupeCheck : RegressionCheck
    {
        public override string Name => Prefix + "garage_dupe";
        public override int TimeoutSeconds => 90;

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<string> _files = new List<string>();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();

        protected override IEnumerator Check()
        {
            var save = Integrations.TypeOf(Integrations.Garage, "VirtualGarage.VirtualGarageSave");
            var load = Integrations.TypeOf(Integrations.Garage, "VirtualGarage.VirtualGarageLoad");
            SkipUnless(save != null && load != null, "no VirtualGarage");
            var spawnWatch = Integrations.TypeOf(Integrations.Watcher, "SentisWatcher.Recording.GridSpawnWatch");
            SkipUnless(spawnWatch != null && Integrations.TypeOf(Integrations.Watcher, "SentisWatcher.Recording.GarageWatch") != null, "no SentisWatcher with GarageWatch");
            var log = TorchLog();
            var start = new FileInfo(log).Length;
            yield return Arrive(Site.At(0, 0, -20));

            // 1. put in
            var ship = Build("garage-dupe", Site.Origin, false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var at = ship.PositionComp.GetPosition();
            var originalId = ship.EntityId;
            var file = (string)save.GetMethod("Store", Any).Invoke(null, new object[] { Identity, new List<MyCubeGrid> { ship } });
            Check(file != null, "VirtualGarage did not put the ship into the garage");
            _files.Add(file);
            yield return Until(() => File.Exists(file) || File.Exists(file.Replace("_unsaved.sbc", ".sbc")) ? 1 : 0, v => v > 0, "the garage file written", 10);
            if (!File.Exists(file)) file = file.Replace("_unsaved.sbc", ".sbc");
            _files.Add(file);
            Check(!MyEntities.EntityExists(originalId), "the ship put into the garage is still in the world");

            // 2. the original made again from the file, its ids as they were
            MyObjectBuilderSerializer.DeserializeXML(file, out MyObjectBuilder_Definitions definitions);
            var builder = definitions.ShipBlueprints[0].CubeGrids[0];
            Check(builder.EntityId == originalId, "the garage file does not keep the ship's id (" + builder.EntityId + " for " + originalId + ")");
            var again = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder);
            _grids.Add(again);
            Check(again != null && again.EntityId == originalId, "the original was not made again with its id");

            // 3. taken out while the original is there
            List<MyCubeGrid> first = null;
            string failed = null;
            Load(load, file, at + Site.Right * 60, g => first = g, why => failed = why);
            yield return Until(() => first != null || failed != null ? 1 : 0, v => v > 0, "the ship taken out of the garage", 20);
            Check(failed == null, "VirtualGarage did not take the ship out: " + failed);
            _grids.AddRange(first);

            // 4. the file put back (a crash's rollback does) and taken out again, the first copy still there
            var takenOut = file + "_spawned_unsaved";
            yield return Until(() => File.Exists(takenOut) ? 1 : 0, v => v > 0, "the garage file marked taken out", 5);
            _files.Add(takenOut);
            File.Move(takenOut, file);
            List<MyCubeGrid> second = null;
            Load(load, file, at + Site.Right * 120, g => second = g, why => failed = why);
            yield return Until(() => second != null || failed != null ? 1 : 0, v => v > 0, "the ship taken out of the garage again", 20);
            Check(failed == null, "VirtualGarage did not take the ship out again: " + failed);
            _grids.AddRange(second);

            yield return Seconds(3); // the original's check (the file read aside) and NLog
            var text = Tail(log, start);
            var dupes = text.Split('\n').Where(l => l.Contains("WARN") && l.Contains("a grid twice in the world")).ToList();
            foreach (var line in dupes) Note(line.Trim());
            Check(dupes.Any(l => l.Contains("what the garage file was made of is still in the world")), "the take-out with the original still there is not told");
            Check(dupes.Any(l => l.Contains("again while the copy taken out")), "the second take-out with the first copy still there is not told");
            // the other checks spawn grids at the same time: this one's, by their ids
            var recent = ((IEnumerable<string>)spawnWatch.GetField("Recent", Any).GetValue(null)).ToList();
            var mine = recent.Where(l => l.StartsWith("garage:") && (first.Concat(second).Any(g => l.Contains(g.EntityId.ToString())))).ToList();
            foreach (var line in mine) Note("spawned: " + line);
            var spawns = mine.Count;
            Check(spawns == 2, "the two take-outs are not told as grids spawned from the garage (" + spawns + ")");
            Result("the take-out with the original there and the second take-out with the first copy there told as a grid twice in the world; " +
                   spawns + " grids told spawned from the garage");
        }

        private void Load(Type load, string file, Vector3D around, Action<List<MyCubeGrid>> spawned, Action<string> failed)
        {
            var placementType = load.GetNestedType("Placement", Any);
            var placement = Activator.CreateInstance(placementType);
            placementType.GetField("Around").SetValue(placement, around);
            load.GetMethods(Any).First(m => m.Name == "Load" && m.GetParameters().Length == 5)
                .Invoke(null, new object[] { file, Identity, placement, spawned, failed });
        }

        public override void Cleanup()
        {
            foreach (var grid in _grids)
                try { if (grid != null && !grid.MarkedForClose) grid.Close(); }
                catch { }
            foreach (var f in _files.Distinct())
                try { if (File.Exists(f)) File.Delete(f); if (File.Exists(f + "_spawned_unsaved")) File.Delete(f + "_spawned_unsaved"); }
                catch { }
            base.Cleanup();
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
    }
}
