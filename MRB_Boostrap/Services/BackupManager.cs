using Microsoft.Extensions.Logging;
using MRB_Boostrap.Models;
using MRB_Boostrap.Utilities;

namespace MRB_Boostrap.Services;

public sealed class BackupManager : IBackupManager
{
    private readonly ILogger<BackupManager> _logger;
    private readonly IFileCompressor _compressor;
    private readonly IFileDecompressor _decompressor;

    /// <summary>
    /// Canonical identifier of the application backup (Backup/Application.mrbak), shared by
    /// backup creation and every restore path so they always agree on the file name.
    /// </summary>
    public const string ApplicationBackupName = "Application";

    // An application restore requires this in the backup: an older bootstrapper could write a database
    // backup under the application backup's name, and restoring that would wipe the installation.
    private const string ApplicationMarkerFile = "MidsReborn.exe";

    // Top-level prefixes left alone by application backup/restore (prefix match, so "Data" also covers "Databases")
    private static readonly string[] ExcludedApplicationPrefixes = ["Logs", "Staging", "Backup", "Data", "Patches"];

    // The running bootstrapper is loaded from these files and cannot be replaced while it runs,
    // so they never take part in an application backup or restore (including entries in older backups).
    private static readonly HashSet<string> BootstrapperFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "MRBBootstrap.exe", "MRBBootstrap.dll", "MRBBootstrap.pdb",
        "MRBBootstrap.runtimeconfig.json", "MRBBootstrap.deps.json"
    };

    public BackupManager(ILogger<BackupManager> logger, IFileCompressor compressor, IFileDecompressor decompressor)
    {
        _logger = logger;
        _compressor = compressor;
        _decompressor = decompressor;
    }

    public async Task<bool> CreateBackupAsync(string installPath, string backupPath, string patchType, string dbName, CancellationToken cancellationToken)
    {
        try
        {
            List<FileEntry> entries = [];

            if (patchType.Equals("Application", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string rel = Path.GetRelativePath(installPath, file);
                    if (IsExcludedFromApplicationBackup(rel))
                        continue;

                    entries.Add(new FileEntry
                    {
                        FileName = Path.GetFileName(file),
                        Directory = Path.GetDirectoryName(rel) ?? string.Empty,
                        Data = await File.ReadAllBytesAsync(file, cancellationToken)
                    });
                }
            }
            else if (patchType.Equals("Database", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dbName))
            {
                string sourceDb = installPath;

                if (!Directory.Exists(sourceDb))
                {
                    _logger.LogError("Database source directory does not exist: {Path}", sourceDb);
                    return false;
                }

                foreach (string file in Directory.EnumerateFiles(sourceDb, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string rel = Path.GetRelativePath(sourceDb, file);

                    entries.Add(new FileEntry
                    {
                        FileName = Path.GetFileName(file),
                        Directory = Path.GetDirectoryName(rel) ?? string.Empty,
                        Data = await File.ReadAllBytesAsync(file, cancellationToken)
                    });
                }
            }

            if (entries.Count == 0)
            {
                _logger.LogWarning("No files found to backup from {Source}", installPath);
                return false;
            }

            string bakPath = GetArchivePath(backupPath);
            string tempPath = bakPath + ".tmp";

            Directory.CreateDirectory(Path.GetDirectoryName(bakPath)!);

            // The previous backup is only replaced once the new one is complete
            try
            {
                bool ok = await _compressor.CompressAsync(entries, tempPath, cancellationToken);
                if (!ok)
                {
                    _logger.LogError("Failed to write compressed backup to {Path}", tempPath);
                    return false;
                }

                File.Move(tempPath, bakPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }

            _logger.LogInformation("Compressed backup created at {Path}", bakPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Backup creation cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup.");
            return false;
        }
    }

    public async Task<bool> RestoreBackupAsync(string installPath, string backupPath, string patchType, string name, CancellationToken cancellationToken, Action? onDestructivePhase = null)
    {
        string bakFile = GetArchivePath(backupPath);
        string scratchPath = backupPath + ".restoring";
        bool isApplication = patchType.Equals("Application", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(bakFile))
            {
                _logger.LogError("Missing backup file: {Path}", bakFile);
                return false;
            }

            // Phase 1: fully read and validate the backup before anything is deleted.
            // The decompressor returns no entries for a truncated or inconsistent archive.
            List<FileEntry> entries = await _decompressor.DecompressAsync(bakFile, cancellationToken);
            if (entries.Count == 0)
            {
                _logger.LogError("Backup is empty, incomplete or corrupt: {Path}", bakFile);
                return false;
            }

            var toRestore = new List<(string RelativePath, FileEntry Entry)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in entries)
            {
                if (!PathSafety.TryResolveEntry(installPath, file.Directory, file.FileName, out string rel, out _))
                {
                    _logger.LogError("Backup contains an unsafe path: Directory='{Dir}', FileName='{File}'", file.Directory, file.FileName);
                    return false;
                }

                if (!seen.Add(rel))
                {
                    _logger.LogError("Backup contains a duplicate entry: {Path}", rel);
                    return false;
                }

                if (file.Directory.Equals("Logs", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (isApplication && IsExcludedFromApplicationBackup(rel))
                {
                    _logger.LogInformation("Skipping excluded backup entry: {Path}", rel);
                    continue;
                }

                toRestore.Add((rel, file));
            }

            if (toRestore.Count == 0)
            {
                _logger.LogError("Backup contains no restorable files: {Path}", bakFile);
                return false;
            }

            if (isApplication && !toRestore.Any(r => r.RelativePath.Equals(ApplicationMarkerFile, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError("Backup does not contain the application ({File}): {Path}", ApplicationMarkerFile, bakFile);
                return false;
            }

            // Phase 2: write the restored files to a scratch folder first, so a write failure
            // (disk full, permissions) is discovered before the installation is touched.
            TryDeleteDirectory(scratchPath);
            foreach (var (rel, file) in toRestore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!PathSafety.TryResolveUnderRoot(scratchPath, rel, out string scratchFile))
                {
                    _logger.LogError("Failed to resolve scratch path for {Path}", rel);
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(scratchFile)!);
                await File.WriteAllBytesAsync(scratchFile, file.Data, cancellationToken);
            }

            // Last cancellation point: from here on the restore is destructive and is never cancelled part-way.
            cancellationToken.ThrowIfCancellationRequested();
            onDestructivePhase?.Invoke();

            // Phase 3: clean existing install path
            if (isApplication)
            {
                foreach (string file in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(installPath, file);
                    if (IsExcludedFromApplicationBackup(rel))
                        continue;

                    try { File.Delete(file); }
                    catch (Exception ex)
                    {
                        // The restored copy overwrites it below where possible; anything else is caught in phase 5
                        _logger.LogWarning(ex, "Failed to delete {Path} before restore", file);
                    }
                }
            }
            else if (patchType.Equals("Database", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(installPath))
                {
                    try { Directory.Delete(installPath, recursive: true); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete existing database directory: {Path}", installPath);
                    }
                }
            }

            // Phase 4: move restored files into place. Keep going on individual failures so as
            // much of the installation as possible is recovered, but report the restore as failed.
            bool complete = true;
            foreach (var (rel, _) in toRestore)
            {
                PathSafety.TryResolveUnderRoot(scratchPath, rel, out string scratchFile);
                PathSafety.TryResolveUnderRoot(installPath, rel, out string targetFile);

                try
                {
                    string? targetDir = Path.GetDirectoryName(targetFile);
                    if (!string.IsNullOrEmpty(targetDir))
                        Directory.CreateDirectory(targetDir);

                    File.Move(scratchFile, targetFile, overwrite: true);
                }
                catch (Exception ex)
                {
                    complete = false;
                    _logger.LogError(ex, "Failed to restore {Path}", targetFile);
                }
            }

            // Phase 5: verify only the restored files remain (excluded application paths are left alone on purpose),
            // so a newer file that could not be deleted is not mistaken for a complete restore.
            var restored = new HashSet<string>(toRestore.Select(r => r.RelativePath), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(installPath))
            {
                foreach (string file in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(installPath, file);
                    if (restored.Contains(rel) || (isApplication && IsExcludedFromApplicationBackup(rel)))
                        continue;

                    complete = false;
                    _logger.LogError("File not in the backup could not be removed: {Path}", file);
                }
            }

            if (!complete)
            {
                _logger.LogError("Backup restored only partially. Backup kept for another attempt: {Path}", bakFile);
                return false;
            }

            _logger.LogInformation("Backup restored from {Path}", bakFile);

            // Delete backup only after a complete restoration
            try
            {
                File.Delete(bakFile);
                _logger.LogInformation("Deleted backup after restore: {Path}", bakFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete backup after restore: {Path}", bakFile);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Restore cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed.");
            return false;
        }
        finally
        {
            TryDeleteDirectory(scratchPath);
        }
    }

    /// <summary>
    /// Resolves the backup path (without the .mrbak extension) for an update entry or rollback target.
    /// Application backups always use <see cref="ApplicationBackupName"/>, whatever display name the entry carries.
    /// Database backups live in Backup/Databases, so no database name can share the application backup.
    /// Backups an older bootstrapper wrote for a database (Backup/&lt;name&gt;.mrbak) are no longer read.
    /// </summary>
    public static string GetBackupPath(string baseDir, string patchType, string name) =>
        patchType.Equals("Application", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(baseDir, "Backup", ApplicationBackupName)
            : Path.Combine(baseDir, "Backup", "Databases", name);

    /// <summary>
    /// The archive file for a <see cref="GetBackupPath"/> result. The extension is appended, never substituted,
    /// so dotted database names stay distinct.
    /// </summary>
    public static string GetArchivePath(string backupPath) => backupPath + ".mrbak";

    private static bool IsExcludedFromApplicationBackup(string relativePath) =>
        BootstrapperFiles.Contains(relativePath) ||
        ExcludedApplicationPrefixes.Any(ex => relativePath.StartsWith(ex, StringComparison.OrdinalIgnoreCase));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
