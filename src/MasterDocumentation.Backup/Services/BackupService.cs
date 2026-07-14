using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Services;

public sealed class BackupService(DatabaseService database)
{
    private static readonly byte[] EncryptedMagic = "MDBKENC1"u8.ToArray();
    public string CreateBackup(bool automatic, string? password = null)
    {
        AppPaths.Ensure(); database.Checkpoint();
        var prefix = automatic ? "auto" : "manual";
        var target = Path.Combine(AppPaths.Backups, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.mdbackup");
        var temp = target + ".zip.tmp";
        if (File.Exists(temp)) File.Delete(temp);
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AddFile(archive, AppPaths.Database, "masterdocumentation.db", checksums); AddDirectory(archive, AppPaths.Assets, "assets", checksums);
                WriteJson(archive, "settings-export.json", new SettingsService().Load());
                WriteJson(archive, "checksum.json", checksums);
                WriteJson(archive, "manifest.json", new { formatVersion = 1, applicationVersion = typeof(BackupService).Assembly.GetName().Version?.ToString(), createdAt = DateTime.UtcNow, documents = database.CountDocuments(), attachments = Directory.EnumerateFiles(AppPaths.Assets, "*", SearchOption.AllDirectories).LongCount(), size = new FileInfo(AppPaths.Database).Length + DirectorySize(AppPaths.Assets), encrypted = !string.IsNullOrEmpty(password) });
            }
            if(string.IsNullOrEmpty(password))File.Move(temp,target,true);else{EncryptFile(temp,target,password);File.Delete(temp);}
        }
        catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        if (automatic) PruneAutomatic();
        LogService.Info($"Создана резервная копия {target}"); return target;
    }

    public static bool IsEncrypted(string path){using var stream=File.OpenRead(path);if(stream.Length<EncryptedMagic.Length)return false;Span<byte> magic=stackalloc byte[EncryptedMagic.Length];stream.ReadExactly(magic);return magic.SequenceEqual(EncryptedMagic);}
    public void Restore(string archivePath,string? password=null)
    {
        string? decrypted=null;var zipPath=archivePath;if(IsEncrypted(archivePath)){if(string.IsNullOrEmpty(password))throw new UnauthorizedAccessException("Для резервной копии требуется пароль.");decrypted=Path.Combine(Path.GetTempPath(),"MasterDocumentation-"+Guid.NewGuid()+".zip");DecryptFile(archivePath,decrypted,password);zipPath=decrypted;}
        try{
        using (var test = ZipFile.OpenRead(zipPath))
        {
            var dbEntry = test.GetEntry("masterdocumentation.db") ?? test.GetEntry("Data/master-documentation.db");
            if ((test.GetEntry("manifest.json") is null && test.GetEntry("manifest.txt") is null) || dbEntry is null)
                throw new InvalidDataException("Архив не является резервной копией MasterDocumentation.");
            if (test.Entries.Any(e => e.FullName.Contains("..") || Path.IsPathRooted(e.FullName))) throw new InvalidDataException("Архив содержит небезопасные пути.");
            var checksumEntry = test.GetEntry("checksum.json"); if (checksumEntry is not null) VerifyChecksums(test, checksumEntry);
        }
        CreateBackup(false); database.Checkpoint();
        var staging = Path.Combine(Path.GetTempPath(), "MasterDocumentation-Restore-" + Guid.NewGuid());
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            var restoredData = Directory.Exists(Path.Combine(staging, "Data")) ? Path.Combine(staging, "Data") : staging;
            var dbTemp = AppPaths.Database + ".restore";
            var restoredDb = File.Exists(Path.Combine(restoredData, "masterdocumentation.db")) ? Path.Combine(restoredData, "masterdocumentation.db") : Path.Combine(restoredData, "master-documentation.db");
            File.Copy(restoredDb, dbTemp, true);
            File.Move(dbTemp, AppPaths.Database, true);
            var restoredAssets = Directory.Exists(Path.Combine(restoredData, "assets")) ? Path.Combine(restoredData, "assets") : Path.Combine(restoredData, "Assets");
            if (Directory.Exists(restoredAssets)) CopyDirectory(restoredAssets, AppPaths.Assets);
            LogService.Info($"Восстановлено из {archivePath}");
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }finally{if(decrypted is not null&&File.Exists(decrypted))File.Delete(decrypted);}
    }

    public void Export(string target)
    {
        database.Checkpoint(); Directory.CreateDirectory(target);
        File.Copy(AppPaths.Database, Path.Combine(target, "master-documentation.db"), true);
        CopyDirectory(AppPaths.Assets, Path.Combine(target, "Assets"));
        File.WriteAllText(Path.Combine(target, "README.txt"), "Переносимый экспорт MasterDocumentation. Поместите базу и папку Assets в Data рядом с приложением.");
    }

    private static void AddDirectory(ZipArchive archive, string source, string prefix, Dictionary<string,string> checksums)
    { foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) AddFile(archive, file, prefix + "/" + Path.GetRelativePath(source, file).Replace('\\', '/'), checksums); }
    private static void AddFile(ZipArchive archive, string source, string entryName, Dictionary<string,string> checksums) { archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal); using var stream = File.OpenRead(source); checksums[entryName] = Convert.ToHexString(SHA256.HashData(stream)); }
    private static void WriteJson<T>(ZipArchive archive, string name, T value) { var entry = archive.CreateEntry(name); using var stream = entry.Open(); JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); }
    private static void VerifyChecksums(ZipArchive archive, ZipArchiveEntry checksumEntry) { using var stream = checksumEntry.Open(); var checksums = JsonSerializer.Deserialize<Dictionary<string,string>>(stream) ?? throw new InvalidDataException("Некорректный checksum.json"); foreach (var pair in checksums) { var entry = archive.GetEntry(pair.Key) ?? throw new InvalidDataException("В копии отсутствует " + pair.Key); using var content = entry.Open(); if (!Convert.ToHexString(SHA256.HashData(content)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма не совпадает: " + pair.Key); } }
    private static long DirectorySize(string path) => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length) : 0;
    private static void EncryptFile(string source,string target,string password){var plain=File.ReadAllBytes(source);var salt=RandomNumberGenerator.GetBytes(16);var nonce=RandomNumberGenerator.GetBytes(12);var key=Rfc2898DeriveBytes.Pbkdf2(password,salt,200_000,HashAlgorithmName.SHA256,32);var cipher=new byte[plain.Length];var tag=new byte[16];using(var aes=new AesGcm(key,16))aes.Encrypt(nonce,plain,cipher,tag,EncryptedMagic);using var output=File.Create(target);output.Write(EncryptedMagic);output.Write(salt);output.Write(nonce);output.Write(tag);output.Write(cipher);CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    private static void DecryptFile(string source,string target,string password){var data=File.ReadAllBytes(source);var offset=EncryptedMagic.Length;if(data.Length<offset+44)throw new InvalidDataException("Повреждённая зашифрованная копия.");var salt=data.AsSpan(offset,16);offset+=16;var nonce=data.AsSpan(offset,12);offset+=12;var tag=data.AsSpan(offset,16);offset+=16;var cipher=data.AsSpan(offset);var key=Rfc2898DeriveBytes.Pbkdf2(password,salt,200_000,HashAlgorithmName.SHA256,32);var plain=new byte[cipher.Length];try{using(var aes=new AesGcm(key,16))aes.Decrypt(nonce,cipher,tag,plain,EncryptedMagic);File.WriteAllBytes(target,plain);}catch(CryptographicException){throw new UnauthorizedAccessException("Неверный пароль или резервная копия повреждена.");}finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}}
    private static void CopyDirectory(string source, string target)
    { Directory.CreateDirectory(target); foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir))); foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true); }
    private static void PruneAutomatic()
    { foreach (var file in Directory.GetFiles(AppPaths.Backups, "auto-*.mdbackup").OrderByDescending(File.GetCreationTimeUtc).Skip(10)) File.Delete(file); }
}
