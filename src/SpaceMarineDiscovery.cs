using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

// Observe only the game's 36-byte outbound diagnostic requests. Never save
// payloads, nonces, process IDs, local addresses or a packet capture.
internal sealed class SpaceMarineDiscovery : IDisposable
{
    private readonly IntPtr module;
    private IntPtr handle = new IntPtr(-1);
    private readonly Open open;
    private readonly Recv recv;
    private readonly Shutdown shutdown;
    private readonly Close close;
    private Thread worker;
    private Timer timer;
    private volatile bool stopping;
    private readonly SpaceMarineEchoCache cache;
    private readonly SpaceMarineUdpOwnership ownership;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate IntPtr Open(string filter, int layer, short priority, ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Recv(IntPtr h, [Out] byte[] packet, uint capacity, out uint length, [Out] byte[] address);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Shutdown(IntPtr h, int how);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Close(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    private T Function<T>(string name) { return (T)(object)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, name), typeof(T)); }
    private SpaceMarineDiscovery(string library, SpaceMarineEchoCache cache, SpaceMarineUdpOwnership ownership)
    {
        this.cache = cache;
        this.ownership = ownership;
        module = LoadLibraryEx(library, IntPtr.Zero, 0x1100);
        if (module == IntPtr.Zero) throw new InvalidOperationException("Не удалось открыть библиотеку локальной UDP-диагностики.");
        try
        {
            open = Function<Open>("WinDivertOpen"); recv = Function<Recv>("WinDivertRecv");
            shutdown = Function<Shutdown>("WinDivertShutdown"); close = Function<Close>("WinDivertClose");
            // SNIFF | RECV_ONLY | NO_INSTALL. The main filter owns the driver.
            handle = open("outbound and ip and udp and udp.DstPort == 50000 and udp.PayloadLength == 36", 0, 29000, 0x15);
            if (handle == new IntPtr(-1)) throw new InvalidOperationException("Недоступен локальный сбор адресов UDP-диагностики.");
            timer = new Timer(_ => cache.Flush(), null, 3000, 3000);
            worker = new Thread(Observe) { IsBackground = true, Name = "Space Marine UDP discovery" }; worker.Start();
        }
        catch { if (timer != null) timer.Dispose(); if (handle != new IntPtr(-1) && close != null) close(handle); FreeLibrary(module); throw; }
    }
    internal static SpaceMarineDiscovery TryStart(string library, SpaceMarineEchoCache cache, SpaceMarineUdpOwnership ownership = null)
    {
        try { return new SpaceMarineDiscovery(library, cache, ownership ?? new SpaceMarineUdpOwnership()); }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }
    private void Observe()
    {
        var packet = new byte[65535]; var address = new byte[80];
        try
        {
            while (!stopping)
            {
                uint length; if (!recv(handle, packet, (uint)packet.Length, out length, address)) break;
                ProcessPacket(packet, (int)length, () => ownership.GameOwner(packet, (int)length, true), cache);
            }
        }
        catch (Exception) { } // Optional discovery must never terminate the launcher.
        finally { cache.Flush(); }
    }
    internal static void ProcessPacket(byte[] packet, int length, Func<int> owner, SpaceMarineEchoCache cache)
    {
        IPAddress remote;
        if (TryRequest(packet, length, out remote) && owner() != 0) cache.Remember(remote);
    }
    internal static bool TryRequest(byte[] packet, int length, out IPAddress remote)
    {
        remote = null;
        if (packet == null || length < 64 || length > packet.Length || packet[0] >> 4 != 4 || packet[9] != 17 ||
            (packet[6] & 31) != 0 || packet[7] != 0) return false;
        int header = (packet[0] & 15) * 4;
        if (header < 20 || header + 44 != length || (packet[header + 2] << 8 | packet[header + 3]) != 50000 ||
            (packet[header + 4] << 8 | packet[header + 5]) != 44) return false;
        var payload = new byte[36]; Array.Copy(packet, header + 8, payload, 0, 36);
        if (!SpaceMarineEchoPacket.IsRequest(payload)) return false;
        remote = new IPAddress(new[] { packet[16], packet[17], packet[18], packet[19] });
        return SpaceMarineEchoCache.Valid(remote);
    }
    public void Dispose()
    {
        if (stopping) return; stopping = true;
        if (timer != null) timer.Dispose();
        if (handle != new IntPtr(-1)) shutdown(handle, 1);
        if (worker != null && !worker.Join(2000))
        {
            close(handle); handle = new IntPtr(-1);
            if (!worker.Join(2000)) return; // Never unload code underneath a blocked native call.
        }
        if (handle != new IntPtr(-1)) { close(handle); handle = new IntPtr(-1); }
        cache.Flush(); FreeLibrary(module);
    }
}
