using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class AutoCheckMain
{
    [STAThread] public static int Main(string[] args)
    {
        try {
            var domain=AppDomain.CreateDomain("AutoChecks",null,new AppDomainSetup { ApplicationBase=args[0] });
            try {
                var runner=(AutoCheckRunner)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location,typeof(AutoCheckRunner).FullName);
                runner.Run(args[0],args[1]);
            } finally {AppDomain.Unload(domain);}
            return 0;
        } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
}

public sealed class AutoCheckRunner : MarshalByRefObject
{
    static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    string root,scratch;
    static void Need(bool value,string message) {if(!value)throw new Exception(message);}
    static void Wait(Task task)
    {
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(!task.IsCompleted) {Application.DoEvents();Thread.Sleep(2);if(DateTime.UtcNow>deadline)throw new Exception("Test timeout");}
        task.GetAwaiter().GetResult();Application.DoEvents();
    }
    static T Field<T>(object source,string name) {return (T)source.GetType().GetField(name,Private).GetValue(source);}
    static object Call(object source,string name,params object[] args) {return source.GetType().GetMethod(name,Private).Invoke(source,args);}
    public void Run(string root,string scratch)
    {
        this.root=root;this.scratch=scratch;Directory.CreateDirectory(scratch);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Acceptance();Backend();Subprocess();UdpChecks.Run(scratch);Ui();
        Console.WriteLine("AUTOSELECT: SCOPE, RESPONSE VALIDATION, ISOLATED PLANS, PROGRESS, FAILURE, CANCELLATION, CLEANUP, UI, APPLY AND PERSISTENCE OK; NO EXTERNAL NETWORK/FILTER/TUNNEL");
    }
    void Acceptance()
    {
        var ok=new CurlResponse {ExitCode=0,Status=200,Bytes=1000,ContentType="image/png",Body="warp=off\n"};
        Need(AutoProbe.Accept(AutoProbeKind.Image,ok),"Image probe");
        Need(!AutoProbe.Accept(AutoProbeKind.WarpTrace,ok),"WARP off false positive");
        ok.Body="ip=TEST\nwarp=on\n";Need(AutoProbe.Accept(AutoProbeKind.WarpTrace,ok),"WARP trace on");
        ok.Body="warp=onion\n";Need(!AutoProbe.Accept(AutoProbeKind.WarpTrace,ok),"WARP trace partial match");
        ok.Body="{\"url\":\"wss://gateway.discord.gg\"}";Need(AutoProbe.Accept(AutoProbeKind.DiscordApi,ok),"Discord API JSON");
        ok.Body="{\"url\":\"wss:\\/\\/gateway.discord.gg\"}";Need(AutoProbe.Accept(AutoProbeKind.DiscordApi,ok),"Escaped Discord API JSON");
        ok.Body="{\"url\":\"wss://gateway.discord.gg\"";Need(!AutoProbe.Accept(AutoProbeKind.DiscordApi,ok),"Malformed Discord API JSON");
        ok.Body="<html>blocked</html>";Need(!AutoProbe.Accept(AutoProbeKind.DiscordApi,ok),"HTML block page as API");
        ok.ContentType="text/html";Need(!AutoProbe.Accept(AutoProbeKind.Image,ok),"HTML block page as image");
        ok.Status=403;Need(AutoProbe.Accept(AutoProbeKind.Tls,ok)&&!AutoProbe.Accept(AutoProbeKind.Https,ok),"TLS-only scope");
        ok.Status=429;Need(!AutoProbe.Accept(AutoProbeKind.Tls,ok),"Rate limit accepted");
        ok.Status=200;ok.ExitCode=60;Need(!AutoProbe.Accept(AutoProbeKind.Tls,ok),"TLS certificate failure accepted");
        var parsed=CurlAutoHttp.Parse("hello\nBREAKWALL_PROBE:200:123:application/json\r\n",0);
        Need(parsed.Body=="hello"&&parsed.Status==200&&parsed.Bytes==123&&parsed.ContentType=="application/json","curl parser");
        Need(CurlAutoHttp.Parse("invalid",0).Status==0,"Invalid metadata accepted");
        foreach(AutoGroup group in Enum.GetValues(typeof(AutoGroup))) {
            var targets=AutoProbe.Targets(group);
            AutoProbe.ValidateTargets(Path.Combine(root,"profiles"),group,targets);
            foreach(var target in targets) {
                var command=CurlAutoHttp.Arguments(target);
                Need(command.StartsWith("-q --noproxy \"*\" ")&&!command.Contains("--insecure")&&!command.Contains("--location"),"Proxy/curlrc/cert isolation");
                Need(target.Url.Scheme=="https"&&target.Url.Port==443,"Unexpected probe transport");
            }
        }
        var rejected=false;
        try {AutoProbe.ValidateTargets(Path.Combine(root,"profiles"),AutoGroup.Darktide,new[]{new AutoProbeTarget("wrong","https://example.org/",AutoProbeKind.Tls)});}
        catch(InvalidOperationException){rejected=true;}
        Need(rejected,"Out-of-profile target accepted");
        var transport=new FakeHttp();var udp=new FakeUdp();var probe=new AutoProbe(Path.Combine(root,"profiles"),transport,udp);
        var report=probe.Test(AutoGroup.Darktide,CancellationToken.None).GetAwaiter().GetResult();
        Need(report.Passed&&transport.Count==4,"Two independent rounds");
        udp.Passed=false;Need(!probe.Test(AutoGroup.Darktide,CancellationToken.None).GetAwaiter().GetResult().Passed,"TLS without UDP accepted for Darktide");udp.Passed=true;
        transport.Fail=true;Need(!probe.Test(AutoGroup.Darktide,CancellationToken.None).GetAwaiter().GetResult().Passed,"Probe failure accepted");
    }
    static AutoCandidate Candidate(string code)
    {
        return new AutoCandidate {Group=AutoGroup.Darktide,Id="Standard",Code=code,Label=code+" test",Darktide=new StrategySelection<Strategy>(Strategy.Standard)};
    }
    void Backend()
    {
        var candidates=new[]{Candidate("DT01"),Candidate("DT02")};
        var session=new FakeSession();var selector=new AutoSelector(session,new FakeProbe());var updates=new InlineProgress();
        Wait(selector.Run(candidates,updates,CancellationToken.None));
        Need(session.Starts==2&&session.Stops==2&&session.Verifies==2&&selector.Results.All(r=>r.Passed),"Candidate lifecycle");
        session=new FakeSession();selector=new AutoSelector(session,new UnavailableProbe());Wait(selector.Run(candidates,null,CancellationToken.None));
        Need(session.Starts==0&&selector.Results.All(r=>r.Skipped&&!r.Passed)&&selector.SkippedGroups.Count==1,"Missing UDP targets must skip group without filters");
        var adaptive=Candidate("DT23");adaptive.Id="Zapret2TlsCircular";
        var adaptiveProbe=new WarmingProbe();session=new FakeSession();selector=new AutoSelector(session,adaptiveProbe);
        Wait(selector.Run(new[]{adaptive},null,CancellationToken.None));
        Need(session.Starts==1&&session.Stops==1&&adaptiveProbe.Count==4&&selector.Results.Single().Passed,"Adaptive warm-up must retain one filter process");
        Need(updates.Values.Last().Percent==100&&updates.Values.Select(p=>p.Percent).SequenceEqual(updates.Values.Select(p=>p.Percent).OrderBy(x=>x)),"Progress monotonicity");
        session=new FakeSession{FailStart=true};selector=new AutoSelector(session,new FakeProbe());Wait(selector.Run(candidates,null,CancellationToken.None));
        Need(session.Stops==2&&selector.Results.All(r=>!r.Passed),"Failed starts cleanup");
        session=new FakeSession{FailVerify=true};selector=new AutoSelector(session,new FakeProbe());Wait(selector.Run(candidates,null,CancellationToken.None));
        Need(selector.Results.All(r=>!r.Passed)&&session.Stops==2,"Filter crash accepted");
        session=new FakeSession{FailStop=true};selector=new AutoSelector(session,new FakeProbe());var failed=false;
        try{Wait(selector.Run(candidates,null,CancellationToken.None));}catch(InvalidOperationException){failed=true;}
        Need(failed&&session.Starts==1&&selector.Results.Count==0,"Cleanup failure must stop run");
        session=new FakeSession{WaitForCancel=true};selector=new AutoSelector(session,new FakeProbe());
        using(var cancellation=new CancellationTokenSource()) {
            var task=selector.Run(candidates,null,cancellation.Token);cancellation.Cancel();var cancelled=false;
            try{Wait(task);}catch(OperationCanceledException){cancelled=true;}
            Need(cancelled&&session.Starts==1&&session.Stops==1&&selector.Results.Count==0,"Cancellation cleanup");
        }
        session=new FakeSession();selector=new AutoSelector(session,new FakeProbe());
        using(var cancellation=new CancellationTokenSource()) {
            cancellation.Cancel();try{Wait(selector.Run(candidates,null,cancellation.Token));}catch(OperationCanceledException){}
            Need(session.Starts==0,"Pre-cancelled selection started filter");
        }
    }
    void Subprocess()
    {
        var http=new CurlAutoHttp(Path.Combine(scratch,"CurlFixture.exe"));
        var request=http.Request(new AutoProbeTarget("fixture","https://fixture.invalid/ok",AutoProbeKind.Https),CancellationToken.None);
        Wait(request);Need(request.Result.ExitCode==0&&request.Result.Status==200&&request.Result.Body=="fixture","Real subprocess/argument parsing");
        using(var cancellation=new CancellationTokenSource()) {
            cancellation.CancelAfter(300);
            var pending=http.Request(new AutoProbeTarget("fixture","https://fixture.invalid/hang",AutoProbeKind.Tls),cancellation.Token);
            var cancelled=false;try{Wait(pending);}catch(OperationCanceledException){cancelled=true;}
            Need(cancelled,"Curl subprocess cancellation");
        }
        Thread.Sleep(50);
        foreach(var process in System.Diagnostics.Process.GetProcessesByName("CurlFixture")) {
            using(process) Need(process.HasExited,"Curl subprocess survived cancellation");
        }
    }
    void Ui()
    {
        var preferences=Path.Combine(scratch,"preferences.txt");
        using(var window=new MainWindow(preferences,false)) {
            window.ShowInTaskbar=false;window.StartPosition=FormStartPosition.Manual;window.Location=new Point(-32000,-32000);
            window.Show();Application.DoEvents();
            var groups=Field<Dictionary<AutoGroup,CheckBox>>(window,"autoGroups");
            var run=Field<Button>(window,"runAuto");
            Need(groups.Count==5&&groups.Values.All(option=>!option.Checked)&&!run.Enabled,"Empty selection must disable start");
            var expected=new Dictionary<AutoGroup,int>{{AutoGroup.Darktide,26},{AutoGroup.SpaceMarine2,26},{AutoGroup.Youtube,12},{AutoGroup.Discord,13},{AutoGroup.Warp,8}};
            foreach(var group in groups.Keys.ToArray()) {
                groups[group].Checked=true;Need(run.Enabled,"Checked group must enable start");
                Need(!Field<Label>(window,"autoStatus").Text.StartsWith("Отметьте"),"Selected group still asks for a checkbox");
                var plans=(List<AutoCandidate>)Call(window,"AutoCandidates");Need(plans.Count==expected[group],"Candidate count");
                foreach(var plan in plans) {
                    Need(plan.Group==group,"Unchecked group scheduled");
                    var enabled=new[]{plan.Darktide.BuiltIn!=Strategy.None,plan.SpaceMarine.BuiltIn!=Strategy.None,
                        plan.Discord.Imported!=null||plan.Discord.BuiltIn!=ServiceStrategy.None,
                        plan.Youtube.Imported!=null||plan.Youtube.BuiltIn!=ServiceStrategy.None,plan.Warp.BuiltIn!=WarpStrategy.None};
                    Need(enabled.Count(value=>value)==1,"Other group enabled in plan");
                    var method=typeof(Engine).GetMethod("Arguments",BindingFlags.Static|BindingFlags.NonPublic);
                    var args=(string)method.Invoke(null,new object[]{plan.Darktide,plan.Warp,plan.Discord,plan.Youtube,plan.SpaceMarine});
                    Need(args!=null&&args.Contains("--name="),"Candidate does not use real rule builder");
                    if(group==AutoGroup.Darktide)Need(args.Contains("--filter-udp=35500-35650,35670"),"DT ports lost");
                    if(group==AutoGroup.SpaceMarine2)Need(args.Contains("space-marine-2-udp-ip.txt")&&args.Contains("--filter-tcp=11700-11720"),"SM ports/IPs lost");
                }
                groups[group].Checked=false;
            }
            foreach(var option in groups.Values)option.Checked=true;
            Need(((List<AutoCandidate>)Call(window,"AutoCandidates")).Count==85,"Full catalog size");
            foreach(var option in groups.Values)option.Checked=false;
            groups[AutoGroup.Darktide].Checked=true;
            var fakeSession=new FakeSession();var selector=new AutoSelector(fakeSession,new FakeProbe());
            window.GetType().GetField("autoSelectorFactory",Private).SetValue(window,new Func<AutoSelector>(()=>selector));
            Call(window,"ShowPage",Field<Panel>(window,"autoPage"));
            Render(window,"autoselect-empty.png");
            Wait((Task)Call(window,"RunAutoSelection"));
            Need(Field<AutoProgressBar>(window,"autoProgress").Value==100&&fakeSession.Starts==26,"UI run/progress");
            var rows=Field<FlowLayoutPanel>(window,"autoResults");Need(rows.Controls.Count==26,"Passed results missing");
            var previous=Field<object>(window,"spaceMarineStrategy").GetType().GetProperty("Selected").GetValue(Field<object>(window,"spaceMarineStrategy"),null).ToString();
            var chosen=selector.Results[4].Candidate;
            var buttons=Field<List<Button>>(window,"autoApplyButtons");buttons[4].PerformClick();
            Need(File.ReadAllText(preferences).Contains("Darktide="+chosen.Id),"Apply did not persist selected strategy");
            Need(File.ReadAllText(preferences).Contains("SpaceMarine2="+previous)&&!Field<Engine>(window,"engine").Running,"Apply changed other group or started filter");
            Need(!Field<CheckBox>(window,"useWarp").Checked,"Apply enabled tunnel");
            Render(window,"autoselect-results.png");
            Call(window,"ShowPage",Field<Panel>(window,"mainPage"));Render(window,"main-with-autoselect.png");
            Call(window,"ShowPage",Field<Panel>(window,"autoPage"));
            fakeSession=new FakeSession{WaitForCancel=true};selector=new AutoSelector(fakeSession,new FakeProbe());
            window.GetType().GetField("autoSelectorFactory",Private).SetValue(window,new Func<AutoSelector>(()=>selector));
            var running=(Task)Call(window,"RunAutoSelection");Application.DoEvents();
            Need(Field<Button>(window,"openAuto").Enabled,"Cannot return to running selection");
            Wait((Task)Call(window,"RunAutoSelection"));Wait(running);
            Need(fakeSession.Stops==1&&!Field<bool>(window,"busy")&&!Field<bool>(window,"autoSelecting"),"UI cancellation stuck");
            Need(rows.Controls.Count==0&&Field<AutoProgressBar>(window,"autoProgress").Value<100,"Cancelled run presented as complete");
            Need(Field<Label>(window,"autoStatus").Text.StartsWith("Подбор остановлен"),"Cancellation status overwritten");
            // Selecting a WARP result changes its dropdown only, never the
            // tunnel checkbox or any other strategy, even without a live test.
            var warpCandidate=Candidate("W04");warpCandidate.Group=AutoGroup.Warp;warpCandidate.Id="Circular";
            Call(window,"ApplyAutoCandidate",warpCandidate);
            Need(File.ReadAllText(preferences).Contains("WarpStrategy=Circular")&&!Field<CheckBox>(window,"useWarp").Checked,"WARP apply connects tunnel");
            window.Hide();
        }
        using(var closing=new MainWindow(Path.Combine(scratch,"close-preferences.txt"),false)) {
            closing.ShowInTaskbar=false;closing.StartPosition=FormStartPosition.Manual;closing.Location=new Point(-32000,-32000);closing.Show();
            var groups=Field<Dictionary<AutoGroup,CheckBox>>(closing,"autoGroups");groups[AutoGroup.Darktide].Checked=true;
            var session=new FakeSession{WaitForCancel=true};var selector=new AutoSelector(session,new FakeProbe());
            closing.GetType().GetField("autoSelectorFactory",Private).SetValue(closing,new Func<AutoSelector>(()=>selector));
            var pending=(Task)Call(closing,"RunAutoSelection");closing.Close();Wait(pending);Application.DoEvents();
            Need(closing.IsDisposed&&session.Stops==1,"Closing did not cancel and finish cleanup");
        }
    }
    void Render(Form window,string name)
    {
        Application.DoEvents();using(var picture=new Bitmap(window.Width,window.Height)){
            window.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size));picture.Save(Path.Combine(scratch,name));
        }
    }
    sealed class FakeSession : IAutoSession
    {
        public int Starts,Stops,Verifies;public bool FailStart,FailStop,FailVerify,WaitForCancel;
        public Task Prepare(CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();return Task.FromResult(0);}
        public Task Start(AutoCandidate candidate,CancellationToken cancellation){Starts++;if(FailStart)throw new InvalidOperationException("start fixture");return WaitForCancel?Task.Delay(-1,cancellation):Task.FromResult(0);}
        public Task Verify(AutoCandidate candidate,CancellationToken cancellation){Verifies++;if(FailVerify)throw new InvalidOperationException("filter fixture");return Task.FromResult(0);}
        public Task Stop(){Stops++;if(FailStop)throw new InvalidOperationException("stop fixture");return Task.FromResult(0);}
    }
    sealed class FakeProbe : IAutoProbe
    {
        public Task<ProbeReport> Test(AutoGroup group,CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();return Task.FromResult(new ProbeReport{Passed=true,Detail="fixture"});}
    }
    sealed class WarmingProbe : IAutoProbe
    {
        public int Count;
        public Task<ProbeReport> Test(AutoGroup group,CancellationToken cancellation){Count++;return Task.FromResult(new ProbeReport{Passed=Count>=4,Detail="adaptive fixture"});}
    }
    sealed class FakeHttp : IAutoHttp
    {
        public int Count;public bool Fail;
        public Task<CurlResponse> Request(AutoProbeTarget target,CancellationToken cancellation){Count++;return Task.FromResult(new CurlResponse{ExitCode=Fail?60:0,Status=200,Bytes=1024,ContentType="image/png",Body="warp=on\n"});}
    }
    sealed class FakeUdp : IDarktideUdpProbe
    {
        public bool Passed=true;
        public Task<ProbeReport> Prepare(CancellationToken cancellation){return Task.FromResult(new ProbeReport{Passed=true});}
        public Task<ProbeReport> Test(CancellationToken cancellation){return Task.FromResult(new ProbeReport{Passed=Passed,Detail="synthetic UDP fixture"});}
    }
    sealed class UnavailableProbe : IAutoProbe, IAutoProbePreparation
    {
        public Task<ProbeReport> PrepareGroup(AutoGroup group,CancellationToken cancellation){return Task.FromResult(new ProbeReport{Detail="synthetic unavailable target fixture"});}
        public Task<ProbeReport> Test(AutoGroup group,CancellationToken cancellation){throw new Exception("Unavailable group must not send probes");}
    }
    sealed class InlineProgress : IProgress<AutoProgress>
    {
        public readonly List<AutoProgress> Values=new List<AutoProgress>();public void Report(AutoProgress value){Values.Add(value);}
    }
}
