using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SolastaUnfinishedBusiness.Models;

internal enum SaveDependency { Unknown, Required, NoKnownDependency }

internal static class SaveStampContext
{
    private const string Prefix = "FB-save/1 ";

    // Listing reads at most the ten-byte fixed header and a bounded comment. The FB writer emits
    // FCOMMENT alone. Other gzip variants, malformed stamps and old or vanilla saves are unknown.
    internal static SaveDependency Read(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(input);
            var header = reader.ReadBytes(10);
            if (header.Length != 10 || header[0] != 31 || header[1] != 139 || header[2] != 8 || header[3] != 16)
            {
                return SaveDependency.Unknown;
            }

            var comment = new List<byte>();
            while (comment.Count < 160)
            {
                var value = reader.ReadByte();
                if (value != 0)
                {
                    if (value < 32 || value > 126) { return SaveDependency.Unknown; }
                    comment.Add(value);
                    continue;
                }

                var stamp = Encoding.ASCII.GetString(comment.ToArray());
                if (!stamp.StartsWith(Prefix, StringComparison.Ordinal)) { break; }
                var fields = stamp.Substring(Prefix.Length).Split(' ');
                if (fields.Length != 2 || fields[0].Length == 0) { break; }
                return fields[1] switch
                {
                    "required" => SaveDependency.Required,
                    "clear" => SaveDependency.NoKnownDependency,
                    _ => SaveDependency.Unknown
                };
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }

        return SaveDependency.Unknown;
    }

    internal static GZipStream Open(Stream output, CompressionMode mode, GameSerializationManager manager)
    {
        var classification = SaveDependencyClassifier.Classify(manager.cachedSaveMemoryStream);
        var token = classification switch
        {
            SaveDependency.Required => "required",
            SaveDependency.NoKnownDependency => "clear",
            _ => "unknown"
        };
        var version = UpdateContext.LoadedBuildVersion ?? Main.ModVersion;
        var comment = Encoding.ASCII.GetBytes(Prefix + version + " " + token + "\0");
        return new GZipStream(new HeaderWriter(output, comment), mode);
    }

    // GZipStream owns compression and its original checksum/trailer. Buffer only its fixed header
    // even when split over writes, then stream the compressed payload directly into the game's file.
    // Cloud publication reads this same file after both writers have closed.
    private sealed class HeaderWriter(Stream output, byte[] comment) : Stream
    {
        private readonly byte[] _header = new byte[10];
        private int _received;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_received < _header.Length)
            {
                var take = Math.Min(count, _header.Length - _received);
                Buffer.BlockCopy(buffer, offset, _header, _received, take);
                _received += take;
                if (_received < _header.Length) { return; }
                var plain = _header[0] == 31 && _header[1] == 139 && _header[2] == 8 && _header[3] == 0;
                if (plain) { _header[3] = 16; }
                output.Write(_header, 0, _header.Length);
                if (plain) { output.Write(comment, 0, comment.Length); }
                offset += take;
                count -= take;
            }

            output.Write(buffer, offset, count);
        }

        public override void Flush() => output.Flush();
        protected override void Dispose(bool disposing)
        {
            // The game opens overwrites with File.OpenWrite, which can leave an old, longer tail.
            if (disposing && output.CanSeek) { output.SetLength(output.Position); }
            base.Dispose(disposing);
        }
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
