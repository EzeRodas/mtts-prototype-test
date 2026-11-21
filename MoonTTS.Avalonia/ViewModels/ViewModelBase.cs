using CommunityToolkit.Mvvm.ComponentModel;
using MoonTTS.Avalonia.Services;

namespace MoonTTS.Avalonia.ViewModels;

/* File Structure:
        - ViewModelBase (Class)
            - L
*/

/* Base class for all ViewModels providing property-change notification and localization */
public abstract class ViewModelBase : ObservableObject
{
    /* Exposes the singleton localization service to all compiled XAML views */
    public LocalizationService L => LocalizationService.Instance;
}
