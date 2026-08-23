using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

/// <summary>
/// Owns the temporary Full Stealth files placed in the Steam directory.
/// A persisted transaction record makes recovery possible after a crash or forced shutdown.
/// </summary>
public static class FullStealthService
{
    private const string TransactionFileName = ".glm-full-stealth.json";
    private const string User32BackupName = ".glm-user32.backup";
    private const string AppListBackupName = ".glm-applist.backup";
    private const string User32StagingName = ".glm-user32.staging";
    private const string AppListStagingName = ".glm-applist.staging";
    private const string LegacyMarkerName = ".glm-full-stealth";
    private const string LegacyUser32BackupName = "user32.dll.glm-backup";
    private const string LegacyAppListBackupName = "AppList.glm-backup";

    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string GetSelectedDllName(Config config) =>
        config.FullStealthVariant == FullStealthVariant.SteamFamilies ? "user32SF.dll" : "user32.dll";

    public static List<string> ValidateSource(Config config)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(config.GreenLumaPath) || !Directory.Exists(config.GreenLumaPath))
        {
            issues.Add("Full Stealth source directory does not exist.");
            return issues;
        }

        if (!string.IsNullOrWhiteSpace(config.SteamPath) &&
            string.Equals(Path.GetFullPath(config.GreenLumaPath), Path.GetFullPath(config.SteamPath),
                StringComparison.OrdinalIgnoreCase))
            issues.Add("Full Stealth source directory must be outside the Steam directory.");

        var dllName = GetSelectedDllName(config);
        if (!File.Exists(Path.Combine(config.GreenLumaPath, dllName)))
            issues.Add($"Full Stealth source file {dllName} is missing.");
        if (!Directory.Exists(Path.Combine(config.GreenLumaPath, "AppList")))
            issues.Add("Full Stealth source AppList is missing.");

        return issues;
    }

    public static bool TryStage(Config config, out string? error)
    {
        error = null;
        var sourceIssues = ValidateSource(config);
        if (sourceIssues.Count > 0)
        {
            error = string.Join(" ", sourceIssues);
            return false;
        }

        var steamPath = Path.GetFullPath(config.SteamPath);
        lock (PathLocks.GetOrAdd(steamPath, _ => new object()))
        {
            try
            {
                CleanupCore(steamPath);

                var paths = new TransactionPaths(steamPath);
                if (File.Exists(paths.BackupDll) || Directory.Exists(paths.BackupAppList))
                    throw new IOException("Unresolved Full Stealth backup files were found in the Steam directory.");
                CleanupStagingPaths(paths);

                File.Copy(Path.Combine(config.GreenLumaPath, GetSelectedDllName(config)), paths.StagedDll, true);
                CopyDirectory(Path.Combine(config.GreenLumaPath, "AppList"), paths.StagedAppList);

                var transaction = new FullStealthTransaction
                {
                    HadOriginalUser32 = File.Exists(paths.DestinationDll),
                    HadOriginalAppList = Directory.Exists(paths.DestinationAppList)
                };

                // Persist intent before touching any pre-existing Steam files.
                WriteTransaction(paths.TransactionFile, transaction);

                if (transaction.HadOriginalUser32)
                    File.Move(paths.DestinationDll, paths.BackupDll);
                if (transaction.HadOriginalAppList)
                    Directory.Move(paths.DestinationAppList, paths.BackupAppList);

                File.Move(paths.StagedDll, paths.DestinationDll);
                Directory.Move(paths.StagedAppList, paths.DestinationAppList);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FullStealthService.Stage");
                try
                {
                    CleanupCore(steamPath);
                }
                catch (Exception cleanupEx)
                {
                    Logger.Error(cleanupEx, "FullStealthService.StageRollback");
                }

                error = ex.Message;
                return false;
            }
        }
    }

    public static void Cleanup(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath)) return;

        try
        {
            var normalizedPath = Path.GetFullPath(steamPath);
            lock (PathLocks.GetOrAdd(normalizedPath, _ => new object()))
                CleanupCore(normalizedPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "FullStealthService.Cleanup");
        }
    }

    private static void CleanupCore(string steamPath)
    {
        CleanupLegacyTransaction(steamPath);
        var paths = new TransactionPaths(steamPath);
        if (!File.Exists(paths.TransactionFile))
        {
            CleanupStagingPaths(paths);
            return;
        }

        FullStealthTransaction transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<FullStealthTransaction>(
                              File.ReadAllText(paths.TransactionFile)) ?? new FullStealthTransaction();
        }
        catch
        {
            // Backups are authoritative if the transaction record was only partially written.
            transaction = new FullStealthTransaction
            {
                HadOriginalUser32 = File.Exists(paths.BackupDll),
                HadOriginalAppList = Directory.Exists(paths.BackupAppList)
            };
        }

        if (transaction.HadOriginalUser32)
        {
            if (File.Exists(paths.BackupDll))
            {
                if (File.Exists(paths.DestinationDll)) File.Delete(paths.DestinationDll);
                File.Move(paths.BackupDll, paths.DestinationDll);
            }
        }
        else if (File.Exists(paths.DestinationDll))
        {
            File.Delete(paths.DestinationDll);
        }

        if (transaction.HadOriginalAppList)
        {
            if (Directory.Exists(paths.BackupAppList))
            {
                if (Directory.Exists(paths.DestinationAppList)) Directory.Delete(paths.DestinationAppList, true);
                Directory.Move(paths.BackupAppList, paths.DestinationAppList);
            }
        }
        else if (Directory.Exists(paths.DestinationAppList))
        {
            Directory.Delete(paths.DestinationAppList, true);
        }

        CleanupStagingPaths(paths);
        File.Delete(paths.TransactionFile);
    }

    private static void CleanupLegacyTransaction(string steamPath)
    {
        var marker = Path.Combine(steamPath, LegacyMarkerName);
        if (!File.Exists(marker)) return;

        var destinationDll = Path.Combine(steamPath, "user32.dll");
        var backupDll = Path.Combine(steamPath, LegacyUser32BackupName);
        var destinationAppList = Path.Combine(steamPath, "AppList");
        var backupAppList = Path.Combine(steamPath, LegacyAppListBackupName);

        if (File.Exists(backupDll))
        {
            if (File.Exists(destinationDll)) File.Delete(destinationDll);
            File.Move(backupDll, destinationDll);
        }
        else if (File.Exists(destinationDll))
        {
            File.Delete(destinationDll);
        }

        if (Directory.Exists(backupAppList))
        {
            if (Directory.Exists(destinationAppList)) Directory.Delete(destinationAppList, true);
            Directory.Move(backupAppList, destinationAppList);
        }
        else if (Directory.Exists(destinationAppList))
        {
            Directory.Delete(destinationAppList, true);
        }

        File.Delete(marker);
    }

    private static void CleanupStagingPaths(TransactionPaths paths)
    {
        if (File.Exists(paths.StagedDll)) File.Delete(paths.StagedDll);
        if (Directory.Exists(paths.StagedAppList)) Directory.Delete(paths.StagedAppList, true);
    }

    private static void WriteTransaction(string path, FullStealthTransaction transaction)
    {
        using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, transaction);
        stream.Flush(true);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }

    private sealed class FullStealthTransaction
    {
        public bool HadOriginalUser32 { get; set; }
        public bool HadOriginalAppList { get; set; }
    }

    private sealed class TransactionPaths(string steamPath)
    {
        public string TransactionFile { get; } = Path.Combine(steamPath, TransactionFileName);
        public string DestinationDll { get; } = Path.Combine(steamPath, "user32.dll");
        public string DestinationAppList { get; } = Path.Combine(steamPath, "AppList");
        public string BackupDll { get; } = Path.Combine(steamPath, User32BackupName);
        public string BackupAppList { get; } = Path.Combine(steamPath, AppListBackupName);
        public string StagedDll { get; } = Path.Combine(steamPath, User32StagingName);
        public string StagedAppList { get; } = Path.Combine(steamPath, AppListStagingName);
    }
}
