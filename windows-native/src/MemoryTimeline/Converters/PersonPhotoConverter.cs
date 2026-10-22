using MemoryTimeline.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MemoryTimeline.Converters;

/// <summary>
/// Converts a person's stored photo path (media-root-relative, written by
/// <see cref="IMediaService.ImportPersonPhotoAsync"/>) into a
/// <see cref="BitmapImage"/> for a <c>PersonPicture</c>. Returns null when
/// no photo is set or the file is gone, so the initials avatar underneath
/// stays visible; a malformed path never takes the page down.
/// </summary>
public class PersonPhotoConverter : IValueConverter
{
    private IMediaService? _mediaService;

    /// <summary>Resolves the stored path against the media root and loads it.</summary>
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string photoPath || string.IsNullOrWhiteSpace(photoPath))
        {
            return null;
        }

        try
        {
            _mediaService ??= App.Current.Services.GetRequiredService<IMediaService>();
            var absolutePath = _mediaService.GetAbsolutePersonPhotoPath(photoPath);
            if (absolutePath == null || !File.Exists(absolutePath))
            {
                return null;
            }

            return new BitmapImage(new Uri(absolutePath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Not supported.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
