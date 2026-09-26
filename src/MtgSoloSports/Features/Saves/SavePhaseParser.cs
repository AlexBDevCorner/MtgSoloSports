namespace MtgSoloSports.Features.Saves;

/// <summary>
/// Parses persisted phase text. Unknown values are invariant failures and abort.
/// </summary>
public static class SavePhaseParser
{
    public static SavePhase Parse(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        if (Enum.TryParse<SavePhase>(phase, ignoreCase: false, out SavePhase parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Unsupported save phase '{phase}'.");
    }

    public static string ToText(SavePhase phase)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new InvalidOperationException($"Unsupported save phase '{phase}'.");
        }

        return phase.ToString("G");
    }
}
