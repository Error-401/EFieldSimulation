namespace EFieldSimulation.Models;

/// <summary>
/// Serialization DTO for probe definitions (saved in 
/// manifest).
/// </summary>
public sealed class ProbeDto
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Point"; // "Point" or "LineSegment"
    public float AX { get; set; }
    public float AY { get; set; }
    public float AZ { get; set; }
    public float BX { get; set; }
    public float BY { get; set; }
    public float BZ { get; set; }
    public int SampleCount { get; set; } = 50;
    public bool IsVisible { get; set; } = true;

    public static ProbeDto FromDefinition(ProbeDefinition p) => new()
    {
        Name = p.Name,
        Type = p.Type == ProbeType.Point ? "Point" : "LineSegment",
        AX = p.AX,
        AY = p.AY,
        AZ = p.AZ,
        BX = p.BX,
        BY = p.BY,
        BZ = p.BZ,
        SampleCount = p.SampleCount,
        IsVisible = p.IsVisible
    };

    public ProbeDefinition ToDefinition() => new()
    {
        Type = Type == "LineSegment" ? ProbeType.LineSegment : ProbeType.Point,
        Name = Name,
        AX = AX,
        AY = AY,
        AZ = AZ,
        BX = BX,
        BY = BY,
        BZ = BZ,
        SampleCount = SampleCount,
        IsVisible = IsVisible
    };
}