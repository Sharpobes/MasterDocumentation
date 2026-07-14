namespace MasterDocumentation.Models;

public sealed record AttachmentInfo(long Id, long DocumentId, string FileName, string StoredName, string MimeType, long Size, string Sha256, DateTime CreatedAt);

