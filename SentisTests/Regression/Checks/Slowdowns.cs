using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisOptimisations' slowdowns, held on for the check alone ("always"; the suite keeps them off for the other
    /// checks, and runs this one only when named: run_regression.py slowdowns): a programmable block with a script on
    /// Update1 and Update10 and a turret with nothing to shoot. With the slowdowns on and no player within 5 km (made so
    /// for the scripts' slowdown) the script gets Update1 on a third of the frames and a third of its Update10 runs;
    /// with a player near (the real lookup: the check's player stands by the grid) Update1 on every frame and Update10
    /// still a third; off again, all of both. The idle turret's updates are skipped. Skipped without the slowdowns or
    /// in-game scripts.
    /// </summary>
    public sealed class SlowdownsCheck : RegressionCheck
    {
        public override string Name => Prefix + "slowdowns";

        // the slowdowns it holds on would slow down the checks running beside it: only when asked by name
        public override bool OnlyWhenAsked => true;

        private const string Script = @"
long one, ten;
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update10; }
public void Main(string argument, UpdateType update)
{
    if ((update & UpdateType.Update1) != 0) one++;
    if ((update & UpdateType.Update10) != 0) ten++;
    Me.CustomData = one + "" "" + ten;
}";

        private const double Window = 10;

        private Type _peak;
        private FieldInfo _configField, _nearField;
        private object _savedConfig, _savedNear;

        private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        protected override IEnumerator Check()
        {
            _peak = Find("SentisOptimisations.Slowdowns.PeakLoad");
            if (_peak == null) throw new Core.ScenarioSkippedException("SentisOptimisations' slowdowns are not loaded");
            SkipUnless(MySession.Static.EnableIngameScripts, "in-game scripts are off in this world");
            _configField = _peak.GetField("Config", BindingFlags.Public | BindingFlags.Static);
            var settingsType = _peak.GetNestedType("Settings");
            _nearField = Find("SentisOptimisations.Slowdowns.ScriptSlowdown")?.GetField("PlayerNear", BindingFlags.Public | BindingFlags.Static);
            Check(_configField != null && settingsType != null && _nearField != null, "the slowdowns are not what this check knows");
            var idle = Find("SentisOptimisations.Slowdowns.IdleTurretSlowdown");
            long Skipped() => idle == null ? 0 : (long)idle.GetField("Skipped").GetValue(null);
            bool Active() => (bool)_peak.GetProperty("Active").GetValue(null);
            bool Busy() => (bool)_peak.GetProperty("Busy").GetValue(null);
            void Hold(bool on)
            {
                var settings = Activator.CreateInstance(settingsType);
                settingsType.GetField("Enabled").SetValue(settings, on);
                settingsType.GetField("Always").SetValue(settings, on);
                // a Func<Settings> made of a constant
                _configField.SetValue(null, Expression.Lambda(_configField.FieldType, Expression.Constant(settings, settingsType)).Compile());
                _peak.GetMethod("Second").Invoke(null, new object[] { 0.0, 1.0, settings });
            }
            // no player near any block, or the real lookup
            void NoPlayers(bool none) => _nearField.SetValue(null, none
                ? Expression.Lambda(_nearField.FieldType, Expression.Constant(false), Expression.Parameter(typeof(VRageMath.Vector3D))).Compile()
                : _savedNear);

            _savedConfig = _configField.GetValue(null);
            _savedNear = _nearField.GetValue(null);
            // a player by the grid: the freezer stops a grid nobody is near, and its script with it
            yield return Arrive(Site.Origin + Site.Up * 3 + Site.Forward * 6);
            var grid = Build("slowdowns", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => ((MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", 1, 0, 0).With(b => ((MyObjectBuilder_MyProgrammableBlock)b).Program = Script),
                new Part("LargeGatlingTurret/", 0, 1, 0));
            var pb = Block<MyProgrammableBlock>(grid);
            (long one, long ten) Runs()
            {
                var parts = (pb.CustomData ?? "").Split(' ');
                return parts.Length == 2 && long.TryParse(parts[0], out var a) && long.TryParse(parts[1], out var b) ? (a, b) : (0, 0);
            }
            yield return Seconds(2);
            Check(Runs().one > 0, "the script does not run: " + pb.CustomData);

            // the share of the frames the script got Update1 in, and of its Update10 runs (one in 10 frames), over a window
            (long one, long ten) start = default;
            long frame0 = 0;
            void Begin()
            {
                start = Runs();
                frame0 = MySession.Static.GameplayFrameCounter;
            }
            (double one, double ten) Shares()
            {
                var now = Runs();
                double frames = MySession.Static.GameplayFrameCounter - frame0;
                return ((now.one - start.one) / frames, (now.ten - start.ten) * 10.0 / frames);
            }

            Hold(true);
            Check(Active() && Busy(), "the slowdowns did not come on");
            NoPlayers(true);
            var skipped0 = Skipped();
            Begin();
            yield return Seconds(Window);
            var far = Shares();
            var turretSkipped = Skipped() - skipped0;

            NoPlayers(false);
            Begin();
            yield return Seconds(Window);
            var near = Shares();

            Hold(false);
            Check(!Active() && !Busy(), "the slowdowns did not go off");
            Begin();
            yield return Seconds(Window);
            var off = Shares();

            var line = $"Update1 a frame / Update10 of its runs: no player near {far.one:F2} / {far.ten:F2}, a player near {near.one:F2} / {near.ten:F2}, " +
                       $"without the slowdowns {off.one:F2} / {off.ten:F2}; idle turret updates skipped {turretSkipped}";
            Note(line);
            Check(far.one > 0.27 && far.one < 0.4, $"with no player near the script got Update1 on {far.one:F2} of the frames (a third is its share)");
            Check(far.ten > 0.2 && far.ten < 0.47, $"with no player near the script got {far.ten:F2} of its Update10 runs (a third is its share)");
            Check(near.one > 0.9, $"with a player near the script got Update1 on {near.one:F2} of the frames (all is its share)");
            Check(near.ten > 0.2 && near.ten < 0.47, $"with a player near the script got {near.ten:F2} of its Update10 runs (a third is its share)");
            Check(off.one > 0.9 && off.ten > 0.85, $"without the slowdowns the script got Update1 on {off.one:F2} of the frames and {off.ten:F2} of its Update10 runs");
            Check(idle == null || turretSkipped > 50, $"the idle turret's updates were not skipped: {turretSkipped}");
            Result(line);
        }

        public override void Cleanup()
        {
            try
            {
                if (_nearField != null && _savedNear != null) _nearField.SetValue(null, _savedNear);
                if (_configField != null && _savedConfig != null)
                {
                    _configField.SetValue(null, _savedConfig);
                    // back to what the settings say at once
                    var settings = _peak.GetNestedType("Settings").GetMethod("FromPlugin").Invoke(null, null);
                    _peak.GetMethod("Second").Invoke(null, new[] { (object)0.0, 1.0, settings });
                }
            }
            catch (Exception)
            {
            }
            base.Cleanup();
        }
    }
}
