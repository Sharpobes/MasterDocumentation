using System.IO;
using System.IO.Compression;
using System.Text;

namespace MasterDocumentation.Setup;

/// <summary>
/// Дистрибутив приложения дописан в конец файла установщика: [ZIP][длина Int64][сигнатура].
/// Так один EXE остаётся автономным (не требует ни распакованных файлов рядом, ни интернета),
/// а сборка не должна встраивать сотни мегабайт в ресурсы во время компиляции.
/// </summary>
public static class PayloadArchive
{
    public static readonly byte[] Signature = Encoding.ASCII.GetBytes("MDSETUP1");
    private const int FooterSize = 16;
    /// <summary>
    /// Подписанный установщик хранит таблицу сертификатов в конце файла, поэтому метка footer'а
    /// уже не последние байты: она ищется в хвосте файла с конца.
    /// </summary>
    private const int TailSearchSize = 1 << 20;

    public static bool Exists => TryReadFooter(out _, out _);

    /// <summary>Открывает встроенный дистрибутив как ZIP-архив.</summary>
    public static ZipArchive Open()
    {
        if (!TryReadFooter(out var offset, out var length))
            throw new InvalidOperationException("Установщик собран без дистрибутива приложения. Скачайте официальный файл установки со страницы релизов.");
        var file = new FileStream(SetupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new ZipArchive(new SubStream(file, offset, length), ZipArchiveMode.Read, leaveOpen: false);
    }

    public static string SetupPath => Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к файлу установки.");

    private static bool TryReadFooter(out long offset, out long length)
    {
        offset = 0;
        length = 0;
        try
        {
            using var file = new FileStream(SetupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length <= FooterSize) return false;
            var tailLength = (int)Math.Min(TailSearchSize, file.Length);
            var tail = new byte[tailLength];
            file.Seek(-tailLength, SeekOrigin.End);
            file.ReadExactly(tail);
            var tailStart = file.Length - tailLength;

            for (var index = tail.Length - Signature.Length; index >= 8; index--)
            {
                if (!MatchesSignature(tail, index)) continue;
                var candidate = BitConverter.ToInt64(tail, index - 8);
                var start = tailStart + index - 8 - candidate;
                if (candidate <= 0 || start <= 0) continue;
                length = candidate;
                offset = start;
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool MatchesSignature(byte[] buffer, int index)
    {
        for (var i = 0; i < Signature.Length; i++)
            if (buffer[index + i] != Signature[i]) return false;
        return true;
    }

    /// <summary>Окно только для чтения внутри файла установщика — ZipArchive требует поток с произвольным доступом.</summary>
    private sealed class SubStream(Stream source, long offset, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int index, int count)
        {
            if (_position >= length) return 0;
            source.Position = offset + _position;
            var read = source.Read(buffer, index, (int)Math.Min(count, length - _position));
            _position += read;
            return read;
        }

        public override long Seek(long value, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => value,
                SeekOrigin.Current => _position + value,
                _ => length + value,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int index, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}
