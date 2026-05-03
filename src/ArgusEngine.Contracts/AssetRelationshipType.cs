namespace ArgusEngine.Contracts;

public enum AssetRelationshipType
{
    Contains = 0,
    ResolvesTo = 1,
    ServedBy = 2,
    Exposes = 3,
    Defines = 4,
    References = 5,
    ExtractedFrom = 6,
    DerivedFrom = 7,
    ObservedOn = 8,
}
