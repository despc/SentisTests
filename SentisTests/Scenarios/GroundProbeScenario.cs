using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Voxels;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The real ground (voxel content, not the generated surface) in columns at the wheel sites where the stand's runs of
    /// 30.09.2026 found no ground within 40 m of the surface, and at the point where a wheel of wheel_small3_lag was found
    /// "more than 1.5 m under the ground". Each column is read from 60 m over the generated surface to 60 m under it,
    /// a metre a step: where the rock starts, where air comes again under it (a tunnel, a cave), and for the wheel's
    /// point whether its centre itself is in rock or in air.
    /// </summary>
    public sealed class GroundProbeScenario : TestScenario
    {
        public const string ScenarioName = "ground_probe";

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;

        private static readonly Vector3D[] NoGround =
        {
            new Vector3D(-45833.566, -106942.305, -185390.145),
            new Vector3D(-45879.356, -106955.328, -185439.400),
            new Vector3D(-45883.734, -106942.658, -185457.602),
            new Vector3D(-45886.027, -106952.581, -185455.454),
            new Vector3D(-45974.747, -109880.636, -185473.874),
            new Vector3D(-45855.060, -108137.712, -185384.171),
        };

        private static readonly Vector3D WheelFell = new Vector3D(-45638.2, -106937.4, -185449.0);

        private const double SiteStepM = 25;

        private readonly MyStorageData _probe = new MyStorageData(MyStorageDataTypeFlags.Content);
        private MyPlanet _planet;

        public override IEnumerator Run()
        {
            _planet = MyGamePruningStructure.GetClosestPlanet(WheelFell);
            Check(_planet?.Storage != null, "no planet near the wheel site");
            foreach (var point in NoGround) Note("NO-GROUND POINT " + Column(point));
            Note("WHEEL FELL " + Column(WheelFell));
            // more points to look at, one "x y z" a line (the fall points of a run)
            var extra = System.IO.Path.Combine(TestRunner.ReportDirectory ?? ".", "ground-points.txt");
            if (System.IO.File.Exists(extra))
                foreach (var line in System.IO.File.ReadAllLines(extra))
                {
                    var parts = line.Split(new[] { ' ', '	', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ||
                        !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ||
                        !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)) continue;
                    var at = new Vector3D(x, y, z);
                    var pointUp = Vector3D.Normalize(at - _planet.PositionComp.GetPosition());
                    Note("POINT " + Column(at) + " | at the point " + Content(at) + ", +1.5 m " + Content(at + pointUp * 1.5) + ", -1 m " + Content(at - pointUp) +
                         "; " + Vector3D.Dot(at - Generated(at), pointUp).ToString("F1") + " m from the generated surface; around at 2 m: " +
                         string.Join(" ", new[] { Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(pointUp)) }.SelectMany(e =>
                         {
                             var n = Vector3D.Cross(pointUp, e);
                             return new[] { e, -e, n, -n }.Select(d => Content(at + d * 2).ToString());
                         })));
                }
            var up = Vector3D.Normalize(WheelFell - _planet.PositionComp.GetPosition());
            Note("WHEEL FELL centre content " + Content(WheelFell) + ", +1.5 m " + Content(WheelFell + up * 1.5) + ", -1 m " + Content(WheelFell - up) +
                 "; centre " + Vector3D.Dot(WheelFell - Generated(WheelFell), up).ToString("F1") + " m from the generated surface");
            // the ground around the wheel: a 40 m square, 5 m apart
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var north = Vector3D.Cross(up, east);
            var sb = new StringBuilder("WHEEL SITE real ground minus generated, m (rows north, 5 m apart; x: no ground):");
            for (var j = -4; j <= 4; j++)
            {
                sb.Append(" |");
                for (var i = -4; i <= 4; i++)
                {
                    var p = WheelFell + east * (i * 5) + north * (j * 5);
                    var top = FirstRock(p, out var generated);
                    sb.Append(' ').Append(top == null ? "x" : top.Value.ToString("F0"));
                }
            }
            Note(sb.ToString());

            // every wheel site, 25 m apart: where the real ground is off the generated by more than 2 m
            var dug = new List<(double Off, Vector3D At)>();
            var columns = 0;
            foreach (var site in TerrainRestoreScenario.WheelSites)
            {
                var siteUp = Vector3D.Normalize(site - _planet.PositionComp.GetPosition());
                var siteEast = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(siteUp));
                var siteNorth = Vector3D.Cross(siteUp, siteEast);
                var steps = (int)(TerrainRestoreScenario.WheelSiteRadiusM / SiteStepM);
                for (var i = -steps; i <= steps; i++)
                {
                    for (var j = -steps; j <= steps; j++)
                    {
                        if (i * i + j * j > steps * steps) continue;
                        var p = site + siteEast * (i * SiteStepM) + siteNorth * (j * SiteStepM);
                        columns++;
                        var top = FirstRock(p, out var generated);
                        if (top == null || System.Math.Abs(top.Value) > 2) dug.Add((top ?? -99, generated));
                    }
                    yield return null;
                }
            }
            // against the scan before: what came and what went, so a scenario that digs shows itself
            var file = System.IO.Path.Combine(TestRunner.ReportDirectory ?? ".", "ground-scan.txt");
            var now = dug.Select(d => d.At.ToString("F0") + " " + d.Off.ToString("F0")).ToList();
            try
            {
                if (System.IO.File.Exists(file))
                {
                    var before = new HashSet<string>(System.IO.File.ReadAllLines(file));
                    var came = now.Where(x => !before.Contains(x)).ToList();
                    var went = before.Where(x => !now.Contains(x)).ToList();
                    Note("WHEEL SITES CHANGE since the last scan | new " + came.Count + (came.Count == 0 ? "" : ": " + string.Join("; ", came.Take(15))) +
                         " | gone " + went.Count + (went.Count == 0 ? "" : ": " + string.Join("; ", went.Take(15))));
                }
                System.IO.File.WriteAllLines(file, now);
            }
            catch (System.Exception e) { Note("scan file: " + e.Message); }
            Note("WHEEL SITES | " + columns + " columns 25 m apart, " + dug.Count + " off the generated ground by more than 2 m" +
                 (dug.Count == 0 ? "" : ": " + string.Join("; ", dug.OrderBy(d => d.Off).Take(12).Select(d => d.At.ToString("F0") + " " + d.Off.ToString("F0") + " m"))));
        }

        private Vector3D Generated(Vector3D point) => _planet.GetClosestSurfacePointGlobal(ref point);

        private byte Content(Vector3D point)
        {
            var voxel = Vector3I.Floor(point - _planet.PositionLeftBottomCorner) + _planet.StorageMin;
            _probe.Resize(Vector3I.One);
            _planet.Storage.ReadRange(_probe, MyStorageDataTypeFlags.Content, 0, voxel, voxel);
            return _probe.Content(0);
        }

        /// <summary>Height of the first rock from 60 m over the generated surface down, relative to it; null if none to 60 m under.</summary>
        private double? FirstRock(Vector3D point, out Vector3D generated)
        {
            generated = Generated(point);
            var up = Vector3D.Normalize(generated - _planet.PositionComp.GetPosition());
            for (var h = 60; h >= -60; h--)
                if (Content(generated + up * h) >= 128) return h;
            return null;
        }

        private string Column(Vector3D point)
        {
            var generated = Generated(point);
            var up = Vector3D.Normalize(generated - _planet.PositionComp.GetPosition());
            var runs = new StringBuilder();
            bool? rock = null;
            var from = 60;
            for (var h = 60; h >= -61; h--)
            {
                var isRock = h >= -60 && Content(generated + up * h) >= 128;
                if (rock == null) { rock = isRock; continue; }
                if (h >= -60 && isRock == rock.Value) continue;
                runs.Append(rock.Value ? " rock " : " air ").Append(from).Append("..").Append(h + 1);
                rock = isRock;
                from = h;
            }
            return point.ToString("F0") + ": from +60 to -60 m of the generated surface:" + runs;
        }
    }
}
