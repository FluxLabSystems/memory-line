using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for the Eras management page with Gantt-style visualization.
/// Provides CRUD operations for eras, categories, and milestones, plus
/// save-as-draft / resume-draft support for the era editor.
/// </summary>
public partial class ErasViewModel : ObservableObject
{
    private readonly IEraRepository _eraRepository;
    private readonly IEraCategoryRepository _categoryRepository;
    private readonly IMilestoneRepository _milestoneRepository;
    private readonly ILogger<ErasViewModel> _logger;
    private readonly IDraftService _draftService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    /// <summary>
    /// Projects era writes onto the sync feed (design §19 Phase 3). Optional so
    /// the page still works with the publisher unregistered — a companion then
    /// simply never learns the archive has eras.
    ///
    /// <para><b>Why a ViewModel is doing this.</b> Eras are the one archive
    /// entity with no Core service: events go through <c>IEventService</c>,
    /// people through <c>IPersonService</c>, but an era is created, updated and
    /// deleted straight from this class through <see cref="IEraRepository"/>.
    /// Publishing from here is therefore the only place a write can be observed
    /// at all, and companion devices showing no eras is a worse failure than the
    /// layering being wrong. It IS wrong: this couples a sync concern to a UI
    /// class, and any future non-UI era writer (import already is one — see
    /// <c>ImportService.ImportErasAsync</c>) publishes nothing. The fix is an
    /// <c>EraService</c> in Core owning create/update/delete plus the tag
    /// replacement below, with the publish calls moved into it and this
    /// ViewModel reduced to calling it — deliberately left out of this change
    /// because it is a refactor of its own.</para>
    /// </summary>
    private readonly ITimelineProjectionPublisher? _projectionPublisher;

    // Data collections
    [ObservableProperty]
    private ObservableCollection<Era> _eras = new();

    [ObservableProperty]
    private ObservableCollection<EraCategory> _categories = new();

    [ObservableProperty]
    private ObservableCollection<Milestone> _milestones = new();

    // Display collections (DTOs for visualization)
    [ObservableProperty]
    private ObservableCollection<GanttEraBarDto> _ganttEraBars = new();

    [ObservableProperty]
    private ObservableCollection<MilestoneMarkerDto> _milestoneMarkers = new();

    [ObservableProperty]
    private ObservableCollection<EraCategoryDto> _categoryFilters = new();

    [ObservableProperty]
    private ObservableCollection<EraRowGroupDto> _groupedEras = new();

    [ObservableProperty]
    private ObservableCollection<TimeRulerTickDto> _timeRulerTicks = new();

    // Selection
    [ObservableProperty]
    private Era? _selectedEra;

    [ObservableProperty]
    private Milestone? _selectedMilestone;

    [ObservableProperty]
    private GanttEraBarDto? _selectedEraBar;

    [ObservableProperty]
    private MilestoneMarkerDto? _selectedMarker;

    // Viewport state
    [ObservableProperty]
    private DateTime _viewportStart;

    [ObservableProperty]
    private DateTime _viewportEnd;

    [ObservableProperty]
    private double _pixelsPerDay = 2.0;

    [ObservableProperty]
    private double _viewportWidth = 1200;

    [ObservableProperty]
    private double _viewportHeight = 600;

    /// <summary>
    /// Width of the era-name label column in the Gantt layout (ErasPage.xaml).
    /// The milestone zone, time ruler, and era bar canvases all start after this
    /// offset so pixel positions computed from ViewportStart stay aligned.
    /// </summary>
    public const double EraNameColumnWidth = 140;

    /// <summary>
    /// Total horizontal content size of the Gantt chart: the era-name label
    /// column plus the plotted viewport width. Used for the scrollable extent.
    /// </summary>
    public double TotalContentWidth => EraNameColumnWidth + ViewportWidth;

    partial void OnViewportWidthChanged(double value)
    {
        OnPropertyChanged(nameof(TotalContentWidth));
    }

    // UI state
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _totalEraCount;

    [ObservableProperty]
    private int _totalMilestoneCount;

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private bool _showMilestones = true;

    [ObservableProperty]
    private double _totalContentHeight;

    // Form fields for Add/Edit Era
    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editSubtitle = string.Empty;

    [ObservableProperty]
    private DateTime _editStartDate = DateTime.Now;

    [ObservableProperty]
    private DateTime? _editEndDate;

    [ObservableProperty]
    private string _editColorCode = "#4169E1";

    /// <summary>
    /// Optional per-era color override (hex). Empty string means no override.
    /// </summary>
    [ObservableProperty]
    private string _editColorOverride = string.Empty;

    [ObservableProperty]
    private string? _editCategoryId;

    [ObservableProperty]
    private string _editDescription = string.Empty;

    [ObservableProperty]
    private string _editNotes = string.Empty;

    /// <summary>
    /// Tags attached to the era being edited (era_tags junction rows).
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> _editTags = new();

    [ObservableProperty]
    private bool _isEditMode;

    // Form fields for Add/Edit Milestone
    [ObservableProperty]
    private string _editMilestoneName = string.Empty;

    [ObservableProperty]
    private DateTime _editMilestoneDate = DateTime.Now;

    [ObservableProperty]
    private MilestoneType _editMilestoneType = MilestoneType.Standard;

    [ObservableProperty]
    private string? _editMilestoneLinkedEraId;

    [ObservableProperty]
    private string _editMilestoneColorOverride = string.Empty;

    [ObservableProperty]
    private string _editMilestoneDescription = string.Empty;

    [ObservableProperty]
    private bool _isMilestoneEditMode;

    private string? _editingEraId;
    private string? _editingMilestoneId;

    /// <summary>
    /// Id of the era draft the editor was resumed from (or last saved to);
    /// null when the editor session is not backed by a draft. The draft is
    /// deleted after the era is really saved.
    /// </summary>
    private string? _currentEraDraftId;

    // Predefined color palette for eras
    public static readonly List<string> ColorPalette = new()
    {
        "#FF6B6B", "#4ECDC4", "#45B7D1", "#96CEB4", "#FFEAA7", "#DDA0DD",
        "#98D8C8", "#F7DC6F", "#BB8FCE", "#85C1E9", "#F8B500", "#27AE60",
        "#E74C3C", "#3498DB", "#9B59B6", "#1ABC9C",
    };

    public ErasViewModel(
        IEraRepository eraRepository,
        IEraCategoryRepository categoryRepository,
        IMilestoneRepository milestoneRepository,
        ILogger<ErasViewModel> logger,
        IDraftService draftService,
        IDbContextFactory<AppDbContext> contextFactory,
        ITimelineProjectionPublisher? projectionPublisher = null)
    {
        _eraRepository = eraRepository;
        _categoryRepository = categoryRepository;
        _milestoneRepository = milestoneRepository;
        _logger = logger;
        _draftService = draftService;
        _contextFactory = contextFactory;
        _projectionPublisher = projectionPublisher;

        // Initialize viewport to show last 10 years
        var now = DateTime.Now;
        ViewportStart = now.AddYears(-10);
        ViewportEnd = now.AddYears(1);
    }

    /// <summary>
    /// Initializes the eras page, loading all data.
    /// </summary>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        await LoadCategoriesAsync();
        await LoadErasAsync();
        await LoadMilestonesAsync();
        CalculateViewport();
        GenerateGanttLayout();
    }

    /// <summary>
    /// Loads all categories from the database.
    /// </summary>
    [RelayCommand]
    public async Task LoadCategoriesAsync()
    {
        try
        {
            // Ensure default categories exist
            await _categoryRepository.EnsureDefaultCategoriesAsync();

            var categories = await _categoryRepository.GetOrderedAsync();
            Categories.Clear();
            CategoryFilters.Clear();

            foreach (var category in categories)
            {
                Categories.Add(category);
                CategoryFilters.Add(EraCategoryDto.FromCategory(category));
            }

            _logger.LogInformation("Loaded {Count} categories", Categories.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading categories");
            StatusMessage = "Error loading categories";
        }
    }

    /// <summary>
    /// Loads all eras from the database.
    /// </summary>
    [RelayCommand]
    public async Task LoadErasAsync()
    {
        try
        {
            StatusMessage = "Loading eras...";

            var eras = await _eraRepository.GetOrderedByDateAsync();
            Eras.Clear();
            foreach (var era in eras)
            {
                Eras.Add(era);
            }

            TotalEraCount = Eras.Count;

            // Update category era counts
            foreach (var filter in CategoryFilters)
            {
                filter.EraCount = Eras.Count(e => e.CategoryId == filter.CategoryId);
            }

            StatusMessage = $"Loaded {TotalEraCount} eras";
            _logger.LogInformation("Loaded {Count} eras", TotalEraCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading eras");
            StatusMessage = "Error loading eras";
        }
    }

    /// <summary>
    /// Loads all milestones from the database.
    /// </summary>
    [RelayCommand]
    public async Task LoadMilestonesAsync()
    {
        try
        {
            var milestones = await _milestoneRepository.GetOrderedByDateAsync();
            Milestones.Clear();
            foreach (var milestone in milestones)
            {
                Milestones.Add(milestone);
            }

            TotalMilestoneCount = Milestones.Count;
            _logger.LogInformation("Loaded {Count} milestones", TotalMilestoneCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading milestones");
            StatusMessage = "Error loading milestones";
        }
    }

    /// <summary>
    /// Calculates optimal viewport based on era data.
    /// </summary>
    private void CalculateViewport()
    {
        if (!Eras.Any())
        {
            var now = DateTime.Now;
            ViewportStart = now.AddYears(-10);
            ViewportEnd = now.AddYears(1);
            return;
        }

        var earliestDate = Eras.Min(e => e.StartDate);
        var latestDate = Eras.Max(e => e.EndDate ?? DateTime.Now);

        // Add padding
        ViewportStart = earliestDate.AddMonths(-6);
        ViewportEnd = latestDate.AddMonths(6);

        // Calculate pixels per day based on viewport width
        var totalDays = (ViewportEnd - ViewportStart).TotalDays;
        PixelsPerDay = ViewportWidth / totalDays;
    }

    /// <summary>
    /// Generates the Gantt-style layout with grouped eras and milestones.
    /// </summary>
    [RelayCommand]
    public void GenerateGanttLayout()
    {
        GenerateEraBars();
        GenerateMilestoneMarkers();
        GenerateTimeRulerTicks();
        GroupErasByCategory();
        CalculateTotalContentHeight();
    }

    /// <summary>
    /// Generates era bars for the Gantt chart.
    /// </summary>
    private void GenerateEraBars()
    {
        GanttEraBars.Clear();

        // Filter by visible categories and search
        var visibleCategoryIds = CategoryFilters
            .Where(c => c.IsVisible)
            .Select(c => c.CategoryId)
            .ToHashSet();

        var filteredEras = Eras
            .Where(e => e.CategoryId == null || visibleCategoryIds.Contains(e.CategoryId))
            .Where(e => string.IsNullOrEmpty(SearchFilter) ||
                        e.Name.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Category?.SortOrder ?? int.MaxValue)
            .ThenBy(e => e.StartDate);

        foreach (var era in filteredEras)
        {
            var dto = GanttEraBarDto.FromEra(era);
            CalculateEraBarPosition(dto);
            GanttEraBars.Add(dto);
        }
    }

    /// <summary>
    /// Calculates the pixel position and width for an era bar.
    /// </summary>
    private void CalculateEraBarPosition(GanttEraBarDto era)
    {
        var startDays = (era.StartDate - ViewportStart).TotalDays;
        era.PixelX = startDays * PixelsPerDay;

        var endDate = era.EndDate ?? ViewportEnd;
        var endDays = (endDate - ViewportStart).TotalDays;
        var endPixelX = endDays * PixelsPerDay;

        era.Width = Math.Max(EraLayoutConstants.MinBarWidth, endPixelX - era.PixelX);
        era.IsVisible = era.PixelX + era.Width >= 0 && era.PixelX <= ViewportWidth;
    }

    /// <summary>
    /// Generates milestone markers for the timeline.
    /// </summary>
    private void GenerateMilestoneMarkers()
    {
        MilestoneMarkers.Clear();

        if (!ShowMilestones) return;

        var markers = Milestones
            .OrderBy(m => m.Date)
            .Select(m => MilestoneMarkerDto.FromMilestone(m))
            .ToList();

        // Calculate positions and handle collision/stacking
        var occupiedRanges = new List<(double left, double right, int tier)>();

        foreach (var marker in markers)
        {
            var days = (marker.Date - ViewportStart).TotalDays;
            marker.PixelX = days * PixelsPerDay;

            // Calculate label width (estimate)
            double labelWidth = marker.Name.Length * 7 + EraLayoutConstants.MilestoneIconSize + 8;
            double totalWidth = labelWidth;
            double left = marker.PixelX - totalWidth / 2;
            double right = marker.PixelX + totalWidth / 2;

            // Find lowest tier that doesn't overlap
            int tier = 0;
            while (occupiedRanges.Any(r => r.tier == tier &&
                                          !(right < r.left || left > r.right)))
            {
                tier++;
            }

            occupiedRanges.Add((left, right, tier));
            marker.StackTier = tier;
            marker.PixelY = EraLayoutConstants.MilestoneZoneHeight -
                           (tier + 1) * EraLayoutConstants.MilestoneStackOffset;

            marker.IsVisible = marker.PixelX >= 0 && marker.PixelX <= ViewportWidth;
            MilestoneMarkers.Add(marker);
        }
    }

    /// <summary>
    /// Generates time ruler ticks for the Gantt chart.
    /// </summary>
    private void GenerateTimeRulerTicks()
    {
        TimeRulerTicks.Clear();

        // Determine tick interval based on pixels per day
        var (majorInterval, minorInterval, labelFormat) = GetTickIntervals();

        // Generate major ticks
        var currentDate = RoundToInterval(ViewportStart, majorInterval);
        while (currentDate <= ViewportEnd)
        {
            var days = (currentDate - ViewportStart).TotalDays;
            var pixelX = days * PixelsPerDay;

            if (pixelX >= 0 && pixelX <= ViewportWidth)
            {
                TimeRulerTicks.Add(new TimeRulerTickDto
                {
                    Date = currentDate,
                    PixelX = pixelX,
                    IsMajor = true,
                    Label = currentDate.ToString(labelFormat)
                });
            }

            currentDate = AddInterval(currentDate, majorInterval);
        }

        // Generate minor ticks
        currentDate = RoundToInterval(ViewportStart, minorInterval);
        while (currentDate <= ViewportEnd)
        {
            var days = (currentDate - ViewportStart).TotalDays;
            var pixelX = days * PixelsPerDay;

            // Skip if already a major tick
            if (!TimeRulerTicks.Any(t => Math.Abs(t.PixelX - pixelX) < 5) &&
                pixelX >= 0 && pixelX <= ViewportWidth)
            {
                TimeRulerTicks.Add(new TimeRulerTickDto
                {
                    Date = currentDate,
                    PixelX = pixelX,
                    IsMajor = false,
                    Label = null
                });
            }

            currentDate = AddInterval(currentDate, minorInterval);
        }
    }

    private (string major, string minor, string format) GetTickIntervals()
    {
        return PixelsPerDay switch
        {
            < 0.5 => ("year", "quarter", "yyyy"),
            < 2 => ("quarter", "month", "MMM yyyy"),
            < 7 => ("month", "week", "MMM yyyy"),
            < 30 => ("week", "day", "MMM d"),
            _ => ("day", "day", "MMM d")
        };
    }

    private DateTime RoundToInterval(DateTime date, string interval)
    {
        return interval switch
        {
            "year" => new DateTime(date.Year, 1, 1),
            "quarter" => new DateTime(date.Year, ((date.Month - 1) / 3) * 3 + 1, 1),
            "month" => new DateTime(date.Year, date.Month, 1),
            "week" => date.AddDays(-(int)date.DayOfWeek),
            "day" => date.Date,
            _ => date
        };
    }

    private DateTime AddInterval(DateTime date, string interval)
    {
        return interval switch
        {
            "year" => date.AddYears(1),
            "quarter" => date.AddMonths(3),
            "month" => date.AddMonths(1),
            "week" => date.AddDays(7),
            "day" => date.AddDays(1),
            _ => date.AddDays(1)
        };
    }

    /// <summary>
    /// Groups eras by category for display.
    /// </summary>
    private void GroupErasByCategory()
    {
        GroupedEras.Clear();

        var groups = GanttEraBars
            .GroupBy(e => e.CategoryId ?? "uncategorized")
            .OrderBy(g => CategoryFilters.FirstOrDefault(c => c.CategoryId == g.Key)?.SortOrder ?? int.MaxValue);

        double currentY = EraLayoutConstants.MilestoneZoneHeight + EraLayoutConstants.TimeRulerHeight;

        foreach (var group in groups)
        {
            var categoryDto = CategoryFilters.FirstOrDefault(c => c.CategoryId == group.Key)
                ?? new EraCategoryDto { CategoryId = group.Key, Name = "Uncategorized" };

            var eras = group.ToList();

            // Calculate row positions within category
            int rowIndex = 0;
            foreach (var era in eras.OrderBy(e => e.StartDate))
            {
                era.RowIndex = rowIndex;
                era.RowY = currentY + EraLayoutConstants.CategoryHeaderHeight +
                          (rowIndex * (EraLayoutConstants.EraRowHeight + EraLayoutConstants.RowSpacing));
                rowIndex++;
            }

            var groupDto = new EraRowGroupDto(categoryDto, eras);
            GroupedEras.Add(groupDto);

            currentY += EraLayoutConstants.CategoryHeaderHeight +
                       (eras.Count * (EraLayoutConstants.EraRowHeight + EraLayoutConstants.RowSpacing));
        }
    }

    /// <summary>
    /// Calculates the total content height for scrolling.
    /// </summary>
    private void CalculateTotalContentHeight()
    {
        TotalContentHeight = EraLayoutConstants.MilestoneZoneHeight +
                            EraLayoutConstants.TimeRulerHeight +
                            GroupedEras.Sum(g => EraLayoutConstants.CategoryHeaderHeight +
                                                 g.Eras.Count * (EraLayoutConstants.EraRowHeight + EraLayoutConstants.RowSpacing));
    }

    #region Era CRUD Operations

    /// <summary>
    /// Prepares the form for adding a new era.
    /// </summary>
    [RelayCommand]
    public void PrepareAddEra()
    {
        _editingEraId = null;
        _currentEraDraftId = null;
        IsEditMode = false;
        EditName = string.Empty;
        EditSubtitle = string.Empty;
        EditStartDate = DateTime.Now;
        EditEndDate = null;
        EditColorCode = ColorPalette[Eras.Count % ColorPalette.Count];
        EditColorOverride = string.Empty;
        EditCategoryId = Categories.FirstOrDefault()?.CategoryId;
        EditDescription = string.Empty;
        EditNotes = string.Empty;
        EditTags.Clear();
    }

    /// <summary>
    /// Prepares the form for editing an existing era.
    /// </summary>
    [RelayCommand]
    public void PrepareEditEra(Era? era)
    {
        if (era == null) return;

        _editingEraId = era.EraId;
        _currentEraDraftId = null;
        IsEditMode = true;
        EditName = era.Name;
        EditSubtitle = era.Subtitle ?? string.Empty;
        EditStartDate = era.StartDate;
        EditEndDate = era.EndDate;
        EditColorCode = era.ColorCode;
        EditColorOverride = era.ColorOverride ?? string.Empty;
        EditCategoryId = era.CategoryId;
        EditDescription = era.Description ?? string.Empty;
        EditNotes = era.Notes ?? string.Empty;

        EditTags.Clear();
        foreach (var tag in era.EraTags.Select(et => et.Tag).OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            EditTags.Add(tag);
        }
    }

    /// <summary>
    /// Adds a tag to the era being edited (trimmed; case-insensitive de-dupe).
    /// </summary>
    public void AddEditTag(string? tag)
    {
        var trimmed = tag?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        if (!EditTags.Any(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            EditTags.Add(trimmed);
        }
    }

    /// <summary>
    /// Removes a tag from the era being edited.
    /// </summary>
    public void RemoveEditTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;

        var match = EditTags.FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            EditTags.Remove(match);
        }
    }

    /// <summary>
    /// Saves the current era (create or update). Command form of
    /// <see cref="TrySaveEraAsync"/> for bindings that do not need the result.
    /// </summary>
    [RelayCommand]
    public async Task SaveEraAsync()
    {
        await TrySaveEraAsync();
    }

    /// <summary>
    /// Saves the current era (create or update) and reports whether it
    /// succeeded. On failure <see cref="StatusMessage"/> carries the reason
    /// (validation, missing row, or the exception message) so the era dialog
    /// can stay open and show it inline instead of closing over a lost edit.
    /// </summary>
    public async Task<bool> TrySaveEraAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            StatusMessage = "Era name is required";
            return false;
        }

        try
        {
            IsLoading = true;

            string savedEraId;
            if (_editingEraId == null)
            {
                var newEra = new Era
                {
                    Name = EditName.Trim(),
                    Subtitle = string.IsNullOrWhiteSpace(EditSubtitle) ? null : EditSubtitle.Trim(),
                    StartDate = EditStartDate,
                    EndDate = EditEndDate,
                    ColorCode = EditColorCode,
                    ColorOverride = string.IsNullOrWhiteSpace(EditColorOverride) ? null : EditColorOverride.Trim(),
                    CategoryId = EditCategoryId,
                    Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim(),
                    Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim()
                };

                await _eraRepository.AddAsync(newEra);
                savedEraId = newEra.EraId;
                StatusMessage = $"Created era: {newEra.Name}";
                _logger.LogInformation("Created era: {Name}", newEra.Name);
            }
            else
            {
                var existingEra = await _eraRepository.GetByIdAsync(_editingEraId);
                if (existingEra == null)
                {
                    StatusMessage = "Era not found";
                    return false;
                }

                existingEra.Name = EditName.Trim();
                existingEra.Subtitle = string.IsNullOrWhiteSpace(EditSubtitle) ? null : EditSubtitle.Trim();
                existingEra.StartDate = EditStartDate;
                existingEra.EndDate = EditEndDate;
                existingEra.ColorCode = EditColorCode;
                existingEra.ColorOverride = string.IsNullOrWhiteSpace(EditColorOverride) ? null : EditColorOverride.Trim();
                existingEra.CategoryId = EditCategoryId;
                existingEra.Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim();
                existingEra.Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim();
                existingEra.UpdatedAt = DateTime.UtcNow;

                await _eraRepository.UpdateAsync(existingEra);
                savedEraId = existingEra.EraId;
                StatusMessage = $"Updated era: {existingEra.Name}";
                _logger.LogInformation("Updated era: {EraId} - {Name}", existingEra.EraId, existingEra.Name);
            }

            // The era row is committed, so a companion is now entitled to see it
            // (design §19 Phase 3). Published here rather than after the tag
            // replacement below for two reasons: the era projection carries no
            // tags, so waiting would add nothing; and a failure replacing the
            // junction rows must not cost the companion an era that is already
            // in the database. See _projectionPublisher on why a ViewModel is
            // the one making this call.
            await TryPublishEraProjectionAsync(savedEraId);

            await ReplaceEraTagsAsync(savedEraId, EditTags.ToList());
            await DeleteResumedDraftAsync();

            await LoadErasAsync();
            CalculateViewport();
            GenerateGanttLayout();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving era");
            StatusMessage = $"Error saving era: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Replaces the era_tags rows for an era with the given tag set (trimmed,
    /// case-insensitively de-duplicated). Uses a short-lived context because
    /// the era repository's detached-graph Update cannot add or remove
    /// junction rows.
    /// </summary>
    private async Task ReplaceEraTagsAsync(string eraId, IReadOnlyCollection<string> tags)
    {
        var desired = tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var context = await _contextFactory.CreateDbContextAsync();

        var existing = await context.EraTags
            .Where(et => et.EraId == eraId)
            .ToListAsync();

        var desiredSet = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        foreach (var row in existing.Where(et => !desiredSet.Contains(et.Tag)))
        {
            context.EraTags.Remove(row);
        }

        var existingSet = new HashSet<string>(existing.Select(et => et.Tag), StringComparer.OrdinalIgnoreCase);
        foreach (var tag in desired.Where(t => !existingSet.Contains(t)))
        {
            context.EraTags.Add(new EraTag { EraId = eraId, Tag = tag });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes the draft this editor session was resumed from (if any) after a
    /// successful real save. A draft-delete failure must not fail the save.
    /// </summary>
    private async Task DeleteResumedDraftAsync()
    {
        if (_currentEraDraftId == null) return;

        var draftId = _currentEraDraftId;
        _currentEraDraftId = null;
        try
        {
            await _draftService.DeleteDraftAsync(draftId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error deleting era draft after save: {DraftId}", draftId);
        }
    }

    /// <summary>
    /// Saves the current era editor state as a draft (upsert: when the editor
    /// was resumed from a draft, or a draft was already saved this session,
    /// that draft is updated in place).
    /// </summary>
    [RelayCommand]
    public async Task SaveEraDraftAsync()
    {
        try
        {
            var payload = new EraDraftPayload
            {
                Name = EditName.Trim(),
                Subtitle = string.IsNullOrWhiteSpace(EditSubtitle) ? null : EditSubtitle.Trim(),
                StartDate = EditStartDate,
                EndDate = EditEndDate,
                CategoryId = EditCategoryId,
                ColorCode = EditColorCode,
                ColorOverride = string.IsNullOrWhiteSpace(EditColorOverride) ? null : EditColorOverride.Trim(),
                Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim(),
                Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim(),
                Tags = EditTags.ToList()
            };

            var json = JsonSerializer.Serialize(payload);
            var draft = await _draftService.SaveDraftAsync(DraftTypes.Era, payload.Name, json, _currentEraDraftId);
            _currentEraDraftId = draft.DraftId;

            StatusMessage = $"Draft saved: {draft.Title}";
            _logger.LogInformation("Saved era draft: {DraftId} - {Title}", draft.DraftId, draft.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving era draft");
            StatusMessage = "Error saving draft";
        }
    }

    /// <summary>
    /// Loads an era draft and prefills the era editor fields from its payload.
    /// Returns true when the draft was found and readable, so the page can
    /// open the editor dialog.
    /// </summary>
    public async Task<bool> LoadEraDraftAsync(string draftId)
    {
        try
        {
            var draft = await _draftService.GetDraftAsync(draftId);
            if (draft == null || draft.DraftType != DraftTypes.Era)
            {
                StatusMessage = "Draft not found";
                return false;
            }

            var payload = draft.GetPayload<EraDraftPayload>();
            if (payload == null)
            {
                StatusMessage = "Draft could not be read";
                _logger.LogWarning("Era draft payload malformed: {DraftId}", draftId);
                return false;
            }

            _editingEraId = null;
            IsEditMode = false;
            EditName = payload.Name;
            EditSubtitle = payload.Subtitle ?? string.Empty;
            EditStartDate = payload.StartDate ?? DateTime.Now;
            EditEndDate = payload.EndDate;
            EditColorCode = string.IsNullOrWhiteSpace(payload.ColorCode)
                ? ColorPalette[Eras.Count % ColorPalette.Count]
                : payload.ColorCode;
            EditColorOverride = payload.ColorOverride ?? string.Empty;
            EditCategoryId = payload.CategoryId ?? Categories.FirstOrDefault()?.CategoryId;
            EditDescription = payload.Description ?? string.Empty;
            EditNotes = payload.Notes ?? string.Empty;

            EditTags.Clear();
            foreach (var tag in payload.Tags.Select(t => t.Trim()).Where(t => t.Length > 0))
            {
                AddEditTag(tag);
            }

            _currentEraDraftId = draftId;
            StatusMessage = $"Resumed draft: {draft.Title}";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading era draft: {DraftId}", draftId);
            StatusMessage = "Error loading draft";
            return false;
        }
    }

    /// <summary>
    /// Deletes the specified era.
    /// </summary>
    [RelayCommand]
    public async Task DeleteEraAsync(Era? era)
    {
        if (era == null) return;

        try
        {
            IsLoading = true;
            StatusMessage = "Deleting era...";

            await _eraRepository.DeleteAsync(era);

            // The row is gone, so the tombstone is the only thing left that can
            // tell a companion to drop its copy — otherwise the phone keeps
            // showing an era the user deleted on the PC. Same layering caveat as
            // the save path; see _projectionPublisher.
            await TryPublishEraDeletionAsync(era.EraId);

            if (SelectedEra?.EraId == era.EraId)
            {
                SelectedEra = null;
            }

            StatusMessage = $"Deleted era: {era.Name}";
            _logger.LogInformation("Deleted era: {EraId} - {Name}", era.EraId, era.Name);

            await LoadErasAsync();
            CalculateViewport();
            GenerateGanttLayout();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting era: {EraId}", era.EraId);
            StatusMessage = "Error deleting era";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Selects an era for viewing/editing.
    /// </summary>
    [RelayCommand]
    public void SelectEra(Era? era)
    {
        SelectedEra = era;

        // Update selection state in DTOs
        foreach (var bar in GanttEraBars)
        {
            bar.IsSelected = bar.EraId == era?.EraId;
        }
    }

    /// <summary>
    /// Projects a saved era toward companion devices. Deliberately unconditional
    /// on WHAT changed: the publisher drops a projection identical to the last
    /// one it published for the era, so a caller trying to guess whether an edit
    /// was "worth" sending would only get it wrong. A colour or category change
    /// counts, incidentally — the payload carries
    /// <see cref="Era.EffectiveColor"/>, not the raw column.
    ///
    /// Publishing failure must never fail a write that already committed; the
    /// user's era is saved either way, and the next edit republishes.
    /// </summary>
    private async Task TryPublishEraProjectionAsync(string eraId)
    {
        if (_projectionPublisher == null)
        {
            return;
        }

        try
        {
            await _projectionPublisher.PublishEraAsync(eraId);
        }
        catch (Exception publishEx)
        {
            _logger.LogWarning(publishEx, "Failed to publish era projection for {EraId}", eraId);
        }
    }

    /// <summary>
    /// Tombstones a deleted era for companion devices. Unlike the upsert this
    /// has no second chance — nothing will ever republish for an id whose row no
    /// longer exists — so a failure here is logged as the one place a companion
    /// can be left holding a stale era.
    /// </summary>
    private async Task TryPublishEraDeletionAsync(string eraId)
    {
        if (_projectionPublisher == null)
        {
            return;
        }

        try
        {
            await _projectionPublisher.PublishDeletedAsync(TimelineProjectionEntity.Era, eraId);
        }
        catch (Exception publishEx)
        {
            _logger.LogWarning(
                publishEx,
                "Failed to publish deletion of era {EraId}; companions may keep showing it", eraId);
        }
    }

    #endregion

    #region Milestone CRUD Operations

    /// <summary>
    /// Prepares the form for adding a new milestone.
    /// </summary>
    [RelayCommand]
    public void PrepareAddMilestone()
    {
        _editingMilestoneId = null;
        IsMilestoneEditMode = false;
        EditMilestoneName = string.Empty;
        EditMilestoneDate = DateTime.Now;
        EditMilestoneType = MilestoneType.Standard;
        EditMilestoneLinkedEraId = null;
        EditMilestoneColorOverride = string.Empty;
        EditMilestoneDescription = string.Empty;
    }

    /// <summary>
    /// Prepares the form for editing an existing milestone.
    /// </summary>
    [RelayCommand]
    public void PrepareEditMilestone(Milestone? milestone)
    {
        if (milestone == null) return;

        _editingMilestoneId = milestone.MilestoneId;
        IsMilestoneEditMode = true;
        EditMilestoneName = milestone.Name;
        EditMilestoneDate = milestone.Date;
        EditMilestoneType = milestone.Type;
        EditMilestoneLinkedEraId = milestone.LinkedEraId;
        EditMilestoneColorOverride = milestone.ColorOverride ?? string.Empty;
        EditMilestoneDescription = milestone.Description ?? string.Empty;
    }

    /// <summary>
    /// Saves the current milestone (create or update). Command form of
    /// <see cref="TrySaveMilestoneAsync"/> for bindings that do not need the result.
    /// </summary>
    [RelayCommand]
    public async Task SaveMilestoneAsync()
    {
        await TrySaveMilestoneAsync();
    }

    /// <summary>
    /// Saves the current milestone (create or update) and reports whether it
    /// succeeded; on failure <see cref="StatusMessage"/> carries the reason so
    /// the milestone dialog can stay open and show it inline.
    /// </summary>
    public async Task<bool> TrySaveMilestoneAsync()
    {
        if (string.IsNullOrWhiteSpace(EditMilestoneName))
        {
            StatusMessage = "Milestone name is required";
            return false;
        }

        try
        {
            IsLoading = true;

            if (_editingMilestoneId == null)
            {
                var newMilestone = new Milestone
                {
                    Name = EditMilestoneName.Trim(),
                    Date = EditMilestoneDate,
                    Type = EditMilestoneType,
                    LinkedEraId = EditMilestoneLinkedEraId,
                    ColorOverride = string.IsNullOrWhiteSpace(EditMilestoneColorOverride) ? null : EditMilestoneColorOverride,
                    Description = string.IsNullOrWhiteSpace(EditMilestoneDescription) ? null : EditMilestoneDescription.Trim()
                };

                await _milestoneRepository.AddAsync(newMilestone);
                StatusMessage = $"Created milestone: {newMilestone.Name}";
                _logger.LogInformation("Created milestone: {Name}", newMilestone.Name);
            }
            else
            {
                var existingMilestone = await _milestoneRepository.GetByIdAsync(_editingMilestoneId);
                if (existingMilestone == null)
                {
                    StatusMessage = "Milestone not found";
                    return false;
                }

                existingMilestone.Name = EditMilestoneName.Trim();
                existingMilestone.Date = EditMilestoneDate;
                existingMilestone.Type = EditMilestoneType;
                existingMilestone.LinkedEraId = EditMilestoneLinkedEraId;
                existingMilestone.ColorOverride = string.IsNullOrWhiteSpace(EditMilestoneColorOverride) ? null : EditMilestoneColorOverride;
                existingMilestone.Description = string.IsNullOrWhiteSpace(EditMilestoneDescription) ? null : EditMilestoneDescription.Trim();
                existingMilestone.UpdatedAt = DateTime.UtcNow;

                await _milestoneRepository.UpdateAsync(existingMilestone);
                StatusMessage = $"Updated milestone: {existingMilestone.Name}";
                _logger.LogInformation("Updated milestone: {MilestoneId} - {Name}", existingMilestone.MilestoneId, existingMilestone.Name);
            }

            await LoadMilestonesAsync();
            GenerateGanttLayout();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving milestone");
            StatusMessage = $"Error saving milestone: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Deletes the specified milestone.
    /// </summary>
    [RelayCommand]
    public async Task DeleteMilestoneAsync(Milestone? milestone)
    {
        if (milestone == null) return;

        try
        {
            IsLoading = true;
            await _milestoneRepository.DeleteAsync(milestone);

            if (SelectedMilestone?.MilestoneId == milestone.MilestoneId)
            {
                SelectedMilestone = null;
            }

            StatusMessage = $"Deleted milestone: {milestone.Name}";
            _logger.LogInformation("Deleted milestone: {MilestoneId} - {Name}", milestone.MilestoneId, milestone.Name);

            await LoadMilestonesAsync();
            GenerateGanttLayout();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting milestone: {MilestoneId}", milestone.MilestoneId);
            StatusMessage = $"Error deleting milestone: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    #region Filtering and Navigation

    /// <summary>
    /// Toggles visibility of a category.
    /// </summary>
    [RelayCommand]
    public void ToggleCategoryVisibility(string categoryId)
    {
        var category = CategoryFilters.FirstOrDefault(c => c.CategoryId == categoryId);
        if (category != null)
        {
            category.IsVisible = !category.IsVisible;
            GenerateGanttLayout();
        }
    }

    /// <summary>
    /// Shows all categories.
    /// </summary>
    [RelayCommand]
    public void ShowAllCategories()
    {
        foreach (var category in CategoryFilters)
        {
            category.IsVisible = true;
        }
        GenerateGanttLayout();
    }

    /// <summary>
    /// Hides all categories.
    /// </summary>
    [RelayCommand]
    public void HideAllCategories()
    {
        foreach (var category in CategoryFilters)
        {
            category.IsVisible = false;
        }
        GenerateGanttLayout();
    }

    /// <summary>
    /// Applies the search filter.
    /// </summary>
    partial void OnSearchFilterChanged(string value)
    {
        GenerateGanttLayout();
    }

    /// <summary>
    /// Zooms in on the timeline.
    /// </summary>
    [RelayCommand]
    public void ZoomIn()
    {
        PixelsPerDay = Math.Min(PixelsPerDay * 1.5, 100);
        GenerateGanttLayout();
    }

    /// <summary>
    /// Zooms out on the timeline.
    /// </summary>
    [RelayCommand]
    public void ZoomOut()
    {
        PixelsPerDay = Math.Max(PixelsPerDay / 1.5, 0.1);
        GenerateGanttLayout();
    }

    /// <summary>
    /// Navigates to the earliest era.
    /// </summary>
    [RelayCommand]
    public void NavigateToStart()
    {
        if (Eras.Any())
        {
            var earliest = Eras.Min(e => e.StartDate);
            CenterOnDate(earliest);
        }
    }

    /// <summary>
    /// Navigates to the latest era or today.
    /// </summary>
    [RelayCommand]
    public void NavigateToEnd()
    {
        if (Eras.Any())
        {
            var latest = Eras.Max(e => e.EndDate ?? DateTime.Now);
            CenterOnDate(latest);
        }
        else
        {
            CenterOnDate(DateTime.Now);
        }
    }

    /// <summary>
    /// Centers the viewport on a specific date.
    /// </summary>
    public void CenterOnDate(DateTime date)
    {
        var visibleDays = ViewportWidth / PixelsPerDay;
        ViewportStart = date.AddDays(-visibleDays / 2);
        ViewportEnd = date.AddDays(visibleDays / 2);
        GenerateGanttLayout();
    }

    #endregion

    /// <summary>
    /// Gets the duration text for an era.
    /// </summary>
    public static string GetDurationText(Era era)
    {
        if (era.EndDate.HasValue)
        {
            var duration = era.EndDate.Value - era.StartDate;
            if (duration.TotalDays >= 365)
            {
                var years = (int)(duration.TotalDays / 365);
                return $"{years} year{(years != 1 ? "s" : "")}";
            }
            else if (duration.TotalDays >= 30)
            {
                var months = (int)(duration.TotalDays / 30);
                return $"{months} month{(months != 1 ? "s" : "")}";
            }
            else
            {
                return $"{(int)duration.TotalDays} day{((int)duration.TotalDays != 1 ? "s" : "")}";
            }
        }
        return "Ongoing";
    }
}
