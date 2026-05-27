namespace WhatJolo;

public sealed class YoloTrainingProfile
{
    public string Name { get; init; } = string.Empty;

    public string RecommendedModel { get; init; } = string.Empty;

    public int Epochs { get; init; }

    public int ImageSize { get; init; }

    public int Batch { get; init; }

    public string DisplayName => $"{Name} ({RecommendedModel}, {ImageSize})";

    public override string ToString()
    {
        return DisplayName;
    }
}
