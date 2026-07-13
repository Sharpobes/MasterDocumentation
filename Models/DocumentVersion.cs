namespace MasterDocumentation.Models;

public sealed record DocumentVersion(long Id, long DocumentId, DateTime CreatedAt, string Reason, long Size);

