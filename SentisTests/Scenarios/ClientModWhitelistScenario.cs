using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SentisTests.Core;
using VRage.Library.Compiler;
using VRage.Scripting;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The client-only mods of the Sentis plugins compile as the game compiles a mod: the server's own script compiler
    /// with the mod whitelist (<c>MyApiTarget.Mod</c>). The server never loads these mods, so a call the whitelist forbids
    /// would show only on a client, as a mod that did not load. The folders are <see cref="Folders"/> (or the
    /// <c>focus</c> of the test call, a folder of <c>.cs</c> files). Passes when every one compiles with no error, and a
    /// script reading a file does not (the whitelist is on).
    /// </summary>
    public sealed class ClientModWhitelistScenario : TestScenario
    {
        public const string ScenarioName = "client_mod_whitelist";

        private static readonly string[] Folders =
        {
            @"C:\Users\user\sources\SentisGameplayImprovements\ClientMods\GridPrediction\Data\Scripts",
            @"C:\Users\user\sources\SentisRender\ClientMod\Data\Scripts",
        };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            var folders = TestRunner.Focus != null ? new[] { TestRunner.Focus } : Folders.Where(Directory.Exists).ToArray();
            Check(folders.Length > 0, "no client mod folder found");
            // the whitelist is on: a mod that reads a file does not compile
            var probe = new List<Message>();
            var forbidden = MyScriptCompiler.Static.Compile(MyApiTarget.Mod, "client_mod_check_forbidden",
                new[] { new Script("Forbidden.cs", "namespace P { class C { string R() { return System.IO.File.ReadAllText(\"x\"); } } }") },
                probe, "client_mod_whitelist");
            var w = 0;
            while (!forbidden.IsCompleted && w++ < 60 * 60) yield return null;
            Check(forbidden.IsCompleted && forbidden.Result == null && probe.Any(m => m.IsError),
                "a mod reading a file compiled: the server's compiler has no mod whitelist, the check means nothing");
            Note("WHITELIST ON | " + probe.Where(m => m.IsError).Select(m => m.Text).FirstOrDefault());

            var failed = new List<string>();
            foreach (var folder in folders)
            {
                var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);
                Check(files.Length > 0, "no scripts in " + folder);
                var scripts = files.Select(f => new Script(Path.GetFileName(f), File.ReadAllText(f))).ToList();
                var messages = new List<Message>();
                var compile = MyScriptCompiler.Static.Compile(MyApiTarget.Mod, "client_mod_check_" + Path.GetFileName(Path.GetDirectoryName(folder)),
                    scripts, messages, "client_mod_whitelist");
                var waited = 0;
                while (!compile.IsCompleted && waited++ < 60 * 60) yield return null;
                Check(compile.IsCompleted, "the compile of " + folder + " did not end in a minute");
                var errors = messages.Where(m => m.IsError).Select(m => m.Text).ToList();
                var ok = compile.Exception == null && compile.Result != null && errors.Count == 0;
                Note($"CLIENT MOD {(ok ? "OK" : "FAILED")} | {folder} | {files.Length} files" +
                     (errors.Count > 0 ? " | " + string.Join(" | ", errors) : "") +
                     (compile.Exception != null ? " | " + compile.Exception.GetBaseException().Message : ""));
                if (!ok) failed.Add(folder);
            }
            Check(failed.Count == 0, "does not compile as a mod: " + string.Join(", ", failed));
        }
    }
}
