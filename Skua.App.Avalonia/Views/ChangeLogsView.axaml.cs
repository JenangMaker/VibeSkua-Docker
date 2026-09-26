using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;
using CommunityToolkit.Mvvm.DependencyInjection;

namespace Skua.App.Avalonia.Views;

public partial class ChangeLogsView : UserControl
{
    public ChangeLogsView()
    {
        InitializeComponent();
        // As WPF: this view always shows its own view model (it is also the
        // fallback view for any bot control), and links go through it.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ChangeLogsViewModel vm)
            {
                if (Markdownview.Engine is global::Markdown.Avalonia.IMarkdownEngine engine)
                    engine.HyperlinkCommand = vm.NavigateCommand;
            }
            else if (Ioc.Default.GetService<ChangeLogsViewModel>() is { } own)
            {
                DataContext = own;
            }
        };
    }
}
