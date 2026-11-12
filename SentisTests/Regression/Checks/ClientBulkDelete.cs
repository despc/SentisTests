using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The client plugin SentisClientOptimisation's fix of a big grid's deleting (<c>BulkChildDelete</c>: its blocks taken
    /// off the end of the grid's list of children and unsubscribed from the grid's container at once), tried on the
    /// server - the code of <c>MyEntity.Delete</c> is the same. Grids of 3000 lights: two deleted as the game
    /// does it, two with the fix patched in for the check alone. With the fix every block is closed, none is left
    /// in the grid's children or among the subscribers of the grid's container, and it takes less (the rest - the grid's systems, the blocks' own closing - is the game's).
    /// Skipped when the plugin is not built.
    /// </summary>
    public sealed class ClientBulkDeleteCheck : RegressionCheck
    {
        public override string Name => Prefix + "client_bulk_delete";

        private const string HarmonyId = "SentisClientOptimisation.stand-check";

        private static readonly string Dll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"sources\SentisClientOptimisation\SentisClientOptimisation\bin\Release\SentisClientOptimisation.dll");

        private readonly SessionSettingsOverride _settings = new SessionSettingsOverride();
        private object _harmony;

        protected override IEnumerator Check()
        {
            if (!File.Exists(Dll)) throw new Core.ScenarioSkippedException("SentisClientOptimisation is not built: " + Dll);
            var harmonyType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HarmonyLib.Harmony", false)).FirstOrDefault(t => t != null);
            if (harmonyType == null) throw new Core.ScenarioSkippedException("no Harmony loaded");
            var plugin = Assembly.LoadFrom(Dll);
            var fix = plugin.GetType("SentisClientOptimisation.BulkChildDelete", true);
            _settings.Set("TrashRemovalEnabled", false);

            MyCubeGrid Lights(string name, double off)
            {
                var parts = new List<Part>();
                for (var x = 0; x < 30; x++)
                for (var y = 0; y < 10; y++)
                for (var z = 0; z < 10; z++)
                    parts.Add(new Part(x == 0 && y == 0 && z == 0 ? "BatteryBlock/LargeBlockBatteryBlock" : "InteriorLight/SmallLight", x, y, z));
                return Build(name, Site.Origin + Site.Forward * off, true, parts);
            }
            double Delete(MyCubeGrid grid, out List<MyEntity> blocks)
            {
                blocks = grid.GetFatBlocks().Cast<MyEntity>().ToList();
                var clock = Stopwatch.StartNew();
                grid.Close();
                MyEntities.DeleteRememberedEntities();
                return clock.Elapsed.TotalMilliseconds;
            }

            MyCubeGrid Small(string name, double off) => Build(name, Site.Origin + Site.Forward * off, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0), new Part("InteriorLight/SmallLight", 0, 1, 0));
            // two of each, the faster of the two taken: the frame's other load (the checks round) varies
            var vanilla = Lights("bulk-vanilla", 0);
            var vanilla2 = Lights("bulk-vanilla2", 180);
            var patched = Lights("bulk-fix", 60);
            var patched2 = Lights("bulk-fix2", 240);
            var warm = Small("bulk-warm", 120);
            var small = Small("bulk-small", 130);
            yield return Seconds(1);
            var fatBlocks = patched.GetFatBlocks().Count;
            var subscribersBefore = Subscribers(patched);
            Note($"{fatBlocks} blocks with entities a grid; the grid's container has {subscribersBefore} subscribers of ComponentAdded");
            Check(fatBlocks >= 2990 && subscribersBefore >= fatBlocks, $"the grid has {fatBlocks} blocks with entities, {subscribersBefore} subscribers");

            // the methods compiled before either is timed
            Delete(warm, out _);
            var vanillaMs = Delete(vanilla, out var vanillaBlocks);
            yield return null;
            vanillaMs = Math.Min(vanillaMs, Delete(vanilla2, out _));
            Check(vanilla.Closed && vanillaBlocks.All(b => b.Closed), "the game's deleting left blocks");

            _harmony = Activator.CreateInstance(harmonyType, HarmonyId);
            fix.GetMethod("Patch", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { _harmony });
            long Count(string field) => (long)fix.GetField(field, BindingFlags.Public | BindingFlags.Static).GetValue(null);
            Delete(small, out var smallBlocks);
            Check(small.Closed && smallBlocks.All(b => b.Closed), "a small grid with the fix in was not deleted");
            var bulk0 = Count("Bulk");
            var end0 = Count("FromEnd");
            var container = patched.Components;
            var hierarchy = patched.Hierarchy;
            var fixMs = Delete(patched, out var fixBlocks);
            var bulk = Count("Bulk") - bulk0;
            var fromEnd = Count("FromEnd") - end0;
            var left = Subscribers(container);
            yield return null;
            fixMs = Math.Min(fixMs, Delete(patched2, out _));
            Note($"deleting 3000 lights: the game's {vanillaMs:0.0} ms, with the fix {fixMs:0.0} ms; deleted at once {bulk}, taken off the end {fromEnd}; subscribers left {left}, children left {hierarchy.Children.Count}");
            Check(patched.Closed && fixBlocks.All(b => b.Closed), "with the fix blocks were left: " + fixBlocks.Count(b => !b.Closed));
            Check(bulk >= 1 && fromEnd >= fatBlocks, $"the fix did not take the blocks: at once {bulk}, off the end {fromEnd}");
            Check(hierarchy.Children.Count == 0, $"{hierarchy.Children.Count} children are left in the grid's list");
            Check(left < subscribersBefore - fatBlocks + 1, $"the blocks' subscriptions are left: {left} of {subscribersBefore}");
            // the rest of a grid's deleting (its systems, its blocks' own closing) is the game's
            Check(fixMs < vanillaMs * 0.9, $"with the fix {fixMs:0.0} ms, the game's {vanillaMs:0.0} ms");
            Result($"3000 lights deleted in {fixMs:0.0} ms with the fix, {vanillaMs:0.0} ms as the game does it; every block closed, none left among the children or subscribers");
        }

        private static int Subscribers(object entityOrContainer)
        {
            var container = entityOrContainer is MyEntity entity ? entity.Components : entityOrContainer;
            var field = typeof(VRage.Game.Components.MyEntityComponentContainer).GetField("ComponentAdded", BindingFlags.Instance | BindingFlags.NonPublic);
            return (field?.GetValue(container) as Delegate)?.GetInvocationList().Length ?? 0;
        }

        public override void Cleanup()
        {
            try { _harmony?.GetType().GetMethod("UnpatchAll", new[] { typeof(string) })?.Invoke(_harmony, new object[] { HarmonyId }); }
            catch (Exception) { }
            try { _settings.Restore(); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
