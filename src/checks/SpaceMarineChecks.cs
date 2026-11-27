using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class SpaceMarineChecks
{
    static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run(string root, string scratch)
    {
        var nonce = SpaceMarineEchoPacket.Create();
        Need(nonce.Length == 36 && SpaceMarineEchoPacket.IsRequest(nonce), "SM2 nonce format");
        Need(SpaceMarineEchoPacket.Accept(nonce, (byte[])nonce.Clone()) && !SpaceMarineEchoPacket.Accept(nonce, SpaceMarineEchoPacket.Create()) &&
            !SpaceMarineEchoPacket.Accept(nonce, new byte[36]) && !SpaceMarineEchoPacket.Accept(nonce, null), "SM2 echo correlation");
        var time = DateTime.UtcNow;
        var path = Path.Combine(scratch, "sm2-fixture.dpapi");
        var cache = new SpaceMarineEchoCache(path, () => time);
        cache.Remember(IPAddress.Loopback); Need(cache.Read(CancellationToken.None).Count == 0, "SM2 private target rejected");
        var packet = new byte[64]; packet[0] = 0x45; packet[3] = 64; packet[9] = 17;
        packet[16] = packet[17] = packet[18] = packet[19] = 1;
        packet[22] = 0xc3; packet[23] = 0x50; packet[25] = 44; Array.Copy(nonce,0,packet,28,36);
        IPAddress remote;
        Need(SpaceMarineDiscovery.TryRequest(packet,64,out remote), "SM2 diagnostic request parser");
        SpaceMarineDiscovery.ProcessPacket(packet,64,()=>0,cache); Need(cache.Read(CancellationToken.None).Count == 0,"Foreign process accepted for SM2 discovery");
        SpaceMarineDiscovery.ProcessPacket(packet,64,()=>123,cache); Need(cache.Read(CancellationToken.None).Count == 1,"Game discovery not learned");
        packet[22] = 0;Need(!SpaceMarineDiscovery.TryRequest(packet,64,out remote),"Off-profile discovery accepted");
        cache.Flush();Need(File.Exists(path)&&!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("1.1.1.1"),"SM2 cache must be encrypted");
        Need(new SpaceMarineEchoCache(path,()=>time).Read(CancellationToken.None).Count == 1,"SM2 encrypted cache reload");
        Need(new SpaceMarineEchoCache(path,()=>time.AddHours(25)).Read(CancellationToken.None).Count == 0,"Expired SM2 cache accepted");
        Need(!File.Exists(path),"Expired SM2 cache not removed");
        File.WriteAllBytes(Path.Combine(scratch,"bad-cache.dpapi"),new byte[]{1,2,3});
        Need(new SpaceMarineEchoCache(Path.Combine(scratch,"bad-cache.dpapi"),()=>time).Read(CancellationToken.None).Count == 0,"Corrupt cache accepted");
        var datagrams = new EchoTransport(); var probe = new SpaceMarineUdpProbe(cache,datagrams);
        Need(probe.Prepare(CancellationToken.None).Result.Passed,"SM2 probe preparation");
        Need(probe.Test(CancellationToken.None).GetAwaiter().GetResult().Passed&&datagrams.Packets.Count==2,"SM2 requires two fresh echoes");
        Need(!datagrams.Packets[0].SequenceEqual(datagrams.Packets[1]),"SM2 reused nonce");
        datagrams.Bad=true;Need(!probe.Test(CancellationToken.None).GetAwaiter().GetResult().Passed,"SM2 bad echo accepted");
        var candidates = new[]{new AutoCandidate{Group=AutoGroup.SpaceMarine2}};
        var auto=new AutoProbe(Path.Combine(root,"profiles"),new Http(),new Dt(),probe);
        auto.Configure(candidates[0]);Need(candidates[0].SpaceMarineEchoAddresses.Length==1,"SM2 scope snapshot not assigned");
        Need(!auto.Test(AutoGroup.SpaceMarine2,CancellationToken.None).GetAwaiter().GetResult().Passed,"SM2 TLS accepted without UDP");
        EchoWithNoise().GetAwaiter().GetResult();
        var scopeFile=Path.Combine(scratch,"synthetic-sm2-scope.txt");File.WriteAllText(scopeFile,"1.1.1.1\n8.8.8.8\n");
        int count=0;
        foreach(Strategy strategy in Enum.GetValues(typeof(Strategy)))
        {
            if(strategy==Strategy.None)continue;
            foreach(var discord in new[]{ServiceStrategy.None,ServiceStrategy.Standard})
            {
                var arguments=Zapret2.Arguments(Path.Combine(root,"profiles"),new StrategySelection<Strategy>(Strategy.None),new StrategySelection<WarpStrategy>(WarpStrategy.None),new StrategySelection<ServiceStrategy>(discord),new StrategySelection<ServiceStrategy>(ServiceStrategy.None),new StrategySelection<Strategy>(strategy),scopeFile);
                Need(arguments.Contains("--name=space-marine-2-echo --filter-udp=50000 --ipset=")&&arguments.Contains("ip.DstAddr == 1.1.1.1")&&arguments.Contains("ip.DstAddr == 8.8.8.8"),"SM2 extra scope missing from WFP or profile");
                if(discord!=ServiceStrategy.None)Need(arguments.IndexOf("--name=discord-voice",StringComparison.Ordinal)<arguments.IndexOf("--name=space-marine-2-echo",StringComparison.Ordinal),"Discord priority changed");
                File.WriteAllText(Path.Combine(scratch,"sm2-native-"+(count++).ToString("D2")+".args"),arguments);
            }
        }
        Console.WriteLine("SM2: ENCRYPTED CACHE, TTL, GAME OWNERSHIP GATE, UDP NONCES, TWO ROUNDS, TLS GATING AND "+count+" NARROW RULE SETS OK");
    }
    static async Task EchoWithNoise()
    {
        using (var server = new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        {
            var endpoint = (IPEndPoint)server.Client.LocalEndPoint;
            var responder = Task.Run(async () => {
                var received = await server.ReceiveAsync();
                var noise = SpaceMarineEchoPacket.Create();
                await server.SendAsync(noise,noise.Length,received.RemoteEndPoint);
                await server.SendAsync(new byte[1250],1250,received.RemoteEndPoint);
                await Task.Delay(50);
                await server.SendAsync(received.Buffer,received.Buffer.Length,received.RemoteEndPoint);
            });
            var nonce = SpaceMarineEchoPacket.Create();
            var reply = await new DarktideDatagrams().RequestMatching(endpoint,nonce,value=>SpaceMarineEchoPacket.Accept(nonce,value),CancellationToken.None);
            Need(SpaceMarineEchoPacket.Accept(nonce,reply),"Reflected fake must not hide the real echo"); await responder;
        }
    }
    sealed class EchoTransport : IDarktideDatagrams
    {
        internal bool Bad; internal readonly List<byte[]> Packets=new List<byte[]>();
        public Task<byte[]> Request(IPEndPoint endpoint,byte[] request,CancellationToken token){Packets.Add(request);return Task.FromResult(Bad?SpaceMarineEchoPacket.Create():(byte[])request.Clone());}
    }
    sealed class Http : IAutoHttp
    {
        public Task<CurlResponse> Request(AutoProbeTarget target,CancellationToken token){return Task.FromResult(new CurlResponse{ExitCode=0,Status=200});}
    }
    sealed class Dt : IDarktideUdpProbe
    {
        public Task<ProbeReport> Prepare(CancellationToken token){return Task.FromResult(new ProbeReport{Passed=true});}
        public Task<ProbeReport> Test(CancellationToken token){return Task.FromResult(new ProbeReport{Passed=true});}
    }
}
