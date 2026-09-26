namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Where a save athlete currently resides. MSS-006 places every selected
/// athlete in its sporting color's common pool because Season 1 leagues are
/// created by a later task. Future tasks add active-league states; existing
/// rows keep their persisted value.
/// </summary>
public enum SaveAthleteStatus
{
    CommonPool = 0,
}
