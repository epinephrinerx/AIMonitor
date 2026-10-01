using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AIMonitor.Infrastructure.Storage;

internal sealed class WindowsDpapiProtector(string entropy) : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes(entropy ?? throw new ArgumentNullException(nameof(entropy)));

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true);

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Transform(protectedData, protect: false);

    private byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secret storage requires Windows DPAPI.");
        }

        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var entropyPointer = Marshal.AllocHGlobal(_entropy.Length);
        var output = default(DataBlob);
        var managedInput = input.ToArray();
        try
        {
            Marshal.Copy(managedInput, 0, inputPointer, input.Length);
            Marshal.Copy(_entropy, 0, entropyPointer, _entropy.Length);
            var inputBlob = new DataBlob(input.Length, inputPointer);
            var entropyBlob = new DataBlob(_entropy.Length, entropyPointer);
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), protect ? "DPAPI protection failed." : "DPAPI unprotection failed.");
            }

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(managedInput);
            ZeroUnmanaged(inputPointer, input.Length);
            Marshal.FreeHGlobal(inputPointer);
            Marshal.FreeHGlobal(entropyPointer);
            if (output.Data != IntPtr.Zero)
            {
                ZeroUnmanaged(output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }

    private static void ZeroUnmanaged(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero || length <= 0) return;
        var zeros = new byte[Math.Min(length, 4096)];
        for (var offset = 0; offset < length; offset += zeros.Length)
        {
            Marshal.Copy(zeros, 0, IntPtr.Add(pointer, offset), Math.Min(zeros.Length, length - offset));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
