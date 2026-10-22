using CommunityToolkit.Mvvm.ComponentModel;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Core.DTOs;

/// <summary>
/// DTO for displaying and editing a contact-book person. Colors are plain hex
/// strings (no UI framework types); the app layer converts them to brushes.
/// </summary>
public partial class PersonDto : ObservableObject
{
    [ObservableProperty]
    private string _personId = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _nickname;

    [ObservableProperty]
    private string? _relationship;

    [ObservableProperty]
    private string? _email;

    [ObservableProperty]
    private string? _phone;

    [ObservableProperty]
    private DateTime? _birthday;

    [ObservableProperty]
    private string? _company;

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private string? _photoPath;

    [ObservableProperty]
    private string? _avatarColor;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private DateTime? _firstMetDate;

    [ObservableProperty]
    private DateTime _createdAt;

    [ObservableProperty]
    private DateTime _updatedAt;

    [ObservableProperty]
    private int _eventCount;

    [ObservableProperty]
    private DateTime? _firstEventDate;

    [ObservableProperty]
    private DateTime? _lastEventDate;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Ten pleasant mid-tone hex colors used for deterministic avatar
    /// backgrounds. All are readable behind white text on both light and dark
    /// themes.
    /// </summary>
    public static readonly string[] AvatarPalette =
    {
        "#C05A5A", // muted red
        "#BD7C48", // amber
        "#A8973B", // olive gold
        "#5B9E5B", // sage green
        "#3E9E83", // teal
        "#4A8FB5", // steel blue
        "#5C74C4", // indigo
        "#8A66C2", // violet
        "#B45CB4", // orchid
        "#C25B85"  // rose
    };

    /// <summary>
    /// Name for display: the plain name, or "Name (Nickname)" when a nickname
    /// is set.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Nickname) ? Name : $"{Name} ({Nickname})";

    /// <summary>
    /// Up to two uppercase initials taken from the words of <see cref="Name"/>;
    /// "?" when the name is empty.
    /// </summary>
    public string Initials
    {
        get
        {
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0)
            {
                return "?";
            }

            return words.Length == 1
                ? char.ToUpperInvariant(words[0][0]).ToString()
                : $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
        }
    }

    /// <summary>
    /// The avatar color to render: <see cref="AvatarColor"/> when set,
    /// otherwise a deterministic pick from <see cref="AvatarPalette"/> based on
    /// a stable (FNV-1a) hash of <see cref="Name"/>.
    /// </summary>
    public string EffectiveAvatarColor =>
        !string.IsNullOrWhiteSpace(AvatarColor)
            ? AvatarColor
            : AvatarPalette[(int)(Fnv1aHash(Name) % (uint)AvatarPalette.Length)];

    /// <summary>
    /// Human-readable event count: "No events", "1 event", or "N events".
    /// </summary>
    public string EventCountDisplay => EventCount switch
    {
        <= 0 => "No events",
        1 => "1 event",
        _ => $"{EventCount} events"
    };

    /// <summary>
    /// Birthday formatted as "MMM d, yyyy", or an empty string when unset.
    /// </summary>
    public string BirthdayDisplay =>
        Birthday.HasValue ? Birthday.Value.ToString("MMM d, yyyy") : string.Empty;

    /// <summary>True when a birthday is set.</summary>
    public bool HasBirthday => Birthday.HasValue;

    /// <summary>True when an email address is set.</summary>
    public bool HasEmail => !string.IsNullOrWhiteSpace(Email);

    /// <summary>True when a phone number is set.</summary>
    public bool HasPhone => !string.IsNullOrWhiteSpace(Phone);

    /// <summary>True when a company is set.</summary>
    public bool HasCompany => !string.IsNullOrWhiteSpace(Company);

    /// <summary>True when notes are set.</summary>
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>True when a relationship is set.</summary>
    public bool HasRelationship => !string.IsNullOrWhiteSpace(Relationship);

    /// <summary>
    /// True when a photo is stored for the person (<see cref="PhotoPath"/>,
    /// media-root-relative; resolved for display through
    /// <c>IMediaService.GetAbsolutePersonPhotoPath</c>).
    /// </summary>
    public bool HasPhoto => !string.IsNullOrWhiteSpace(PhotoPath);

    // Property change notifications for computed properties.

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(EffectiveAvatarColor));
    }

    partial void OnNicknameChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnAvatarColorChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveAvatarColor));
    }

    partial void OnEventCountChanged(int value)
    {
        OnPropertyChanged(nameof(EventCountDisplay));
    }

    partial void OnBirthdayChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(BirthdayDisplay));
        OnPropertyChanged(nameof(HasBirthday));
    }

    partial void OnEmailChanged(string? value)
    {
        OnPropertyChanged(nameof(HasEmail));
    }

    partial void OnPhoneChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPhone));
    }

    partial void OnCompanyChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCompany));
    }

    partial void OnNotesChanged(string? value)
    {
        OnPropertyChanged(nameof(HasNotes));
    }

    partial void OnRelationshipChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRelationship));
    }

    partial void OnPhotoPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPhoto));
    }

    /// <summary>
    /// Creates a DTO from a <see cref="Person"/> entity, optionally carrying
    /// event statistics.
    /// </summary>
    public static PersonDto FromPerson(
        Person person,
        int eventCount = 0,
        DateTime? firstEventDate = null,
        DateTime? lastEventDate = null)
    {
        return new PersonDto
        {
            PersonId = person.PersonId,
            Name = person.Name,
            Nickname = person.Nickname,
            Relationship = person.Relationship,
            Email = person.Email,
            Phone = person.Phone,
            Birthday = person.Birthday,
            Company = person.Company,
            Notes = person.Notes,
            PhotoPath = person.PhotoPath,
            AvatarColor = person.AvatarColor,
            IsFavorite = person.IsFavorite,
            FirstMetDate = person.FirstMetDate,
            CreatedAt = person.CreatedAt,
            UpdatedAt = person.UpdatedAt,
            EventCount = eventCount,
            FirstEventDate = firstEventDate,
            LastEventDate = lastEventDate
        };
    }

    /// <summary>
    /// Converts the DTO to a full <see cref="Person"/> entity copy, including
    /// <see cref="PersonId"/>.
    /// </summary>
    public Person ToPerson()
    {
        return new Person
        {
            PersonId = PersonId,
            Name = Name,
            Nickname = Nickname,
            Relationship = Relationship,
            Email = Email,
            Phone = Phone,
            Birthday = Birthday,
            Company = Company,
            Notes = Notes,
            PhotoPath = PhotoPath,
            AvatarColor = AvatarColor,
            IsFavorite = IsFavorite,
            FirstMetDate = FirstMetDate,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }

    /// <summary>
    /// Copies the editable contact fields onto an existing entity. Does not
    /// touch <see cref="Person.PersonId"/>, <see cref="Person.CreatedAt"/>, or
    /// <see cref="Person.MergedIntoId"/> (the merge tombstone is service-owned
    /// state, deliberately absent from this DTO: tombstoned people are never
    /// surfaced as DTOs, so exposing it here could only cause accidental
    /// resurrection on update).
    /// </summary>
    public void CopyTo(Person person)
    {
        person.Name = Name;
        person.Nickname = Nickname;
        person.Relationship = Relationship;
        person.Email = Email;
        person.Phone = Phone;
        person.Birthday = Birthday;
        person.Company = Company;
        person.Notes = Notes;
        person.PhotoPath = PhotoPath;
        person.AvatarColor = AvatarColor;
        person.IsFavorite = IsFavorite;
        person.FirstMetDate = FirstMetDate;
    }

    /// <summary>
    /// Stable 32-bit FNV-1a hash over the string's UTF-16 code units. Unlike
    /// <see cref="string.GetHashCode()"/>, the result is identical across
    /// processes, so avatar colors stay consistent between runs.
    /// </summary>
    private static uint Fnv1aHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
