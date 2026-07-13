namespace MasterDocumentation.Models;

public sealed class HotkeySetting
{
    public required string Id { get; init; }
    public required string Command { get; init; }
    public required string Category { get; init; }
    public required string Gesture { get; set; }
}
