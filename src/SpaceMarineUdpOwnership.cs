using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

internal sealed class SpaceMarineUdpOwnership
{
    internal const string GameProcessName = "Warhammer 40000 Space Marine 2 - Retail";
    private readonly string processName;
    internal SpaceMarineUdpOwnership() : this(GameProcessName) { }
    internal SpaceMarineUdpOwnership(string processName) { this.processName = processName; }
    sealed class Row { internal byte[] Address; internal int Port, Pid; }
    readonly Dictionary<int,List<Row>> tables = new Dictionary<int,List<Row>>();
    readonly Dictionary<int,long> refreshed = new Dictionary<int,long>();
    readonly Dictionary<int,bool> matching = new Dictionary<int,bool>();

    [DllImport("iphlpapi.dll",SetLastError=true)]
    static extern uint GetExtendedUdpTable(IntPtr table,ref uint size,[MarshalAs(UnmanagedType.Bool)] bool sorted,uint family,int tableClass,uint reserved);

    static List<Row> ReadTable(int family)
    {
        uint size=0;
        var status=GetExtendedUdpTable(IntPtr.Zero,ref size,false,(uint)family,1,0);
        if(status!=122 && status!=0)throw new Win32Exception((int)status);
        for(int attempt=0;attempt<3;attempt++)
        {
            if(size<4||size>4194304)throw new Exception("Unexpected UDP table size");
            var allocated=size;var memory=Marshal.AllocHGlobal((int)size);
            try
            {
                status=GetExtendedUdpTable(memory,ref size,false,(uint)family,1,0);
                if(status==122)continue;
                if(status!=0)throw new Win32Exception((int)status);
                int count=Marshal.ReadInt32(memory), stride=family==2?12:28;
                if(count<0||4L+(long)count*stride>allocated)throw new Exception("Unexpected UDP table layout");
                var rows=new List<Row>();
                for(int i=0;i<count;i++)
                {
                    int start=4+i*stride,portOffset=start+(family==2?4:20),pidOffset=start+(family==2?8:24);
                    var address=new byte[family==2?4:16];Marshal.Copy(IntPtr.Add(memory,start),address,0,address.Length);
                    rows.Add(new Row{Address=address,Port=(Marshal.ReadByte(memory,portOffset)<<8)|Marshal.ReadByte(memory,portOffset+1),Pid=Marshal.ReadInt32(memory,pidOffset)});
                }
                return rows;
            }
            finally {Marshal.FreeHGlobal(memory);}
        }
        throw new Exception("UDP ownership table changed too frequently");
    }

    int Owner(byte[] address,int port,string processName)
    {
        int family=address.Length==4?2:23;List<Row> rows;
        long now=Stopwatch.GetTimestamp(), prior;
        if(!tables.TryGetValue(family,out rows)||!refreshed.TryGetValue(family,out prior)||now-prior>Stopwatch.Frequency/20)
        {
            rows=ReadTable(family);tables[family]=rows;refreshed[family]=now;matching.Clear();
        }
        var owners=rows.Where(row=>row.Port==port&&(row.Address.All(value=>value==0)||row.Address.SequenceEqual(address))).Select(row=>row.Pid).Distinct().ToArray();
        // A newly bound port must be looked up immediately, not after a cache
        // interval: its first packet is precisely the handshake we need.
        if(owners.Length==0)
        {
            rows=ReadTable(family);tables[family]=rows;refreshed[family]=now;
            owners=rows.Where(row=>row.Port==port&&(row.Address.All(value=>value==0)||row.Address.SequenceEqual(address))).Select(row=>row.Pid).Distinct().ToArray();
        }
        if(owners.Length!=1||owners[0]<=0)return 0;
        int pid=owners[0];bool accepted;
        if(!matching.TryGetValue(pid,out accepted))
        {
            accepted=false;
            try {using(var process=Process.GetProcessById(pid))accepted=process.ProcessName.Equals(processName,StringComparison.OrdinalIgnoreCase)&&!process.HasExited;}
            catch(ArgumentException){}catch(InvalidOperationException){}catch(Win32Exception){}
            matching[pid]=accepted;
        }
        return accepted?pid:0;
    }

    internal int GameOwner(byte[] packet,int length,bool outbound)
    {
        if(length<20)return 0;
        int version=packet[0]>>4,offset,ipOffset,ipLength;
        if(version==4)
        {
            if(packet[9]!=17||(packet[6]&31)!=0||packet[7]!=0)return 0;
            offset=(packet[0]&15)*4;ipOffset=outbound?12:16;ipLength=4;
        }
        else if(version==6)
        {
            if(length<40)return 0;offset=40;ipOffset=outbound?8:24;ipLength=16;int next=packet[6];
            while(next!=17)
            {
                if(offset+8>length)return 0;int current=next;next=packet[offset];
                if(current==0||current==43||current==60)offset+=(packet[offset+1]+1)*8;
                else if(current==44){if((packet[offset+2]&0xff)!=0||(packet[offset+3]&0xf8)!=0)return 0;offset+=8;}
                else if(current==51)offset+=(packet[offset+1]+2)*4;
                else return 0;
            }
        }
        else return 0;
        if(offset+8>length||offset<20)return 0;
        int portOffset=offset+(outbound?0:2),port=(packet[portOffset]<<8)|packet[portOffset+1];
        var address=new byte[ipLength];Array.Copy(packet,ipOffset,address,0,ipLength);
        return Owner(address,port,processName);
    }

}
