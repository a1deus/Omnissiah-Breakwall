using System;
using System.Linq;
using System.Threading;

// Offline subprocess fixture: never connects to any network endpoint.
internal static class CurlFixture
{
    public static int Main(string[] args)
    {
        if(args.Length<2||args[0]!="-q"||!args.Contains("--noproxy")||args[Array.IndexOf(args,"--noproxy")+1]!="*")return 2;
        if(!args.Any(arg=>arg.StartsWith("\nBREAKWALL_PROBE:")))return 3;
        if(args.Last().Contains("hang"))Thread.Sleep(30000);
        Console.Write("fixture\nBREAKWALL_PROBE:200:7:text/plain");
        return 0;
    }
}
