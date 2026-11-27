using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

public static class CheckMain
{
    [STAThread] public static int Main(string[] args)
    {
        try {
            var setup = new AppDomainSetup { ApplicationBase = args[0] };
            var domain = AppDomain.CreateDomain("Breakwall150Checks", null, setup);
            try {
                var runner = (CheckRunner)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(CheckRunner).FullName);
                runner.Run(args);
            } finally { AppDomain.Unload(domain); }
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

public sealed class CheckRunner : MarshalByRefObject
{
    Assembly app;
    Type engine, game, service, warp, selection;
    string root, scratch;
    static readonly BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly string[] games = {
        "None","Standard","Alternate","FlowAlt2","FlowAlt3","FlowExp","FlowSimpleAlt2","Combined","FlowAlt12","CommunityAlt10",
        "ActiveGameShortTen","ActiveGameMediumFourteen","ActiveGameMediumTen","ActiveGameSix","ActiveGameEight","ActiveGameFour",
        "Zapret2Exp","Zapret2DoubleFake","DbankShort","ActiveGameMinimal","TlsSmallOverlap","TlsMd5Disorder","Zapret2TlsClone","Zapret2TlsCircular",
        "FlowsealLongTen","FlowsealLongFourteen","FlowsealDualFour"
    };
    readonly string[] discord = { "None","Standard","BadSequence","TimestampFake","HostSplit","TimestampOverlap","SimpleFake","SniSplit",
        "DS:ALT12","DS:ALT13","DS:EXP","AlternateTls","Zapret2DiscordExp","Zapret2DiscordCircular" };
    readonly string[] youtube = { "None","Standard","TimestampFake","HostSplit","TimestampOverlap","SimpleFake","SniSplit",
        "DS:GENERAL","DS:ALT11","DS:ALT12","DS:SIMPLE FAKE ALT2","Zapret2YoutubeCircular","Zapret2YoutubeExp" };
    readonly string[] warps = { "None","Standard","QuicBurst","MasqueTls","Circular","CircularStun","DoubleFakeSequence","DoubleFakeSteam","DoubleFakeTimestamp" };

    public void Run(string[] args)
    {
        root = args[0]; scratch = args[2]; Directory.CreateDirectory(scratch);
        app = Assembly.LoadFrom(Path.Combine(root, "Omnissiah Breakwall.exe"));
        engine = app.GetType("Engine"); game = app.GetType("Strategy"); service = app.GetType("ServiceStrategy");
        warp = app.GetType("WarpStrategy"); selection = app.GetType("StrategySelection`1");
        if (args[1] == "config") { Configurations(); PickerMigration(); return; }
        if (args[1] == "dryrun") { DryRuns(); return; }
        if (args[1] == "updater") { UpdateChecks(args[3],args[4]); return; }
        throw new Exception("Unknown check mode");
    }
    static void Need(bool condition, string message) { if (!condition) throw new Exception(message); }
    object Pick(Type type, string name)
    {
        object value = name.StartsWith("DS:") ? app.GetType("DsStrategy").GetMethod("Get").Invoke(null,new object[]{name.Substring(3)}) : Enum.Parse(type,name);
        return Activator.CreateInstance(selection.MakeGenericType(type),new[]{value});
    }
    string Arguments(string dt, string sm, string w, string ds, string yt)
    {
        return (string)engine.GetMethod("Arguments",StaticPrivate).Invoke(null,new[]{Pick(game,dt),Pick(warp,w),Pick(service,ds),Pick(service,yt),Pick(game,sm)});
    }
    List<string[]> Cases(bool full)
    {
        var cases = new List<string[]> { new[]{"None","None","None","None","None"} };
        foreach(var g in games.Skip(1)) {
            cases.Add(new[]{g,"None","None","None","None"}); cases.Add(new[]{"None",g,"None","None","None"});
        }
        foreach(var d in discord.Skip(1)) cases.Add(new[]{"None","None","None",d,"None"});
        foreach(var y in youtube.Skip(1)) cases.Add(new[]{"None","None","None","None",y});
        foreach(var w in warps.Skip(1)) cases.Add(new[]{"None","None",w,"None","None"});
        for(var i=1;i<games.Length;i++) {
            for(var j=0;j<(full?warps.Length:1);j++)
                cases.Add(new[]{games[i],games[games.Length-i],warps[(i+j)%warps.Length],discord[i%discord.Length],youtube[i%youtube.Length]});
        }
        foreach(var w in warps) {
            cases.Add(new[]{"Zapret2TlsCircular","Zapret2TlsClone",w,"Zapret2DiscordCircular","Zapret2YoutubeCircular"});
            cases.Add(new[]{"Zapret2TlsClone","Zapret2TlsCircular",w,"DS:EXP","DS:ALT12"});
        }
        if(full) foreach(var d in discord)foreach(var y in youtube)foreach(var w in warps)
            cases.Add(new[]{"Zapret2TlsCircular","Zapret2TlsClone",w,d,y});
        return cases;
    }
    void Configurations()
    {
        var count=0; var longest=0;
        var lua=string.Join("\n",new[]{"zapret-lib.lua","zapret-antidpi.lua","zapret-auto.lua"}
            .Select(file=>File.ReadAllText(Path.Combine(root,"engine","zapret2",file))));
        var functions=new HashSet<string>(Regex.Matches(lua,@"\bfunction\s+(\w+)\s*\(").Cast<Match>().Select(m=>m.Groups[1].Value));
        foreach(var c in Cases(true)) {
            var a=Arguments(c[0],c[1],c[2],c[3],c[4]); count++;
            if(c.All(x=>x=="None")) { Need(a==null,"All disabled must produce no command"); continue; }
            Need(a!=null&&!a.Contains("--dpi-desync")&&!a.Contains("{")&&!a.Contains("}"),"Legacy or unresolved command");
            Need(a.Length+root.Length+128<32767,"Windows command-line limit"); longest=Math.Max(longest,a.Length);
            Need(a.Contains("--wf-tcp-in=")&&a.Contains("--wf-tcp-out="),"TCP capture lost");
            foreach(Match call in Regex.Matches(a,@"--lua-desync=(\w+)"))
                Need(functions.Contains(call.Groups[1].Value),"Missing Lua function: "+call.Value);
            var loaded=new HashSet<string>(Regex.Matches(a,@"--blob=(\w+):@").Cast<Match>().Select(m=>m.Groups[1].Value));
            loaded.UnionWith(new[]{"fake_default_tls","fake_default_wireguard","ch_clone"});
            foreach(Match reference in Regex.Matches(a,@"(?:blob|seqovl_pattern|pattern|fallback)=(\w+)"))
                Need(loaded.Contains(reference.Groups[1].Value),"Missing blob import: "+reference.Value);
            var rules=a.Split(new[]{" --new "},StringSplitOptions.None);
            var names=rules.Select(r=>Regex.Match(r,@"--name=(\S+)").Groups[1].Value).ToArray();
            Need(names.All(n=>n.Length>0)&&names.Distinct().Count()==names.Length,"Missing/duplicate profile name");
            var keys=Regex.Matches(a,@"circular:key=([^:\s]+)").Cast<Match>().Select(m=>m.Groups[1].Value).ToArray();
            Need(keys.Distinct().Count()==keys.Length,"Circular counters shared across profiles");
            if(c[0]!="None") {
                var rule=rules.First(r=>r.Contains("--name=darktide-udp "));
                Need(rule.Contains("--filter-udp=35500-35650,35670")&&rule.Contains("--out-range=-n"),"DT UDP scope/cutoff");
            } else Need(!names.Any(n=>n.StartsWith("darktide-")),"Disabled DT captured by own profile");
            if(c[1]!="None") {
                var rule=rules.First(r=>r.Contains("--name=space-marine-2-udp "));
                Need(rule.Contains("space-marine-2-ip.txt")&&rule.Contains("space-marine-2-udp-ip.txt"),"SM UDP IP restriction");
                Need(rules.First(r=>r.Contains("--name=space-marine-2-extra-tls ")).Contains("--filter-l7=tls"),"SM extra protocol restriction");
                if(c[3]!="None") {
                    var voice=rules.First(r=>r.Contains("--name=discord-voice "));
                    Need(!voice.Contains("unknown")&&Array.IndexOf(names,"discord-voice")<Array.IndexOf(names,"space-marine-2-udp"),"SM/Discord overlap");
                }
            } else Need(!names.Any(n=>n.StartsWith("space-marine-2-")),"Disabled SM profile");
            if(c[2]=="None") Need(!names.Any(n=>n.StartsWith("warp-")),"Disabled WARP profile");
            if(c[3]=="None") Need(!names.Any(n=>n.StartsWith("discord-")),"Disabled Discord profile");
            if(c[4]=="None") Need(!names.Any(n=>n.StartsWith("youtube-")),"Disabled YouTube profile");
            if(c[3]!="None"&&c[4]!="None")foreach(var rule in rules.Where(r=>r.Contains("--name=youtube-")))
                Need(rule.Contains("--hostlist-exclude=\"")&&rule.Contains("/discord.txt\""),"YouTube steals Discord hostname");
        }
        foreach(var item in new[]{new[]{"Standard","-n1"},new[]{"Alternate","-n3"},new[]{"FlowAlt2","-n1"},new[]{"FlowSimpleAlt2","-n2"},
            new[]{"ActiveGameSix","-n4"},new[]{"FlowsealLongTen","-n4"},new[]{"FlowsealLongFourteen","-n3"},new[]{"FlowsealDualFour","-n4"}}) {
            var rule=Arguments(item[0],"None","None","None","None").Split(new[]{" --new "},StringSplitOptions.None).Last();
            Need(rule.Contains("--out-range="+item[1]+" "),"Packet cutoff changed: "+item[0]);
        }
        Need(Arguments("Zapret2Circular","None","None","None","None")==Arguments("ActiveGameEight","None","None","None","None"),"Merged game alias differs");
        Console.WriteLine("CONFIGURATIONS "+count+"; MAX ARGUMENT LENGTH "+longest);
    }
    void PickerMigration()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var main=app.GetType("MainWindow");
        var preferenceFile=Path.Combine(scratch,"empty-preferences.txt");
        using(var window=(Form)Activator.CreateInstance(main,new object[]{preferenceFile,false})) {
            foreach(var field in new[]{"darktideStrategy","spaceMarineStrategy","discordStrategy","youtubeStrategy","warpStrategy"}) {
                var picker=main.GetField(field,InstancePrivate).GetValue(window); var type=picker.GetType();
                var options=(ListBox)type.GetField("options",InstancePrivate).GetValue(picker);
                var expected=field=="darktideStrategy"||field=="spaceMarineStrategy"?27:field=="discordStrategy"?14:field=="youtubeStrategy"?13:9;
                Need(options.Items.Count==expected,"Wrong picker count: "+field);
                var ids=field=="darktideStrategy"||field=="spaceMarineStrategy"?games:field=="discordStrategy"?discord:field=="youtubeStrategy"?youtube:warps;
                foreach(var id in ids) { type.GetMethod("Restore").Invoke(picker,new object[]{id});
                    Need(type.GetProperty("Selected").GetValue(picker,null).ToString()==id,"Preference ID lost: "+id); }
                if(expected==27)foreach(var alias in new[]{"Zapret2Circular","FlowAlt7","DS:ALT5"}) {
                    type.GetMethod("Restore").Invoke(picker,new object[]{alias});
                    var actual=type.GetProperty("Selected").GetValue(picker,null).ToString();
                    Need(actual==(alias=="Zapret2Circular"?"ActiveGameEight":alias=="FlowAlt7"?"FlowAlt2":"ActiveGameMediumFourteen"),"Alias migration failed");
                }
                if(field=="discordStrategy") {
                    type.GetMethod("Restore").Invoke(picker,new object[]{"LegacyAlternateTls"});
                    Need(type.GetProperty("Selected").GetValue(picker,null).ToString()=="AlternateTls","DS11 migration");
                }
                type.GetMethod("Restore").Invoke(picker,new object[]{field=="spaceMarineStrategy"?"None":"Standard"});
            }
            // Render our own form offscreen; never load the user's preferences
            // or start a filter/tunnel. Child handles need a shown parent.
            window.ShowInTaskbar=false; window.StartPosition=FormStartPosition.Manual; window.Location=new Point(-32000,-32000);
            window.Show(); Application.DoEvents();
            using(var picture=new Bitmap(window.Width,window.Height)) { window.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size)); picture.Save(Path.Combine(scratch,"main-window.png")); }
            var page=(Panel)main.GetField("strategiesPage",InstancePrivate).GetValue(window);
            main.GetMethod("ShowPage",InstancePrivate).Invoke(window,new object[]{page}); Application.DoEvents();
            using(var picture=new Bitmap(window.Width,window.Height)) { window.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size)); picture.Save(Path.Combine(scratch,"strategies-window.png")); }
            window.Hide();
        }
        Console.WriteLine("PICKERS AND PREFERENCE ALIASES OK");
    }
    void DryRuns()
    {
        var count=0;
        // Match the production launcher: executable and all runtime DLLs are
        // colocated, while lists/Lua are read from the portable project folder.
        var runtime=Path.Combine(scratch,"runtime"); Directory.CreateDirectory(runtime);
        foreach(var file in new[]{"zapret2/winws2.exe","zapret2/WinDivert.dll","cygwin1.dll","WinDivert64.sys"})
            File.Copy(Path.Combine(root,"engine",file.Replace('/',Path.DirectorySeparatorChar)),Path.Combine(runtime,Path.GetFileName(file)),true);
        using(var log=new StreamWriter(Path.Combine(scratch,"dry-run.log"))) foreach(var c in Cases(false)) {
            var a=Arguments(c[0],c[1],c[2],c[3],c[4]); if(a==null) continue;
            var bundle=Path.Combine(root,"engine","zapret2");
            var info=new ProcessStartInfo(Path.Combine(runtime,"winws2.exe"),"--dry-run "+a) {
                WorkingDirectory=bundle,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
            };
            info.EnvironmentVariables["PATH"]=Path.Combine(root,"engine")+";"+Environment.GetEnvironmentVariable("PATH");
            info.EnvironmentVariables["__COMPAT_LAYER"]="RunAsInvoker";
            using(var process=Process.Start(info)) {
                var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(10000)) { process.Kill(); throw new Exception("Dry-run timeout"); }
                var text=stdout.Result+stderr.Result;
                log.WriteLine(string.Join("|",c)+" exit="+process.ExitCode); log.WriteLine(text); log.Flush();
                Need(process.ExitCode==0,"Dry-run failed: "+string.Join("|",c)+"\n"+text); count++;
            }
            Thread.Sleep(50);
        }
        Console.WriteLine("WINWS2 DRY-RUNS "+count);
    }

    void UpdateChecks(string zip, string oldAssembly)
    {
        var update=app.GetType("ReleaseUpdate");
        var payload=Path.Combine(scratch,"payload");
        update.GetMethod("ExtractPackage",StaticPrivate).Invoke(null,new object[]{zip,payload});
        Need(!File.Exists(Path.Combine(payload,"engine","winws.exe")),"Old engine shipped");
        var install=Path.Combine(scratch,"installed"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install,"README.md"),"old readme");
        Directory.CreateDirectory(Path.Combine(install,"engine"));
        File.WriteAllText(Path.Combine(install,"engine","winws.exe"),"old engine fixture");
        Directory.CreateDirectory(Path.Combine(install,"profiles"));
        File.WriteAllText(Path.Combine(install,"profiles","base.args"),"old rule fixture");
        File.WriteAllText(Path.Combine(install,"profiles","user-custom.txt"),"keep custom");
        var work=Path.Combine(scratch,"transaction"); Directory.CreateDirectory(work);
        var type=update.GetNestedType("UpdateFiles",BindingFlags.NonPublic);
        var transaction=Activator.CreateInstance(type,new object[]{install,payload,work});
        type.GetMethod("Apply").Invoke(transaction,null);
        Need(!File.Exists(Path.Combine(install,"engine","winws.exe"))&&!File.Exists(Path.Combine(install,"profiles","base.args")),"Retired files not removed");
        Need(File.Exists(Path.Combine(install,"engine","zapret2","winws2.exe")),"New engine absent");
        Need(File.ReadAllText(Path.Combine(install,"profiles","user-custom.txt"))=="keep custom","User file lost");
        type.GetMethod("Rollback").Invoke(transaction,null);
        Need(File.ReadAllText(Path.Combine(install,"README.md"))=="old readme","Replacement rollback");
        Need(File.ReadAllText(Path.Combine(install,"engine","winws.exe"))=="old engine fixture","Retirement rollback");
        Need(File.ReadAllText(Path.Combine(install,"profiles","base.args"))=="old rule fixture","Profile rollback");
        Need(!File.Exists(Path.Combine(install,"engine","zapret2","winws2.exe")),"Added-file rollback");
        // Fail after a partial application, then recover only applied changes.
        var failRoot=Path.Combine(scratch,"fail-root"); var failPayload=Path.Combine(scratch,"fail-payload");
        var failWork=Path.Combine(scratch,"fail-work");
        Directory.CreateDirectory(failRoot); Directory.CreateDirectory(failPayload); Directory.CreateDirectory(failWork);
        foreach(var file in new[]{"a.txt","b.txt"}) {
            File.WriteAllText(Path.Combine(failRoot,file),"old "+file);
            File.WriteAllText(Path.Combine(failPayload,file),"new "+file);
        }
        var failedTransaction=Activator.CreateInstance(type,new object[]{failRoot,failPayload,failWork});
        var changes=(List<string>)type.GetField("changes",InstancePrivate).GetValue(failedTransaction);
        var failed=false;
        using(File.Open(Path.Combine(failRoot,changes.Last()),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            try { type.GetMethod("Apply").Invoke(failedTransaction,null); }
            catch(TargetInvocationException error) { failed=error.InnerException is IOException; }
        }
        Need(failed,"Locked-file application unexpectedly succeeded");
        type.GetMethod("Rollback").Invoke(failedTransaction,null);
        foreach(var file in new[]{"a.txt","b.txt"}) Need(File.ReadAllText(Path.Combine(failRoot,file))=="old "+file,"Partial-failure rollback");
        // The old helper runs old code, so a new EXE cannot repair its ZIP checks.
        var previous=Assembly.LoadFile(oldAssembly);
        var old=previous.GetType("ReleaseUpdate");
        var oldLayout=previous.GetName().Version.Major==1&&previous.GetName().Version.Minor<5;
        var rejected=false;
        try { old.GetMethod("ExtractPackage",StaticPrivate).Invoke(null,new object[]{zip,Path.Combine(scratch,"old-updater-payload")}); }
        catch(TargetInvocationException error) { if(!oldLayout) throw; rejected=error.InnerException is InvalidDataException && error.InnerException.Message.Contains("winws.exe"); }
        Need(rejected==oldLayout,oldLayout?"Expected old updater to reject new layout":"Previous zapret2 updater must accept the new package");
        // An omitted native dependency is rejected before installation.
        var broken=Path.Combine(scratch,"missing-blob.zip"); File.Copy(zip,broken);
        using(var archive=ZipFile.Open(broken,ZipArchiveMode.Update)) { archive.GetEntry("engine/ACTIVE_GAME_UDP.bin").Delete(); }
        rejected=false;
        try { update.GetMethod("ExtractPackage",StaticPrivate).Invoke(null,new object[]{broken,Path.Combine(scratch,"broken-payload")}); }
        catch(TargetInvocationException error) { rejected=error.InnerException is InvalidDataException; }
        Need(rejected,"Incomplete package accepted");
        // Keep path traversal protection in the new package layout.
        var traversal=Path.Combine(scratch,"traversal.zip");
        using(var archive=ZipFile.Open(traversal,ZipArchiveMode.Create)) using(var writer=new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("bad");
        rejected=false;
        try { update.GetMethod("ExtractPackage",StaticPrivate).Invoke(null,new object[]{traversal,Path.Combine(scratch,"traversal-payload")}); }
        catch(TargetInvocationException error) { rejected=error.InnerException is InvalidDataException; }
        Need(rejected&&!File.Exists(Path.Combine(scratch,"escape.txt")),"ZIP traversal protection");
        Console.WriteLine("UPDATER: NEW PACKAGE, APPLY, RETIREMENT, ROLLBACK, PARTIAL FAILURE, CUSTOM FILE, MISSING DEPENDENCY, TRAVERSAL OK; PREVIOUS VERSION COMPATIBILITY CHECKED");
    }
}
