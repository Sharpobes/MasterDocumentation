namespace MasterDocumentation.Models;

public sealed record DocumentSearchResult(long Id,string Title,string Snippet,string Status,DateTime ModifiedAt,bool IsFavorite,bool IsDeleted,int AttachmentCount,string ParentTitle);
