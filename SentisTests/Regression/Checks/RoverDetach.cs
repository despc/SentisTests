using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A player in the game's respawn rover drives on over the planet, the rover's cluster attached, then detached by hand
    /// (SentisClusters' <c>!clusters detach</c>), as an admin did on the stand and found the car odd to drive: the detached
    /// cluster at 34-43 ticks a second, 16 ms a tick, the game thread waiting for its steps (28.10.2026). The player dies and
    /// respawns in the rover (the game's own RespawnRover, the player in its seat); the rover drives straight on on its
    /// wheels' propulsion, <see cref="PhaseSeconds"/> attached, as long detached. Each phase: the server's speed and frames,
    /// the rover's speed, the detached cluster's ticks and step, how often and how long the frame's physics and the game's
    /// updates waited for it. Not part of the regression: it detaches a cluster.
    /// </summary>
    public sealed class RoverDetachCheck : RegressionCheck
    {
        public const string ScenarioName = "rover_detach";
        private const double PhaseSeconds = 20;
        // detached it went to 60 ticks a second, 3.5 ms a tick for the first ten seconds, then to 16 ms a tick
        private const double DetachedSeconds = 90;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;
        public override SiteKind Kind => SiteKind.Planet;
        protected override bool NeedsDamage => true;

        private bool? _initialDetach;

        protected override IEnumerator Check()
        {
            SkipUnless(Integrations.IsLoaded("SentisClusters"), "SentisClusters is not loaded");
            SkipUnless(MySession.Static.Settings.EnableRespawnShips, "respawn ships are off in this world");
            yield return Arrive(Site.At(0, 6, -10));
            var body = Body;
            body.DoDamage(1000f, MyDamageType.Suicide, true, attackerId: 0);
            yield return Until(() => body.IsDead ? 1 : 0, v => v > 0, "the player dead", 10);
            Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "OnRespawnRequest", new Sandbox.Game.Multiplayer.MyPlayerCollection.RespawnMsg
            {
                JoinGame = false,
                NewIdentity = false,
                RespawnEntityId = Site.Planet.EntityId,
                RespawnShipId = "RespawnRover",
                PlayerSerialId = 0,
                Color = Color.Red,
            });
            yield return Until(() => Body != null && Body != body && !Body.IsDead && Body.Parent is MyCockpit ? 1 : 0, v => v > 0, "the player in the rover's seat", 60);
            Track(Body);
            var rover = ((MyCockpit)Body.Parent).CubeGrid;
            Track(rover);
            // on its wheels: the rover comes down on its parachutes from over a hundred metres
            yield return Until(() => Over(rover), v => v < 4, "the rover down on the ground", 60);
            yield return Seconds(3);
            var suspensions = MyEntities.GetEntities().OfType<MyCubeGrid>().SelectMany(g => g.GetFatBlocks().OfType<MyMotorSuspension>())
                .Where(s => s.CubeGrid == rover).ToList();
            foreach (var s in suspensions)
                if (s.TopGrid != null) Track(s.TopGrid);
            Check(suspensions.Count > 0, "the rover has no suspensions");
            foreach (var s in suspensions)
            {
                s.Enabled = true;
                s.PropulsionOverride = 1f;
                // as fast as the wheels go: the admin drove 20-25 m/s onto ground the rover had not been on
                s.SpeedLimit = 80f;
                s.SteeringOverride = 0f;
            }
            Note("rover " + rover.DisplayName + ", " + rover.BlocksCount + " blocks, " + suspensions.Count + " wheels, the player in " + Body.Parent.DisplayNameText);
            yield return Seconds(5);
            // straight on, the way it went at first: the ground under it pulled it round in circles of a hundred metres,
            // and the admin drove away onto new ground
            var upAt = Vector3D.Normalize(rover.PositionComp.GetPosition() - Site.Planet.PositionComp.GetPosition());
            var velocity = (Vector3D)(rover.Physics?.LinearVelocity ?? Vector3.Zero);
            _heading = Vector3D.Normalize(velocity - upAt * Vector3D.Dot(velocity, upAt));

            var config = Integrations.TypeOf("SentisClusters", "SentisClusters.SentisClustersPlugin")?.GetProperty("Config")?.GetValue(null);
            var detachSwitch = config?.GetType().GetProperty("Detach");
            _initialDetach = (bool?)detachSwitch?.GetValue(config);
            detachSwitch?.SetValue(config, true);

            var results = new List<string>();
            var cluster = -1;
            var poke = System.Text.RegularExpressions.Regex.IsMatch(TestRunner.Focus ?? "", @"poke=1");
            for (var phase = 0; phase < 2; phase++)
            {
                if (phase == 1)
                {
                    var at = rover.PositionComp.GetPosition();
                    var answer = string.Join(" ", Command($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                    Note("detach: " + answer);
                    Check(answer.Contains("detached"), "the rover's cluster was not detached: " + answer);
                    var m = System.Text.RegularExpressions.Regex.Match(answer, @"Cluster (\d+)");
                    cluster = m.Success ? int.Parse(m.Groups[1].Value) : -1;
                    Sample(cluster, out _, out _, out _);
                }
                var from = rover.PositionComp.GetPosition();
                var frame0 = MySession.Static.GameplayFrameCounter;
                var started = DateTime.UtcNow;
                var havok = Counter("HavokGate", "Waits");
                var havokTicks = Counter("HavokGate", "WaitTicks");
                var step = Counter("StepGate", "Waits");
                var stepTicks = Counter("StepGate", "WaitTicks");
                TickMetrics.Take();
                for (var second = 0; second < (phase == 0 ? PhaseSeconds : DetachedSeconds); second++)
                {
                    for (var f = 0; f < 60; f++)
                    {
                        if (f % 15 == 0) Steer(rover, suspensions);
                        // focus poke=1: the game thread writes the rover's body every frame, as a client's state of what it
                        // drives is applied to it (a fake client sends none)
                        if (poke && phase == 1 && rover.Physics != null) rover.Physics.LinearVelocity = rover.Physics.LinearVelocity;
                        yield return null;
                    }
                    if (second % (phase == 0 ? 4 : 5) != 0) continue;
                    var p = rover.PositionComp.GetPosition();
                    var tick = "";
                    if (phase == 1 && Sample(cluster, out var nowSpeed, out var nowTick, out var nowStep))
                        tick = ", cluster sim " + F(nowSpeed) + ", tick " + F(nowTick) + " ms, step " + F(nowStep) + " ms";
                    Note("  " + (phase == 0 ? "attached" : "detached") + " " + second + " s" + tick + ": speed " + F(rover.Physics?.LinearVelocity.Length() ?? -1) +
                         " m/s, at " + F(Vector3D.Distance(p, from)) + " m from the phase's start, over the surface " + F(Over(rover)) + " m, wheels spin " +
                         string.Join("/", suspensions.Select(s => F(s.TopGrid?.Physics?.AngularVelocity.Length() ?? -1))) +
                         ", wheels' speed " + string.Join("/", suspensions.Select(s => F(s.TopGrid?.Physics?.LinearVelocity.Length() ?? -1))) +
                         ", rover sleeping " + (rover.Physics?.RigidBody?.IsActive == false));
                }
                var seconds = (DateTime.UtcNow - started).TotalSeconds;
                var metrics = TickMetrics.Take();
                double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                var havokWaits = Counter("HavokGate", "Waits") - havok;
                var stepWaits = Counter("StepGate", "Waits") - step;
                var text = (phase == 0 ? "attached" : "detached") + ": server " + F((MySession.Static.GameplayFrameCounter - frame0) / seconds / 60.0) +
                           ", frame avg " + F(metrics.AvgFrameMs) + " ms, p99 " + F(metrics.P99FrameMs) +
                           ", the rover " + F(Vector3D.Distance(rover.PositionComp.GetPosition(), from) / seconds) + " m/s";
                if (phase == 1 && Sample(cluster, out var speed, out var tickMs, out var stepMs))
                    text += "; cluster " + cluster + ": sim " + F(speed) + ", tick " + F(tickMs) + " ms, of it the step " + F(stepMs) + " ms";
                text += "; the frame's physics waited for detached pieces " + havokWaits + " times (" + F(havokWaits > 0 ? Ms(Counter("HavokGate", "WaitTicks") - havokTicks) / havokWaits : 0) +
                        " ms each), the game's updates for detached steps " + stepWaits + " times (" + F(stepWaits > 0 ? Ms(Counter("StepGate", "WaitTicks") - stepTicks) / stepWaits : 0) + " ms each)";
                Note("PHASE " + text);
                results.Add(text);
            }
            var waits = Command("!clusters status").SelectMany(a => a.Split('\n')).Where(l => l.Contains("waited")).Select(l => l.Trim());
            Note("waits: " + string.Join(" | ", waits));
            Result("ROVER DETACH | " + string.Join(" || ", results));
        }

        private Vector3D _heading;
        private double _lastError = double.NaN;
        private float _steerSign = 1f;
        private int _worse;

        /// <summary>Steers the rover back to its heading; the steering's sign is learnt (a correction that makes it worse flips it).</summary>
        private void Steer(MyCubeGrid rover, List<MyMotorSuspension> suspensions)
        {
            var up = Vector3D.Normalize(rover.PositionComp.GetPosition() - Site.Planet.PositionComp.GetPosition());
            var v = (Vector3D)(rover.Physics?.LinearVelocity ?? Vector3.Zero);
            var flat = v - up * Vector3D.Dot(v, up);
            if (flat.Length() < 2 || _heading == Vector3D.Zero) return;
            flat.Normalize();
            var heading = _heading - up * Vector3D.Dot(_heading, up);
            heading.Normalize();
            var error = Math.Atan2(Vector3D.Dot(Vector3D.Cross(flat, heading), up), Vector3D.Dot(flat, heading));
            if (!double.IsNaN(_lastError) && Math.Abs(error) > Math.Abs(_lastError) + 0.05 && Math.Abs(error) > 0.2)
            {
                if (++_worse >= 3) { _steerSign = -_steerSign; _worse = 0; }
            }
            else _worse = 0;
            _lastError = error;
            var steer = (float)Math.Max(-1, Math.Min(1, error * 2)) * _steerSign;
            foreach (var s in suspensions) s.SteeringOverride = steer;
        }

        private double Over(MyCubeGrid rover)
        {
            var planet = Site.Planet;
            var p = rover.PositionComp.GetPosition();
            var surface = planet.GetClosestSurfacePointGlobal(ref p);
            return Vector3D.Dot(p - surface, Vector3D.Normalize(p - planet.PositionComp.GetPosition()));
        }

        private static long Counter(string type, string field) =>
            Convert.ToInt64(Integrations.TypeOf("SentisClusters", "SentisClusters.Run." + type)?.GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? 0L);

        private static bool Sample(int cluster, out double speed, out double tickMs, out double stepMs)
        {
            speed = tickMs = stepMs = 0;
            var method = Integrations.TypeOf("SentisClusters", "SentisClusters.Run.Detacher")?.GetMethod("Sample", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null || cluster < 0) return false;
            var args = new object[] { cluster, 0.0, 0.0, 0.0 };
            var ok = (bool)method.Invoke(null, args);
            speed = (double)args[1];
            tickMs = (double)args[2];
            stepMs = (double)args[3];
            return ok;
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
            try
            {
                Command("!clusters attach all");
                if (_initialDetach.HasValue)
                {
                    var config = Integrations.TypeOf("SentisClusters", "SentisClusters.SentisClustersPlugin")?.GetProperty("Config")?.GetValue(null);
                    config?.GetType().GetProperty("Detach")?.SetValue(config, _initialDetach.Value);
                }
            }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
