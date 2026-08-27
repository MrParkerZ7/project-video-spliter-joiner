using System;
using System.Globalization;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Pure, WPF-free logic for the Bulk row's editable IN/OUT time field (T-118). Extracted from
/// <c>BulkCutView</c> so both the clock parsing and — critically — the decision of *whether a commit
/// should happen at all* are unit-testable without a visual.
///
/// <para><b>Why the guard exists.</b> The field is bound one-way to a VM-rendered time and commits on
/// <c>LostFocus</c>. Before T-118 that commit ran on EVERY focus loss, even when the user never typed,
/// parsing the *rendered* text (the keyframe-snapped time, truncated to 0.1s) back into
/// <c>Requested</c> — silently destroying the user's real request, zeroing the snap delta that is the
/// only evidence a snap occurred, and sending the truncated value to the engine. A value the VM itself
/// rendered must never be written back as if the user had typed it.</para>
/// </summary>
public static class CutTimeCommit
{
    /// <summary>
    /// True when <paramref name="boxText"/> is (ignoring surrounding whitespace) exactly what the VM
    /// rendered for <paramref name="renderedValue"/> — i.e. the user did not edit the field, so there is
    /// nothing to commit. Callers pass whichever time the field currently displays.
    /// </summary>
    public static bool IsUnchanged(string? boxText, TimeSpan renderedValue)
        => string.Equals(
            boxText?.Trim(),
            CutMarkerViewModel.FormatClock(renderedValue),
            StringComparison.Ordinal);

    /// <summary>
    /// Resolve a field commit: <c>true</c> (with <paramref name="requested"/> set) only when the text is a
    /// REAL user edit that parses; <c>false</c> for an untouched field (guarding the write-back corruption)
    /// or unparseable input (which the caller reverts by refreshing the binding).
    /// </summary>
    public static bool TryResolveEdit(string? boxText, TimeSpan renderedValue, out TimeSpan requested)
    {
        requested = TimeSpan.Zero;
        if (IsUnchanged(boxText, renderedValue))
        {
            return false;
        }

        return TryParseClock(boxText, out requested);
    }

    /// <summary>Parse <c>mm:ss.f</c> / <c>h:mm:ss.f</c> / plain seconds into a non-negative TimeSpan.</summary>
    public static bool TryParseClock(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length > 3)
        {
            return false;
        }

        double h = 0, m = 0, s;
        try
        {
            if (parts.Length == 3)
            {
                h = double.Parse(parts[0], CultureInfo.InvariantCulture);
                m = double.Parse(parts[1], CultureInfo.InvariantCulture);
                s = double.Parse(parts[2], CultureInfo.InvariantCulture);
            }
            else if (parts.Length == 2)
            {
                m = double.Parse(parts[0], CultureInfo.InvariantCulture);
                s = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            else
            {
                s = double.Parse(parts[0], CultureInfo.InvariantCulture);
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        var total = (h * 3600d) + (m * 60d) + s;
        if (total < 0 || double.IsNaN(total) || double.IsInfinity(total))
        {
            return false;
        }

        value = TimeSpan.FromSeconds(total);
        return true;
    }
}
