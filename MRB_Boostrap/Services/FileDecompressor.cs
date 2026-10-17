using ICSharpCode.SharpZipLib.Zip.Compression;
using Microsoft.Extensions.Logging;
using MRB_Boostrap.Models;
using MRB_Boostrap.Utilities;

namespace MRB_Boostrap.Services;

public sealed class FileDecompressor : IFileDecompressor
{
    private readonly ILogger<FileDecompressor> _logger;

    public FileDecompressor(ILogger<FileDecompressor> logger)
    {
        _logger = logger;
    }

    public async Task<List<FileEntry>> DecompressAsync(string filePath, CancellationToken cancellationToken)
    {
        var result = new List<FileEntry>();

        if (!File.Exists(filePath))
        {
            _logger.LogError("File not found: {Path}", filePath);
            return result;
        }

        _logger.LogInformation("Reading compressed file: {Path}", filePath);

        byte[] compressed;
        try
        {
            compressed = await File.ReadAllBytesAsync(filePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read file: {Path}", filePath);
            return result;
        }

        var inflater = new Inflater();
        inflater.SetInput(compressed);

        using var uncompressed = new MemoryStream();
        byte[] buffer = new byte[8192];

        try
        {
            while (!inflater.IsFinished)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int count = inflater.Inflate(buffer);
                if (count == 0 && inflater.IsNeedingInput)
                    break;

                uncompressed.Write(buffer, 0, count);
            }

            if (!inflater.IsFinished)
            {
                // A truncated stream must never be treated as a usable (partial) archive
                _logger.LogError("Inflater finished prematurely. Compressed data is incomplete: {Path}", filePath);
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inflater failed during decompression of {Path}", filePath);
            return result;
        }

        uncompressed.Position = 0;

        try
        {
            using var reader = new BinaryReader(uncompressed);

            string magic = ReadDotNetString(reader);
            if (magic != "Mids Reborn Patch Data" && magic != "Mids Reborn Backup Data")
            {
                _logger.LogError("Invalid header: {Header} in file {Path}", magic, filePath);
                return result;
            }

            uint fileCount = reader.ReadUInt32();
            _logger.LogInformation("Expecting {Count} file entries in: {Path}", fileCount, filePath);

            for (uint i = 0; i < fileCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    uint dataLength = reader.ReadUInt32();
                    string fileName = ReadDotNetString(reader);
                    string directory = NormalizeDirectory(ReadDotNetString(reader));

                    long remaining = uncompressed.Length - uncompressed.Position;
                    if (dataLength > remaining)
                        throw new InvalidDataException($"Entry declares {dataLength} bytes but only {remaining} remain.");

                    byte[] data = reader.ReadBytes((int)dataLength);
                    if (data.Length != dataLength)
                        throw new EndOfStreamException($"Short payload: expected {dataLength} bytes, read {data.Length}.");

                    result.Add(new FileEntry
                    {
                        FileName = fileName,
                        Directory = directory,
                        Data = data
                    });

                    _logger.LogInformation("Parsed file [{Index}/{Total}]: {Dir}/{File} ({Size:N0} bytes)",
                        i + 1, fileCount, directory, fileName, data.Length);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error parsing file entry {Index}", i);
                    break;
                }
            }

            // All-or-nothing: an incomplete archive yields no entries so callers never act on partial data
            if (result.Count != fileCount)
            {
                _logger.LogError("Parsed only {Count} of {Expected} files from: {Path}. Rejecting archive.", result.Count, fileCount, filePath);
                result.Clear();
            }
            else if (uncompressed.Position != uncompressed.Length)
            {
                _logger.LogError("Unexpected trailing data after {Count} entries in: {Path}. Rejecting archive.", fileCount, filePath);
                result.Clear();
            }
            else
            {
                _logger.LogInformation("Decompression complete. Parsed {Count} files from: {Path}", result.Count, filePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error reading decompressed data from {Path}", filePath);
            result.Clear();
        }

        return result;
    }

    // Shared with hash validation so archive and hash entries normalize identically.
    // Containment is enforced by the consumers (staging, install, restore) via PathSafety.
    private static string NormalizeDirectory(string dir) => PathSafety.NormalizeRelative(dir);

    private static string ReadDotNetString(BinaryReader reader)
    {
        int length = Read7BitEncodedInt(reader);
        if (length < 0 || length > 65536)
            throw new InvalidDataException($"Invalid string length: {length}");

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException($"Short string: expected {length} bytes, read {bytes.Length}.");

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        int result = 0;
        int shift = 0;

        for (int i = 0; i < 5; i++)
        {
            byte b = reader.ReadByte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;

            shift += 7;
        }

        return -1;
    }
}