namespace MasterDocumentation.Models;

public sealed record DocumentMetadata(long Id,string Guid,string Title,string Status,string Author,DateTime CreatedAt,DateTime ModifiedAt,string ParentTitle,string TemplateTitle,int WordCount,int CharacterCount,int AttachmentCount);
public sealed record CustomPropertyValue(string Name,string Value);
