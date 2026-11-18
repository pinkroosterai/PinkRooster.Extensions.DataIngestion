namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Controls how a separator is retained when it is used to split text.</summary>
public enum SeparatorPlacement
{
    /// <summary>Omit the separator.</summary>
    None,

    /// <summary>Attach the separator to the following fragment.</summary>
    Leading,

    /// <summary>Attach the separator to the preceding fragment.</summary>
    Trailing,
}
