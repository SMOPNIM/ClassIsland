using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using ClassIsland.Core;
using ClassIsland.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.ViewModels.SettingsPages;

public partial class AppearanceSettingsViewModel(SettingsService settingsService) : ObservableRecipient
{
    [ObservableProperty] private string _fontSizeTestText = "滚滚长江东逝水，浪花淘尽英雄。The quick brown fox jumps over a lazy dog.";
    
    public ObservableCollection<FontFamily> FontFamilies { get; } =
        new([..FontManager.Current.SystemFonts, MainWindow.DefaultFontFamily]);
    
    public SettingsService SettingsService { get; } = settingsService;
}
