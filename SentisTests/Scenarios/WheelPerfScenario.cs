using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Wheeled vehicle benchmark and fall-through check. The operator's WHEEL_TEST (a large-grid
    /// chassis on six 5x5 off-road suspensions, each wheel its own grid) is spawned
    /// <see cref="_count"/> times on a lattice next to where it was parked on the planet, every
    /// copy at the authored height over the ground under it. The handbrake is released and the
    /// suspensions drive themselves through their propulsion and steering overrides - no pilot -
    /// on circles, at <see cref="SpeedLimitKmh"/>. The driving is the profiling window.
    ///
    /// All along (spawn, settle, window) the centre of every chassis and wheel grid is checked
    /// against the voxel storage: a centre more than DeepM under the ground means the grid went
    /// through it (a grid that went through keeps falling), and the run fails. Heights are taken from the storage too, not from the generated surface, because the
    /// ground here may be flattened by hand. Wheels that come off their suspension and chassis
    /// that end on their side are counted as well.
    /// </summary>
    public sealed class WheelPerfScenario : TestScenario
    {
        public const string ScenarioName = "wheel_perf";
        public const string ScenarioName64 = "wheel_perf_64";
        public const string FallScenarioName = "wheel_fall";
        public const string FallLagScenarioName = "wheel_fall_lag";
        // A small-grid rover built in code (armor plate, four small batteries, four suspensions
        // with small wheels) instead of WHEEL_TEST: small wheels move more than their own radius
        // in one physics step at speed, which the big 5x5 wheels never do.
        public const string SmallScenarioName = "wheel_small";
        public const string SmallLagScenarioName = "wheel_small_lag";
        public const string Small3ScenarioName = "wheel_small3";
        public const string Small3LagScenarioName = "wheel_small3_lag";
        // Fall-through recovery (SentisGameplayImprovements, AutoRestoreFromVoxel): every vehicle is
        // pushed under the ground mid-drive - moved down, tilted, falling - and must come back.
        public const string RestoreScenarioName = "wheel_restore";
        // Parked: handbrake on, no propulsion - what most vehicles on a server do most of the time.
        public const string ParkedScenarioName = "wheel_parked_64";
        // Physics A/B: 64 vehicles driving, the window split into phases that each change one
        // physics setting of the wheels, physics time measured per phase.
        public const string PhysicsAbScenarioName = "wheel_physics_ab";
        private const double AbPhaseSeconds = 20;
        private static readonly string[] AbPhases = { "baseline", "wheel CallbackLimit=1", "wheel contact callbacks off", "wheel Debris quality",
            "cylinder side segments 8", "baseline again", "Havok threads 1", "Havok threads 15", "Havok threads back to default" };
        public const string RestoreSmallScenarioName = "wheel_restore_small";
        // Cost of the checks alone: 100 vehicles drive with AutoRestoreFromVoxel on, none sinks.
        public const string RestoreCostScenarioName = "wheel_restore_cost";
        private const double SinkAtSeconds = 10;
        private const double RestoreWindowSeconds = 40;
        private const double SinkDepthLargeM = 12;
        private const double SinkDepthSmallM = 4;
        private const double SinkTiltDegrees = 60;
        private const double SinkSpeedMps = 15;
        private const double MaxRecoverySeconds = 3;
        internal const string ResourceName = "SentisTests.Resources.WheelTest.xml";
        private const string GridPrefix = "wheel-perf-";
        private const int GridsPerRow = 8;
        private const double LatticeStep = 150.0;
        // The lattice goes on the flattest ground within this distance of the authored vehicle,
        // searched on a grid of this step, but never closer than LatticeOffset to the vehicle.
        private const double FlatSearchM = 2500.0;
        private const double FlatSearchStepM = 150.0;
        private const double FlatSampleStepM = 50.0;
        private const double LatticeOffset = 300.0;
        // Every wheel starts this far over the real ground under it.
        private const double WheelClearanceM = 0.5;
        private const float SpeedLimitKmh = 40f;
        private const float Steering = 1f;
        // wheel_fall: straight ahead, parallel, as fast as the wheels go, into terrain whose voxel
        // collision has never been built.
        private const float FastSpeedLimitKmh = 360f;
        // The wheels alone make ~13 m/s; players go much faster. The harness adds a push forward up
        // to this speed, capped at one weight of the vehicle, and drops every vehicle from this
        // height at the start, so the wheels hit the ground at ~22 m/s.
        private const double FastSpeedMps = 35.0;
        // Small grids may go 100 m/s; the rover is pushed to this.
        private const double RoverSpeedMps = 60.0;
        private const double FastPushG = 1.0;
        private const double RoverPushG = 3.0;
        private const double StabilizeGain = 3.0;
        private const double DropHeightM = 25.0;
        // wheel_fall stands in one line across the driving direction, this far apart.
        private const double FastSpacingM = 100.0;
        // Circles drift; past this distance from its spawn point a vehicle steers back toward it.
        private const double HomeRadiusM = 40.0;
        private const double SettleSeconds = 10;
        private const double ApproachSeconds = 10;
        private const double WindowSeconds = 120;
        private const int LogEverySeconds = 10;
        private const int CheckEveryFrames = 30;
        // Ground search: this far over and under the generated surface, in steps.
        private const double GroundSearchM = 40.0;
        private const double GroundStepM = 0.25;
        private const double DeepM = 1.5;

        private readonly int _count;
        private readonly List<List<MyCubeGrid>> _vehicles = new List<List<MyCubeGrid>>();
        private readonly List<MyMotorSuspension> _suspensions = new List<MyMotorSuspension>();
        private readonly List<string> _fallen = new List<string>();
        private readonly HashSet<long> _fallenIds = new HashSet<long>();
        private MyPlanet _planet;
        private bool _captured;
        private bool _initialFreezerEnabled;
        private readonly VRage.Voxels.MyStorageData _probe = new VRage.Voxels.MyStorageData(VRage.Voxels.MyStorageDataTypeFlags.Content);

        private readonly bool _fast;
        // wheel_fall_lag: every vehicle grid is held at Debris quality - no continuous collision
        // against static bodies - which is what the physics step optimizer of a slow server
        // (MyPhysics.StepWorlds after 11 slow steps) does to grids with recent TOI contacts.
        private readonly bool _lag;
        // Suspension subtype of the small rover ("SmallSuspension1x1"/"SmallSuspension3x3"), null for WHEEL_TEST.
        private readonly string _rover;
        private readonly bool _restore;
        private readonly bool _parked;
        private readonly bool _ab;
        private bool? _initialAutoRestore;
        private readonly bool _sink = true;

        public WheelPerfScenario(int count = 32, bool fast = false, bool lag = false, string rover = null, bool restore = false,
            bool parked = false, bool ab = false, bool sink = true)
        {
            _sink = sink;
            _parked = parked;
            _ab = ab;
            _count = count;
            _rover = rover;
            _fast = fast || lag || rover != null;
            _lag = lag;
            _restore = restore;
        }

        public override string Name => _restore && !_sink ? RestoreCostScenarioName : _ab ? PhysicsAbScenarioName : _parked ? ParkedScenarioName : _restore
            ? (_rover != null ? RestoreSmallScenarioName : RestoreScenarioName)
            : _rover != null
            ? (_rover.EndsWith("3x3") ? (_lag ? Small3LagScenarioName : Small3ScenarioName) : (_lag ? SmallLagScenarioName : SmallScenarioName))
            : _lag ? FallLagScenarioName : _fast ? FallScenarioName : _count == 32 ? ScenarioName : ScenarioName64;
        public override int TimeoutSeconds => (int)(SettleSeconds + ApproachSeconds + WindowSeconds) + 400;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name + " start");
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _captured = true;
            RuntimePluginControls.SetFreezerEnabled(false);
            Note("Freezer disabled for the benchmark");
            if (_restore)
            {
                _initialAutoRestore = AutoRestore;
                AutoRestore = true;
                Note("AutoRestoreFromVoxel enabled (was " + _initialAutoRestore + ")");
            }

            var template = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + GridPrefix + "template");
            var chassis0 = template[0].PositionAndOrientation.Value.GetMatrix();
            var authored = chassis0.Translation;
            _planet = MyGamePruningStructure.GetClosestPlanet(authored);
            Check(_planet != null, "no planet near the authored vehicle");
            var planetCenter = _planet.PositionComp.GetPosition();
            var up0 = Vector3D.Normalize(authored - planetCenter);
            var height0 = HeightOverGround(authored);
            var east = Vector3D.Normalize(chassis0.Forward - up0 * Vector3D.Dot(chassis0.Forward, up0));
            var north = Vector3D.Cross(up0, east);
            var rows = (_count + GridsPerRow - 1) / GridsPerRow;
            var center = TestRunner.RunOrigin ?? FlattestSite(authored, east, north, rows);
            Note("planet " + _planet.StorageName + ", vehicle of " + template.Count + " grids, chassis " +
                 height0.ToString("F2") + " m over the ground, chassis up · vertical = " + Vector3D.Dot(chassis0.Up, up0).ToString("F3"));

            var groundRadii = new List<double>();
            var lifts = new List<double>();
            for (var i = 0; i < _count; i++)
            {
                // The vehicles drive along their chassis X axis, across their forward axis, so the
                // line runs along forward (east) and they drive side by side, not into each other.
                var guess = _fast
                    ? center + east * ((i - (_count - 1) / 2.0) * FastSpacingM)
                    : center + east * ((i % GridsPerRow - (GridsPerRow - 1) / 2.0) * LatticeStep) +
                      north * ((i / GridsPerRow - (rows - 1) / 2.0) * LatticeStep);
                var surface = Ground(guess);
                var up = Vector3D.Normalize(surface - planetCenter);
                groundRadii.Add((surface - planetCenter).Length());
                if (_rover != null)
                {
                    var roverOb = RoverOb(WorldApi.EntityPrefix + GridPrefix + i.ToString("D2"),
                        MatrixD.CreateWorld(surface + up * DropHeightM, Rotate(east, up0, up), up));
                    var rover = WorldApi.SpawnGrid(roverOb);
                    Track(rover);
                    WorldApi.ChargeBatteries(rover);
                    foreach (var suspension in WorldApi.FindFunctionals<MyMotorSuspension>(rover))
                        CreateTopPart.Invoke(suspension, new[] { (object)WorldApi.PlayerIdentityId(), NormalTopSize, true });
                    _vehicles.Add(new List<MyCubeGrid> { rover });
                    lifts.Add(DropHeightM);
                    yield return null;
                    continue;
                }
                var chassis = MatrixD.CreateWorld(surface + up * height0, Rotate(chassis0.Forward, up0, up), Rotate(chassis0.Up, up0, up));
                var move = MatrixD.Invert(chassis0) * chassis;

                var group = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + GridPrefix + i.ToString("D2"));
                // Lift the vehicle until every wheel clears the ground under it: the lattice is
                // flat overall, but not under every wheel.
                var lift = 0.0;
                foreach (var wheel in group.Skip(1))
                {
                    var at = wheel.PositionAndOrientation.Value.GetMatrix().Translation;
                    at = Vector3D.Transform(at, move);
                    lift = Math.Max(lift, WheelRadius(wheel) + (_fast ? DropHeightM : WheelClearanceM) - HeightOverGround(at));
                }
                move *= MatrixD.CreateTranslation(up * lift);
                lifts.Add(lift);
                group[0].Handbrake = _parked;
                foreach (var ob in group)
                {
                    ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
                    ob.IsStatic = false;
                    foreach (var suspension in ob.CubeBlocks.OfType<Sandbox.Common.ObjectBuilders.MyObjectBuilder_MotorSuspension>())
                    {
                        suspension.SpeedLimit = _fast ? FastSpeedLimitKmh : SpeedLimitKmh;
                        suspension.PropulsionOverride = 0;
                        suspension.SteeringOverride = 0;
                    }
                }
                var grids = group.Select(WorldApi.SpawnGrid).ToList();
                foreach (var grid in grids) Track(grid);
                _vehicles.Add(grids);
                yield return null;
            }

            Note("lattice at " + center.ToString("F0") + ", " + (center - authored).Length().ToString("F0") + " m from the authored vehicle; ground spans " +
                 (groundRadii.Max() - groundRadii.Min()).ToString("F1") + " m in height; vehicles lifted " + lifts.Min().ToString("F1") + "-" +
                 lifts.Max().ToString("F1") + " m to clear their wheels");
            var settle = WaitForSeconds(SettleSeconds, "vehicles settle on their wheels");
            while (settle.MoveNext())
            {
                CheckFallThrough();
                yield return settle.Current;
            }
            foreach (var grids in _vehicles)
            {
                WorldApi.EnsureDistributor(grids[0]);
                var suspensions = WorldApi.FindFunctionals<MyMotorSuspension>(grids[0]);
                _suspensions.AddRange(suspensions);
                // The rover's wheels were created after spawn; track them like the extracted ones.
                foreach (var suspension in suspensions)
                    if (suspension.TopGrid != null && !grids.Contains(suspension.TopGrid))
                    {
                        grids.Add(suspension.TopGrid);
                        Track(suspension.TopGrid);
                    }
            }
            var attached = _suspensions.Count(s => s.TopGrid != null);
            Note("spawned " + _count + " vehicles, " + _suspensions.Count + " suspensions, " + attached + " with a wheel");
            Check(attached == _suspensions.Count, "not every suspension got its wheel after spawn");

            // Go: full propulsion, and every vehicle turns the same way so it drives a circle
            // around its own lattice cell.
            foreach (var suspension in _suspensions)
            {
                suspension.PropulsionOverride = _parked ? 0f : 1f;
                suspension.SteeringOverride = _fast || _parked ? 0f : Steering;
            }
            var starts = _vehicles.Select(v => v[0].PositionComp.GetPosition()).ToArray();
            var started = DateTime.UtcNow;
            var windowStarted = DateTime.MinValue;
            var lastLog = DateTime.UtcNow;
            var frame = 0;
            var sunkAt = DateTime.MinValue;
            var recovered = new double[_vehicles.Count];
            var fellAgain = new bool[_vehicles.Count];
            var restoredBefore = RestoredCount;
            var abPhase = -1;
            while (true)
            {
                if (_restore && windowStarted != DateTime.MinValue)
                {
                    var sinceWindow = (DateTime.UtcNow - windowStarted).TotalSeconds;
                    if (_sink && sunkAt == DateTime.MinValue && sinceWindow >= SinkAtSeconds)
                    {
                        sunkAt = DateTime.UtcNow;
                        for (var v = 0; v < _vehicles.Count; v++) Sink(_vehicles[v]);
                        Note("SANK " + _vehicles.Count + " vehicles " + (_rover != null ? SinkDepthSmallM : SinkDepthLargeM) + " m under the ground, tilted " +
                             SinkTiltDegrees + " deg, falling at " + SinkSpeedMps + " m/s; under the ground now: " + _vehicles.Count(v => UnderGround(v[0])) +
                             ", wheels attached " + _suspensions.Count(x => x.TopGrid != null) + "/" + _suspensions.Count);
                    }
                    else if (sunkAt != DateTime.MinValue)
                    {
                        var since = (DateTime.UtcNow - sunkAt).TotalSeconds;
                        if (since < 4 && (int)(since * 2) != _lastAttachLog)
                        {
                            _lastAttachLog = (int)(since * 2);
                            Note("after sinking " + since.ToString("F1") + " s: wheels attached " + _suspensions.Count(x => x.TopGrid != null) + "/" + _suspensions.Count +
                                 ", under the ground " + _vehicles.Count(v => UnderGround(v[0])) + ", chassis speed median " +
                                 _vehicles.Select(v => (double)(v[0].Physics?.LinearVelocity.Length() ?? 0)).OrderBy(x => x).ElementAt(_vehicles.Count / 2).ToString("F1"));
                        }
                        for (var v = 0; v < _vehicles.Count; v++)
                        {
                            var under = UnderGround(_vehicles[v][0]);
                            if (recovered[v] == 0 && !under)
                            {
                                recovered[v] = since;
                                if (recovered.All(t => t > 0))
                                    Note("all back after " + since.ToString("F1") + " s, wheels attached " +
                                         _suspensions.Count(x => x.TopGrid != null) + "/" + _suspensions.Count);
                            }
                            else if (recovered[v] > 0 && under && since > recovered[v] + 1) fellAgain[v] = true;
                        }
                    }
                }
                if (++frame % CheckEveryFrames == 0)
                {
                    CheckFallThrough();
                    if (!_fast && !_parked) SteerHome(starts);
                }
                if (_fast && windowStarted != DateTime.MinValue) Push();
                if (_fast) Stabilize();
                if (_lag && frame % 10 == 0) DropToDebris();
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (windowStarted == DateTime.MinValue && elapsed >= ApproachSeconds)
                {
                    windowStarted = DateTime.UtcNow;
                    if (_fast) LearnTravelDirection();
                    TickMetrics.Take();
                    FrameProbe.Take();
                    Note("PROFILE WINDOW START: " + _count + " vehicles driving");
                }
                if (_ab && windowStarted != DateTime.MinValue)
                {
                    var phase = (int)((DateTime.UtcNow - windowStarted).TotalSeconds / AbPhaseSeconds);
                    if (phase != abPhase)
                    {
                        var probe = FrameProbe.Take();
                        if (abPhase >= 0) Note("AB PHASE " + abPhase + " (" + AbPhases[abPhase] + "): " + PhaseSummary(probe));
                        if (phase >= AbPhases.Length) break;
                        abPhase = phase;
                        ApplyAbPhase(abPhase);
                    }
                }
                if (windowStarted != DateTime.MinValue)
                {
                    var inWindow = (DateTime.UtcNow - windowStarted).TotalSeconds;
                    if (inWindow >= (_restore && _sink ? RestoreWindowSeconds : _ab ? AbPhases.Length * AbPhaseSeconds + 5 : WindowSeconds)) break;
                    if ((DateTime.UtcNow - lastLog).TotalSeconds >= LogEverySeconds)
                    {
                        lastLog = DateTime.UtcNow;
                        Note("driving: " + inWindow.ToString("F0") + "s, " + Motion(starts));
                    }
                }
                yield return null;
            }

            var metrics = TickMetrics.Take();
            var simWork = FrameProbe.Take();
            CheckFallThrough();
            Note("PROFILE WINDOW END: " + _count + " vehicles drove " + WindowSeconds + "s, " + Motion(starts) +
                 " | fall-through: " + (_fallen.Count == 0 ? "none" : _fallen.Count + " grids: " + string.Join("; ", _fallen.Take(10))) +
                 " | " + metrics.Format() + " | " + simWork);

            foreach (var suspension in _suspensions)
            {
                suspension.PropulsionOverride = 0;
                suspension.SteeringOverride = 0;
            }
            if (_restore && !_sink)
            {
                Check(RestoredCount == restoredBefore, (RestoredCount - restoredBefore) + " grids restored although none fell");
                yield break;
            }
            if (_restore)
            {
                var upright = _vehicles.Count(v => Vector3D.Dot(v[0].WorldMatrix.Up,
                    Vector3D.Normalize(v[0].PositionComp.GetPosition() - _planet.PositionComp.GetPosition())) > 0.7);
                var times = recovered.Where(t => t > 0).OrderBy(t => t).ToList();
                Note("RESTORE RESULT: recovered " + times.Count + "/" + _vehicles.Count + (times.Count == 0 ? "" :
                         " in " + times.First().ToString("F1") + "-" + times.Last().ToString("F1") + " s (median " + times[times.Count / 2].ToString("F1") + " s)") +
                     ", fell again " + fellAgain.Count(x => x) + ", upright " + upright + "/" + _vehicles.Count +
                     ", wheels attached " + _suspensions.Count(s => s.TopGrid != null) + "/" + _suspensions.Count +
                     ", restores by the plugin " + (RestoredCount - restoredBefore));
                Check(times.Count == _vehicles.Count, (_vehicles.Count - times.Count) + " vehicles were not brought back");
                Check(times.Last() <= MaxRecoverySeconds, "slowest recovery took " + times.Last().ToString("F1") + " s");
                Check(!fellAgain.Any(x => x), fellAgain.Count(x => x) + " vehicles fell through again after recovery");
                Check(upright == _vehicles.Count, (_vehicles.Count - upright) + " vehicles are not upright after recovery");
                yield break;
            }
            Check(_fallen.Count == 0, _fallen.Count + " grids fell through the voxels: " + string.Join("; ", _fallen.Take(10)));
        }

        /// <summary>Voxel content (0 air - 255 rock) at a world point.</summary>
        private byte ContentAt(Vector3D point)
        {
            var voxel = Vector3I.Floor(point - _planet.PositionLeftBottomCorner) + _planet.StorageMin;
            _probe.Resize(Vector3I.One);
            _planet.Storage.ReadRange(_probe, VRage.Voxels.MyStorageDataTypeFlags.Content, 0, voxel, voxel);
            return _probe.Content(0);
        }

        /// <summary>
        /// The real ground (edits included) under a point: walks down the local vertical from
        /// GroundSearchM over the generated surface to the first rock.
        /// </summary>
        private Vector3D Ground(Vector3D point)
        {
            var center = _planet.PositionComp.GetPosition();
            var generated = _planet.GetClosestSurfacePointGlobal(ref point);
            var up = Vector3D.Normalize(generated - center);
            for (var h = GroundSearchM; h > -GroundSearchM; h -= GroundStepM)
                if (ContentAt(generated + up * h) >= 128)
                    return generated + up * (h + GroundStepM);
            throw new ScenarioFailedException("no ground within " + GroundSearchM + " m of the surface at " + point);
        }

        /// <summary>
        /// The centre of the flattest lattice-sized patch of generated terrain on a grid around
        /// the authored vehicle (height spread of the sampled surface; edits are local and ignored).
        /// </summary>
        private Vector3D FlattestSite(Vector3D authored, Vector3D east, Vector3D north, int rows)
        {
            var planetCenter = _planet.PositionComp.GetPosition();
            var halfX = (GridsPerRow - 1) / 2.0 * LatticeStep + LatticeStep / 2;
            var halfY = (rows - 1) / 2.0 * LatticeStep + LatticeStep / 2;
            var best = authored + east * LatticeOffset;
            var bestSpread = double.MaxValue;
            for (var cx = -FlatSearchM; cx <= FlatSearchM; cx += FlatSearchStepM)
            for (var cy = -FlatSearchM; cy <= FlatSearchM; cy += FlatSearchStepM)
            {
                var candidate = authored + east * cx + north * cy;
                if ((candidate - authored).Length() < LatticeOffset + Math.Max(halfX, halfY)) continue;
                double min = double.MaxValue, max = double.MinValue;
                for (var x = -halfX; x <= halfX && max - min < bestSpread; x += FlatSampleStepM)
                for (var y = -halfY; y <= halfY; y += FlatSampleStepM)
                {
                    var probe = candidate + east * x + north * y;
                    var radius = (_planet.GetClosestSurfacePointGlobal(ref probe) - planetCenter).Length();
                    min = Math.Min(min, radius);
                    max = Math.Max(max, radius);
                }
                if (max - min >= bestSpread) continue;
                bestSpread = max - min;
                best = candidate;
            }
            return best;
        }

        private static readonly System.Reflection.MethodInfo CreateTopPart = typeof(Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase)
            .GetMethod("CreateTopPartAndAttach", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly object NormalTopSize = CreateTopPart == null ? null
            : Enum.Parse(CreateTopPart.GetParameters()[1].ParameterType, "Normal");

        /// <summary>
        /// Small-grid rover: a 12x6 armor plate, four small batteries on top, and four suspensions of
        /// the scenario's subtype on its long sides, oriented like WHEEL_TEST's (Forward=Down, Up
        /// pointing out of the side; the mirrored subtype on the -Z side). Wheels come from the
        /// suspensions themselves after spawn, as "Add wheel" does in game.
        /// </summary>
        private MyObjectBuilder_CubeGrid RoverOb(string name, MatrixD world)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            MyObjectBuilder_CubeBlock Block(string subtype, int x, int y, int z,
                Base6Directions.Direction forward = Base6Directions.Direction.Forward, Base6Directions.Direction up = Base6Directions.Direction.Up)
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(x, y, z);
                block.BlockOrientation = new SerializableBlockOrientation(forward, up);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                blocks.Add(block);
                return block;
            }
            for (var x = 0; x < 12; x++)
            for (var z = 0; z < 6; z++)
                Block("SmallBlockArmorBlock", x, 0, z);
            for (var x = 4; x < 8; x++)
                Block("SmallBlockSmallBatteryBlock", x, 1, 2);
            foreach (var x in new[] { 1, 10 })
            {
                var left = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_MotorSuspension)Block(_rover + "mirrored", x, 0, -2,
                    Base6Directions.Direction.Down, Base6Directions.Direction.Forward);
                var right = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_MotorSuspension)Block(_rover, x, 0, 6,
                    Base6Directions.Direction.Down, Base6Directions.Direction.Backward);
                foreach (var suspension in new[] { left, right })
                {
                    suspension.SpeedLimit = 1000f;
                    // Lowest wheel position the suspension allows, so small wheels reach under the plate.
                    suspension.Height = -0.32f;
                    suspension.PropulsionOverride = 0;
                    suspension.SteeringOverride = 0;
                }
            }
            return new MyObjectBuilder_CubeGrid
            {
                Name = name,
                DisplayName = name,
                GridSizeEnum = MyCubeSize.Small,
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(world),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
        }

        private static double WheelRadius(MyObjectBuilder_CubeGrid wheel)
        {
            var block = wheel.CubeBlocks[0];
            var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetCubeBlockDefinition(block.GetId());
            var size = definition == null ? Vector3I.One * 5 : definition.Size;
            var cell = wheel.GridSizeEnum == MyCubeSize.Large ? 2.5 : 0.5;
            return Math.Max(size.X, Math.Max(size.Y, size.Z)) * cell / 2;
        }

        private double HeightOverGround(Vector3D point) =>
            Vector3D.Dot(point - Ground(point), Vector3D.Normalize(point - _planet.PositionComp.GetPosition()));

        private void CheckFallThrough()
        {
            for (var v = 0; v < _vehicles.Count; v++)
            for (var g = 0; g < _vehicles[v].Count; g++)
            {
                var grid = _vehicles[v][g];
                if (grid.Closed || grid.MarkedForClose || _fallenIds.Contains(grid.EntityId)) continue;
                var at = grid.PositionComp.WorldAABB.Center;
                // Voxel content is sampled at 1 m, and the centre of a small wheel is a quarter of a
                // metre over the ground; a grid that went through keeps falling, so "under the
                // ground" means rock still at DeepM over the centre.
                var up = Vector3D.Normalize(at - _planet.PositionComp.GetPosition());
                if (ContentAt(at + up * DeepM) < 128) continue;
                _fallenIds.Add(grid.EntityId);
                var what = "vehicle " + v + (g == 0 ? " chassis" : " wheel " + g) + " more than " + DeepM + " m under the ground at " + at.ToString("F1") +
                           ", speed " + (grid.Physics?.LinearVelocity.Length() ?? 0).ToString("F1") + " m/s";
                _fallen.Add(what);
                Note("FELL THROUGH: " + what);
            }
        }

        /// <summary>
        /// Keeps every vehicle near its spawn point: a full-lock circle while inside HomeRadiusM,
        /// otherwise full lock toward home (the side the spawn point is on).
        /// </summary>
        private void SteerHome(Vector3D[] starts)
        {
            var planetCenter = _planet.PositionComp.GetPosition();
            for (var v = 0; v < _vehicles.Count; v++)
            {
                var chassis = _vehicles[v][0];
                if (chassis.Closed) continue;
                var position = chassis.PositionComp.GetPosition();
                var up = Vector3D.Normalize(position - planetCenter);
                var toHome = starts[v] - position;
                toHome -= up * Vector3D.Dot(toHome, up);
                var steer = Steering;
                if (toHome.Length() > HomeRadiusM)
                {
                    // Positive: home is to the right of the chassis' forward direction.
                    var side = Vector3D.Dot(Vector3D.Cross(chassis.WorldMatrix.Forward, toHome), up);
                    steer = side < 0 ? Steering : -Steering;
                }
                var grid = chassis;
                foreach (var suspension in _suspensions)
                    if (suspension.CubeGrid == grid && suspension.SteeringOverride != steer)
                        suspension.SteeringOverride = steer;
            }
        }

        /// <summary>
        /// wheel_fall: a push along the chassis' forward direction (flattened onto the local
        /// horizontal) toward FastSpeedMps, at most FastPushG of the vehicle's weight.
        /// </summary>
        private Vector3D[] _travelLocal;

        /// <summary>
        /// What MyGridPhysics.ConsiderDisablingTOIs does to a grid with recent TOI contacts when the
        /// physics step optimizer is on (a slow server): Debris quality, no continuous collision.
        /// Forced on every vehicle grid, so the run shows what that state does to driving wheels.
        /// </summary>
        private void DropToDebris()
        {
            foreach (var grids in _vehicles)
            foreach (var grid in grids)
            {
                var body = grid.Physics?.RigidBody;
                if (body != null && body.Quality != Havok.HkCollidableQualityType.Debris)
                    body.Quality = Havok.HkCollidableQualityType.Debris;
            }
        }

        /// <summary>
        /// The direction each vehicle drives on its wheels alone, in the chassis frame (WHEEL_TEST
        /// drives along its X axis, and which way depends on the build); the push goes the same way.
        /// </summary>
        private void LearnTravelDirection()
        {
            _travelLocal = _vehicles.Select(grids =>
            {
                var chassis = grids[0];
                var velocity = (Vector3D)(chassis.Physics?.LinearVelocity ?? Vector3.Zero);
                var local = Vector3D.TransformNormal(velocity, MatrixD.Transpose(chassis.WorldMatrix));
                local.Y = 0;
                return local.LengthSquared() > 0.25 ? Vector3D.Normalize(local) : Vector3D.Right;
            }).ToArray();
        }

        /// <summary>
        /// The gyroscopes of a player's rover: roll and pitch rates are cancelled and the chassis is
        /// turned back toward upright; yaw and the wheels are left alone.
        /// </summary>
        private void Stabilize()
        {
            var planetCenter = _planet.PositionComp.GetPosition();
            foreach (var grids in _vehicles)
            {
                var chassis = grids[0];
                var physics = chassis.Physics;
                if (physics == null || chassis.Closed) continue;
                var up = Vector3D.Normalize(chassis.PositionComp.GetPosition() - planetCenter);
                var spin = (Vector3D)physics.AngularVelocity;
                var yaw = up * Vector3D.Dot(spin, up);
                var upright = Vector3D.Cross(chassis.WorldMatrix.Up, up) * StabilizeGain;
                physics.AngularVelocity = (Vector3)(yaw + upright);
            }
        }

        private void Push()
        {
            var planetCenter = _planet.PositionComp.GetPosition();
            foreach (var grids in _vehicles)
            {
                var chassis = grids[0];
                var physics = chassis.Physics;
                if (physics == null || chassis.Closed) continue;
                var position = chassis.PositionComp.GetPosition();
                var up = Vector3D.Normalize(position - planetCenter);
                // Along the way it already goes once it moves (a push across the wheels only fights
                // their side grip); along the learned direction from a standstill.
                var velocity = (Vector3D)physics.LinearVelocity;
                var horizontal = velocity - up * Vector3D.Dot(velocity, up);
                var axis = horizontal.LengthSquared() > 9
                    ? horizontal
                    : Vector3D.TransformNormal(_travelLocal[_vehicles.IndexOf(grids)], chassis.WorldMatrix);
                var forward = axis - up * Vector3D.Dot(axis, up);
                if (forward.LengthSquared() < 0.01) continue;
                forward.Normalize();
                var speed = Vector3D.Dot(physics.LinearVelocity, forward);
                var target = _rover != null ? RoverSpeedMps : FastSpeedMps;
                if (speed >= target) continue;
                double mass = 0;
                foreach (var grid in grids) mass += grid.Physics?.Mass ?? 0;
                var accel = Math.Min((_rover != null ? RoverPushG : FastPushG) * 9.81, (target - speed) * 2.0);
                physics.AddForce(VRage.Game.Components.MyPhysicsForceType.APPLY_WORLD_FORCE, (Vector3)(forward * accel * mass), null, null);
            }
        }

        private readonly Dictionary<Havok.HkRigidBody, (int Limit, bool Callbacks, Havok.HkCollidableQualityType Quality)> _abOriginal =
            new Dictionary<Havok.HkRigidBody, (int, bool, Havok.HkCollidableQualityType)>();

        private void ApplyAbPhase(int phase)
        {
            foreach (var grids in _vehicles)
            foreach (var wheel in grids.Skip(1))
            {
                var body = wheel.Physics?.RigidBody;
                if (body == null) continue;
                if (!_abOriginal.TryGetValue(body, out var original))
                    _abOriginal[body] = original = (body.CallbackLimit, body.ContactPointCallbackEnabled, body.Quality);
                body.CallbackLimit = phase == 1 ? 1 : original.Limit;
                body.ContactPointCallbackEnabled = phase == 2 ? false : original.Callbacks;
                body.Quality = phase == 3 ? Havok.HkCollidableQualityType.Debris : original.Quality;
            }
            Havok.HkCylinderShape.SetNumberOfVirtualSideSegments(phase == 4 ? 8 : 32);
            if (phase == 6) SwapHavokThreads(1);
            if (phase == 7) SwapHavokThreads(15);
            if (phase == 8) SwapHavokThreads(_defaultHavokThreads);
            if (phase == 0) Note("physics setup: " + PhysicsSetup());
        }

        private static int _defaultHavokThreads = 7;
        // Replaced pools stay referenced: their native workers must not be finalized mid-run.
        private static readonly List<object> RetiredPools = new List<object>();

        /// <summary>
        /// A new Havok job thread pool and queue for MyPhysics and every existing Havok world (they
        /// keep the ones given at creation). Between physics steps, on the game thread.
        /// </summary>
        private void SwapHavokThreads(int threads)
        {
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var physicsType = typeof(Sandbox.Engine.Physics.MyPhysics);
            var poolField = physicsType.GetField("m_threadPool", statics);
            var queueField = physicsType.GetField("m_jobQueue", statics);
            var oldPool = (Havok.HkJobThreadPool)poolField.GetValue(null);
            if (RetiredPools.Count == 0) _defaultHavokThreads = oldPool.ThreadCount;
            RetiredPools.Add(oldPool);
            RetiredPools.Add(queueField.GetValue(null));
            var pool = new Havok.HkJobThreadPool(threads);
            var queue = new Havok.HkJobQueue(threads + 1);
            poolField.SetValue(null, pool);
            queueField.SetValue(null, queue);
            var worlds = 0;
            foreach (var world in Sandbox.Engine.Physics.MyPhysics.Clusters.GetList().OfType<Havok.HkWorld>())
            {
                world.InitMultithreading(pool, queue);
                worlds++;
            }
            Note("Havok thread pool " + oldPool.ThreadCount + " -> " + pool.ThreadCount + " threads, " + worlds + " worlds");
        }

        private string PhysicsSetup()
        {
            var pool = typeof(Sandbox.Engine.Physics.MyPhysics).GetField("m_threadPool",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as Havok.HkJobThreadPool;
            var shapes = new List<string>();
            foreach (var wheel in _vehicles.SelectMany(v => v.Skip(1)).Select(g => g.GetFatBlocks().FirstOrDefault()).Where(b => b != null)
                         .GroupBy(b => b.BlockDefinition.Id.SubtypeName).Select(g => g.First()))
            {
                var model = VRage.Game.Models.MyModels.GetModelOnlyData(wheel.BlockDefinition.Model);
                var types = model?.HavokCollisionShapes?.Select(x => x.ShapeType.ToString()) ?? Enumerable.Empty<string>();
                shapes.Add(wheel.BlockDefinition.Id.SubtypeName + ": " + string.Join(",", types));
            }
            return "Havok threads " + (pool?.ThreadCount.ToString() ?? "?") + ", PhysicsIterations " +
                   Sandbox.Game.World.MySession.Static.Settings.PhysicsIterations + ", wheel collision shapes " + string.Join("; ", shapes);
        }

        private static string PhaseSummary(string probe)
        {
            string Pick(string pattern)
            {
                var m = System.Text.RegularExpressions.Regex.Match(probe, pattern);
                return m.Success ? m.Groups[1].Value : "?";
            }
            return "sim-work avg " + Pick(@"sim-work frames=\d+ avg=([\d.]+)ms") + " ms, p99 " + Pick(@"sim-work frames=\d+ avg=[\d.]+ms p50=[\d.]+ p95=[\d.]+ p99=([\d.]+)") +
                   ", physics " + Pick(@"physics=\d+ms/\d+calls\([\d.]+ms each, ([\d.]+)ms/frame") + " ms/frame, entities.before " +
                   Pick(@"entities\.before=\d+ms/\d+calls\([\d.]+ms each, ([\d.]+)ms/frame") + ", wheel.contact " +
                   Pick(@"wheel\.contact=\d+ms/\d+calls\([\d.]+ms each, ([\d.]+)ms/frame");
        }

        private int _lastAttachLog = -1;

        private bool UnderGround(MyCubeGrid grid)
        {
            var at = grid.PositionComp.WorldAABB.Center;
            var up = Vector3D.Normalize(at - _planet.PositionComp.GetPosition());
            return ContentAt(at + up * DeepM) >= 128;
        }

        /// <summary>
        /// A fall-through, made to order: the whole vehicle moved under the ground, rolled
        /// SinkTiltDegrees about its forward axis, and falling. Same steps as MyCubeGrid.Teleport.
        /// </summary>
        private void Sink(List<MyCubeGrid> grids)
        {
            var chassis = grids[0];
            // Around the middle of the chassis: WHEEL_TEST's grid origin is ~10 m under its blocks.
            var pivot = chassis.PositionComp.WorldAABB.Center;
            var up = Vector3D.Normalize(pivot - _planet.PositionComp.GetPosition());
            var depth = HeightOverGround(chassis.PositionComp.WorldAABB.Center) + (_rover != null ? SinkDepthSmallM : SinkDepthLargeM);
            var move = MatrixD.CreateTranslation(-pivot) *
                       MatrixD.CreateFromAxisAngle(chassis.WorldMatrix.Forward, MathHelper.ToRadians(SinkTiltDegrees)) *
                       MatrixD.CreateTranslation(pivot - up * depth);
            var matrices = grids.Select(g => g.WorldMatrix * move).ToList();
            foreach (var g in grids)
                if (g.Physics != null && g.Physics.Enabled) g.Physics.Enabled = false;
            for (var i = 0; i < grids.Count; i++)
            {
                var m = matrices[i];
                grids[i].PositionComp.SetWorldMatrix(ref m, null, false, true, true, true);
            }
            foreach (var g in grids)
            {
                if (g.Physics == null) continue;
                g.Physics.Enabled = true;
                g.Physics.LinearVelocity = (Vector3)(-up * SinkSpeedMps);
            }
        }

        private static Type GameplayPlugin => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SentisGameplayImprovements.SentisGameplayImprovementsPlugin")).FirstOrDefault(t => t != null);

        private static bool AutoRestore
        {
            get
            {
                var config = GameplayPlugin?.GetProperty("Config")?.GetValue(null) ?? throw new ScenarioFailedException("SentisGameplayImprovements is not loaded");
                return (bool)config.GetType().GetProperty("AutoRestoreFromVoxel").GetValue(config);
            }
            set
            {
                var config = GameplayPlugin?.GetProperty("Config")?.GetValue(null) ?? throw new ScenarioFailedException("SentisGameplayImprovements is not loaded");
                config.GetType().GetProperty("AutoRestoreFromVoxel").SetValue(config, value);
            }
        }

        private static int RestoredCount
        {
            get
            {
                var detector = GameplayPlugin?.Assembly.GetType("SentisGameplayImprovements.BackgroundActions.BackgroundActionsProcessor")
                    ?.GetField("FallInVoxelDetector")?.GetValue(null);
                return (int)(detector?.GetType().GetProperty("RestoredCount")?.GetValue(detector) ?? -1);
            }
        }

        private string Motion(Vector3D[] starts)
        {
            var speeds = new List<double>();
            var away = new List<double>();
            var flipped = 0;
            for (var v = 0; v < _vehicles.Count; v++)
            {
                var chassis = _vehicles[v][0];
                if (chassis.Closed) continue;
                speeds.Add(chassis.Physics?.LinearVelocity.Length() ?? 0);
                away.Add((chassis.PositionComp.GetPosition() - starts[v]).Length());
                var up = Vector3D.Normalize(chassis.PositionComp.GetPosition() - _planet.PositionComp.GetPosition());
                if (Vector3D.Dot(chassis.WorldMatrix.Up, up) < 0.3) flipped++;
            }
            speeds.Sort();
            away.Sort();
            var chassisHeights = new List<double>();
            var wheelHeights = new List<double>();
            foreach (var grids in _vehicles)
            {
                if (grids[0].Closed) continue;
                chassisHeights.Add(HeightOverGround(grids[0].PositionComp.WorldAABB.Center));
                foreach (var wheel in grids.Skip(1))
                    if (!wheel.Closed) wheelHeights.Add(HeightOverGround(wheel.PositionComp.WorldAABB.Center));
            }
            chassisHeights.Sort();
            wheelHeights.Sort();
            var debris = 0;
            foreach (var grids in _vehicles)
            foreach (var grid in grids)
                if (grid.Physics?.RigidBody != null && grid.Physics.RigidBody.Quality == Havok.HkCollidableQualityType.Debris)
                    debris++;
            var closest = double.MaxValue;
            for (var a = 0; a < _vehicles.Count; a++)
            for (var b = a + 1; b < _vehicles.Count; b++)
                closest = Math.Min(closest, (_vehicles[a][0].PositionComp.GetPosition() - _vehicles[b][0].PositionComp.GetPosition()).Length());
            return "speed median " + speeds[speeds.Count / 2].ToString("F1") + " m/s (max " + speeds[speeds.Count - 1].ToString("F1") + ", min " + speeds[0].ToString("F1") +
                   "), from start median " + away[away.Count / 2].ToString("F0") + " m / max " + away[away.Count - 1].ToString("F0") +
                   " m, closest two vehicles " + closest.ToString("F0") + " m, over the ground chassis " + (chassisHeights.Count == 0 ? "-" : chassisHeights[chassisHeights.Count / 2].ToString("F2")) +
                   " m / wheels " + (wheelHeights.Count == 0 ? "-" : wheelHeights[wheelHeights.Count / 2].ToString("F2")) +
                   " m (median), active rigid bodies " + _vehicles.Sum(v => v.Count(g => g.Physics?.RigidBody != null && g.Physics.RigidBody.IsActive)) +
                   "/" + _vehicles.Sum(v => v.Count) + ", grids without TOI (Debris) " + debris + ", on the side " + flipped + ", wheels attached " + _suspensions.Count(s => s.TopGrid != null) + "/" + _suspensions.Count +
                   ", grids inside rock " + _fallen.Count;
        }

        private static Vector3D Rotate(Vector3D v, Vector3D from, Vector3D to)
        {
            var axis = Vector3D.Cross(from, to);
            var sin = axis.Length();
            var cos = Vector3D.Dot(from, to);
            if (sin < 1e-12) return v;
            axis /= sin;
            var angle = Math.Atan2(sin, cos);
            return v * Math.Cos(angle) + Vector3D.Cross(axis, v) * Math.Sin(angle) +
                   axis * Vector3D.Dot(axis, v) * (1 - Math.Cos(angle));
        }

        public override void Cleanup()
        {
            try { RestoreRuntimeConfig(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                RestoreRuntimeConfig();
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + GridPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }

        private void RestoreRuntimeConfig()
        {
            if (!_captured) return;
            if (_initialAutoRestore.HasValue) AutoRestore = _initialAutoRestore.Value;
            _initialAutoRestore = null;
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            _captured = false;
        }
    }
}
