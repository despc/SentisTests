using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using Torch.API.Managers;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRageMath;
using NormalTurret = SpaceEngineers.Game.Entities.Weapons.MyLargeGatlingTurret;
using NormalApi = Sandbox.ModAPI.Ingame.IMyLargeTurretBase;
using ControllerApi = SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock;

namespace SentisTests.Regression.Checks
{
    /// <summary>Observes only forwarded searches, never attempts to infer cadence from global counters.</summary>
    public sealed class TurretSearchCadenceCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret_search_cadence";
        public override int TimeoutSeconds => 240;
        protected override bool NeedsDamage => true;

        private sealed class SearchSample
        {
            public long Id;
            public ulong Frame;
            public bool Target, Normal;
        }

        private readonly object _gate = new object();
        private readonly HashSet<long> _owned = new HashSet<long>();
        private readonly List<SearchSample> _samples = new List<SearchSample>();
        private FieldInfo _observerField;
        private object _previousObserver;
        private Action<long, ulong, bool, bool> _observer;
        private bool _attached;
        private static ulong Now => Sandbox.MySandboxGame.Static.SimulationFrameCounter;

        // Who took a watched turret's target away: the call stack of every SetTarget(null) while it had one, so a
        // lost target in the cadence window says which of the game's paths (stuck rotors, an invalid target, a search
        // that came back empty) dropped it.
        private static readonly object LossGate = new object();
        private static readonly HashSet<object> Watched = new HashSet<object>();
        private static readonly List<string> Losses = new List<string>();
        private static readonly FieldInfo SystemOfController = typeof(SpaceEngineers.Game.Entities.Blocks.MyTurretControlBlock)
            .GetField("m_targetingSystem", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SystemOfTurret = typeof(Sandbox.Game.Weapons.MyLargeTurretBase)
            .GetField("m_targetingSystem", BindingFlags.Instance | BindingFlags.NonPublic);
        private PatchManager _patches;
        private PatchContext _patchContext;

        private static void SetTargetPrefix(Sandbox.Game.Weapons.MyLargeTurretTargetingSystem __instance, MyEntity target)
        {
            try
            {
                if (target != null || __instance.Target == null) return;
                lock (LossGate)
                {
                    if (!Watched.Contains(__instance) || Losses.Count >= 20) return;
                    var frames = new System.Diagnostics.StackTrace(1, false).GetFrames() ?? new System.Diagnostics.StackFrame[0];
                    Losses.Add("frame " + Now + " thread " + System.Threading.Thread.CurrentThread.ManagedThreadId + " lost " +
                               __instance.Target.EntityId + " <- " + string.Join(" <- ", frames.Take(8).Select(f =>
                                   f.GetMethod() == null ? "?" : f.GetMethod().DeclaringType?.Name + "." + f.GetMethod().Name)));
                }
            }
            catch { /* a diagnostic only */ }
        }

        private void WatchTargets(IEnumerable<MyCubeBlock> blocks)
        {
            lock (LossGate)
            {
                Losses.Clear();
                foreach (var block in blocks)
                {
                    var system = block is SpaceEngineers.Game.Entities.Blocks.MyTurretControlBlock
                        ? SystemOfController?.GetValue(block) : SystemOfTurret?.GetValue(block);
                    if (system != null) Watched.Add(system);
                }
            }
            var setTarget = typeof(Sandbox.Game.Weapons.MyLargeTurretTargetingSystem).GetMethod("SetTarget",
                BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(MyEntity), typeof(bool) }, null);
            _patches = SentisTestsPlugin.TorchInstance.Managers.GetManager<PatchManager>();
            if (setTarget == null || _patches == null) return;
            _patchContext = _patches.AcquireContext();
            _patchContext.GetPattern(setTarget).Prefixes.Add(typeof(TurretSearchCadenceCheck).GetMethod(nameof(SetTargetPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            _patches.Commit();
        }

        /// <summary>
        /// Which blocks of the turret's grids reach into blocks of another grid (its own other grids, or anything near)
        /// and which characters stand within them: what can hold a rotor that is told to turn.
        /// </summary>
        private static string Touching(List<MyCubeGrid> own)
        {
            var found = new List<string>();
            var box = own.Select(g => g.PositionComp.WorldAABB).Aggregate((a, b) => a.Include(b)).Inflate(2);
            var near = MyEntities.GetEntitiesInAABB(ref box, true).ToList();
            foreach (var grid in own)
            foreach (var other in near.OfType<MyCubeGrid>())
            {
                if (other == grid || other.Physics == null || own.IndexOf(other) >= 0 && own.IndexOf(other) <= own.IndexOf(grid)) continue;
                if (!grid.PositionComp.WorldAABB.Intersects(other.PositionComp.WorldAABB)) continue;
                foreach (var a in grid.GetBlocks())
                {
                    var ab = a.WorldAABB;
                    // a shared cell face is a touch, not a reach: shrink a little
                    ab = ab.Inflate(-0.05);
                    foreach (var b in other.GetBlocks())
                        if (ab.Intersects(b.WorldAABB.Inflate(-0.05)))
                        {
                            if (found.Count < 12)
                                found.Add(grid.DisplayName + "/" + a.BlockDefinition.Id.SubtypeName + a.Position + " x " +
                                          (own.Contains(other) ? "" : "FOREIGN ") + other.DisplayName + "/" + b.BlockDefinition.Id.SubtypeName + b.Position);
                        }
                }
            }
            foreach (var character in near.OfType<Sandbox.Game.Entities.Character.MyCharacter>())
                found.Add("character " + character.DisplayName + " at " + character.PositionComp.GetPosition().ToString("F1"));
            near.Clear();
            return found.Count == 0 ? "nothing" : string.Join(", ", found);
        }

        private static string LossReport()
        {
            lock (LossGate) return Losses.Count == 0 ? "no SetTarget(null) seen" : string.Join(" | ", Losses);
        }

        private void UnwatchTargets()
        {
            lock (LossGate) Watched.Clear();
            if (_patchContext == null) return;
            _patches.FreeContext(_patchContext);
            _patchContext = null;
            _patches.Commit();
        }

        // Called on the search path: no game API, no logging, no exception can escape to the weapon update.
        private void Observe(long id, ulong frame, bool target, bool normal)
        {
            try
            {
                lock (_gate)
                    if (_owned.Contains(id))
                        _samples.Add(new SearchSample { Id = id, Frame = frame, Target = target, Normal = normal });
            }
            catch { /* The optional diagnostic must never disrupt shooting. */ }
        }

        private SearchSample[] Samples(ulong start, ulong end)
        {
            lock (_gate) return _samples.Where(s => s.Frame >= start && s.Frame < end).ToArray();
        }

        private void RestoreObserver()
        {
            if (!_attached) return;
            // Do not overwrite a slot another diagnostic took over after us.
            if (ReferenceEquals(_observerField.GetValue(null), _observer))
                _observerField.SetValue(null, _previousObserver);
            _attached = false;
        }

        public override void Cleanup()
        {
            try { RestoreObserver(); }
            finally
            {
                try { UnwatchTargets(); }
                finally { base.Cleanup(); }
            }
        }

        protected override IEnumerator Check()
        {
            SkipUnless(!Game.Mods.WeaponCore, "WeaponCore owns the weapons, not vanilla targeting");
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Optimizer.Optimizations.TurretControlTargetSelect", false)).FirstOrDefault(t => t != null);
            SkipUnless(type != null, "SentisOptimisations turret search observer is not loaded");
            _observerField = type.GetField("SearchObserved", BindingFlags.Public | BindingFlags.Static);
            Check(_observerField != null && _observerField.FieldType == typeof(Action<long, ulong, bool, bool>),
                "SearchObserved must be public static Action<long, ulong, bool, bool>");
            Check((int)type.GetField("Period").GetRawConstantValue() == 20, "idle search period is not 20 frames");
            _previousObserver = _observerField.GetValue(null);
            SkipUnless(_previousObserver == null, "another diagnostic owns the global turret observer slot");
            HoldHighQuality();
            yield return Arrive(Site.At(-15, 10, -15));
            var enemy = -1;
            yield return Join(Site.At(15, 10, 2000), i => enemy = i);

            // Four ordinary turrets on ONE grid: a grid-wide phase cannot satisfy the distribution assertion.
            var parts = new List<Part>();
            for (var x = 0; x < 19; x++) parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, -1, 0));
            for (var i = 0; i < 4; i++) parts.Add(new Part("LargeGatlingTurret/", i * 6, 0, 0));
            parts.Add(new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -2, 0).With(b => Motion.Charged(b)));
            var normalGrid = Build("cadence-normal", Site.At(60, 0, 0), true, parts);
            var normals = normalGrid.GetFatBlocks().OfType<NormalTurret>().ToList();
            Check(normals.Count == 4, "ordinary turret fixture must contain four turrets");
            foreach (var gun in normals) Put(Inventory(gun), "AmmoMagazine/NATO_25x184mm", 20);

            var group = WorldApi.LoadAuthoredGroup(TurretControllerCheck.ResourceName, WorldApi.EntityPrefix + "cadence-custom");
            var move = MatrixD.Invert(group[0].PositionAndOrientation.Value.GetMatrix()) * MatrixD.CreateWorld(Site.Origin, Site.Forward, Site.Up);
            foreach (var ob in group)
            {
                ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
                foreach (var block in ob.CubeBlocks) { block.Owner = Identity; block.BuiltBy = Identity; }
                // the seat of the fixture stands where the launcher swings to aim at this check's station: a launcher
                // pressed into it holds the azimuth rotor, and the game's stuck test (CheckBeingStuck) drops the target
                ob.CubeBlocks.RemoveAll(b => b.SubtypeName == "LargeBlockCockpitSeat");
            }
            var grids = group.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in grids) { Track(grid); WorldApi.ChargeBatteries(grid); }
            var controller = grids.SelectMany(g => g.GetFatBlocks()).First(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("TurretControlBlock"));
            var launcher = grids.SelectMany(g => g.GetFatBlocks()).First(b => b.BlockDefinition.Id.SubtypeName == "LargeMissileLauncher");
            var custom = (ControllerApi)controller;
            Put(Inventory(launcher), "AmmoMagazine/Missile200mm", 4);
            var all = normals.Cast<MyCubeBlock>().Concat(new[] { controller }).ToList();
            lock (_gate) foreach (var block in all) _owned.Add(block.EntityId);
            bool HasTarget(MyCubeBlock b) => b == controller ? custom.HasTarget : ((NormalApi)b).HasTarget;
            string Status() => string.Join("; ", all.Select(b => b.EntityId + " target=" + HasTarget(b) + " " + State(b)));
            yield return Until(() => all.Count(b => b.IsWorking) + (custom.AzimuthRotor != null && custom.ElevationRotor != null ? 1 : 0),
                n => n == all.Count + 1, "all cadence fixtures powered and rotors connected", 20, Status);
            yield return WaitForTicks(120);
            _observer = Observe;
            // Recheck after the yielding setup; a concurrent check must not lose its observer.
            SkipUnless(_observerField.GetValue(null) == null, "another diagnostic acquired the turret observer during setup");
            _observerField.SetValue(null, _observer);
            _attached = true;
            try
            {
                yield return Until(() => all.Count(b => !HasTarget(b)), n => n == all.Count, "initially idle turrets", 15, Status);
                var idleStart = Now;
                yield return WaitForTicks(240);
                var idle = Samples(idleStart, Now);
                AssertIdle(all, controller, idle, "initial idle");
                var phases = normals.Select(b => idle.Where(s => s.Id == b.EntityId).First().Frame % 20).Distinct().Count();
                Check(phases > 1, "all ordinary turrets on one grid searched on the same phase: no per-block distribution");

                // No forced target/aim/shoot calls: real enemy arrival must trigger autonomous acquisition and fire.
                var wall = new List<Part>();
                for (var x = -5; x <= 5; x++)
                for (var y = -4; y <= 4; y++)
                {
                    wall.Add(new Part("CubeBlock/LargeHeavyBlockArmorBlock", x, y, 0));
                    if (Math.Abs(x) <= 2 && Math.Abs(y) <= 2)
                        wall.Add(new Part("BatteryBlock/LargeBlockBatteryBlock", x, y, 1).With(b => Motion.Charged(b)));
                }
                var station = Build("cadence-enemy", Site.At(10, 7, 100), true, wall,
                    ownedBy: FakeClients.PlayerOf(enemy).Identity.IdentityId);
                Func<double> integrity = () => station.MarkedForClose ? 0 : station.GetBlocks().Sum(b => (double)b.Integrity);
                var whole = integrity();
                var rockets = Amount(Inventory(launcher), "AmmoMagazine/Missile200mm");
                var fired = new HashSet<long>();
                var acquired = new HashSet<long>();
                var arrival = Now;
                var targetSince = new Dictionary<long, ulong>();
                void CheckTracking()
                {
                    foreach (var block in all)
                    {
                        if (!HasTarget(block)) { targetSince.Remove(block.EntityId); continue; }
                        if (!targetSince.ContainsKey(block.EntityId)) targetSince[block.EntityId] = Now;
                        var since = targetSince[block.EntityId];
                        var cadence = 10UL;
                        ulong last;
                        lock (_gate)
                            last = _samples.Where(s => s.Id == block.EntityId && s.Target && s.Frame >= since)
                                .Select(s => s.Frame).DefaultIfEmpty(since).Max();
                        // One frame of slack for runner/update ordering, but never a fresh 20-frame idle wait.
                        Check(Now - last <= cadence + 1,
                            "target-bearing turret did not switch to 10-frame searches: " + block.EntityId +
                            ", " + (Now - last) + " frames since forwarded search (cadence " + cadence + ")");
                    }
                }
                yield return Until(() =>
                {
                    CheckTracking();
                    foreach (var b in all)
                        if (HasTarget(b))
                        {
                            var targetId = b == controller ? custom.GetTargetedEntity().EntityId : ((NormalApi)b).GetTargetedEntity().EntityId;
                            Check(targetId == station.EntityId, "turret " + b.EntityId + " acquired a foreign target " + targetId);
                            acquired.Add(b.EntityId);
                        }
                    foreach (var gun in normals) if (((NormalApi)gun).IsShooting) fired.Add(gun.EntityId);
                    if (((Sandbox.Game.Weapons.MySmallMissileLauncher)launcher).IsShooting) fired.Add(controller.EntityId);
                    return acquired.Count == all.Count && fired.Count == all.Count && whole > integrity() &&
                        Amount(Inventory(launcher), "AmmoMagazine/Missile200mm") < rockets ? 1 : 0;
                }, n => n > 0, "every turret acquiring and firing at the real enemy", 60,
                    () => "acquired " + acquired.Count + ", fired " + fired.Count + ", damage " + F(whole - integrity()) + "; " + Status());
                Check(Amount(Inventory(launcher), "AmmoMagazine/Missile200mm") < rockets,
                    "custom launcher reports shooting but used no missile magazine");
                Note("all " + all.Count + " turrets acquired and fired in " + (Now - arrival) + " simulation frames");

                // Damage and real ammunition use were proved above. Keep this target alive during the
                // cadence-only window: destroying the selected block legitimately resets a turret's Target.
                station.DestructibleBlocks = false;
                yield return Until(() => all.Count(HasTarget), n => n == all.Count,
                    "all turrets holding the protected cadence target", 15, Status);
                WatchTargets(all);
                var activeStart = Now;
                // the custom turret's rotors frame by frame: what the game's stuck test (CheckBeingStuck) looked at
                var rotors = new Queue<string>();
                var aimed = typeof(SpaceEngineers.Game.Entities.Blocks.MyTurretControlBlock).GetProperty("IsAimed");
                var unstepped = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Optimizer.Optimizations.SelectivePhysicsBodies", false)).FirstOrDefault(t => t != null)
                    ?.GetMethod("InUnstepped", BindingFlags.Static | BindingFlags.NonPublic);
                var havokMotor = typeof(Sandbox.Game.Entities.Cube.MyMotorStator).GetField("m_motor", BindingFlags.Instance | BindingFlags.NonPublic);
                string Motor(Sandbox.ModAPI.IMyMotorStator m) => m == null ? "" :
                    " (havok " + (havokMotor?.GetValue(m) is object hm ? hm.GetType().GetProperty("VelocityTarget")?.GetValue(hm)?.ToString() : "-") +
                    ", updates " + ((VRage.Game.Entity.MyEntity)m).NeedsUpdate + ", " + (m.IsWorking ? "working" : "NOT WORKING") + (m.Enabled ? "" : " off") + (m.IsAttached ? "" : " detached") +
                    (m.RotorLock ? " locked" : "") + ", top spin " + (m.TopGrid?.Physics == null ? "-" : m.TopGrid.Physics.AngularVelocity.Length().ToString("F3")) +
                    ", top static " + (m.TopGrid?.IsStatic) + ", " + (m.CubeGrid as MyCubeGrid)?.GridSystems?.ResourceDistributor?.ResourceStateByType(
                        Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId) + ")";
                string Stepped(MyCubeGrid g) => g == null || unstepped == null ? "" : (bool)unstepped.Invoke(null, new object[] { g }) ? " NOT STEPPED" : "";
                for (var frame = 0; frame < 120; frame++)
                {
                    var azimuth = custom.AzimuthRotor as Sandbox.ModAPI.IMyMotorStator;
                    var elevation = custom.ElevationRotor as Sandbox.ModAPI.IMyMotorStator;
                    rotors.Enqueue((Now - activeStart) + ": aimed " + aimed?.GetValue(controller) + Stepped(controller.CubeGrid) +
                                   Stepped((azimuth?.TopGrid) as MyCubeGrid) + ", azimuth " +
                                   (azimuth == null ? "-" : azimuth.Angle.ToString("F5") + " at " + azimuth.TargetVelocityRPM.ToString("F3") + Motor(azimuth) +
                                    (azimuth.TopGrid?.Physics?.IsActive == false ? " asleep" : "")) + ", elevation " +
                                   (elevation == null ? "-" : elevation.Angle.ToString("F5") + " at " + elevation.TargetVelocityRPM.ToString("F3") + Motor(elevation) +
                                    (elevation.TopGrid?.Physics?.IsActive == false ? " asleep" : "")));
                    if (rotors.Count > 10) rotors.Dequeue();
                    if (!all.All(HasTarget))
                        Check(false, "target lost during the active cadence window, frame " + (Now - activeStart) + ": " +
                                     string.Join(", ", all.Where(b => !HasTarget(b)).Select(b => b.EntityId + " (" + b.GetType().Name + ")")) +
                                     "; who dropped it: " + LossReport() + "; touching: " + Touching(grids) + "; the custom turret's rotors: " + string.Join(" / ", rotors) + "; " + Status());
                    yield return null;
                }
                UnwatchTargets();
                var active = Samples(activeStart, Now);
                foreach (var block in all)
                {
                    var frames = active.Where(s => s.Id == block.EntityId && s.Target).Select(s => s.Frame).Distinct().OrderBy(f => f).ToArray();
                    var cadence = 10UL;
                    Check(frames.Length >= 10, "too few actual active searches for " + block.EntityId + ": " + frames.Length);
                    Check(frames.First() - activeStart <= cadence && Now - frames.Last() <= cadence + 1,
                        "active search window edges starved for " + block.EntityId);
                    if (block == controller)
                        Check(frames.All(f => f % 10 == (ulong)block.EntityId % 10),
                            "active controller searches lost their per-block 10-frame phase");
                    Check(frames.Zip(frames.Skip(1), (a, b) => b - a).All(gap => gap == cadence),
                        "active search intervals must be exactly " + cadence + " frames for " + block.EntityId + ": " + string.Join(",", frames.Zip(frames.Skip(1), (a, b) => b - a)));
                }

                yield return CloseAndWait(station);
                yield return Until(() => all.Count(b => !HasTarget(b)), n => n == all.Count, "all turrets losing the removed enemy", 30, Status);
                // Let potential-target state and the last in-flight search clear before measuring idle again.
                yield return WaitForTicks(60);
                var resumedStart = Now;
                yield return WaitForTicks(240);
                AssertIdle(all, controller, Samples(resumedStart, Now), "idle after target loss");
                Result("actual forwarded searches: 20-frame per-block idle, " + phases + " phases on one grid; all acquired/fired; active controller and ordinary every 10; target loss resumed idle");
            }
            finally { RestoreObserver(); }
        }

        private void AssertIdle(List<MyCubeBlock> blocks, MyCubeBlock controller, SearchSample[] samples, string phase)
        {
            foreach (var block in blocks)
            {
                var calls = samples.Where(s => s.Id == block.EntityId).OrderBy(s => s.Frame).ToArray();
                Check(calls.Length >= 10, phase + ": starvation for " + block.EntityId + ", only " + calls.Length + " forwarded calls in 240 frames");
                Check(calls.All(s => !s.Target && s.Normal == (block != controller)), phase + ": unexpected target or weapon kind for " + block.EntityId);
                Check(calls.All(s => s.Frame % 20 == (ulong)block.EntityId % 20), phase + ": search not on block EntityId phase for " + block.EntityId);
                Check(calls.Zip(calls.Skip(1), (a, b) => b.Frame - a.Frame).All(gap => gap == 20),
                    phase + ": intervals must be exactly 20 (duplicates also fail), block " + block.EntityId + ": " + string.Join(",", calls.Zip(calls.Skip(1), (a, b) => b.Frame - a.Frame)));
                Note(phase + ": " + (block == controller ? "controller " : "ordinary ") + block.EntityId + ", " + calls.Length + " searches, phase " + calls[0].Frame % 20);
            }
        }
    }
}
