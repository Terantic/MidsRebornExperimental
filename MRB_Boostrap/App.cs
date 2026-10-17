using Microsoft.Extensions.Logging;
using MRB_Boostrap.Models;
using MRB_Boostrap.Services;
using MRB_Boostrap.UI;
using MRB_Boostrap.Utilities;
using Serilog;

namespace MRB_Boostrap;

public sealed class App
{
    private readonly IPatchFlowManager _flowManager;
    private readonly List<UpdateEntry> _entries;
    private readonly IUIManager _ui;
    private readonly ILogger<App> _logger;

    public App(IPatchFlowManager flowManager, List<UpdateEntry> entries, IUIManager ui, ILogger<App> logger)
    {
        _flowManager = flowManager;
        _entries = entries;
        _ui = ui;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Mids Reborn stays closed until every selected entry is applied; restarting between
        // entries would load a half-updated install (e.g. new app with the old database).
        foreach (var entry in _entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Update cancelled before {Name}. Skipping restart.", entry.Name);
                return;
            }

            // Each entry can be cancelled until it starts installing, including after an earlier entry
            // disabled Cancel for its own installation. Entries already installed stay installed.
            _ui.EnableCancel();

            int result = await _flowManager.ExecutePatchFlowAsync(entry, cancellationToken);
            if (result != 0)
            {
                _logger.LogWarning("Patch failed for {Name}. Skipping restart.", entry.Name);
                return;
            }
        }

        if (_entries.Count == 0)
            return;

        _ui.UpdateStatus("Restarting Mids Reborn...");
        await Task.Delay(1000, CancellationToken.None);

        ProcessUtils.StartMidsReborn();
    }
}