using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;

class NavigationTests
{
    [STAThread] static void Main()
    {
        using (var worker = new Bridge.NavigationWorker())
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var finished = new ManualResetEvent(false))
        {
            ApartmentState apartment = ApartmentState.Unknown;
            int order = 0;
            worker.Post(delegate { apartment = Thread.CurrentThread.GetApartmentState(); started.Set(); release.WaitOne(); order = 1; });
            if (!started.WaitOne(3000)) throw new Exception("Navigation worker did not start independently of UI message loop");
            worker.Post(delegate { if (order == 1) order = 2; finished.Set(); });
            if (finished.WaitOne(50)) throw new Exception("Navigation keys ran out of order");
            release.Set();
            if (!finished.WaitOne(3000) || order != 2 || apartment != ApartmentState.MTA) throw new Exception("Navigation must execute serially on an MTA worker");
            Console.WriteLine("PASS: slow navigation leaves caller responsive; keys retain order on an MTA worker.");
        }
        var content = new StackPanel();
        for (int i = 0; i < 80; i++) content.Children.Add(new TextBlock { Text = "Conversation line " + i, Height = 35 });
        var conversation = new ScrollViewer { Content = content, Width = 650, Height = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(conversation, "conversation");
        var outerContent = new StackPanel { Width = 1100 };
        outerContent.Children.Add(conversation);
        var outer = new ScrollViewer { Content = outerContent, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(outer, "horizontal-shell");
        var window = new Window { Title = "T1Vibe navigation regression fixture", Width = 1000, Height = 700, Content = outer, ShowInTaskbar = false };
        Exception failure = null;
        window.ContentRendered += delegate
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            var worker = new Thread(delegate()
            {
                try
                {
                    var root = AutomationElement.FromHandle(handle);
                    var scroller = Bridge.FindConversationScroller(root);
                    if (scroller == null || scroller.Current.AutomationId != "conversation")
                        throw new Exception("Up/Down targets " + (scroller == null ? "no scroller" : scroller.Current.AutomationId) + " instead of the vertically scrollable conversation");
                    var pattern = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
                    double before = pattern.Current.VerticalScrollPercent;
                    pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                    if (pattern.Current.VerticalScrollPercent <= before) throw new Exception("Down did not scroll conversation");
                    pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallDecrement);
                    if (pattern.Current.VerticalScrollPercent >= before + 0.01) throw new Exception("Up did not scroll conversation back");
                    Console.WriteLine("PASS: nested horizontal shell excluded; conversation scrolls down and up through UI Automation.");
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Dispatcher.BeginInvoke(new Action(window.Close)); }
            });
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        };
        new Application().Run(window);
        if (failure != null) { Console.Error.WriteLine("FAIL: " + failure.Message); Environment.Exit(1); }
    }
}
