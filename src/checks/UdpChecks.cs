using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class UdpChecks
{
    static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run(string scratch)
    {
        // Entirely synthetic UUID/vector, unrelated to any real client.
        var packet = DarktideConnectPacket.Create(new Guid("00112233-4455-6677-8899-aabbccddeeff"));
        Need(BitConverter.ToString(packet).Replace("-", "") == "36E53E0000008E6D5B6494579FD630303131323233332D343435352D363637372D383839392D6161626263636464656566660000100121", "Synthetic connectionless packet vector");
        Need(DarktideConnectPacket.Hash(new byte[0]) == 0, "Empty Murmur vector");
        Need(DarktideConnectPacket.Accept(Reply(31)) && DarktideConnectPacket.Accept(Reply(32)) && DarktideConnectPacket.Accept(Reply(33)), "Initial response family");
        Need(!DarktideConnectPacket.Accept(null) && !DarktideConnectPacket.Accept(new byte[32]) && !DarktideConnectPacket.Accept(Reply(30)) && !DarktideConnectPacket.Accept(Reply(34)) && !DarktideConnectPacket.Accept(packet), "Malformed/echo response accepted");
        foreach (var bad in new[] {"0.0.0.0", "10.0.0.1", "100.64.0.1", "127.0.0.1", "169.254.1.1", "172.16.0.1", "192.168.0.1", "192.0.2.1", "198.18.0.1", "198.51.100.1", "203.0.113.1", "224.0.0.1", "::1"})
            Need(!DarktideLogTargets.IsPublicGameEndpoint(IPAddress.Parse(bad), 35530), "Non-public target accepted");
        Need(!DarktideLogTargets.IsPublicGameEndpoint(IPAddress.Parse("1.1.1.1"), 443), "Off-profile UDP port accepted");
        Discovery(scratch); Probe(); Loopback().GetAwaiter().GetResult();
        Console.WriteLine("DARKTIDE UDP: SYNTHETIC PACKET, TARGET DISCOVERY, PRIVATE ADDRESS REJECTION, TWO ROUNDS, FRESH UUIDS, LOOPBACK SOURCE ISOLATION, TIMEOUT AND CANCELLATION OK");
    }
    static byte[] Reply(int length) { var reply = new byte[length]; reply[0] = 0x3e; return reply; }
    static string Line(string address) { return "00:00:01.000 [Lua] INFO [PartyImmateriumHubSessionBoot] Searching for WAN lobby on " + address; }
    static void Discovery(string scratch)
    {
        var directory = Path.Combine(scratch, "synthetic-game-logs"); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "console-fixture.log");
        File.WriteAllLines(file, new[] {"unrelated fake token must not be a target", Line("1.1.1.1:35530"), Line("127.0.0.1:35530"), Line("8.8.8.8:443"), Line("8.8.8.8:35540"), Line("1.1.1.1:35530")});
        var source = new DarktideLogTargets(directory, () => DateTime.UtcNow);
        var targets = source.Read(CancellationToken.None);
        Need(targets.Count == 2 && targets[0].Equals(new IPEndPoint(IPAddress.Parse("1.1.1.1"),35530)), "Local discovery ordering/deduplication/privacy filtering");
        File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddDays(-8));Need(source.Read(CancellationToken.None).Count == 0, "Expired logs accepted");
        Need(new DarktideLogTargets(Path.Combine(directory,"missing"),()=>DateTime.UtcNow).Read(CancellationToken.None).Count == 0, "Missing logs");
        using(var cancellation=new CancellationTokenSource()) { cancellation.Cancel(); var cancelled=false; try { new DarktideLogTargets(directory,()=>DateTime.UtcNow).Read(cancellation.Token); } catch(OperationCanceledException){cancelled=true;} Need(cancelled,"Discovery cancellation"); }
    }
    static void Probe()
    {
        var source=new Targets();var transport=new Datagrams();var probe=new DarktideUdpProbe(source,transport);
        Need(probe.Prepare(CancellationToken.None).GetAwaiter().GetResult().Passed,"UDP preparation");
        Need(probe.Test(CancellationToken.None).GetAwaiter().GetResult().Passed&&transport.Requests.Count==2,"Two independent UDP replies required");
        Need(!transport.Requests[0].SequenceEqual(transport.Requests[1]),"UUID reused across attempts");
        Need(source.Reads==1,"Targets changed between attempts");
        transport.FailSecond=true;Need(!probe.Test(CancellationToken.None).GetAwaiter().GetResult().Passed,"One UDP response incorrectly passed");
        source.Empty=true;var empty=new DarktideUdpProbe(source,transport);Need(!empty.Prepare(CancellationToken.None).GetAwaiter().GetResult().Passed,"Empty targets passed");
    }
    static async Task Loopback()
    {
        using(var server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        {
            var endpoint=(IPEndPoint)server.Client.LocalEndPoint;
            var responder=Task.Run(async()=>{
                var received=await server.ReceiveAsync();
                using(var wrong=new UdpClient(AddressFamily.InterNetwork))await wrong.SendAsync(Reply(31),31,received.RemoteEndPoint);
                await Task.Delay(80);
                await server.SendAsync(Reply(32),32,received.RemoteEndPoint);
            });
            var timer=Stopwatch.StartNew();
            var reply=await new DarktideDatagrams().Request(endpoint,DarktideConnectPacket.Create(Guid.NewGuid()),CancellationToken.None);
            Need(reply!=null&&reply.Length==32&&timer.ElapsedMilliseconds>=60,"UDP source isolation");await responder;
            using(var cancellation=new CancellationTokenSource())
            {
                cancellation.CancelAfter(80);var cancelled=false;
                try{await new DarktideDatagrams().Request(endpoint,DarktideConnectPacket.Create(Guid.NewGuid()),cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
                Need(cancelled,"UDP socket cancellation");
            }
            Need(await new DarktideDatagrams().Request(endpoint,DarktideConnectPacket.Create(Guid.NewGuid()),CancellationToken.None)==null,"Silent UDP endpoint passed");
        }
    }
    sealed class Targets : IDarktideTargets
    {
        internal bool Empty;internal int Reads;
        public IList<IPEndPoint> Read(CancellationToken cancellation){Reads++;return Empty?new List<IPEndPoint>():new List<IPEndPoint>{new IPEndPoint(IPAddress.Parse("1.1.1.1"),35530)};}
    }
    sealed class Datagrams : IDarktideDatagrams
    {
        internal bool FailSecond;internal readonly List<byte[]> Requests=new List<byte[]>();
        public Task<byte[]> Request(IPEndPoint endpoint,byte[] request,CancellationToken cancellation){Requests.Add(request);return Task.FromResult(FailSecond&&Requests.Count%2==0?null:Reply(32));}
    }
}
