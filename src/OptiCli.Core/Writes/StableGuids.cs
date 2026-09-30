using System.Security.Cryptography;
using System.Text;

namespace OptiCli.Core.Writes;

/// <summary>Name-based GUIDs (RFC 9562 version 5): the same namespace and name always give the same GUID.</summary>
public static class StableGuids
{
    public static Guid Create(Guid @namespace, string name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        @namespace.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        var input = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        namespaceBytes.CopyTo(input);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));

#pragma warning disable CA5350 // SHA-1 is what version 5 is defined with; this is naming, not security.
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
