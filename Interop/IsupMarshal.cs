using System.Text;
using static QuickNV.HikvisionISUPSDK.Defines;

namespace HikvisionIsupServer.Interop;

/// <summary>
/// Helpers for the fixed-size character buffers the ISUP SDK structs are built from.
/// </summary>
public static class IsupMarshal
{
    /// <summary>
    /// Writes an address into a NET_EHOME_IPADDRESS, clearing the buffer first and always
    /// leaving room for the terminator.
    /// </summary>
    public static void WriteAddress(ref NET_EHOME_IPADDRESS address, string ip, ushort port)
    {
        address.szIP ??= new byte[IPADDRESS_LENGTH];
        Array.Clear(address.szIP, 0, address.szIP.Length);

        var bytes = Encoding.ASCII.GetBytes(ip);
        Array.Copy(bytes, address.szIP, Math.Min(bytes.Length, address.szIP.Length - 1));

        address.wPort = port;
    }

    /// <summary>Reads a null-terminated string out of a fixed-size SDK buffer.</summary>
    public static string ReadFixedString(byte[]? bytes, Encoding? encoding = null)
    {
        if (bytes is null || bytes.Length == 0)
            return "";

        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0)
            end = bytes.Length;

        return (encoding ?? Encoding.ASCII).GetString(bytes, 0, end).Trim();
    }
}
