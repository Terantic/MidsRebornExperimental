using Microsoft.Extensions.Logging;
using MRB_Boostrap.Models;
using MRB_Boostrap.Services;
using MRB_Boostrap.UI;
using MRB_Boostrap.Utilities;

namespace MRB_Boostrap;

public sealed class RestoreApp
{
    private readonly IRestoreFlowManager _flow;
    private readonly BootstrapArguments _args;
    private readonly IUIManager _ui;
    private readonly ILogger<RestoreApp> _logger;

    public RestoreApp(IRestoreFlowManager flow, BootstrapArguments args, IUIManager ui, ILogger<RestoreApp> logger)
    {
        _flow = flow;
        _args = args;
        _ui = ui;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        string target = _args.RollbackTarget!;

        // "application" (or the application's display name, which update entries carry) selects the
        // application backup; the backup file name itself comes from BackupManager.ApplicationBackupName.
        bool isApplication = target.Equals("application", StringComparison.OrdinalIgnoreCase) ||
                             target.Equals(BackupManager.ApplicationBackupName, StringComparison.OrdinalIgnoreCase) ||
                             target.Equals(RestoreFlowManager.LegacyApplicationBackupName, StringComparison.OrdinalIgnoreCase);
        string type = isApplication ? "Application" : "Database";
        string name = isApplication ? BackupManager.ApplicationBackupName : target;

        _logger.LogInformation("Initiating restore for {Target} ({Type})", target, type);
        int result = await _flow.ExecuteRestoreAsync(name, type, cancellationToken);


        if (result == 0)
        {
            _logger.LogInformation("Restore succeeded. Restarting Mids Reborn...");
            _ui.UpdateStatus("Restarting Mids Reborn...");
            await Task.Delay(1000);
            ProcessUtils.StartMidsReborn();
        }
        else
        {
            _logger.LogWarning("Restore failed or was cancelled for {Target}", target);
        }
    }
}