using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// ALE ShipFixer's fixship on a ship with many subgrids (prod 03.11.2026: "Puffy", 22 grids, its subgrids came
    /// broken after !fixship). The player's blueprint is spawned 20 km up in space with an observer by it, left to settle,
    /// and its rotors, hinges and pistons counted with their heads; then ShipFixer's own command (!fixshipmodid, twice
    /// for its confirmation) cuts and pastes it, and the same is counted on the copy: grids, blocks, heads on, heads
    /// off and how far they are, blocks damaged. Needs ALE ShipFixer. focus "bp=path" for another blueprint.
    /// </summary>
    public sealed class FixShipPuffyScenario : TestScenario
    {
        public const string ScenarioName = "fixship_puffy";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        private const string DefaultBlueprint = @"C:\Users\user\AppData\Roaming\SpaceEngineers\Blueprints\local\Puffy\bp.sbc";
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly ConfigOverride _clusters = new ConfigOverride(ConfigOverride.Clusters);
        private const string Prefix = "fixpuffy-";

        public override IEnumerator Run()
        {
            SkipUnless(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "ALE-ShipFixer"), "ALE ShipFixer is not loaded");
            var path = System.Text.RegularExpressions.Regex.Match(TestRunner.Focus ?? "", @"bp=(\S+)") is var m && m.Success ? m.Groups[1].Value : DefaultBlueprint;
            WorldApi.EnsureUnpaused(Name);
            _config.Set("FreezerEnabled", false);
            var focus = TestRunner.Focus ?? "";
            if (focus.Contains("top=0")) _config.Set("MechanicalTopCheck", false);
            if (focus.Contains("reattach=0")) _config.Set("HeadReattach", false);
            if (focus.Contains("clusters=0")) _clusters.Set("Enabled", false);
            var fixerConfig = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ALE_ShipFixer.ShipFixerPlugin", false)).FirstOrDefault(t => t != null)
                ?.GetField("Instance")?.GetValue(null)?.GetType().GetProperty("Config");
            var fixerInstance = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ALE_ShipFixer.ShipFixerPlugin", false)).FirstOrDefault(t => t != null)?.GetField("Instance")?.GetValue(null);
            _fixer = fixerConfig?.GetValue(fixerInstance);
            var parallel = _fixer?.GetType().GetProperty("FixShipInParallel");
            _parallelWas = parallel?.GetValue(_fixer) as bool?;
            parallel?.SetValue(_fixer, !focus.Contains("parallel=0"));
            Note("ShipFixer pastes in parallel: " + parallel?.GetValue(_fixer));
            Note("switches: " + focus);
            _guard = focus.Contains("guard=1");
            if (!_registered)
            {
                _registered = true;
                Sandbox.ModAPI.MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, OnDamage);
            }
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchor.Translation - planet.PositionComp.GetPosition());
            var site = anchor.Translation + up * 20000;
            // what earlier runs left there (pieces blown off) is cleared first: it would be hit by the paste
            var leftovers = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && Vector3D.Distance(g.PositionComp.GetPosition(), site) < 3000).ToList();
            foreach (var g in leftovers) g.Close();
            if (leftovers.Count > 0) Note("cleared " + leftovers.Count + " grids left at the site");
            yield return WaitForTicks(2);
            FakeClients.Add(1, Network, i => (site + up * 150, 0, 0), withCharacters: true);
            yield return WaitForSeconds(5, "the observer arrives");

            var name = WorldApi.EntityPrefix + Prefix + "ship";
            var obs = WorldApi.LoadBlueprintFile(path, name);
            var shift = site - (Vector3D)obs[0].PositionAndOrientation.Value.Position;
            foreach (var ob in obs)
            {
                var po = ob.PositionAndOrientation.Value;
                po.Position = (Vector3D)po.Position + shift;
                ob.PositionAndOrientation = po;
                ob.LinearVelocity = default;
                ob.AngularVelocity = default;
                ob.CreatePhysics = true;   // as saved in a blueprint: false; the game's paste turns it on
            }
            Note("the blueprint: " + string.Join(", ", obs.Select(o => o.DisplayName + " " + o.CubeBlocks.Count)));
            lock (Damage) Damage.Clear();
            _recording = true;
            var grids = obs.Select(WorldApi.SpawnGrid).ToList();
            foreach (var g in grids) Track(g);
            Note("spawned: " + string.Join(", ", grids.Select(g => g.DisplayName + " " + g.BlocksCount)));
            yield return null;
            Note("a frame later: " + string.Join(", ", grids.Select(g => g.DisplayName + " " + (g.MarkedForClose ? "closed" : g.BlocksCount.ToString()))));
            yield return WaitForSeconds(10, "the ship settles");
            _recording = false;
            lock (Damage)
                foreach (var pair in Damage.OrderByDescending(p => p.Value.Item2).Take(8))
                    Note($"  at the spawn: damage {pair.Key}: {pair.Value.Item1} hits, {pair.Value.Item2:0}");
            var main = grids.OrderByDescending(g => g.BlocksCount).First();
            var poses = Poses(main);
            // the live ship against its blueprint: how far each grid went while it settled (rotors, hinges turning)
            var mainOb = obs.First(o => o.EntityId == main.EntityId);
            var obInverse = MatrixD.Invert(mainOb.PositionAndOrientation.Value.GetMatrix());
            foreach (var pose in poses)
            {
                var ob = obs.FirstOrDefault(o => o.EntityId == pose.Grid.EntityId);
                if (ob == null || pose.Grid == main) continue;
                var planned = ob.PositionAndOrientation.Value.GetMatrix() * obInverse;
                var off = Vector3D.Distance(planned.Translation, pose.Local.Translation);
                var turn = Math.Acos(MathHelper.Clamp(Vector3D.Dot(planned.Forward, pose.Local.Forward), -1, 1)) * 180 / Math.PI;
                if (off > 0.02 || turn > 0.5) Note($"  settled away from the blueprint: {pose.Name} ({pose.Blocks}) {off:0.00} m, {turn:0.0} deg");
            }
            var before = State(main);
            Note("before: " + before.Text);

            Note("warheads before the fixship: " + Warheads(main));
            lock (Damage) Damage.Clear();
            lock (FirstHits) FirstHits.Clear();
            _frame = 0;
            _recording = true;
            _pastedAt = DateTime.UtcNow;
            var first = Command("!fixshipmodid " + main.EntityId);
            yield return WaitForTicks(5);
            var second = Command("!fixshipmodid " + main.EntityId);
            Note("ShipFixer: " + string.Join(" / ", first.Concat(second)));
            Check(main.MarkedForClose || main.Closed, "ShipFixer did not cut the ship: " + string.Join(" / ", first.Concat(second)));

            MyCubeGrid copy = null;
            for (var i = 0; i < 600 && copy == null; i++)
            {
                copy = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g.InScene && g.DisplayName == main.DisplayName && g != main && Vector3D.Distance(g.PositionComp.GetPosition(), site) < 1000).FirstOrDefault();
                if (copy == null) yield return null;
            }
            Check(copy != null, "the pasted ship not found in 10 s");
            foreach (var g in MyCubeGridGroups.Static.Mechanical.GetGroup(copy)?.Nodes.Select(n => n.NodeData) ?? Enumerable.Empty<MyCubeGrid>()) Track(g);
            var pastedAt = DateTime.UtcNow;
            var after = State(copy);
            Note("right after the paste: " + after.Text);
            Note("warheads right after the paste: " + Warheads(copy));
            // each grid of the copy against where its original stood on the ship, frame by frame
            var copyGroup = MyCubeGridGroups.Static.Mechanical.GetGroup(copy)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { copy };
            var match = new Dictionary<MyCubeGrid, MatrixD>();
            var copyPoses = Poses(copy);
            foreach (var c in copyPoses)
            {
                var best = poses.Where(o => o.Name == c.Name && o.Blocks == c.Blocks)
                    .OrderBy(o => Vector3D.Distance(o.Local.Translation, c.Local.Translation)).FirstOrDefault();
                if (best != null) match[c.Grid] = best.Local;
            }
            var firstOff = new Dictionary<MyCubeGrid, string>();
            var worstOff = new Dictionary<MyCubeGrid, double>();
            for (var f = 0; f < 180; f++)
            {
                foreach (var pair in match)
                {
                    var g = pair.Key;
                    if (g.MarkedForClose || copy.MarkedForClose) continue;
                    var local = g.WorldMatrix * MatrixD.Invert(copy.WorldMatrix);
                    var off = Vector3D.Distance(local.Translation, pair.Value.Translation);
                    var turn = Math.Acos(MathHelper.Clamp(Vector3D.Dot(local.Forward, pair.Value.Forward), -1, 1)) * 180 / Math.PI;
                    worstOff.TryGetValue(g, out var w);
                    worstOff[g] = Math.Max(w, off);
                    if (f == 2 && pair.Key == copy) Note("warheads 2 frames after the paste: " + Warheads(copy));
                    if (!firstOff.ContainsKey(g) && (off > 0.05 || turn > 1))
                        firstOff[g] = $"frame {f}: {off:0.00} m, {turn:0.0} deg, {g.Physics?.LinearVelocity.Length() ?? 0:0.0} m/s";
                }
                _frame++;
                yield return null;
            }
            lock (FirstHits) foreach (var hit in FirstHits) Note("  first hits: " + hit);
            Note($"poses: {match.Count} of {copyGroup.Count} grids matched to their original; at frame 0 off by up to " +
                 $"{match.Max(pair => Vector3D.Distance((pair.Key.WorldMatrix * MatrixD.Invert(copy.WorldMatrix)).Translation, pair.Value.Translation)):0.00} m (now)");
            foreach (var pair in firstOff) Note($"  moved: {pair.Key.DisplayName} ({pair.Key.BlocksCount} blocks) {pair.Value}; at most {worstOff[pair.Key]:0.00} m in 3 s");
            yield return WaitForSeconds(15, "the copy settles");
            foreach (var g in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => Vector3D.Distance(g.PositionComp.GetPosition(), site) < 2000)) Track(g);
            var later = State(copy);
            Note("15 s after the paste: " + later.Text);
            foreach (var line in later.Off.Take(20)) Note("  head off: " + line);
            _recording = false;
            lock (Damage)
                foreach (var pair in Damage.OrderByDescending(p => p.Value.Item2).Take(15))
                    Note($"  damage {pair.Key}: {pair.Value.Item1} hits, {pair.Value.Item2:0}");
            Note($"FIXSHIP RESULT | before {before.Summary} | after {later.Summary}");
            Check(later.Grids == before.Grids && later.HeadsOff == before.HeadsOff && later.Damaged <= before.Damaged,
                  "the subgrids came out otherwise: before " + before.Summary + ", after " + later.Summary);
        }

        private static bool _registered, _recording;
        private static int _frame;
        private static readonly List<string> FirstHits = new List<string>();

        private static string Warheads(MyCubeGrid main)
        {
            var group = MyCubeGridGroups.Static.Mechanical.GetGroup(main)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { main };
            var all = group.SelectMany(g => g.GetFatBlocks().OfType<Sandbox.ModAPI.Ingame.IMyWarhead>()).ToList();
            var timers = group.SelectMany(g => g.GetFatBlocks().OfType<SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock>()).ToList();
            return $"{all.Count} warheads, armed {all.Count(w => w.IsArmed)}, counting down {all.Count(w => w.IsCountingDown)}; " +
                   $"{timers.Count} timers, counting {timers.Count(t => t.IsCountingDown)}, on {timers.Count(t => t.Enabled)}";
        }
        private object _fixer;
        private bool? _parallelWas;
        private static readonly Dictionary<string, (int, double)> Damage = new Dictionary<string, (int, double)>();

        private static bool _guard;
        private static DateTime _pastedAt = DateTime.MinValue;

        private static void OnDamage(object target, ref VRage.Game.ModAPI.MyDamageInformation info)
        {
            if (!_recording) return;
            var slim = target as VRage.Game.ModAPI.IMySlimBlock;
            var grid = slim?.CubeGrid as MyCubeGrid;
            if (grid == null) return;
            // the remedy tried: no collision damage in a ship's first seconds after its paste
            if (_guard && info.Type == MyDamageType.Deformation && (DateTime.UtcNow - _pastedAt).TotalSeconds < 5)
            {
                info.Amount = 0;
                return;
            }
            VRage.ModAPI.IMyEntity attacker = null;
            Sandbox.Game.Entities.MyEntities.TryGetEntityById(info.AttackerId, out var found);
            attacker = found;
            var by = attacker == null ? "none" : attacker is MyCubeGrid other ? (other == grid ? "own grid" : "grid " + other.DisplayName + " (" + other.BlocksCount + ")" + (MyCubeGridGroups.Static.Mechanical.GetGroup(other) == MyCubeGridGroups.Static.Mechanical.GetGroup(grid) ? " (same ship)" : "")) : attacker.GetType().Name;
            var key = info.Type.String + " to " + grid.DisplayName + " by " + by;
            lock (FirstHits)
                if (FirstHits.Count < 12)
                    FirstHits.Add($"frame {_frame}: {info.Type.String} {info.Amount:0} to {slim.BlockDefinition.Id.SubtypeName} on {grid.DisplayName} ({grid.BlocksCount}) by {by}");
            lock (Damage)
            {
                Damage.TryGetValue(key, out var was);
                Damage[key] = (was.Item1 + 1, was.Item2 + info.Amount);
            }
        }

        private sealed class Pose
        {
            public MyCubeGrid Grid;
            public string Name;
            public int Blocks;
            public MatrixD Local;
        }

        private static List<Pose> Poses(MyCubeGrid main)
        {
            var group = MyCubeGridGroups.Static.Mechanical.GetGroup(main)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { main };
            var inverse = MatrixD.Invert(main.WorldMatrix);
            return group.Select(g => new Pose { Grid = g, Name = g.DisplayName, Blocks = g.BlocksCount, Local = g.WorldMatrix * inverse }).ToList();
        }

        private sealed class Snapshot
        {
            public int Grids, Blocks, Bases, HeadsOff, Damaged;
            public List<string> Off = new List<string>();
            public string Summary => $"{Grids} grids, {Blocks} blocks, {Bases} rotors/hinges/pistons, {HeadsOff} without their head, {Damaged} damaged blocks";
            public string Text;
        }

        private static Snapshot State(MyCubeGrid main)
        {
            var group = MyCubeGridGroups.Static.Mechanical.GetGroup(main)?.Nodes.Select(n => n.NodeData).Where(g => !g.MarkedForClose).ToList() ?? new List<MyCubeGrid> { main };
            var s = new Snapshot { Grids = group.Count, Blocks = group.Sum(g => g.BlocksCount) };
            foreach (var g in group)
            foreach (var block in g.GetBlocks())
            {
                if (block.Integrity < block.MaxIntegrity * block.BuildLevelRatio - 0.01f || block.CurrentDamage > 0.01f) s.Damaged++;
                if (!(block.FatBlock is MyMechanicalConnectionBlockBase stator)) continue;
                s.Bases++;
                if (stator.TopBlock != null) continue;
                s.HeadsOff++;
                s.Off.Add($"{stator.BlockDefinition.Id.SubtypeName} '{stator.CustomName}' on {g.DisplayName} ({g.EntityId}), built {block.BuildLevelRatio:P0}");
            }
            var near = MyEntities.GetEntities().OfType<MyCubeGrid>().Count(g => !g.MarkedForClose && !group.Contains(g) &&
                                                                             Vector3D.Distance(g.PositionComp.GetPosition(), main.PositionComp.GetPosition()) < 300);
            s.Text = s.Summary + $"; grids within 300 m outside the group {near}";
            return s;
        }

        private static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        public override void Cleanup()
        {
            _recording = false;
            try { if (_fixer != null && _parallelWas.HasValue) _fixer.GetType().GetProperty("FixShipInParallel")?.SetValue(_fixer, _parallelWas.Value); }
            catch (Exception) { }
            try { _config.Restore(); }
            catch (Exception) { }
            try { _clusters.Restore(); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
