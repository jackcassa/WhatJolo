using System.ComponentModel;

namespace WhatJolo;

public sealed class ProjectClassOption : ViewModelBase
{
    private bool _isSelected;

    public ProjectClassOption(string name, bool isSelected = false)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}
