namespace Skua.Core.ViewModels;

public class ApplicationOptionsViewModel : BotControlViewModelBase
{
    public ApplicationOptionsViewModel(List<DisplayOptionItemViewModelBase> appOptions)
        : base("Application Options", 580, 650)
    {
        ApplicationOptions = appOptions;
    }

    public List<DisplayOptionItemViewModelBase> ApplicationOptions { get; }
}