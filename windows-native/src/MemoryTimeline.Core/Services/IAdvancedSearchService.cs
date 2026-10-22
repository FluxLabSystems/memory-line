using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using System.Diagnostics;
using System.Text.Json;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for advanced search operations.
/// </summary>
public interface IAdvancedSearchService
{
    // Faceted search
    Task<SearchResults> SearchAsync(SearchFilter filter);
    Task<SearchFacets> GetFacetsAsync(SearchFilter? baseFilter = null);

    // Autocomplete
    Task<List<AutocompleteSuggestion>> GetAutocompleteSuggestionsAsync(string query, int maxResults = 10);

    // Saved searches
    Task<SavedSearch> SaveSearchAsync(string name, SearchFilter filter, bool isFavorite = false);
    Task<SavedSearch?> GetSavedSearchAsync(string savedSearchId);
    Task<List<SavedSearch>> GetAllSavedSearchesAsync();
    Task<List<SavedSearch>> GetFavoriteSavedSearchesAsync();
    Task<List<SavedSearch>> GetRecentSavedSearchesAsync(int count = 10);
    Task UpdateSavedSearchAsync(SavedSearch savedSearch);
    Task DeleteSavedSearchAsync(string savedSearchId);
    Task MarkSearchAsUsedAsync(string savedSearchId);

    // Convert between filter and saved search
    SearchFilter ConvertToFilter(SavedSearch savedSearch);
}

/// <summary>
/// Advanced search service implementation with faceted search and autocomplete.
/// </summary>
public class AdvancedSearchService : IAdvancedSearchService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<AdvancedSearchService> _logger;

    public AdvancedSearchService(IDbContextFactory<AppDbContext> contextFactory, ILogger<AdvancedSearchService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    #region Faceted Search

    public async Task<SearchResults> SearchAsync(SearchFilter filter)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new SearchResults
        {
            PageNumber = filter.PageNumber,
            PageSize = filter.PageSize
        };

        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var query = dbContext.Events
                .AsNoTracking() // Ensure fresh data from database, avoid caching issues
                .Include(e => e.EventTags).ThenInclude(et => et.Tag)
                .Include(e => e.EventPeople).ThenInclude(ep => ep.Person)
                .Include(e => e.EventLocations).ThenInclude(el => el.Location)
                .Include(e => e.Era)
                .AsQueryable();

            // Apply filters
            query = ApplyFilters(query, filter);

            // Get total count before pagination
            results.TotalCount = await query.CountAsync();

            // Apply sorting
            query = ApplySorting(query, filter);

            // Apply pagination
            var skip = (filter.PageNumber - 1) * filter.PageSize;
            query = query.Skip(skip).Take(filter.PageSize);

            results.Events = await query.ToListAsync();

            // Get facets for current filter context (without current selections)
            results.Facets = await GetFacetsAsync(filter);

            stopwatch.Stop();
            results.SearchDuration = stopwatch.Elapsed;

            _logger.LogInformation(
                "Search completed: {Count} results in {Duration}ms",
                results.TotalCount, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing search");
            throw;
        }

        return results;
    }

    /// <summary>
    /// The ONE search-term predicate, shared by the result query and every
    /// facet query. Title, description, transcript and the free-text location
    /// all match; the facets used to search only title and description, so a
    /// term that matched a transcript alone counted events in the list that no
    /// facet acknowledged, and the two disagreed on screen.
    /// </summary>
    private static IQueryable<Event> ApplySearchTerm(IQueryable<Event> query, string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return query;
        }

        var term = searchTerm.ToLower();
        return query.Where(e =>
            e.Title.ToLower().Contains(term) ||
            (e.Description != null && e.Description.ToLower().Contains(term)) ||
            (e.RawTranscript != null && e.RawTranscript.ToLower().Contains(term)) ||
            (e.Location != null && e.Location.ToLower().Contains(term)));
    }

    private IQueryable<Event> ApplyFilters(IQueryable<Event> query, SearchFilter filter)
    {
        // Text search
        query = ApplySearchTerm(query, filter.SearchTerm);

        // Category filter
        if (filter.Categories.Any())
        {
            query = query.Where(e => e.Category != null && filter.Categories.Contains(e.Category));
        }

        // Tag filter
        if (filter.TagIds.Any())
        {
            query = query.Where(e => e.EventTags.Any(et => filter.TagIds.Contains(et.TagId)));
        }

        // Person filter
        if (filter.PersonIds.Any())
        {
            query = query.Where(e => e.EventPeople.Any(ep => filter.PersonIds.Contains(ep.PersonId)));
        }

        // Location filter
        if (filter.LocationIds.Any())
        {
            query = query.Where(e => e.EventLocations.Any(el => filter.LocationIds.Contains(el.LocationId)));
        }

        // Era filter
        if (filter.EraIds.Any())
        {
            query = query.Where(e => e.EraId != null && filter.EraIds.Contains(e.EraId));
        }

        // Date range filter
        if (filter.StartDate.HasValue)
        {
            query = query.Where(e => e.StartDate >= filter.StartDate.Value);
        }
        if (filter.EndDate.HasValue)
        {
            query = query.Where(e => e.StartDate <= filter.EndDate.Value);
        }

        // Has audio filter
        if (filter.HasAudio.HasValue)
        {
            if (filter.HasAudio.Value)
                query = query.Where(e => e.AudioFilePath != null && e.AudioFilePath != "");
            else
                query = query.Where(e => e.AudioFilePath == null || e.AudioFilePath == "");
        }

        // Has transcript filter
        if (filter.HasTranscript.HasValue)
        {
            if (filter.HasTranscript.Value)
                query = query.Where(e => e.RawTranscript != null && e.RawTranscript != "");
            else
                query = query.Where(e => e.RawTranscript == null || e.RawTranscript == "");
        }

        // Min confidence filter
        if (filter.MinConfidence.HasValue)
        {
            query = query.Where(e => e.Confidence == null || e.Confidence >= filter.MinConfidence.Value);
        }

        return query;
    }

    private static IQueryable<Event> ApplySorting(IQueryable<Event> query, SearchFilter filter)
    {
        return filter.SortBy switch
        {
            SearchSortOptions.DateDesc => query.OrderByDescending(e => e.StartDate),
            SearchSortOptions.DateAsc => query.OrderBy(e => e.StartDate),
            SearchSortOptions.TitleAsc => query.OrderBy(e => e.Title),
            SearchSortOptions.TitleDesc => query.OrderByDescending(e => e.Title),
            SearchSortOptions.CreatedDesc => query.OrderByDescending(e => e.CreatedAt),
            SearchSortOptions.CreatedAsc => query.OrderBy(e => e.CreatedAt),
            SearchSortOptions.Relevance => ApplyRelevanceOrder(query, filter.SearchTerm),
            _ => query.OrderByDescending(e => e.StartDate)
        };
    }

    /// <summary>
    /// Relevance: where the term matches, then recency. An exact title match
    /// outranks a title that merely contains the term, which outranks a match
    /// in the description or the free-text location, which outranks one buried
    /// in the transcript; ties fall back to the newest first. Without a term
    /// there is nothing to be relevant to, and the order is simply by date.
    /// The score is a sum of conditionals, which EF translates to CASE
    /// expressions and evaluates in the database.
    /// </summary>
    private static IQueryable<Event> ApplyRelevanceOrder(IQueryable<Event> query, string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return query.OrderByDescending(e => e.StartDate);
        }

        var term = searchTerm.ToLower();
        return query
            .OrderByDescending(e =>
                (e.Title.ToLower() == term ? 8 : 0)
                + (e.Title.ToLower().Contains(term) ? 4 : 0)
                + (e.Description != null && e.Description.ToLower().Contains(term) ? 2 : 0)
                + (e.Location != null && e.Location.ToLower().Contains(term) ? 2 : 0)
                + (e.RawTranscript != null && e.RawTranscript.ToLower().Contains(term) ? 1 : 0))
            .ThenByDescending(e => e.StartDate);
    }

    public async Task<SearchFacets> GetFacetsAsync(SearchFilter? baseFilter = null)
    {
        var facets = new SearchFacets();

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var baseQuery = dbContext.Events.AsNoTracking().AsQueryable();

        // Apply base filter if provided (excluding the facet being calculated).
        // The search term uses the SAME predicate as the result list, so every
        // facet counts exactly the events the list shows.
        if (baseFilter != null)
        {
            baseQuery = ApplySearchTerm(baseQuery, baseFilter.SearchTerm);
            if (baseFilter.StartDate.HasValue)
                baseQuery = baseQuery.Where(e => e.StartDate >= baseFilter.StartDate.Value);
            if (baseFilter.EndDate.HasValue)
                baseQuery = baseQuery.Where(e => e.StartDate <= baseFilter.EndDate.Value);
        }

        // Each facet is calculated independently so one failure doesn't abort the rest.

        // Category facets
        try
        {
            facets.Categories = await baseQuery
                .Where(e => e.Category != null)
                .GroupBy(e => e.Category!)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Category, x => x.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating category facets");
        }

        // Tag facets (group by mapped TagName column, not the [NotMapped] Name alias)
        try
        {
            var tagCounts = await dbContext.EventTags
                .Where(et => baseQuery.Any(e => e.EventId == et.EventId))
                .GroupBy(et => new { et.TagId, et.Tag.TagName, et.Tag.Color })
                .Select(g => new { g.Key.TagId, g.Key.TagName, g.Key.Color, Count = g.Count() })
                .ToListAsync();

            facets.Tags = tagCounts.ToDictionary(
                x => x.TagId,
                x => new FacetItem { Id = x.TagId, Name = x.TagName, Count = x.Count, Color = x.Color });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating tag facets");
        }

        // People facets
        try
        {
            var personCounts = await dbContext.EventPeople
                .Where(ep => baseQuery.Any(e => e.EventId == ep.EventId))
                .GroupBy(ep => new { ep.PersonId, ep.Person.Name })
                .Select(g => new { g.Key.PersonId, g.Key.Name, Count = g.Count() })
                .ToListAsync();

            facets.People = personCounts.ToDictionary(
                x => x.PersonId,
                x => new FacetItem { Id = x.PersonId, Name = x.Name, Count = x.Count });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating people facets");
        }

        // Location facets
        try
        {
            var locationCounts = await dbContext.EventLocations
                .Where(el => baseQuery.Any(e => e.EventId == el.EventId))
                .GroupBy(el => new { el.LocationId, el.Location.Name })
                .Select(g => new { g.Key.LocationId, g.Key.Name, Count = g.Count() })
                .ToListAsync();

            facets.Locations = locationCounts.ToDictionary(
                x => x.LocationId,
                x => new FacetItem { Id = x.LocationId, Name = x.Name, Count = x.Count });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating location facets");
        }

        // Era facets (group by mapped ColorCode column, not the [NotMapped] Color alias)
        try
        {
            var eraCounts = await baseQuery
                .Where(e => e.EraId != null && e.Era != null)
                .GroupBy(e => new { e.EraId, e.Era!.Name, e.Era.ColorCode })
                .Select(g => new { EraId = g.Key.EraId!, g.Key.Name, g.Key.ColorCode, Count = g.Count() })
                .ToListAsync();

            facets.Eras = eraCounts.ToDictionary(
                x => x.EraId,
                x => new FacetItem { Id = x.EraId, Name = x.Name, Count = x.Count, Color = x.ColorCode });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating era facets");
        }

        // Audio/transcript counts
        try
        {
            facets.WithAudioCount = await baseQuery.CountAsync(e => e.AudioFilePath != null && e.AudioFilePath != "");
            facets.WithTranscriptCount = await baseQuery.CountAsync(e => e.RawTranscript != null && e.RawTranscript != "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating audio/transcript facet counts");
        }

        // Date range
        try
        {
            facets.EarliestDate = await baseQuery.MinAsync(e => (DateTime?)e.StartDate);
            facets.LatestDate = await baseQuery.MaxAsync(e => (DateTime?)e.StartDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating date range facets");
        }

        return facets;
    }

    #endregion

    #region Autocomplete

    public async Task<List<AutocompleteSuggestion>> GetAutocompleteSuggestionsAsync(string query, int maxResults = 10)
    {
        var suggestions = new List<AutocompleteSuggestion>();

        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return suggestions;

        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            var lowerQuery = query.ToLower();
            var halfResults = maxResults / 2;

            // Event title suggestions
            var eventSuggestions = await dbContext.Events
                .Where(e => e.Title.ToLower().Contains(lowerQuery))
                .OrderByDescending(e => e.StartDate)
                .Take(halfResults)
                .Select(e => new AutocompleteSuggestion
                {
                    Text = e.Title,
                    Type = "event",
                    Id = e.EventId,
                    Context = e.StartDate.ToString("MMM yyyy")
                })
                .ToListAsync();
            suggestions.AddRange(eventSuggestions);

            // Tag suggestions (query the mapped TagName column, not the [NotMapped] Name alias)
            var tagSuggestions = await dbContext.Tags
                .Where(t => t.TagName.ToLower().Contains(lowerQuery))
                .Take(halfResults / 2)
                .Select(t => new AutocompleteSuggestion
                {
                    Text = t.TagName,
                    Type = "tag",
                    Id = t.TagId
                })
                .ToListAsync();
            suggestions.AddRange(tagSuggestions);

            // Person suggestions (living only: a merged-away tombstone keeps
            // its name but has zero event links, so selecting it would filter
            // to guaranteed-empty results)
            var personSuggestions = await dbContext.People
                .Where(p => p.MergedIntoId == null && p.Name.ToLower().Contains(lowerQuery))
                .Take(halfResults / 2)
                .Select(p => new AutocompleteSuggestion
                {
                    Text = p.Name,
                    Type = "person",
                    Id = p.PersonId
                })
                .ToListAsync();
            suggestions.AddRange(personSuggestions);

            // Location suggestions
            var locationSuggestions = await dbContext.Locations
                .Where(l => l.Name.ToLower().Contains(lowerQuery))
                .Take(halfResults / 2)
                .Select(l => new AutocompleteSuggestion
                {
                    Text = l.Name,
                    Type = "location",
                    Id = l.LocationId
                })
                .ToListAsync();
            suggestions.AddRange(locationSuggestions);

            // Era suggestions
            var eraSuggestions = await dbContext.Eras
                .Where(e => e.Name.ToLower().Contains(lowerQuery))
                .Take(halfResults / 2)
                .Select(e => new AutocompleteSuggestion
                {
                    Text = e.Name,
                    Type = "era",
                    Id = e.EraId
                })
                .ToListAsync();
            suggestions.AddRange(eraSuggestions);

            // Score and sort by relevance (prefix match scores higher)
            foreach (var suggestion in suggestions)
            {
                suggestion.Score = suggestion.Text.ToLower().StartsWith(lowerQuery) ? 2.0 : 1.0;
            }

            return suggestions
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Text)
                .Take(maxResults)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting autocomplete suggestions for: {Query}", query);
            return suggestions;
        }
    }

    #endregion

    #region Saved Searches

    public async Task<SavedSearch> SaveSearchAsync(string name, SearchFilter filter, bool isFavorite = false)
    {
        try
        {
            var savedSearch = new SavedSearch
            {
                SavedSearchId = Guid.NewGuid().ToString(),
                Name = name,
                SearchTerm = filter.SearchTerm,
                Categories = filter.Categories.Any() ? JsonSerializer.Serialize(filter.Categories) : null,
                TagIds = filter.TagIds.Any() ? JsonSerializer.Serialize(filter.TagIds) : null,
                PersonIds = filter.PersonIds.Any() ? JsonSerializer.Serialize(filter.PersonIds) : null,
                LocationIds = filter.LocationIds.Any() ? JsonSerializer.Serialize(filter.LocationIds) : null,
                EraIds = filter.EraIds.Any() ? JsonSerializer.Serialize(filter.EraIds) : null,
                StartDate = filter.StartDate,
                EndDate = filter.EndDate,
                HasAudio = filter.HasAudio,
                HasTranscript = filter.HasTranscript,
                MinConfidence = filter.MinConfidence,
                SortBy = filter.SortBy,
                PageSize = filter.PageSize,
                IsFavorite = isFavorite,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await using var dbContext = await _contextFactory.CreateDbContextAsync();
            dbContext.SavedSearches.Add(savedSearch);
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("Saved search created: {Name}", name);
            return savedSearch;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving search: {Name}", name);
            throw;
        }
    }

    public async Task<SavedSearch?> GetSavedSearchAsync(string savedSearchId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();
        return await dbContext.SavedSearches.FindAsync(savedSearchId);
    }

    public async Task<List<SavedSearch>> GetAllSavedSearchesAsync()
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();
        return await dbContext.SavedSearches
            .OrderByDescending(s => s.IsFavorite)
            .ThenByDescending(s => s.LastUsedAt ?? s.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<SavedSearch>> GetFavoriteSavedSearchesAsync()
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();
        return await dbContext.SavedSearches
            .Where(s => s.IsFavorite)
            .OrderByDescending(s => s.UseCount)
            .ToListAsync();
    }

    public async Task<List<SavedSearch>> GetRecentSavedSearchesAsync(int count = 10)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();
        return await dbContext.SavedSearches
            .Where(s => s.LastUsedAt.HasValue)
            .OrderByDescending(s => s.LastUsedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task UpdateSavedSearchAsync(SavedSearch savedSearch)
    {
        try
        {
            savedSearch.UpdatedAt = DateTime.UtcNow;
            await using var dbContext = await _contextFactory.CreateDbContextAsync();
            dbContext.SavedSearches.Update(savedSearch);
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("Saved search updated: {Id}", savedSearch.SavedSearchId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating saved search: {Id}", savedSearch.SavedSearchId);
            throw;
        }
    }

    public async Task DeleteSavedSearchAsync(string savedSearchId)
    {
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();
            var savedSearch = await dbContext.SavedSearches.FindAsync(savedSearchId);
            if (savedSearch != null)
            {
                dbContext.SavedSearches.Remove(savedSearch);
                await dbContext.SaveChangesAsync();
                _logger.LogInformation("Saved search deleted: {Id}", savedSearchId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting saved search: {Id}", savedSearchId);
            throw;
        }
    }

    public async Task MarkSearchAsUsedAsync(string savedSearchId)
    {
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync();
            var savedSearch = await dbContext.SavedSearches.FindAsync(savedSearchId);
            if (savedSearch != null)
            {
                savedSearch.UseCount++;
                savedSearch.LastUsedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking search as used: {Id}", savedSearchId);
        }
    }

    public SearchFilter ConvertToFilter(SavedSearch savedSearch)
    {
        return new SearchFilter
        {
            SearchTerm = savedSearch.SearchTerm,
            Categories = DeserializeList(savedSearch.Categories),
            TagIds = DeserializeList(savedSearch.TagIds),
            PersonIds = DeserializeList(savedSearch.PersonIds),
            LocationIds = DeserializeList(savedSearch.LocationIds),
            EraIds = DeserializeList(savedSearch.EraIds),
            StartDate = savedSearch.StartDate,
            EndDate = savedSearch.EndDate,
            HasAudio = savedSearch.HasAudio,
            HasTranscript = savedSearch.HasTranscript,
            MinConfidence = savedSearch.MinConfidence,
            SortBy = savedSearch.SortBy,
            PageSize = savedSearch.PageSize
        };
    }

    private List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    #endregion
}
