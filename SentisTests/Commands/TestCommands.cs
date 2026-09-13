using System;
using System.Linq;
using NLog;
using SentisTests.Core;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRageMath;


namespace SentisTests.Commands
{
    /// <summary>
    /// Registered by Torch under the "test" category: !test list | run &lt;name|all&gt; | stop | status | results | cleanup
    /// </summary>
    [Category("test")]
    public class TestCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [Command("list", "List available SentisTests scenarios")]
        [Permission(MyPromoteLevel.Admin)]
        public void List()
        {
            var names = ScenarioRegistry.Names;
            Context.Respond("scenarios (" + names.Count + "): " + string.Join(", ", names) +
                            Environment.NewLine + "usage: !test run <name> | !test run all | !test stop | !test status | !test cleanup");
        }

        [Command("run", "Run a scenario by name, 'all', or several names: !test run smoke projector_weld")]
        [Permission(MyPromoteLevel.Admin)]
        public void Run(string namesArg)
        {
            var names = (namesArg ?? "")
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0)
            {
                Context.Respond("specify scenario name(s) or 'all'; see !test list");
                return;
            }

            // a live player runs tests from the field: build the structures 30 m in front of his
            // face and keep everything afterwards - removal is an explicit !test cleanup
            var origin = FieldOfViewOrigin();
            var interactive = origin.HasValue;

            try
            {
                if (names.Length == 1 && string.Equals(names[0], "all", StringComparison.OrdinalIgnoreCase))
                {
                    TestRunner.EnqueueAll(origin, autoCleanup: !interactive);
                    Context.Respond("queued all scenarios: " + string.Join(", ", ScenarioRegistry.Names) +
                                    (interactive ? " (at your position; cleanup via !test cleanup)" : ""));
                    return;
                }

                var unknown = names.Where(n => !ScenarioRegistry.Contains(n)).ToList();
                if (unknown.Count > 0)
                {
                    Context.Respond("unknown scenario(s): " + string.Join(", ", unknown) +
                                    "; known: " + string.Join(", ", ScenarioRegistry.Names));
                    return;
                }

                TestRunner.Enqueue(names, origin, autoCleanup: !interactive);
                Context.Respond("queued: " + string.Join(", ", names) +
                                (interactive ? " 30m ahead of you; entities stay until !test cleanup" : "") +
                                (TestRunner.Active != null ? " (after the current " + TestRunner.Active.Name + ")" : ""));
            }
            catch (Exception e)
            {
                Context.Respond("error: " + e.Message);
            }
        }

        [Command("stop", "Abort the running scenario and clear the queue")]
        [Permission(MyPromoteLevel.Admin)]
        public void Stop()
        {
            TestRunner.StopActive("stopped by command");
            Context.Respond("stopped");
        }

        [Command("cleanup", "Remove all test entities left in the world")]
        [Permission(MyPromoteLevel.Admin)]
        public void Cleanup()
        {
            var count = TestRunner.CleanupNow();
            Context.Respond(count + " test entities removed");
        }

        /// <summary>
        /// 30 m in front of the issuing player's character (eye direction), or null when the
        /// command did not come from a live character (server console, AutoRun).
        /// </summary>
        private Vector3D? FieldOfViewOrigin()
        {
            try
            {
                var player = Context?.Player;
                if (player == null || Context.SentBySelf)
                    return null;

                foreach (var entity in Sandbox.Game.Entities.MyEntities.GetEntities())
                {
                    var character = entity as Sandbox.Game.Entities.Character.MyCharacter;
                    if (character == null || character.MarkedForClose)
                        continue;
                    var owner = Sandbox.ModAPI.MyAPIGateway.Players.GetPlayerControllingEntity(character);
                    if (owner == null || owner.IdentityId != player.IdentityId)
                        continue;

                    var eye = character.PositionComp.GetPosition();
                    var dir = character.PositionComp.WorldMatrixRef.Forward;
                    if (dir.LengthSquared() < 0.001)
                        dir = character.PositionComp.WorldMatrixRef.Up;
                    return eye + Vector3D.Normalize(dir) * 30.0;
                }

                return null;
            }
            catch (Exception e)
            {
                Log.Warn("cannot resolve issuer viewpoint: {0}", e.Message);
                return null;
            }
        }

        [Command("status", "Show current scenario state and recent results")]
        [Permission(MyPromoteLevel.Admin)]
        public void Status()
        {
            var active = TestRunner.Active;
            var history = TestRunner.Snapshot();
            var recent = history.Skip(Math.Max(0, history.Count - 5));
            Context.Respond((active == null
                    ? "idle"
                    : "running: " + active.Name + " [" + active.Progress + "] ticks=" + active.TicksRun) +
                Environment.NewLine + string.Join(Environment.NewLine, recent.Select(r => r.ToString())));
        }

        [Command("results", "Show all recorded results of this session")]
        [Permission(MyPromoteLevel.Admin)]
        public void Results()
        {
            var history = TestRunner.Snapshot();
            if (history.Count == 0)
            {
                Context.Respond("no results yet");
                return;
            }

            Context.Respond(string.Join(Environment.NewLine, history.Select(r => r.ToString())));
        }
    }
}
