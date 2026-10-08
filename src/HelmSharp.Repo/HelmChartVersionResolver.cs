using System.Text.RegularExpressions;

namespace HelmSharp.Repo;

/// <summary>
/// Resolves chart versions against SemVer constraint ranges using the same syntax
/// Helm accepts in Chart.yaml dependency versions (Masterminds/semver style):
/// comparison operators, hyphen ranges, wildcards, tilde/caret ranges, and
/// comma-separated conjunctions joined by <c>||</c> alternatives.
/// </summary>
internal static class HelmChartVersionResolver
{
    /// <summary>
    /// Picks the highest chart version satisfying <paramref name="constraint"/>.
    /// When the constraint is not a valid range it is treated as an exact version string.
    /// </summary>
    public static HelmChartVersion? Resolve(
        IEnumerable<HelmChartVersion> versions,
        string? constraint)
    {
        var selected = ResolveCandidates(
            versions.Select((version, index) => new Candidate<HelmChartVersion>(version, version.Version, index)),
            constraint);
        return selected?.Value;
    }

    /// <summary>
    /// Picks the highest version string satisfying <paramref name="constraint"/>.
    /// Same resolution rules as <see cref="Resolve"/>.
    /// </summary>
    public static string? ResolveVersion(
        IEnumerable<string> versions,
        string? constraint)
    {
        var selected = ResolveCandidates(
            versions.Select((version, index) => new Candidate<string>(version, version, index)),
            constraint);
        return selected?.Value;
    }

    /// <summary>
    /// Orders two version strings SemVer-first. Unparseable versions sort before
    /// parseable ones and fall back to ordinal string comparison between themselves.
    /// </summary>
    public static int CompareVersions(string left, string right)
    {
        var leftParsed = SemanticVersion.TryParse(left, out var leftVersion);
        var rightParsed = SemanticVersion.TryParse(right, out var rightVersion);

        if (leftParsed && rightParsed)
            return SemanticVersionComparer.Instance.Compare(leftVersion, rightVersion);
        if (leftParsed)
            return 1;
        if (rightParsed)
            return -1;
        return string.CompareOrdinal(left, right);
    }

    /// <summary>
    /// Returns whether a single version satisfies the constraint. Unparseable
    /// constraints and versions do not satisfy.
    /// </summary>
    public static bool Satisfies(string version, string? constraint)
    {
        var candidate = new Candidate<string>(version, version, 0);
        return TryParseConstraint(constraint, out var parsedConstraint) &&
               candidate.SemanticVersion is not null &&
               parsedConstraint.IsSatisfiedBy(candidate.SemanticVersion);
    }

    private static Candidate<T>? ResolveCandidates<T>(
        IEnumerable<Candidate<T>> candidates,
        string? constraint)
    {
        var candidateList = candidates.ToList();
        // Chart.yaml "version: 1.2.3" without range syntax is an exact pin; only
        // strings that fail range parsing take this path. Empty constraint selects
        // nothing here — callers wanting "latest" must pass a range or null handling.
        if (!TryParseConstraint(constraint, out var parsedConstraint))
        {
            var exact = constraint?.Trim();
            return string.IsNullOrEmpty(exact)
                ? null
                : candidateList.FirstOrDefault(candidate => candidate.Version.Equals(exact, StringComparison.Ordinal));
        }

        // Highest satisfying version wins; equal versions keep index order (stable
        // across repository entry order) to match Helm's selection on ties.
        return candidateList
            .Where(candidate => candidate.SemanticVersion is not null)
            .Where(candidate => parsedConstraint.IsSatisfiedBy(candidate.SemanticVersion!))
            .OrderByDescending(candidate => candidate.SemanticVersion!, SemanticVersionComparer.Instance)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
    }

    private static bool TryParseConstraint(string? constraint, out VersionConstraint parsed)
    {
        // Constraint grammar pipeline:
        //   1. Split on "||" into alternative groups (union at satisfy time).
        //   2. Each group is either a hyphen range ("1.2 - 2") or a conjunction of
        //      comparator tokens (commas/spaces are equivalent separators).
        //   3. Tokens are re-joined with detached operators (">= 1.2"), then tilde,
        //      caret, wildcard, and partial versions desugar into comparator pairs.
        //   4. Satisfy: a candidate passes when any group's comparators all hold;
        //      prerelease exclusion is decided per group (see ConstraintGroup).
        // A blank constraint means "any stable release" (prereleases excluded unless
        // a comparator mentions one), matching Helm/Masterminds default behavior.
        if (string.IsNullOrWhiteSpace(constraint))
        {
            parsed = VersionConstraint.AnyStable;
            return true;
        }

        // "||" separates alternatives (union); within a group, tokens are AND-ed.
        var groups = new List<ConstraintGroup>();
        foreach (var groupText in constraint.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseConstraintGroup(groupText, out var group))
            {
                parsed = VersionConstraint.AnyStable;
                return false;
            }

            groups.Add(group);
        }

        parsed = new VersionConstraint(groups);
        return groups.Count > 0;
    }

    private static bool TryParseConstraintGroup(string groupText, out ConstraintGroup group)
    {
        groupText = groupText.Trim();
        if (TryParseHyphenRange(groupText, out group))
            return true;

        var comparators = new List<Comparator>();
        foreach (var token in ExpandComparatorTokens(groupText))
        {
            if (!TryAppendComparators(token, comparators))
            {
                group = default!;
                return false;
            }
        }

        group = new ConstraintGroup(comparators);
        return true;
    }

    private static bool TryParseHyphenRange(string text, out ConstraintGroup group)
    {
        // Hyphen range "1.2.3 - 2.3.4" is inclusive on both ends; the bounds are
        // re-fed through comparator parsing so partial bounds work ("1.2 - 2").
        var match = Regex.Match(text, @"^\s*(?<min>\S+)\s+-\s+(?<max>\S+)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            group = default!;
            return false;
        }

        var comparators = new List<Comparator>();
        if (!TryAppendComparators($">={match.Groups["min"].Value}", comparators) ||
            !TryAppendComparators($"<={match.Groups["max"].Value}", comparators))
        {
            group = default!;
            return false;
        }

        group = new ConstraintGroup(comparators);
        return true;
    }

    private static IEnumerable<string> ExpandComparatorTokens(string text)
    {
        // Ranges may write the operator detached from the version (">= 1.2.3");
        // re-join operator tokens with the token that follows them.
        var rawTokens = text
            .Replace(",", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < rawTokens.Length; i++)
        {
            var token = rawTokens[i];
            if (IsOperatorToken(token) && i + 1 < rawTokens.Length)
            {
                yield return token + rawTokens[++i];
            }
            else
            {
                yield return token;
            }
        }
    }

    private static bool TryAppendComparators(string token, List<Comparator> comparators)
    {
        token = token.Trim();
        // Bare "*" / "x" constrain nothing; they exist so "1.x || *" parses.
        if (token is "*" or "x" or "X")
            return true;

        var (operatorText, versionText) = SplitOperator(token);
        if (string.IsNullOrWhiteSpace(versionText))
            return false;

        if (operatorText is "~" or "~>")
            return TryAppendTildeRange(versionText, comparators);

        if (operatorText is "^")
            return TryAppendCaretRange(versionText, comparators);

        if (ContainsCoreWildcard(versionText))
            return TryAppendWildcardRange(operatorText, versionText, comparators);

        // Partial versions ("1", "1.2") expand to the corresponding range rather
        // than a pinned equality, unless compared as a prerelease-carrying version.
        if (!TryParseConstraintVersion(versionText, out var version, out var specifiedParts))
            return false;

        if (specifiedParts < 3 && !version.HasPrerelease)
            return TryAppendPartialVersionComparators(operatorText, version, specifiedParts, comparators);

        var op = operatorText switch
        {
            "" or "=" or "==" => ComparisonOperator.Equal,
            "!=" => ComparisonOperator.NotEqual,
            ">" => ComparisonOperator.GreaterThan,
            ">=" => ComparisonOperator.GreaterThanOrEqual,
            "<" => ComparisonOperator.LessThan,
            "<=" => ComparisonOperator.LessThanOrEqual,
            _ => (ComparisonOperator?)null
        };

        if (op is null)
            return false;

        comparators.Add(new Comparator(op.Value, version));
        return true;
    }

    private static void AppendPartialVersionRange(
        SemanticVersion lower,
        int specifiedParts,
        List<Comparator> comparators)
    {
        var upper = GetPartialVersionUpperBound(lower, specifiedParts);

        comparators.Add(new Comparator(ComparisonOperator.GreaterThanOrEqual, lower));
        comparators.Add(new Comparator(ComparisonOperator.LessThanCore, upper));
    }

    private static bool TryAppendPartialVersionComparators(
        string operatorText,
        SemanticVersion lower,
        int specifiedParts,
        List<Comparator> comparators)
    {
        var upper = GetPartialVersionUpperBound(lower, specifiedParts);
        switch (operatorText)
        {
            case "" or "=" or "==":
                AppendPartialVersionRange(lower, specifiedParts, comparators);
                return true;
            case "!=":
                comparators.Add(new Comparator(
                    ComparisonOperator.OutsideRange,
                    lower,
                    upper,
                    AllowsPrereleaseInsideRange: specifiedParts == 2));
                return true;
            case ">=":
                comparators.Add(new Comparator(ComparisonOperator.GreaterThanOrEqual, lower));
                return true;
            case ">":
                comparators.Add(new Comparator(ComparisonOperator.GreaterThanOrEqualCore, upper));
                return true;
            case "<=":
                comparators.Add(new Comparator(ComparisonOperator.LessThanCore, upper));
                return true;
            case "<":
                comparators.Add(new Comparator(ComparisonOperator.LessThan, lower));
                return true;
            default:
                return false;
        }
    }

    private static SemanticVersion GetPartialVersionUpperBound(SemanticVersion lower, int specifiedParts)
        => specifiedParts <= 1
            ? new SemanticVersion(lower.Major + 1, 0, 0, [])
            : new SemanticVersion(lower.Major, lower.Minor + 1, 0, []);

    private static (string Operator, string Version) SplitOperator(string token)
    {
        foreach (var op in new[] { ">=", "<=", "==", "!=", "~>", ">", "<", "=", "~", "^" })
        {
            if (token.StartsWith(op, StringComparison.Ordinal))
                return (op, token[op.Length..].Trim());
        }

        return (string.Empty, token);
    }

    private static bool IsOperatorToken(string token)
        => token is ">=" or "<=" or "==" or "!=" or "~>" or ">" or "<" or "=" or "~" or "^";

    private static bool ContainsCoreWildcard(string version)
        => StripBuild(version)
            .Split('-', 2)[0]
            .TrimStart('v', 'V')
            .Split('.')
            .Any(part => part is "*" or "x" or "X");

    private static bool TryAppendWildcardRange(
        string operatorText,
        string versionText,
        List<Comparator> comparators)
    {
        if (!TryParseVersionPattern(versionText, out var lower, out _, out var wildcardIndex) ||
            wildcardIndex is null)
        {
            return false;
        }

        var upper = GetWildcardUpperBound(lower, wildcardIndex.Value);
        switch (operatorText)
        {
            case "" or "=" or "==":
                comparators.Add(new Comparator(ComparisonOperator.GreaterThanOrEqual, lower));
                if (upper is not null)
                    comparators.Add(new Comparator(ComparisonOperator.LessThanCore, upper));
                return true;
            case ">=":
                comparators.Add(new Comparator(ComparisonOperator.GreaterThanOrEqual, lower));
                return true;
            case ">":
                comparators.Add(upper is not null
                    ? new Comparator(ComparisonOperator.GreaterThanOrEqualCore, upper)
                    : new Comparator(ComparisonOperator.GreaterThan, lower));
                return true;
            case "<=":
                if (upper is not null)
                    comparators.Add(new Comparator(ComparisonOperator.LessThanCore, upper));
                return true;
            case "<":
                comparators.Add(new Comparator(ComparisonOperator.LessThan, lower));
                return true;
            case "!=":
                comparators.Add(new Comparator(
                    ComparisonOperator.OutsideRange,
                    lower,
                    upper,
                    AllowsPrereleaseInsideRange: wildcardIndex == 2));
                return true;
            default:
                return false;
        }
    }

    private static SemanticVersion? GetWildcardUpperBound(SemanticVersion lower, int wildcardIndex)
        => wildcardIndex switch
        {
            0 => null,
            1 => new SemanticVersion(lower.Major + 1, 0, 0, []),
            2 => new SemanticVersion(lower.Major, lower.Minor + 1, 0, []),
            _ => null
        };

    private static bool TryParseVersionPattern(
        string versionText,
        out SemanticVersion lower,
        out int specifiedParts,
        out int? wildcardIndex)
    {
        var withoutBuild = StripBuild(versionText);
        var prereleaseSplit = withoutBuild.Split('-', 2);
        var parts = prereleaseSplit[0].TrimStart('v', 'V').Split('.');
        specifiedParts = parts.Length;
        wildcardIndex = null;

        if (parts.Length is < 1 or > 3)
        {
            lower = default!;
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part is "*" or "x" or "X")
            {
                wildcardIndex = i;
                specifiedParts = i;
                if (parts.Skip(i + 1).Any(remaining => remaining is not ("*" or "x" or "X")))
                {
                    lower = default!;
                    return false;
                }

                break;
            }

            if (wildcardIndex is not null || !int.TryParse(part, out numbers[i]))
            {
                lower = default!;
                return false;
            }
        }

        if (wildcardIndex is not null && prereleaseSplit.Length == 2)
        {
            lower = default!;
            return false;
        }

        var prerelease = prereleaseSplit.Length == 2
            ? prereleaseSplit[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(PrereleaseIdentifier.Parse)
                .ToArray()
            : [];

        lower = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    private static bool TryAppendTildeRange(string versionText, List<Comparator> comparators)
    {
        // Tilde: patch-level changes when minor is specified ("~1.2.3" -> >=1.2.3 <1.3.0),
        // minor-level when only major is ("~1.2" -> >=1.2 <1.3, "~1" -> >=1 <2).
        if (!TryParseVersionPattern(versionText, out var lower, out var specifiedParts, out _))
            return false;

        var upper = specifiedParts <= 1
            ? new SemanticVersion(lower.Major + 1, 0, 0, [])
            : new SemanticVersion(lower.Major, lower.Minor + 1, 0, []);

        comparators.Add(new Comparator(ComparisonOperator.GreaterThanOrEqual, lower));
        comparators.Add(new Comparator(ComparisonOperator.LessThanCore, upper));
        return true;
    }

    private static bool TryAppendCaretRange(string versionText, List<Comparator> comparators)
    {
        // Caret allows changes that do not modify the left-most non-zero digit:
        // "^1.2.3" -> >=1.2.3 <2.0.0, "^0.2.3" -> >=0.2.3 <0.3.0, "^0.0.3" -> >=0.0.3 <0.0.4.
        if (!TryParseVersionPattern(versionText, out var lower, out var specifiedParts, out _))
            return false;

        SemanticVersion upper;
        if (lower.Major > 0)
        {
            upper = new SemanticVersion(lower.Major + 1, 0, 0, []);
        }
        else if (lower.Minor > 0)
        {
            upper = new SemanticVersion(0, lower.Minor + 1, 0, []);
        }
        else if (lower.Patch > 0)
        {
            upper = new SemanticVersion(0, 0, lower.Patch + 1, []);
        }
        else
        {
            upper = specifiedParts <= 1
                ? new SemanticVersion(1, 0, 0, [])
                : specifiedParts == 2
                    ? new SemanticVersion(0, 1, 0, [])
                    : new SemanticVersion(0, 0, 1, []);
        }

        comparators.Add(new Comparator(ComparisonOperator.GreaterThanOrEqual, lower));
        comparators.Add(new Comparator(ComparisonOperator.LessThanCore, upper));
        return true;
    }

    private static bool TryParseConstraintVersion(string text, out SemanticVersion version)
        => TryParseConstraintVersion(text, out version, out _);

    private static bool TryParseConstraintVersion(string text, out SemanticVersion version, out int specifiedParts)
    {
        if (TryParseVersionPattern(text, out version, out specifiedParts, out var wildcardIndex) &&
            wildcardIndex is null)
        {
            return true;
        }

        version = default!;
        return false;
    }

    private static string StripBuild(string text)
        => text.Split('+', 2)[0];

    private sealed record Candidate<T>(T Value, string Version, int Index)
    {
        public SemanticVersion? SemanticVersion { get; } =
            SemanticVersion.TryParse(Version, out var parsed) ? parsed : null;
    }

    private sealed record VersionConstraint(IReadOnlyList<ConstraintGroup> Groups)
    {
        public static VersionConstraint AnyStable { get; } = new([new ConstraintGroup([])]);

        public bool IsSatisfiedBy(SemanticVersion version)
            => Groups.Any(group => group.IsSatisfiedBy(version));
    }

    private sealed record ConstraintGroup(IReadOnlyList<Comparator> Comparators)
    {
        // Masterminds/semver rule: a prerelease version only satisfies a group when
        // that group itself mentions a prerelease; otherwise "1.2.3-beta" is excluded.
        private bool AllowsPrerelease { get; } = Comparators.Any(comparator => comparator.Version.HasPrerelease);

        public bool IsSatisfiedBy(SemanticVersion version)
            => (!version.HasPrerelease || AllowsPrerelease) &&
               Comparators.All(comparator => comparator.IsSatisfiedBy(version));
    }

    private sealed record Comparator(
        ComparisonOperator Operator,
        SemanticVersion Version,
        SemanticVersion? UpperBound = null,
        bool AllowsPrereleaseInsideRange = false)
    {
        // *Core* operators compare major.minor.patch only: the upper bound of a range
        // ("<1.3.0" from "~1.2") must reject the next core's prereleases ("1.3.0-beta")
        // that a plain LessThan would incorrectly allow past the boundary.
        public bool IsSatisfiedBy(SemanticVersion version)
        {
            var comparison = SemanticVersionComparer.Instance.Compare(version, Version);
            return Operator switch
            {
                ComparisonOperator.Equal => comparison == 0,
                ComparisonOperator.NotEqual => comparison != 0,
                ComparisonOperator.GreaterThan => comparison > 0,
                ComparisonOperator.GreaterThanOrEqual => comparison >= 0,
                ComparisonOperator.GreaterThanOrEqualCore =>
                    SemanticVersionComparer.CompareCore(version, Version) >= 0,
                ComparisonOperator.LessThan => comparison < 0,
                ComparisonOperator.LessThanOrEqual => comparison <= 0,
                ComparisonOperator.LessThanCore => SemanticVersionComparer.CompareCore(version, Version) < 0,
                ComparisonOperator.OutsideRange => SemanticVersionComparer.CompareCore(version, Version) < 0 ||
                                                   UpperBound is not null &&
                                                   SemanticVersionComparer.CompareCore(version, UpperBound) >= 0 ||
                                                   AllowsPrereleaseInsideRange && version.HasPrerelease,
                _ => false
            };
        }
    }

    private enum ComparisonOperator
    {
        Equal,
        NotEqual,
        GreaterThan,
        GreaterThanOrEqual,
        GreaterThanOrEqualCore,
        LessThan,
        LessThanOrEqual,
        LessThanCore,
        OutsideRange
    }

    private sealed record SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        IReadOnlyList<PrereleaseIdentifier> Prerelease)
    {
        private static readonly Regex VersionRegex = new(
            @"^[vV]?(?<major>0|[1-9]\d*)(?:\.(?<minor>0|[1-9]\d*))?(?:\.(?<patch>0|[1-9]\d*))?(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public bool HasPrerelease => Prerelease.Count > 0;

        public static bool TryParse(string text, out SemanticVersion version)
        {
            var match = VersionRegex.Match(text.Trim());
            if (!match.Success)
            {
                version = default!;
                return false;
            }

            version = new SemanticVersion(
                int.Parse(match.Groups["major"].Value),
                match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value) : 0,
                match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0,
                match.Groups["pre"].Success
                    ? match.Groups["pre"].Value.Split('.').Select(PrereleaseIdentifier.Parse).ToArray()
                    : []);
            return true;
        }
    }

    private sealed record PrereleaseIdentifier(string Text, int? Number)
    {
        public static PrereleaseIdentifier Parse(string text)
            => int.TryParse(text, out var number)
                ? new PrereleaseIdentifier(text, number)
                : new PrereleaseIdentifier(text, null);
    }

    private sealed class SemanticVersionComparer : IComparer<SemanticVersion>
    {
        public static SemanticVersionComparer Instance { get; } = new();

        public int Compare(SemanticVersion? x, SemanticVersion? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var coreComparison = CompareCore(x, y);
            if (coreComparison != 0)
                return coreComparison;

            if (!x.HasPrerelease && y.HasPrerelease)
                return 1;
            if (x.HasPrerelease && !y.HasPrerelease)
                return -1;

            for (var i = 0; i < Math.Min(x.Prerelease.Count, y.Prerelease.Count); i++)
            {
                var comparison = ComparePrereleaseIdentifier(x.Prerelease[i], y.Prerelease[i]);
                if (comparison != 0)
                    return comparison;
            }

            return x.Prerelease.Count.CompareTo(y.Prerelease.Count);
        }

        public static int CompareCore(SemanticVersion left, SemanticVersion right)
        {
            var comparison = left.Major.CompareTo(right.Major);
            if (comparison != 0)
                return comparison;

            comparison = left.Minor.CompareTo(right.Minor);
            return comparison != 0 ? comparison : left.Patch.CompareTo(right.Patch);
        }

        private static int ComparePrereleaseIdentifier(PrereleaseIdentifier left, PrereleaseIdentifier right)
        {
            if (left.Number is not null && right.Number is not null)
                return left.Number.Value.CompareTo(right.Number.Value);
            if (left.Number is not null)
                return -1;
            if (right.Number is not null)
                return 1;
            return string.CompareOrdinal(left.Text, right.Text);
        }
    }
}
