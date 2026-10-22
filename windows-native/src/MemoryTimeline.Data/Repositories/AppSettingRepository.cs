using Microsoft.EntityFrameworkCore;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

/// <summary>
/// Repository implementation for AppSetting entity.
/// Creates a short-lived <see cref="AppDbContext"/> per operation via
/// <see cref="IDbContextFactory{TContext}"/> so operations are thread-safe
/// and never share change-tracker state.
/// </summary>
public class AppSettingRepository : IAppSettingRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public AppSettingRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<AppSetting?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AppSettings.FindAsync(new object?[] { id }, cancellationToken);
    }

    public async Task<IEnumerable<AppSetting>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AppSettings.AsNoTracking().OrderBy(s => s.SettingKey).ToListAsync(cancellationToken);
    }

    public async Task<AppSetting> AddAsync(AppSetting entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.AppSettings.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task UpdateAsync(AppSetting entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Entity is detached; Update attaches it and marks it Modified.
        context.AppSettings.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(AppSetting entity, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Remove attaches the detached entity and marks it Deleted.
        context.AppSettings.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<AppSetting> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.AppSettings.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRangeAsync(IEnumerable<AppSetting> entities, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.AppSettings.RemoveRange(entities);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<AppSetting, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AppSettings.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<AppSetting, bool>>? predicate = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return predicate == null
            ? await context.AppSettings.CountAsync(cancellationToken)
            : await context.AppSettings.CountAsync(predicate, cancellationToken);
    }

    public async Task<IEnumerable<AppSetting>> FindAsync(System.Linq.Expressions.Expression<Func<AppSetting, bool>> predicate, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AppSettings.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<AppSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AppSettings.FindAsync(new object?[] { key }, cancellationToken);
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var setting = await GetByKeyAsync(key, cancellationToken);
        return setting?.SettingValue;
    }

    public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        // Fetch-then-save happens inside a single context so the read entity
        // is still tracked when SaveChangesAsync runs.
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await context.AppSettings.FindAsync(new object?[] { key }, cancellationToken);
        if (setting != null)
        {
            setting.SettingValue = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            context.AppSettings.Add(new AppSetting
            {
                SettingKey = key,
                SettingValue = value,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SettingExistsAsync(string key, CancellationToken cancellationToken = default) =>
        await ExistsAsync(s => s.SettingKey == key, cancellationToken);

    public async Task<IDictionary<string, string>> GetAllSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetAllAsync(cancellationToken);
        return settings.ToDictionary(s => s.SettingKey, s => s.SettingValue);
    }
}
