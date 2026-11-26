namespace GrowSandbox.Shared.Models;

/// <summary>
/// Representasi item yang jatuh/berada di lantai world secara otoritatif.
/// </summary>
public readonly record struct DroppedItem(
    uint DropId,
    ushort ItemId,
    ushort Count,
    float X,
    float Y,
    float SpawnTime);
