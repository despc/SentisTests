using System;
using System.Linq;
using NLog;
using SentisTests.Core;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;


namespace SentisTests.Commands
{
    /// <summary>
    /// Registered by Torch as module "test": /test list | run &lt;name|all&gt; | stop | status | results
    /// </summary>
    public class TestCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [Command("list", "List available SentisTests scenarios")]
        [Permission(MyPromoteLevel.Admin)]
        public void List()
        {
            var names = ScenarioRegistry.Names;
            Context.Respond("scenarios (" + names.Count + "): " + string.Join(", ", names) +
                            Environment.NewLine + "usage: /test run <name> | /test run all | /test stop | /test status");
        }

        [Command("run", "Run a scenario by name, 'all', or several names: !run smoke projector_weld")]
        [Permission(MyPromoteLevel.Admin)]
        public void Run(string namesArg)
        {
            var names = (namesArg ?? "")
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0)
            {
                Context.Respond("specify scenario name(s) or 'all'; see /test list");
                return;
            }

            try
            {
                if (names.Length == 1 && string.Equals(names[0], "all", StringComparison.OrdinalIgnoreCase))
                {
                    TestRunner.EnqueueAll();
                    Context.Respond("queued all scenarios: " + string.Join(", ", ScenarioRegistry.Names));
                    return;
                }

                var unknown = names.Where(n => !ScenarioRegistry.Contains(n)).ToList();
                if (unknown.Count > 0)
                {
                    Context.Respond("unknown scenario(s): " + string.Join(", ", unknown) +
                                    "; known: " + string.Join(", ", ScenarioRegistry.Names));
                    return;
                }

                TestRunner.Enqueue(names);
                Context.Respond("queued: " + string.Join(", ", names) +
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
