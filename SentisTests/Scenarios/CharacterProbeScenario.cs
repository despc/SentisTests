using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Game.World;
using SentisTests.Core;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A live player's character on the server, every 10th frame for <c>secs=</c> seconds (60 by default): where it is
    /// and how it moves, what its client's controls brought (move and rotation indicators), its physics (enabled, the
    /// character proxy and whether it stands supported), its support (<c>ClosestParentId</c>), the jetpack, its movement
    /// state, whether its player controls it. For a client seeing its character elsewhere or held in place: the player
    /// does what makes it (<c>!sync reset</c>, a step) meanwhile. Focus <c>name=</c> the player (a part of the name).
    /// </summary>
    public sealed class CharacterProbeScenario : TestScenario
    {
        public const string ScenarioName = "character_probe";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;
        public override bool KeepsNewGrids => true;

        public override IEnumerator Run()
        {
            var focus = TestRunner.Focus ?? "";
            var name = Regex.Match(focus, @"name=(\S+)") is var n && n.Success ? n.Groups[1].Value : "";
            var secs = Regex.Match(focus, @"secs=(\d+)") is var s && s.Success ? int.Parse(s.Groups[1].Value) : 60;
            var player = MySession.Static.Players.GetOnlinePlayers().FirstOrDefault(p => p.IsRealPlayer && p.DisplayName.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
            Check(player != null, "no player online named like '" + name + "'");
            // (found anew each time: a rejoin makes another MyPlayer)
            var steam = player.Id.SteamId;
            Note($"probing {player.DisplayName} for {secs} s, every 10th frame");
            var watch = Stopwatch.StartNew();
            var frame = 0;
            Vector3D? last = null;
            while (watch.Elapsed.TotalSeconds < secs)
            {
                if (frame++ % 10 == 0)
                {
                    player = MySession.Static.Players.GetOnlinePlayers().FirstOrDefault(p => p.Id.SteamId == steam);
                    var c = player?.Character;
                    if (c == null) Note($"{watch.Elapsed.TotalSeconds,6:0.0} s: {(player == null ? "not online" : "no character")}");
                    else
                    {
                        var at = c.PositionComp.GetPosition();
                        var step = last.HasValue ? Vector3D.Distance(at, last.Value) : 0;
                        last = at;
                        var physics = c.Physics;
                        var proxy = physics?.CharacterProxy;
                        Note($"{watch.Elapsed.TotalSeconds,6:0.0} s: at {at.X:0.00} {at.Y:0.00} {at.Z:0.00} (moved {step:0.00} m), velocity {physics?.LinearVelocity.Length() ?? -1:0.00}, " +
                             $"move {c.MoveIndicator}, rotation {c.RotationIndicator}, jetpack {c.JetpackRunning}, state {c.CurrentMovementState}, " +
                             $"physics {(physics == null ? "none" : physics.Enabled ? "on" : "OFF")}, proxy {(proxy == null ? "none" : proxy.Supported ? "supported" : "not supported")}, " +
                             $"support {c.ClosestParentId}, parent {c.Parent?.DisplayName ?? "-"}, controlled by its player {player.Controller.ControlledEntity == c}, the server's control table {MySession.Static.Players.GetControllingPlayer(c)?.DisplayName ?? "NOBODY"}, dead {c.IsDead}");
                    }
                }
                yield return null;
            }
            Note("CHARACTER PROBE RESULT | done");
        }
    }
}
