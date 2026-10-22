namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// The per-UTC-day request cap of design §22 decision 5, shared by whichever
/// provider is registered. A single process-wide counter is enough: the
/// service is single-owner and single-instance (§4.2), so there is no other
/// caller spending the same quota. Not persisted — a restart forgives the day,
/// which errs on the side of answering the user rather than of a strict ledger
/// the free tier does not need.
/// </summary>
public sealed class DetourRequestBudget
{
    private readonly object _gate = new();
    private readonly int _dailyCap;
    private DateOnly _day;
    private int _spent;

    /// <param name="dailyCap">Requests allowed per UTC day; 0 means uncapped.</param>
    public DetourRequestBudget(int dailyCap)
    {
        _dailyCap = Math.Max(0, dailyCap);
    }

    /// <summary>Requests allowed per UTC day; 0 means uncapped.</summary>
    public int DailyCap => _dailyCap;

    /// <summary>
    /// Reserves one request for the day containing <paramref name="nowUtc"/>.
    /// Returns false — without counting — when the day's cap is already spent.
    /// </summary>
    public bool TryConsume(DateTime nowUtc)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(nowUtc);
            if (today != _day)
            {
                _day = today;
                _spent = 0;
            }

            if (_dailyCap > 0 && _spent >= _dailyCap)
            {
                return false;
            }

            _spent++;
            return true;
        }
    }

    /// <summary>Requests already spent on the UTC day of <paramref name="nowUtc"/>.</summary>
    public int Spent(DateTime nowUtc)
    {
        lock (_gate)
        {
            return DateOnly.FromDateTime(nowUtc) == _day ? _spent : 0;
        }
    }
}
