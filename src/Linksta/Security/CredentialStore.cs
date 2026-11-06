using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Linksta.Config;

namespace Linksta.Security;

/// <summary>
/// Speichert das ILIAS-Passwort im Betriebssystem-Schlüsselbund:
/// macOS Keychain, Linux Secret Service (secret-tool), Windows DPAPI.
/// Fallback: Datei mit Rechten 600. Die Umgebungsvariable LINKSTA_PASSWORD hat immer Vorrang.
/// </summary>
public static class CredentialStore
{
    private const string Service = "linksta";

    public static string? EnvPassword => Environment.GetEnvironmentVariable("LINKSTA_PASSWORD");

    public static string BackendName
    {
        get
        {
            if (OperatingSystem.IsMacOS()) return "macOS-Schlüsselbund";
            if (OperatingSystem.IsWindows()) return "Windows DPAPI";
            if (OperatingSystem.IsLinux() && HasSecretTool()) return "Secret Service (secret-tool)";
            return "Datei (nur für deinen Benutzer lesbar)";
        }
    }

    public static string? Get(string user)
    {
        if (!string.IsNullOrEmpty(EnvPassword))
            return EnvPassword;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var (code, output, _) = Run("security", ["find-generic-password", "-s", Service, "-a", user, "-w"], null);
                return code == 0 ? output.TrimEnd('\r', '\n') : null;
            }

            if (OperatingSystem.IsWindows())
                return GetDpapi(user);

            if (OperatingSystem.IsLinux() && HasSecretTool())
            {
                var (code, output, _) = Run("secret-tool", ["lookup", "service", Service, "username", user], null);
                if (code == 0 && output.Length > 0)
                    return output.TrimEnd('\r', '\n');
            }
        }
        catch
        {
            // Backend nicht verfügbar → Datei-Fallback probieren
        }

        return GetFile(user);
    }

    public static void Set(string user, string password)
    {
        if (OperatingSystem.IsMacOS())
        {
            // Über "security -i" (stdin), damit das Passwort nicht in der Prozessliste auftaucht.
            var cmd = $"add-generic-password -U -s {Quote(Service)} -a {Quote(user)} -l {Quote("linksta (ILIAS)")} -w {Quote(password)}\n";
            var (code, output, error) = Run("security", ["-i"], cmd);
            if (code != 0 || error.Contains("error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Konnte Passwort nicht im Schlüsselbund speichern: " + error + output);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            SetDpapi(user, password);
            return;
        }

        if (OperatingSystem.IsLinux() && HasSecretTool())
        {
            var (code, _, _) = Run("secret-tool", ["store", "--label=linksta (ILIAS)", "service", Service, "username", user], password);
            if (code == 0)
                return;
        }

        SetFile(user, password);
    }

    public static void Delete(string user)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Run("security", ["delete-generic-password", "-s", Service, "-a", user], null);
            else if (OperatingSystem.IsLinux() && HasSecretTool())
                Run("secret-tool", ["clear", "service", Service, "username", user], null);
        }
        catch
        {
            // egal
        }

        foreach (var f in new[] { DpapiPath, FilePath })
            if (File.Exists(f))
                File.Delete(f);
    }

    // ---------- Windows ----------

    private static string DpapiPath => Path.Combine(ConfigStore.ConfigDir, "credential.dpapi");

    [SupportedOSPlatform("windows")]
    private static string? GetDpapi(string user)
    {
        if (!File.Exists(DpapiPath))
            return null;
        var data = ProtectedData.Unprotect(File.ReadAllBytes(DpapiPath), Encoding.UTF8.GetBytes(user), DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    [SupportedOSPlatform("windows")]
    private static void SetDpapi(string user, string password)
    {
        Directory.CreateDirectory(ConfigStore.ConfigDir);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(user), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(DpapiPath, data);
    }

    // ---------- Datei-Fallback ----------

    private static string FilePath => Path.Combine(ConfigStore.ConfigDir, "credential");

    private static string? GetFile(string user)
    {
        if (!File.Exists(FilePath))
            return null;
        var lines = File.ReadAllLines(FilePath);
        if (lines.Length < 2 || lines[0] != user)
            return null;
        return Encoding.UTF8.GetString(Convert.FromBase64String(lines[1]));
    }

    private static void SetFile(string user, string password)
    {
        Directory.CreateDirectory(ConfigStore.ConfigDir);
        File.WriteAllLines(FilePath, [user, Convert.ToBase64String(Encoding.UTF8.GetBytes(password))]);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ---------- Helpers ----------

    private static bool? _hasSecretTool;

    private static bool HasSecretTool()
    {
        if (_hasSecretTool is { } b)
            return b;
        try
        {
            var (code, _, _) = Run("secret-tool", ["--version"], null);
            _hasSecretTool = code == 0;
        }
        catch
        {
            _hasSecretTool = false;
        }
        return _hasSecretTool.Value;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static (int Code, string Output, string Error) Run(string file, string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} konnte nicht gestartet werden.");
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
