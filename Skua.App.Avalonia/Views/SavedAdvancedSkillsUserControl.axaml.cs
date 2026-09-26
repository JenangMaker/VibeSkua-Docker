using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Skua.Core.Models.Skills;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class SavedAdvancedSkillsUserControl : UserControl
{
    public SavedAdvancedSkillsUserControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SearchBox.TextChanged += (_, _) => _skills?.Refresh();
        AttachedToVisualTree += (_, _) => (DataContext as SavedAdvancedSkillsViewModel)?.LoadAvailableClassesCommand.Execute(null);
        KeyDown += SavedSkills_KeyDown;
    }

    private UiMirror<AdvancedSkill>? _skills;
    private readonly List<AdvancedSkill> _clipboard = new();

    private void Attach()
    {
        _skills?.Dispose();
        if (DataContext is not SavedAdvancedSkillsViewModel vm)
            return;
        _skills = new UiMirror<AdvancedSkill>(vm.LoadedSkills)
        {
            Filter = s => string.IsNullOrEmpty(SearchBox.Text) || s.ClassName.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase),
        };
        SkillsList.ItemsSource = _skills.Items;
    }

    private void Skill_DoubleTapped(object? sender, TappedEventArgs e) =>
        (DataContext as SavedAdvancedSkillsViewModel)?.EditSelectedCommand.Execute(null);

    // Ctrl+C / Ctrl+V copy and paste skill sets within the list.
    private void SavedSkills_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        if (e.Key == Key.C)
        {
            _clipboard.Clear();
            foreach (AdvancedSkill skill in Selection.Of(SkillsList).OfType<AdvancedSkill>())
            {
                _clipboard.Add(new AdvancedSkill
                {
                    ClassName = skill.ClassName,
                    Skills = skill.Skills,
                    SkillTimeout = skill.SkillTimeout,
                    ClassUseMode = skill.ClassUseMode,
                    SkillUseMode = skill.SkillUseMode,
                });
            }
            e.Handled = true;
        }
        else if (e.Key == Key.V && DataContext is SavedAdvancedSkillsViewModel vm)
        {
            foreach (AdvancedSkill skill in _clipboard)
                vm.LoadedSkills.Add(skill);
            e.Handled = true;
        }
    }
}
