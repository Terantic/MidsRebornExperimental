using Microsoft.Extensions.Logging;
using MRB_Boostrap.UI;
using MRB_Boostrap.Utilities;

namespace MRB_Boostrap.Services;

public sealed class RestoreFlowManager : IRestoreFlowManager
{
    private readonly IBackupManager _backupManager;
    private readonly IUIManager _ui;
    private readonly ILogger<RestoreFlowManager> _logger;
    private readonly LoggingPipeline _pipeline;

    /// <summary>
    /// Name application backups were stored under before <see cref="BackupManager.ApplicationBackupName"/>
    /// (the application's display name). Still accepted so an existing backup remains reachable.
    /// </summary>
    public const string LegacyApplicationBackupName = "Mids Reborn";

    public RestoreFlowManager(
        IBackupManager backupManager,
        IUIManager ui,
        ILogger<RestoreFlowManager> logger, LoggingPipeline pipeline)
    {
        _backupManager = backupManager;
        _ui = ui;
        _logger = logger;
        _pipeline = pipeline;
    }

    public async Task<int> ExecuteRestoreAsync(string name, string patchType, CancellationToken cancellationToken)
    {
        using (_logger.BeginScope("RestoreFlow {Name}", name))
        {
            _logger.LogInformation("Starting manual restore for {Name} ({PatchType})", name, patchType);

            bool isApplication = patchType.Equals("Application", StringComparison.OrdinalIgnoreCase);
            if (!isApplication && !PathSafety.IsSafeSegment(name))
            {
                _logger.LogError("Rejected rollback target with unsafe name: {Name}", name);
                await _ui.ShowStatusAsync("Restore failed.", 1500, false, CancellationToken.None);
                return -1;
            }

            string baseDir = AppContext.BaseDirectory;
            string installPath = isApplication
                ? baseDir
                : Path.Combine(baseDir, "Databases", name);

            string backupPath = BackupManager.GetBackupPath(baseDir, patchType, name);
            if (isApplication && !File.Exists(BackupManager.GetArchivePath(backupPath)))
            {
                string legacyPath = Path.Combine(baseDir, "Backup", LegacyApplicationBackupName);
                if (File.Exists(BackupManager.GetArchivePath(legacyPath)))
                {
                    _logger.LogInformation("Using legacy application backup: {Path}", legacyPath);
                    backupPath = legacyPath;
                }
            }

            try
            {
                await _ui.ShowStatusAsync($"Restoring {name}...", 500, false, cancellationToken);

                // Ensure Mids Reborn is not running prior to restore
                await _ui.ShowStatusAsync("Checking for running processes...", 500, false, cancellationToken);

                if (ProcessUtils.IsProcessRunning("MidsReborn"))
                {
                    _logger.LogWarning("Mids Reborn is running. Attempting to shut it down.");
                    await _ui.ShowStatusAsync("Closing Mids Reborn...", 750, false, cancellationToken);

                    bool closed = await ProcessUtils.KillProcessAsync("MidsReborn", cancellationToken);
                    if (!closed)
                    {
                        await _ui.ShowStatusAsync("Please close Mids Reborn and try again.", 1500, false, CancellationToken.None);
                        return -1;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return await CancelledAsync(name);
            }

            _ui.TriggerRollback();
            await _ui.ShowStatusAsync("Rolling back...", 500, true, CancellationToken.None);
            _ui.UpdateProgress(0f);

            // Reading and validating the backup can still be cancelled; once the restore starts
            // changing the installation it always runs to completion
            bool destructive = false;
            bool restored = await _pipeline.RunStepAsync("Restore backup", () =>
                _backupManager.RestoreBackupAsync(installPath, backupPath, patchType, name, cancellationToken, () =>
                {
                    destructive = true;
                    _ui.DisableCancel();
                }));

            if (!restored && !destructive && cancellationToken.IsCancellationRequested)
                return await CancelledAsync(name);

            if (restored)
            {
                _logger.LogInformation("Restore complete for {Name}", name);
                await _ui.ShowStatusAsync("Restore complete.", 1500, false, CancellationToken.None);
            }
            else
            {
                _logger.LogError("Restore failed for {Name}", name);
                await _ui.ShowStatusAsync("Restore failed.", 1500, false, CancellationToken.None);
            }

            _ui.ShowProgressBar(false);
            await Task.Delay(1500, CancellationToken.None);

            return restored ? 0 : -1;
        }
    }

    private async Task<int> CancelledAsync(string name)
    {
        _logger.LogWarning("Restore cancelled by user for {Name}", name);
        _ui.TriggerCancel();
        await _ui.ShowStatusAsync("Restore cancelled.", 1000, false, CancellationToken.None);
        return -1;
    }
}
