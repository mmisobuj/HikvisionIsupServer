using System.Runtime.InteropServices;
using System.Text;
using static QuickNV.HikvisionISUPSDK.Defines;
using static QuickNV.HikvisionISUPSDK.Methods;

namespace HikvisionIsupServer.Services;

public enum IsapiVerb
{
    Get,
    Post,
    Put,
    Delete
}

/// <summary>
/// Sends ISAPI requests down an established ISUP registration.
/// </summary>
/// <remarks>
/// The terminal sits behind NAT and dials out, so it has no reachable HTTP endpoint of its
/// own. The CMS pass-through tunnels an ISAPI request over the registration the device
/// already holds open, which is what makes pulling buffered events possible at all.
/// </remarks>
public sealed class IsapiPassthroughService
{
    private const int DefaultResponseCapacity = 1024 * 1024;

    private readonly ILogger<IsapiPassthroughService> _logger;

    public IsapiPassthroughService(ILogger<IsapiPassthroughService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the response body, or null when the SDK rejected the call. The SDK error code
    /// is logged; a device that has gone offline is the usual cause.
    /// </summary>
    public string? Send(
        int loginId,
        IsapiVerb verb,
        string url,
        string? body = null,
        int responseCapacity = DefaultResponseCapacity)
    {
        var urlBytes = Encoding.ASCII.GetBytes(url);
        var bodyBytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);

        var pUrl = IntPtr.Zero;
        var pIn = IntPtr.Zero;
        var pOut = IntPtr.Zero;
        var pParam = IntPtr.Zero;

        try
        {
            pUrl = Marshal.AllocHGlobal(urlBytes.Length + 1);
            Marshal.Copy(urlBytes, 0, pUrl, urlBytes.Length);
            Marshal.WriteByte(pUrl, urlBytes.Length, 0);

            if (bodyBytes.Length > 0)
            {
                pIn = Marshal.AllocHGlobal(bodyBytes.Length);
                Marshal.Copy(bodyBytes, 0, pIn, bodyBytes.Length);
            }

            pOut = Marshal.AllocHGlobal(responseCapacity);

            var param = new NET_EHOME_PTXML_PARAM
            {
                pRequestUrl = pUrl,
                dwRequestUrlLen = urlBytes.Length,
                pCondBuffer = IntPtr.Zero,
                dwCondSize = 0,
                pInBuffer = pIn,
                dwInSize = bodyBytes.Length,
                pOutBuffer = pOut,
                dwOutSize = responseCapacity,
                dwReturnedXMLLen = 0,
                byRes = new byte[32]
            };

            pParam = Marshal.AllocHGlobal(Marshal.SizeOf<NET_EHOME_PTXML_PARAM>());
            Marshal.StructureToPtr(param, pParam, false);

            var succeeded = verb switch
            {
                IsapiVerb.Get => NET_ECMS_GetPTXMLConfig(loginId, pParam),
                IsapiVerb.Post => NET_ECMS_PostPTXMLConfig(loginId, pParam),
                IsapiVerb.Put => NET_ECMS_PutPTXMLConfig(loginId, pParam),
                IsapiVerb.Delete => NET_ECMS_DeletePTXMLConfig(loginId, pParam),
                _ => false
            };

            if (!succeeded)
            {
                _logger.LogWarning(
                    "ISAPI {Verb} {Url} failed for login {LoginId}. SDK error: {Error}",
                    verb, url, loginId, NET_ECMS_GetLastError());
                return null;
            }

            var result = Marshal.PtrToStructure<NET_EHOME_PTXML_PARAM>(pParam);
            var length = Math.Clamp(result.dwReturnedXMLLen, 0, responseCapacity);

            if (length == 0)
                return string.Empty;

            var buffer = new byte[length];
            Marshal.Copy(pOut, buffer, 0, length);

            return Encoding.UTF8.GetString(buffer).TrimEnd('\0').Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ISAPI {Verb} {Url} threw for login {LoginId}.", verb, url, loginId);
            return null;
        }
        finally
        {
            if (pParam != IntPtr.Zero)
            {
                Marshal.DestroyStructure<NET_EHOME_PTXML_PARAM>(pParam);
                Marshal.FreeHGlobal(pParam);
            }

            if (pOut != IntPtr.Zero) Marshal.FreeHGlobal(pOut);
            if (pIn != IntPtr.Zero) Marshal.FreeHGlobal(pIn);
            if (pUrl != IntPtr.Zero) Marshal.FreeHGlobal(pUrl);
        }
    }
}
