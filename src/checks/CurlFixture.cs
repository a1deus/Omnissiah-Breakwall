using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Reflection;

// Offline subprocess fixture: never connects to any network endpoint.
internal static class CurlFixture
{
    public static int Main(string[] args)
    {
        if(args.Length==1&&args[0]=="--sleep"){Thread.Sleep(30000);return 0;}
        if(args.Length==2&&args[0]=="--spawn-child") {
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(!File.Exists(Path.Combine(args[1],"start"))){Thread.Sleep(10);if(DateTime.UtcNow>deadline)return 4;}
            using(var child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--sleep"){UseShellExecute=false,CreateNoWindow=true})) {
                File.WriteAllText(Path.Combine(args[1],"child.pid.tmp"),child.Id.ToString());
                File.Move(Path.Combine(args[1],"child.pid.tmp"),Path.Combine(args[1],"child.pid"));
            }
            Thread.Sleep(30000);return 0;
        }
        if(args.Length<2||args[0]!="-q"||!args.Contains("--noproxy")||args[Array.IndexOf(args,"--noproxy")+1]!="*")return 2;
        if(!args.Any(arg=>arg.StartsWith("\nBREAKWALL_PROBE:")))return 3;
        if(args.Last().Contains("hang"))Thread.Sleep(30000);
        Console.Write("fixture\nBREAKWALL_PROBE:200:7:text/plain");
        return 0;
    }
}
