using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace winuitest.Services;

/// <summary>Persists the login cookie with Windows DPAPI for the current Windows user.</summary>
public sealed class AccountSessionStore
{
    private const int MaximumCookieBytes = 256 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountSessionStore(string? directory = null)
    {
        var folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaMusic");
        _path = Path.Combine(folder, "account.dat");
    }

    /// <summary>Returns null for a missing, damaged, or other-user credential. Never returns ciphertext.</summary>
    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? plaintext = null;
        try
        {
            if (!File.Exists(_path)) return null;
            var length = new FileInfo(_path).Length;
            if (length <= 0 || length > MaximumCookieBytes + 16384) return null;
            var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            plaintext = Transform(encrypted, protect: false);
            if (plaintext.Length == 0 || plaintext.Length > MaximumCookieBytes) return null;
            var cookie = new UTF8Encoding(false, true).GetString(plaintext);
            return string.IsNullOrWhiteSpace(cookie) ? null : cookie;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            _gate.Release();
        }
    }

    public async Task SaveAsync(string cookie, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);
        if (Encoding.UTF8.GetByteCount(cookie) > MaximumCookieBytes)
            throw new ArgumentException("登录凭据超出允许的大小。", nameof(cookie));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var plaintext = Encoding.UTF8.GetBytes(cookie);
        try
        {
            // No plaintext credential is ever written to disk, including temporary files.
            var encrypted = Transform(plaintext, protect: true);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { File.Delete(_path); }
        finally { _gate.Release(); }
    }

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("登录凭据存储需要 Windows。");
        var input = new DataBlob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var output = default(DataBlob);
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            const uint noUserInterface = 1;
            // Deliberately omit CRYPTPROTECT_LOCAL_MACHINE: only this user can restore the session.
            var succeeded = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, noUserInterface, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, noUserInterface, out output);
            if (!succeeded) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.Length < 0 || output.Length > MaximumCookieBytes + 16384)
                throw new CryptographicException("登录凭据格式无效。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            ZeroNativeBuffer(input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                ZeroNativeBuffer(output.Data, Math.Max(0, output.Length));
                _ = LocalFree(output.Data);
            }
        }
    }

    private static void ZeroNativeBuffer(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero || length == 0) return;
        var zeros = new byte[Math.Min(4096, length)];
        for (var offset = 0; offset < length; offset += zeros.Length)
            Marshal.Copy(zeros, 0, IntPtr.Add(pointer, offset), Math.Min(zeros.Length, length - offset));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
