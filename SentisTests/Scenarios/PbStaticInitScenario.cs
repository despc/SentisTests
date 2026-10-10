using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Scripts with static initialization, compiled the way SentisOptimisations does it (PbCompile: off the game thread,
    /// the methods prepared to machine code there). Preparing a method can run its type's static initializer, and the game
    /// puts its instruction counter into a script's static constructor: run off the game thread and outside a run it threw,
    /// and a type initializer that threw throws for good - "The type initializer for 'Settings' threw an exception" at every
    /// run of the script, and at every recompile (PR #2, AutomaticSystemBase).
    ///
    /// One programmable block for each kind of static initialization; each must run and echo what it computed, without an
    /// exception, and again after a recompile:
    ///  - "minimal": the PR's script - a class with a static constructor and a method, and one with a field initializer;
    ///  - "settings": AutomaticSystemBase's Settings: <c>static Settings() { Ini = new MyIni(); }</c>, used by Main;
    ///  - "components": a field initializer that makes the script's own objects (<c>new MyMinItem(50)</c>);
    ///  - "reads-ctor": a field initializer that reads the statics of a class with a static constructor;
    ///  - "program-statics": the Program itself with <c>static readonly string[]</c> and a list (no script code in them).
    /// </summary>
    public sealed class PbStaticInitScenario : TestScenario
    {
        public const string ScenarioName = "pb_static_init";
        private const string Prefix = "pb-static-";
        private const double OffsetM = 900;
        private const double RunSeconds = 8;

        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        private static readonly (string Name, string Script, string[] Expected)[] Scripts =
        {
            ("minimal", @"
static class WithStaticCtor
{
    public static int Value;
    static WithStaticCtor() { Value = 1; }
    public static int Get() { return Value; }
}
static class WithFieldInit
{
    public static int Value = 1;
    public static int Get() { return Value; }
}
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; }
public void Main(string argument, UpdateType updateSource)
{
    Show(""WithStaticCtor"", () => WithStaticCtor.Get());
    Show(""WithFieldInit"", () => WithFieldInit.Get());
}
void Show(string name, Func<int> get)
{
    try { Echo(name + "" = "" + get()); }
    catch (Exception e) { Echo(name + "": "" + e.GetType().Name); }
}
", new[] { "WithStaticCtor = 1", "WithFieldInit = 1" }),

            ("settings", @"
static public class Settings
{
    static public MyIni Ini;
    static public MyIniParseResult ResultMessage;
    static Settings() { Ini = new MyIni(); }
    static public bool ParceSetting(string data)
    {
        Ini.Clear();
        return Ini.TryParse(data, out ResultMessage);
    }
}
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; }
public void Main(string argument, UpdateType updateSource)
{
    var ok = Settings.ParceSetting(""[main]\nvalue=7"");
    Echo(""settings "" + ok + "" "" + Settings.Ini.Get(""main"", ""value"").ToInt32());
}
", new[] { "settings True 7" }),

            ("components", @"
class MyMinItem
{
    public int Min;
    public MyMinItem(int min) { Min = min; }
}
static class Stock
{
    static internal SortedDictionary<string, MyMinItem> Components = new SortedDictionary<string, MyMinItem>()
    {
        [""BulletproofGlass""] = new MyMinItem(50),
        [""Motor""] = new MyMinItem(150),
    };
}
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; }
public void Main(string argument, UpdateType updateSource)
{
    Echo(""components "" + Stock.Components.Count + "" "" + Stock.Components[""Motor""].Min);
}
", new[] { "components 2 150" }),

            ("reads-ctor", @"
static class WithCtor
{
    public static int Value;
    static WithCtor() { Value = 7; }
}
static class ReadsIt
{
    public static int Copy = WithCtor.Value;
    public static int Get() { return Copy; }
}
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; }
public void Main(string argument, UpdateType updateSource)
{
    Echo(""copy "" + ReadsIt.Get());
}
", new[] { "copy 7" }),

            ("program-statics", @"
static readonly string[] Names = { ""a"", ""b"" };
static readonly List<int> Seen = new List<int>();
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; }
public void Main(string argument, UpdateType updateSource)
{
    Seen.Add(Names.Length);
    Echo(""names "" + Names.Length + "" seen "" + (Seen.Count > 0));
}
", new[] { "names 2 seen True" }),
        };

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(MySession.Static.EnableIngameScripts, "in-game scripts are disabled in this world");
            _config.Set("EnableScriptsPunish", false);
            _config.Set("FreezerEnabled", false);
            // PbCompile's line "N methods to machine code": how much of each script was prepared ahead
            _config.Set("DiagnosticLogs", true);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var station = BuildStation(anchorM.Translation + up * OffsetM, up);
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, i => (anchorM.Translation + up * (OffsetM + 20), 0, 0), withCharacters: true);

            var blocks = WorldApi.FindFunctionals<MyProgrammableBlock>(station);
            Check(blocks.Count == Scripts.Length, $"{blocks.Count} programmable blocks of {Scripts.Length}");
            MyProgrammableBlock Block(string name) => blocks.First(b => b.CustomName.ToString() == WorldApi.EntityPrefix + Prefix + name);

            var first = WaitForSeconds(RunSeconds, "the scripts compile and run");
            while (first.MoveNext()) yield return first.Current;
            var failures = Verify("first compile", Block);

            // recompiled, as a player does from the terminal: a type initializer that threw stayed broken in the old
            // assembly only, the new one went the same way
            var recompile = typeof(MyProgrammableBlock).GetMethod("Recompile", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(recompile != null, "MyProgrammableBlock.Recompile not found");
            foreach (var block in blocks) recompile.Invoke(block, new object[] { true });
            var second = WaitForSeconds(RunSeconds, "the scripts compile and run again");
            while (second.MoveNext()) yield return second.Current;
            failures.AddRange(Verify("recompiled", Block));

            Check(failures.Count == 0, string.Join("; ", failures));
        }

        private List<string> Verify(string when, Func<string, MyProgrammableBlock> block)
        {
            var failures = new List<string>();
            var echoField = typeof(MyProgrammableBlock).GetField("m_echoOutput", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var (name, _, expected) in Scripts)
            {
                var pb = block(name);
                var echo = (echoField?.GetValue(pb) as StringBuilder)?.ToString() ?? "";
                var info = pb.DetailedInfo.ToString();
                var shown = (echo + " " + info).Replace("\r", "").Replace("\n", " | ").Trim();
                Note($"{when}: {name}: working {pb.IsWorking}; {shown}");
                if (info.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 || echo.IndexOf("Exception", StringComparison.Ordinal) >= 0)
                    failures.Add($"{when}: {name} threw: {shown}");
                foreach (var line in expected)
                    if (!echo.Contains(line) && !info.Contains(line))
                        failures.Add($"{when}: {name} did not echo '{line}': {shown}");
            }
            return failures;
        }

        private MyCubeGrid BuildStation(Vector3D position, Vector3D up)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            void Add(MyObjectBuilder_CubeBlock block, Vector3I min)
            {
                block.Min = new SerializableVector3I(min.X, min.Y, min.Z);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                blocks.Add(block);
            }
            for (var x = 0; x < Scripts.Length; x++)
            {
                Add(WorldApi.MakeBlockOb("LargeBlockArmorBlock"), new Vector3I(x, 0, 0));
                Add(WorldApi.MakeBlockOb("LargeBlockBatteryBlock"), new Vector3I(x, 0, 1));
                var pb = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_MyProgrammableBlock)WorldApi.MakeBlockOb("LargeProgrammableBlock");
                pb.CustomName = WorldApi.EntityPrefix + Prefix + Scripts[x].Name;
                pb.Program = Scripts[x].Script;
                pb.Enabled = true;
                Add(pb, new Vector3I(x, 1, 0));
            }
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "station",
                DisplayName = WorldApi.EntityPrefix + Prefix + "station",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(
                    MatrixD.CreateWorld(position, Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up)), up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var station = WorldApi.SpawnGrid(ob);
            Track(station);
            WorldApi.ChargeBatteries(station);
            return station;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
