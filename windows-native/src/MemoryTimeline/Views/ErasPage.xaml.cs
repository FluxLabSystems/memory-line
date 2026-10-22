using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MemoryTimeline.Controls;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.ViewModels;
using Windows.UI;

namespace MemoryTimeline.Views;

/// <summary>
/// Page for managing eras/life phases with Gantt-style visualization.
/// Provides CRUD operations for eras and milestones.
/// </summary>
public sealed partial class ErasPage : Page
{
    public ErasViewModel ViewModel { get; }

    /// <summary>
    /// Color palette for era colors - bound to the GridView.
    /// </summary>
    public List<string> ColorPalette => ErasViewModel.ColorPalette;

    private const string DraftParameterPrefix = "draft:";

    private Era? _eraToDelete;
    private Milestone? _milestoneToDelete;

    /// <summary>
    /// Milestone under the pointer when its context flyout opened (set by
    /// RightTapped, which fires for right-click and press-and-hold before the
    /// ContextFlyout shows); consumed by the Edit/Delete flyout items.
    /// </summary>
    private Milestone? _contextMilestone;

    /// <summary>
    /// Era draft id received via the <c>"draft:&lt;draftId&gt;"</c> navigation
    /// parameter; consumed (and cleared) once the page has loaded its data.
    /// </summary>
    private string? _pendingDraftId;

    public ErasPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.GetRequiredService<ErasViewModel>();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string parameter && parameter.StartsWith(DraftParameterPrefix, StringComparison.Ordinal))
        {
            // Opening the dialog needs the page in the visual tree and its data
            // loaded; defer to Page_Loaded (which dispatches after InitializeAsync).
            _pendingDraftId = parameter[DraftParameterPrefix.Length..];
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();

        if (_pendingDraftId != null)
        {
            var draftId = _pendingDraftId;
            _pendingDraftId = null;

            // Defer via the dispatcher so the dialog opens after the current
            // layout/load pass has completed.
            DispatcherQueue.TryEnqueue(async () => await OpenEraDraftAsync(draftId));
        }
    }

    /// <summary>
    /// Loads an era draft into the ViewModel and opens the era dialog
    /// prefilled from it.
    /// </summary>
    private async Task OpenEraDraftAsync(string draftId)
    {
        var loaded = await ViewModel.LoadEraDraftAsync(draftId);
        if (!loaded)
        {
            return;
        }

        PrepareEraDialogFromViewModel();
        EraDialog.XamlRoot = XamlRoot;
        await EraDialog.ShowAsync();
    }

    /// <summary>
    /// Prepares the era dialog controls from the ViewModel's current edit
    /// fields (used when resuming a draft, where the ViewModel is the source
    /// of truth rather than an <see cref="Era"/> entity).
    /// </summary>
    private void PrepareEraDialogFromViewModel()
    {
        EraDialog.Title = "Resume Era Draft";
        EraDialogErrorBar.IsOpen = false;
        EraNameBox.Text = ViewModel.EditName;
        EraSubtitleBox.Text = ViewModel.EditSubtitle;
        EraStartDatePicker.Date = new DateTimeOffset(ViewModel.EditStartDate);
        EraEndDatePicker.Date = ViewModel.EditEndDate.HasValue ? new DateTimeOffset(ViewModel.EditEndDate.Value) : null;
        EraDescriptionBox.Text = ViewModel.EditDescription;
        EraNotesBox.Text = ViewModel.EditNotes;
        EraTagBox.Text = string.Empty;

        var category = ViewModel.Categories.FirstOrDefault(c => c.CategoryId == ViewModel.EditCategoryId);
        EraCategoryComboBox.SelectedItem = category;

        var colorIndex = ColorPalette.IndexOf(ViewModel.EditColorCode);
        ColorPaletteGrid.SelectedIndex = colorIndex; // -1 clears the selection
        CustomColorBox.Text = ViewModel.EditColorCode;
        UpdateColorPreview(ViewModel.EditColorCode);

        ColorOverrideBox.Text = ViewModel.EditColorOverride;
        UpdateColorOverridePreview(ViewModel.EditColorOverride);
    }

    #region Era CRUD Handlers

    /// <summary>
    /// Opens the Add Era dialog.
    /// </summary>
    private async void AddEra_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareAddEra();
        PrepareEraDialogForAdd();
        await EraDialog.ShowAsync();
    }

    /// <summary>
    /// Opens the Edit Era dialog for the selected era.
    /// </summary>
    private async void EditSelectedEra_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedEra != null)
        {
            ViewModel.PrepareEditEra(ViewModel.SelectedEra);
            PrepareEraDialogForEdit(ViewModel.SelectedEra);
            await EraDialog.ShowAsync();
        }
    }

    /// <summary>
    /// Opens the Delete Confirmation dialog for the selected era.
    /// </summary>
    private async void DeleteSelectedEra_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedEra != null)
        {
            _eraToDelete = ViewModel.SelectedEra;
            await DeleteConfirmDialog.ShowAsync();
        }
    }

    /// <summary>
    /// Opens the narrative-generation dialog scoped to the selected era.
    /// The dialog owns its own ViewModel, progress, and cancellation state.
    /// </summary>
    private async void GenerateEraStory_Click(object sender, RoutedEventArgs e)
    {
        var era = ViewModel.SelectedEra;
        if (era == null)
        {
            return;
        }

        var request = new NarrativeRequest
        {
            Scope = NarrativeScope.Era,
            ScopeId = era.EraId
        };

        var dialog = new NarrativeDialog(request, era.Name)
        {
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Handles era bar click for selection.
    /// </summary>
    private void EraBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is GanttEraBarDto eraBar)
        {
            var era = ViewModel.Eras.FirstOrDefault(era => era.EraId == eraBar.EraId);
            ViewModel.SelectEra(era);
        }
    }

    /// <summary>
    /// Handles era bar double-click for editing.
    /// </summary>
    private async void EraBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is GanttEraBarDto eraBar)
        {
            var era = ViewModel.Eras.FirstOrDefault(era => era.EraId == eraBar.EraId);
            if (era != null)
            {
                ViewModel.PrepareEditEra(era);
                PrepareEraDialogForEdit(era);
                await EraDialog.ShowAsync();
            }
        }
    }

    /// <summary>
    /// Saves the era when the dialog's primary button is clicked.
    /// </summary>
    private async void EraDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(EraNameBox.Text))
        {
            args.Cancel = true;
            ShowEraDialogError("Era name is required.");
            return;
        }

        CollectEraDialogInputs();

        var deferral = args.GetDeferral();
        try
        {
            // A failed save keeps the dialog open with the reason inline; the
            // dialog used to close over a save that never happened.
            var saved = await ViewModel.TrySaveEraAsync();
            if (!saved)
            {
                args.Cancel = true;
                ShowEraDialogError(ViewModel.StatusMessage);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowEraDialogError(string? message)
    {
        EraDialogErrorBar.Message = string.IsNullOrWhiteSpace(message) ? "The era could not be saved." : message;
        EraDialogErrorBar.IsOpen = true;
    }

    /// <summary>
    /// Saves the current era editor state as a draft when the "Save as Draft"
    /// secondary button is clicked. No name validation: the draft service
    /// titles empty drafts "Untitled era". The dialog closes afterwards.
    /// </summary>
    private async void EraDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        CollectEraDialogInputs();

        var deferral = args.GetDeferral();
        try
        {
            await ViewModel.SaveEraDraftAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Copies the era dialog control values into the ViewModel's edit fields.
    /// (Color code and tags are already synced by their own handlers/bindings.)
    /// </summary>
    private void CollectEraDialogInputs()
    {
        ViewModel.EditName = EraNameBox.Text;
        ViewModel.EditSubtitle = EraSubtitleBox.Text;
        ViewModel.EditStartDate = EraStartDatePicker.Date?.DateTime ?? DateTime.Now;
        ViewModel.EditEndDate = EraEndDatePicker.Date?.DateTime;
        ViewModel.EditDescription = EraDescriptionBox.Text;
        ViewModel.EditNotes = EraNotesBox.Text;

        var overrideText = ColorOverrideBox.Text?.Trim() ?? string.Empty;
        ViewModel.EditColorOverride = IsValidHexColor(overrideText) ? overrideText : string.Empty;

        // Category
        if (EraCategoryComboBox.SelectedItem is EraCategory selectedCategory)
        {
            ViewModel.EditCategoryId = selectedCategory.CategoryId;
        }
    }

    /// <summary>
    /// Confirms era deletion.
    /// </summary>
    private async void DeleteConfirmDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_eraToDelete != null)
        {
            var deferral = args.GetDeferral();
            try
            {
                await ViewModel.DeleteEraAsync(_eraToDelete);
            }
            finally
            {
                _eraToDelete = null;
                deferral.Complete();
            }
        }
    }

    /// <summary>
    /// Prepares the dialog for adding a new era.
    /// </summary>
    private void PrepareEraDialogForAdd()
    {
        EraDialog.Title = "Add Era";
        EraDialogErrorBar.IsOpen = false;
        EraNameBox.Text = string.Empty;
        EraSubtitleBox.Text = string.Empty;
        EraStartDatePicker.Date = DateTimeOffset.Now;
        EraEndDatePicker.Date = null;
        EraDescriptionBox.Text = string.Empty;
        EraNotesBox.Text = string.Empty;
        EraTagBox.Text = string.Empty;
        ColorOverrideBox.Text = string.Empty;
        UpdateColorOverridePreview(string.Empty);

        // Select first category
        EraCategoryComboBox.SelectedIndex = 0;

        // Select the default color in the palette
        var defaultColorIndex = ViewModel.Eras.Count % ColorPalette.Count;
        ColorPaletteGrid.SelectedIndex = defaultColorIndex;
        CustomColorBox.Text = ColorPalette[defaultColorIndex];
        UpdateColorPreview(ColorPalette[defaultColorIndex]);
    }

    /// <summary>
    /// Prepares the dialog for editing an existing era.
    /// </summary>
    private void PrepareEraDialogForEdit(Era era)
    {
        EraDialog.Title = "Edit Era";
        EraDialogErrorBar.IsOpen = false;
        EraNameBox.Text = era.Name;
        EraSubtitleBox.Text = era.Subtitle ?? string.Empty;
        EraStartDatePicker.Date = new DateTimeOffset(era.StartDate);
        EraEndDatePicker.Date = era.EndDate.HasValue ? new DateTimeOffset(era.EndDate.Value) : null;
        EraDescriptionBox.Text = era.Description ?? string.Empty;
        EraNotesBox.Text = era.Notes ?? string.Empty;
        EraTagBox.Text = string.Empty;
        ColorOverrideBox.Text = era.ColorOverride ?? string.Empty;
        UpdateColorOverridePreview(era.ColorOverride ?? string.Empty);

        // Select category
        var category = ViewModel.Categories.FirstOrDefault(c => c.CategoryId == era.CategoryId);
        EraCategoryComboBox.SelectedItem = category;

        // Color
        var colorIndex = ColorPalette.IndexOf(era.ColorCode);
        if (colorIndex >= 0)
        {
            ColorPaletteGrid.SelectedIndex = colorIndex;
        }
        else
        {
            ColorPaletteGrid.SelectedItem = null;
        }

        CustomColorBox.Text = era.ColorCode;
        UpdateColorPreview(era.ColorCode);
    }

    #endregion

    #region Era Tag Handlers

    /// <summary>
    /// Adds the typed tag when Enter is pressed in the tag box.
    /// </summary>
    private void EraTagBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ViewModel.AddEditTag(EraTagBox.Text);
            EraTagBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Adds the typed tag via the add button.
    /// </summary>
    private void AddEraTag_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AddEditTag(EraTagBox.Text);
        EraTagBox.Text = string.Empty;
    }

    /// <summary>
    /// Removes the clicked tag chip.
    /// </summary>
    private void RemoveEraTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is string tag)
        {
            ViewModel.RemoveEditTag(tag);
        }
    }

    #endregion

    #region Milestone CRUD Handlers

    /// <summary>
    /// Opens the Add Milestone dialog.
    /// </summary>
    private async void AddMilestone_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareAddMilestone();
        PrepareMilestoneDialogForAdd();
        await MilestoneDialog.ShowAsync();
    }

    /// <summary>
    /// Handles milestone marker click for selection.
    /// </summary>
    private void MilestoneMarker_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MilestoneMarkerDto marker)
        {
            var milestone = ViewModel.Milestones.FirstOrDefault(m => m.MilestoneId == marker.MilestoneId);
            ViewModel.SelectedMilestone = milestone;

            // Update selection state
            foreach (var m in ViewModel.MilestoneMarkers)
            {
                m.IsSelected = m.MilestoneId == marker.MilestoneId;
            }
        }
    }

    /// <summary>
    /// Captures the marker under the pointer for the context flyout (fires
    /// for right-click and for press-and-hold on touch/pen).
    /// </summary>
    private void MilestoneMarker_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MilestoneMarkerDto marker)
        {
            _contextMilestone = ViewModel.Milestones.FirstOrDefault(m => m.MilestoneId == marker.MilestoneId);
        }
    }

    /// <summary>
    /// Double-click edits the milestone (mirrors EraBar_DoubleTapped).
    /// </summary>
    private async void MilestoneMarker_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MilestoneMarkerDto marker)
        {
            var milestone = ViewModel.Milestones.FirstOrDefault(m => m.MilestoneId == marker.MilestoneId);
            if (milestone != null)
            {
                await OpenEditMilestoneDialogAsync(milestone);
            }
        }
    }

    private async void EditMilestoneMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMilestone != null)
        {
            await OpenEditMilestoneDialogAsync(_contextMilestone);
        }
    }

    private async void DeleteMilestoneMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMilestone != null)
        {
            _milestoneToDelete = _contextMilestone;
            await DeleteMilestoneConfirmDialog.ShowAsync();
        }
    }

    /// <summary>
    /// Opens the milestone dialog in edit mode for the given milestone.
    /// </summary>
    private async Task OpenEditMilestoneDialogAsync(Milestone milestone)
    {
        ViewModel.PrepareEditMilestone(milestone);
        PrepareMilestoneDialogForEdit(milestone);
        await MilestoneDialog.ShowAsync();
    }

    /// <summary>
    /// Saves the milestone when the dialog's primary button is clicked.
    /// </summary>
    private async void MilestoneDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(MilestoneNameBox.Text))
        {
            args.Cancel = true;
            ShowMilestoneDialogError("Milestone name is required.");
            return;
        }

        ViewModel.EditMilestoneName = MilestoneNameBox.Text;
        ViewModel.EditMilestoneDate = MilestoneDatePicker.Date?.DateTime ?? DateTime.Now;
        ViewModel.EditMilestoneDescription = MilestoneDescriptionBox.Text;

        // Milestone type
        if (MilestoneTypeComboBox.SelectedItem is ComboBoxItem typeItem && typeItem.Tag is string typeTag)
        {
            ViewModel.EditMilestoneType = Enum.Parse<MilestoneType>(typeTag);
        }

        // Linked era
        if (MilestoneLinkedEraComboBox.SelectedItem is Era linkedEra)
        {
            ViewModel.EditMilestoneLinkedEraId = linkedEra.EraId;
        }
        else
        {
            ViewModel.EditMilestoneLinkedEraId = null;
        }

        var deferral = args.GetDeferral();
        try
        {
            // Keep the dialog open with the reason inline when the save fails.
            var saved = await ViewModel.TrySaveMilestoneAsync();
            if (!saved)
            {
                args.Cancel = true;
                ShowMilestoneDialogError(ViewModel.StatusMessage);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowMilestoneDialogError(string? message)
    {
        MilestoneDialogErrorBar.Message = string.IsNullOrWhiteSpace(message) ? "The milestone could not be saved." : message;
        MilestoneDialogErrorBar.IsOpen = true;
    }

    /// <summary>
    /// Confirms milestone deletion.
    /// </summary>
    private async void DeleteMilestoneConfirmDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_milestoneToDelete != null)
        {
            var deferral = args.GetDeferral();
            try
            {
                await ViewModel.DeleteMilestoneAsync(_milestoneToDelete);
            }
            finally
            {
                _milestoneToDelete = null;
                deferral.Complete();
            }
        }
    }

    /// <summary>
    /// Prepares the dialog for adding a new milestone.
    /// </summary>
    private void PrepareMilestoneDialogForAdd()
    {
        MilestoneDialog.Title = "Add Milestone";
        MilestoneDialogErrorBar.IsOpen = false;
        MilestoneNameBox.Text = string.Empty;
        MilestoneDatePicker.Date = DateTimeOffset.Now;
        MilestoneTypeComboBox.SelectedIndex = 0;
        MilestoneLinkedEraComboBox.SelectedItem = null;
        MilestoneDescriptionBox.Text = string.Empty;
    }

    /// <summary>
    /// Prepares the dialog for editing an existing milestone (the ViewModel's
    /// PrepareEditMilestone has already loaded the edit fields; the controls
    /// are filled from the entity the same way PrepareEraDialogForEdit does).
    /// </summary>
    private void PrepareMilestoneDialogForEdit(Milestone milestone)
    {
        MilestoneDialog.Title = "Edit Milestone";
        MilestoneDialogErrorBar.IsOpen = false;
        MilestoneNameBox.Text = milestone.Name;
        MilestoneDatePicker.Date = new DateTimeOffset(milestone.Date);
        MilestoneDescriptionBox.Text = milestone.Description ?? string.Empty;

        // Type: match the ComboBoxItem whose Tag is the enum name
        // (MilestoneDialog_PrimaryButtonClick parses it back the same way).
        var typeTag = milestone.Type.ToString();
        MilestoneTypeComboBox.SelectedIndex = 0;
        for (var i = 0; i < MilestoneTypeComboBox.Items.Count; i++)
        {
            if (MilestoneTypeComboBox.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == typeTag)
            {
                MilestoneTypeComboBox.SelectedIndex = i;
                break;
            }
        }

        MilestoneLinkedEraComboBox.SelectedItem = milestone.LinkedEraId == null
            ? null
            : ViewModel.Eras.FirstOrDefault(era => era.EraId == milestone.LinkedEraId);
    }

    #endregion

    #region Category Filter Handlers

    /// <summary>
    /// Handles category filter toggle.
    /// </summary>
    private void CategoryFilter_Click(object sender, RoutedEventArgs e)
    {
        // Regenerate layout after category visibility change
        ViewModel.GenerateGanttLayout();
    }

    /// <summary>
    /// Shows all categories.
    /// </summary>
    private void ShowAllCategories_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowAllCategories();
    }

    /// <summary>
    /// Hides all categories.
    /// </summary>
    private void HideAllCategories_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.HideAllCategories();
    }

    #endregion

    #region Zoom Handlers

    /// <summary>
    /// Zooms in on the timeline.
    /// </summary>
    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ZoomIn();
    }

    /// <summary>
    /// Zooms out on the timeline.
    /// </summary>
    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ZoomOut();
    }

    #endregion

    #region Color Picker Handlers

    /// <summary>
    /// Handles color palette selection.
    /// </summary>
    private void ColorPalette_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorPaletteGrid.SelectedItem is string colorCode)
        {
            ViewModel.EditColorCode = colorCode;
            CustomColorBox.Text = colorCode;
            UpdateColorPreview(colorCode);
        }
    }

    /// <summary>
    /// Handles custom color input.
    /// </summary>
    private void CustomColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var colorCode = CustomColorBox.Text?.Trim();
        if (!string.IsNullOrEmpty(colorCode) && IsValidHexColor(colorCode))
        {
            ViewModel.EditColorCode = colorCode;
            UpdateColorPreview(colorCode);

            if (ColorPaletteGrid.SelectedItem is string selectedColor && selectedColor != colorCode)
            {
                ColorPaletteGrid.SelectedItem = null;
            }
        }
    }

    /// <summary>
    /// Updates the color preview rectangle.
    /// </summary>
    private void UpdateColorPreview(string colorCode)
    {
        try
        {
            var color = ParseHexColor(colorCode);
            ColorPreview.Fill = new SolidColorBrush(color);
        }
        catch
        {
            ColorPreview.Fill = new SolidColorBrush(Colors.Gray);
        }
    }

    /// <summary>
    /// Handles color override input, mirroring valid values into the ViewModel.
    /// </summary>
    private void ColorOverrideBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var colorCode = ColorOverrideBox.Text?.Trim() ?? string.Empty;
        if (colorCode.Length == 0)
        {
            ViewModel.EditColorOverride = string.Empty;
            UpdateColorOverridePreview(string.Empty);
        }
        else if (IsValidHexColor(colorCode))
        {
            ViewModel.EditColorOverride = colorCode;
            UpdateColorOverridePreview(colorCode);
        }
    }

    /// <summary>
    /// Updates the color override preview rectangle (transparent when empty).
    /// </summary>
    private void UpdateColorOverridePreview(string colorCode)
    {
        if (string.IsNullOrEmpty(colorCode))
        {
            ColorOverridePreview.Fill = new SolidColorBrush(Colors.Transparent);
            return;
        }

        try
        {
            ColorOverridePreview.Fill = new SolidColorBrush(ParseHexColor(colorCode));
        }
        catch
        {
            ColorOverridePreview.Fill = new SolidColorBrush(Colors.Gray);
        }
    }

    /// <summary>
    /// Validates a hex color code.
    /// </summary>
    private static bool IsValidHexColor(string colorCode)
    {
        if (string.IsNullOrEmpty(colorCode))
            return false;

        if (!colorCode.StartsWith("#"))
            return false;

        var hex = colorCode.Substring(1);
        if (hex.Length != 6 && hex.Length != 8)
            return false;

        return hex.All(c => char.IsDigit(c) || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'));
    }

    /// <summary>
    /// Parses a hex color code to a Color.
    /// </summary>
    private static Color ParseHexColor(string colorCode)
    {
        if (string.IsNullOrEmpty(colorCode) || !colorCode.StartsWith("#"))
            return Colors.Gray;

        var hex = colorCode.Substring(1);

        if (hex.Length == 6)
        {
            return Color.FromArgb(
                255,
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        else if (hex.Length == 8)
        {
            return Color.FromArgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16),
                Convert.ToByte(hex.Substring(6, 2), 16));
        }

        return Colors.Gray;
    }

    #endregion
}
