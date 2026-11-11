using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class Program
{
    private const string PayloadResource = "LumaMusic.Setup.Payload.msi";

    private static int Main(string[] args)
    {
        var verify = args.Length == 2 && args[0] == "--verify-msi";
        var quiet = args.Length == 1 && args[0] is "/quiet" or "/qn" or "--quiet";
        if (args.Length != 0 && !verify && !quiet) return 87;

        string? setupDirectory = null;
        string? packagePath = null;
        try
        {
            using var payload = typeof(Program).Assembly.GetManifestResourceStream(PayloadResource)
                ?? throw new InvalidDataException("安装包中的 MSI 资源缺失。");
            setupDirectory = Directory.CreateTempSubdirectory("LumaMusicSetup-").FullName;
            var extractionPath = Path.Combine(setupDirectory, "LumaMusic.msi");
            using (var extracted = new FileStream(extractionPath, FileMode.CreateNew, FileAccess.Write))
            {
                packagePath = extractionPath;
                payload.CopyTo(extracted);
            }

            if (verify)
            {
                using var extracted = File.OpenRead(extractionPath);
                var hash = Convert.ToHexString(SHA256.HashData(extracted));
                return hash.Equals(args[1], StringComparison.OrdinalIgnoreCase) ? 0 : 13;
            }

            return Install(extractionPath, setupDirectory, quiet);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            if (!verify && !quiet)
                MessageBoxW(0, "无法启动 Luma Music 安装。\n\n" + error.Message, "Luma Music 安装", 0x10);
            return 1603;
        }
        finally
        {
            if (packagePath is not null) TryCleanup(() => File.Delete(packagePath));
            if (setupDirectory is not null) TryCleanup(() => Directory.Delete(setupDirectory));
        }
    }

    private static int Install(string packagePath, string setupDirectory, bool quiet)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = setupDirectory
        };
        start.ArgumentList.Add("/i");
        start.ArgumentList.Add(packagePath);
        start.ArgumentList.Add("/norestart");
        if (quiet) start.ArgumentList.Add("/qn");

        using var installer = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Windows Installer。");
        installer.WaitForExit();
        return installer.ExitCode;
    }

    private static void TryCleanup(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}
