using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using MRB_Boostrap.Models;
using MRB_Boostrap.UI;
using MRB_Boostrap.Utilities;

namespace MRB_Boostrap.Services;

public sealed class HashValidator : IHashValidator
{
    private readonly ILogger<HashValidator> _logger;
    private readonly IUIManager _ui;

    private record HashEntry(string Directory, string FileName, string Hash);

    public HashValidator(ILogger<HashValidator> logger, IUIManager ui)
    {
        _logger = logger;
        _ui = ui;
    }

    public async Task<bool> ValidateAsync(List<FileEntry> files, string stagingPath, string hashPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(hashPath))
        {
            _logger.LogError("Hash file missing: {Path}", hashPath);
            _ui.UpdateStatus("Hash file not found.");
            return false;
        }

        List<HashEntry>? entries;
        try
        {
            string json = await File.ReadAllTextAsync(hashPath, cancellationToken);
            entries = JsonSerializer.Deserialize<List<HashEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or parse hash file.");
            _ui.UpdateStatus("Invalid hash file.");
            return false;
        }

        if (entries == null || entries.Count == 0)
        {
            _logger.LogWarning("Hash file is empty or invalid: {Path}", hashPath);
            _ui.UpdateStatus("Invalid hash file.");
            return false;
        }

        // Require an exact one-to-one match between hash entries and archive entries so that
        // every file that will be installed is covered by a hash, and nothing listed is missing.
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (HashEntry entry in entries)
        {
            if (!PathSafety.TryGetRelativeFilePath(entry.Directory, entry.FileName, out string rel) || string.IsNullOrWhiteSpace(entry.Hash))
            {
                _logger.LogError("Validation failed: Invalid hash entry '{Dir}' / '{File}'", entry.Directory, entry.FileName);
                _ui.UpdateStatus("Invalid hash file.");
                return false;
            }

            if (!expected.TryAdd(rel, entry.Hash))
            {
                _logger.LogError("Validation failed: Duplicate hash entry for '{Path}'", rel);
                _ui.UpdateStatus("Invalid hash file.");
                return false;
            }
        }

        var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileEntry file in files)
        {
            if (!PathSafety.TryGetRelativeFilePath(file.Directory, file.FileName, out string rel) || !archived.Add(rel))
            {
                _logger.LogError("Validation failed: Invalid or duplicate archive entry '{Dir}' / '{File}'", file.Directory, file.FileName);
                _ui.UpdateStatus("Validation failed.");
                return false;
            }

            if (!expected.ContainsKey(rel))
            {
                _logger.LogError("Validation failed: Archive entry '{Path}' has no hash entry", rel);
                _ui.UpdateStatus("Validation failed.");
                return false;
            }
        }

        if (archived.Count != expected.Count)
        {
            _logger.LogError("Validation failed: Hash file lists {Expected} files but archive contains {Actual}", expected.Count, archived.Count);
            _ui.UpdateStatus("Validation failed.");
            return false;
        }

        _ui.UpdateStatus("Validating files...");
        _ui.ShowProgressBar(true);
        _ui.UpdateProgress(0f);

        int total = expected.Count;
        int i = 0;
        foreach ((string rel, string hash) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PathSafety.TryResolveUnderRoot(stagingPath, rel, out string fullPath) || !File.Exists(fullPath))
            {
                _logger.LogError("Validation failed: Missing file '{File}' in staging path '{Staging}'", rel, stagingPath);
                _ui.UpdateStatus("Validation failed.");
                return false;
            }

            string actualHash = await ComputeSha256Async(fullPath, cancellationToken);

            if (!actualHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Validation failed: Hash mismatch for file '{File}'", rel);
                _logger.LogError("  Expected: {Expected}", hash);
                _logger.LogError("  Actual  : {Actual}", actualHash);
                _ui.UpdateStatus("Validation failed (hash mismatch).");
                return false;
            }

            _ui.UpdateProgress((float)++i / total);
        }

        _logger.LogInformation("Validation passed for all files.");
        return true;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}