using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mids_Reborn.Core.Utils
{

    internal static class Compression
    {
        // Upper bound for decompressed build data; real builds are a few KiB, so anything near this is not a build
        internal const int MaxDecodedSize = 64 * 1024 * 1024;

        public static CompressionResult CompressToBase64(byte[] sourceBytes)
        {
            using var uncompressedStream = new MemoryStream(sourceBytes);
            using var compressedStream = new MemoryStream();
            using (var compressionStream = new BrotliStream(compressedStream, CompressionLevel.SmallestSize))
            {
                compressionStream.Write(sourceBytes, 0, sourceBytes.Length);
            }

            var compressedBytes = compressedStream.ToArray();
            var base64String = Convert.ToBase64String(compressedBytes);

            return new CompressionResult(base64String, sourceBytes.Length, compressedBytes.Length, base64String.Length);
        }

        public static CompressionResult DecompressFromBase64(string base64String)
        {
            var compressedBytes = Convert.FromBase64String(base64String);
            using var compressedStream = new MemoryStream(compressedBytes);
            using var decompressionStream = new BrotliStream(compressedStream, CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = decompressionStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decompressedStream.Length + bytesRead > MaxDecodedSize)
                {
                    throw new InvalidDataException("Decompressed data exceeds the maximum allowed size.");
                }

                decompressedStream.Write(buffer, 0, bytesRead);
            }

            var decompressedBytes = decompressedStream.ToArray();
            var outString = Encoding.UTF8.GetString(decompressedBytes);
            return new CompressionResult(outString, decompressedBytes.Length, compressedBytes.Length, base64String.Length);
        }
    }
}
