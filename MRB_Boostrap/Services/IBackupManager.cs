namespace MRB_Boostrap.Services;

public interface IBackupManager
{
    Task<bool> CreateBackupAsync(string installPath, string backupPath, string patchType, string dbName, CancellationToken cancellationToken);
    // onDestructivePhase runs once the backup is validated, just before the installation is changed;
    // cancellation is not observed after that point.
    Task<bool> RestoreBackupAsync(string installPath, string backupPath, string patchType, string dbName, CancellationToken cancellationToken, Action? onDestructivePhase = null);
}