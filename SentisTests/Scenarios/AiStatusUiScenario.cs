using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using SentisTests.Core;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisAi's page in Torch shows the bots' statuses, refreshed by a timer on the window's thread. The statuses look
    /// at the world (a turret's field of fire is rays cast in Havok), and asked for on that thread they took Torch down
    /// the moment the page was opened: an access violation in Havok.dll, no log, no dump (05.10.2026). The scenario
    /// makes the page on a WPF thread of its own, as Torch does, asks it for the statuses three times and lets its
    /// dispatcher run: the text must come, and the server must still run.
    /// </summary>
    public sealed class AiStatusUiScenario : TestScenario
    {
        public const string ScenarioName = "ai_status_ui";

        /// <summary>Where the page's picture goes.</summary>
        public static string PicturePath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sentisai_page.png");

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;

        public override IEnumerator Run()
        {
            var page = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisAi.GUI.ConfigGUI", false)).FirstOrDefault(t => t != null);
            Check(page != null, "no SentisAi.GUI.ConfigGUI");
            var update = page.GetMethod("UpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic);
            var status = page.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(update != null && status != null, "the page has no UpdateStatus or _status");

            string text = null;
            Exception error = null;
            var done = false;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var control = Activator.CreateInstance(page);
                    for (var i = 0; i < 3; i++)
                    {
                        update.Invoke(control, null);
                        // the window's message loop for two seconds: what the game thread sends back is shown
                        var frame = new DispatcherFrame();
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
                        timer.Start();
                        Dispatcher.PushFrame(frame);
                    }
                    text = ((TextBlock)status.GetValue(control)).Text;
                    // the page as Torch would draw it, into a picture next to the logs: what the bots' table looks like
                    var element = (System.Windows.FrameworkElement)control;
                    element.Measure(new System.Windows.Size(1150, 700));
                    element.Arrange(new System.Windows.Rect(0, 0, 1150, 700));
                    element.UpdateLayout();
                    var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(1150, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    var back = new System.Windows.Media.DrawingVisual();
                    using (var g = back.RenderOpen()) g.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, 1150, 700));
                    picture.Render(back);
                    picture.Render(element);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
                    using (var file = System.IO.File.Create(PicturePath)) encoder.Save(file);
                }
                catch (Exception e) { error = e.InnerException ?? e; }
                finally { done = true; }
            }) { IsBackground = true, Name = "ai_status_ui" };
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            for (var k = 0; k < 30 * 60 && !done; k++) yield return null;
            Check(done, "the page did not answer in 30 s");
            Check(error == null, "the page threw: " + error);
            Note($"the page shows {text?.Length ?? 0} characters: {(text ?? "").Split('\n').FirstOrDefault()}");
            Check(!string.IsNullOrEmpty(text) && text.StartsWith("Ботов в игре"), "the page shows no statuses: " + text);
            Note("the page's picture: " + PicturePath);
            yield return WaitForTicks(3 * 60);
            Note("the server still runs");
        }
    }
}
