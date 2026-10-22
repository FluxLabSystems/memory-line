using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Data.Repositories;

public interface IAppSettingRepository : IRepository<AppSetting>
{
    Task<AppSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<bool> SettingExistsAsync(string key, CancellationToken cancellationToken = default);
    Task<IDictionary<string, string>> GetAllSettingsAsync(CancellationToken cancellationToken = default);
}
