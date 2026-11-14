using System.Text;
using System.Text.RegularExpressions;

namespace MoonTTS.Core.Utilities;

/* File Structure:
        - SplitIntoSentences(string text) : List<string>
        - ExtractRawSentences(string normalizedText) : List<string>
        - ShouldSplitAtMatch(string text, Match match, int chunkStart) : bool
        - IsProtectedPeriodBoundary(string text, Match match, int chunkStart) : bool
        - SplitLongClause(string sentence, List<string> output) : void
        - MergeShortSegments(List<string> rawSegments) : List<string>
        - FindFirstLetter(string text, int startIndex) : char?
        - WhitespaceRegex() : Regex
        - CandidateBoundaryRegex() : Regex
        - SingleInitialOrAcronymRegex() : Regex
        - TitleSignatureShortRegex() : Regex
        - TitleSignatureLongRegex() : Regex
*/

/* Language-agnostic structural sentence boundary splitter inspired by UAX #29 and Punkt heuristics */
public static partial class TextSplitter
{
    private const int MinChunkLength = 25;
    private const int TitleLongWindowCutoff = 40;
    private const int MaxSentenceLength = 100;
    private const int MinClauseLength = 30;

    private static readonly string[] ClauseDelimiters = [", ", "; ", " — ", " – ", "、", "，"];

    /* Splits input text into prosodically balanced chunks without English-specific abbreviation lists */
    public static List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        string normalized = WhitespaceRegex().Replace(text.Trim(), " ");
        List<string> rawSentences = ExtractRawSentences(normalized);
        List<string> merged = MergeShortSegments(rawSentences);

        var finalChunks = new List<string>(merged.Count);
        foreach (string sentence in merged)
        {
            if (sentence.Length > MaxSentenceLength)
                SplitLongClause(sentence, finalChunks);
            else
                finalChunks.Add(sentence);
        }

        return finalChunks.Count > 0 ? finalChunks : new List<string> { normalized };
    }

    /* Scans normalized text for candidate boundaries and filters out abbreviations, initials, and continuations */
    private static List<string> ExtractRawSentences(string text)
    {
        var sentences = new List<string>();
        int chunkStart = 0;

        foreach (Match match in CandidateBoundaryRegex().Matches(text))
        {
            if (!ShouldSplitAtMatch(text, match, chunkStart))
                continue;

            int splitEnd = match.Index + match.Groups["term"].Length + match.Groups["close"].Length;
            string piece = text.Substring(chunkStart, splitEnd - chunkStart).Trim();
            if (!string.IsNullOrEmpty(piece))
                sentences.Add(piece);

            chunkStart = match.Index + match.Length;
        }

        if (chunkStart < text.Length)
        {
            string tail = text[chunkStart..].Trim();
            if (!string.IsNullOrEmpty(tail))
                sentences.Add(tail);
        }

        return sentences;
    }

    /* Evaluates whether a candidate punctuation boundary is a true sentence terminator */
    private static bool ShouldSplitAtMatch(string text, Match match, int chunkStart)
    {
        Group globalTerm = match.Groups["global"];
        if (globalTerm.Success)
            return true;

        Group strongTerm = match.Groups["strong"];
        if (strongTerm.Success)
        {
            int nextIdx = match.Index + match.Length;
            char? nextLetter = FindFirstLetter(text, nextIdx);
            return nextLetter == null || !char.IsLower(nextLetter.Value);
        }

        Group periodTerm = match.Groups["period"];
        if (periodTerm.Success)
            return !IsProtectedPeriodBoundary(text, match, chunkStart);

        return false;
    }

    /* Applies Punkt-style structural rules to avoid splitting after initials, acronyms, decimals, or titles */
    private static bool IsProtectedPeriodBoundary(string text, Match match, int chunkStart)
    {
        int dotIndex = match.Groups["period"].Index;
        int nextIdx = match.Index + match.Length;

        /* Rule 1: Lowercase continuation after a period denotes a mid-sentence abbreviation */
        char? nextLetter = FindFirstLetter(text, nextIdx);
        if (nextLetter.HasValue && char.IsLower(nextLetter.Value))
            return true;

        /* Extract text inside the current candidate chunk ending right after the period */
        string upToDot = text.Substring(chunkStart, (dotIndex - chunkStart) + 1);

        /* Rule 2: Multi-dot ellipsis before any remaining text stays attached unless end of string */
        if (upToDot.EndsWith("..", StringComparison.Ordinal) && nextIdx < text.Length)
            return true;

        /* Rule 3: Single-letter initials (J.) or dotted acronyms (U.S., e.g., p.m.) */
        if (SingleInitialOrAcronymRegex().IsMatch(upToDot))
            return true;

        /* Rule 4: Structural title/honorific signature (1 uppercase + 0..2 lowercase + dot, e.g., Dr., Sr., Mme.) */
        if (TitleSignatureShortRegex().IsMatch(upToDot))
            return true;

        /* Extended 3-lowercase title signature (e.g., Prof., Srta., Dott.) when chunk is still short */
        if (upToDot.Length < TitleLongWindowCutoff && TitleSignatureLongRegex().IsMatch(upToDot))
            return true;

        return false;
    }

    /* Merges tiny adjacent sentence fragments into neighboring segments so neural voices keep prosody */
    private static List<string> MergeShortSegments(List<string> rawSegments)
    {
        var merged = new List<string>(rawSegments.Count);
        var buffer = new StringBuilder();

        foreach (string seg in rawSegments)
        {
            if (buffer.Length > 0)
                buffer.Append(' ');

            buffer.Append(seg);

            if (buffer.Length >= MinChunkLength)
            {
                merged.Add(buffer.ToString());
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            string remaining = buffer.ToString();
            if (merged.Count > 0 && remaining.Length < MinChunkLength && merged[^1].Length + 1 + remaining.Length <= 140)
            {
                merged[^1] = $"{merged[^1]} {remaining}";
            }
            else
            {
                merged.Add(remaining);
            }
        }

        return merged;
    }

    /* Breaks overly long sentences on natural clause delimiters while keeping each sub-clause >= 30 chars */
    private static void SplitLongClause(string sentence, List<string> output)
    {
        int start = 0;

        while (sentence.Length - start > MaxSentenceLength)
        {
            int bestSplitEnd = -1;
            int minSplitPos = start + MinClauseLength;

            foreach (string delim in ClauseDelimiters)
            {
                int searchFrom = minSplitPos - delim.Length;
                if (searchFrom < start)
                    searchFrom = start;

                int idx = sentence.IndexOf(delim, searchFrom, StringComparison.Ordinal);
                while (idx >= 0)
                {
                    int candidateEnd = idx + delim.Length;
                    int leftLen = candidateEnd - start;
                    int rightLen = sentence.Length - candidateEnd;

                    if (leftLen >= MinClauseLength && rightLen >= MinClauseLength)
                    {
                        if (bestSplitEnd < 0 || candidateEnd < bestSplitEnd)
                            bestSplitEnd = candidateEnd;
                        break;
                    }

                    idx = sentence.IndexOf(delim, idx + delim.Length, StringComparison.Ordinal);
                }
            }

            if (bestSplitEnd < 0)
                break;

            string slice = sentence[start..bestSplitEnd].Trim();
            if (!string.IsNullOrEmpty(slice))
                output.Add(slice);

            start = bestSplitEnd;
            while (start < sentence.Length && char.IsWhiteSpace(sentence[start]))
                start++;
        }

        if (start < sentence.Length)
        {
            string rest = sentence[start..].Trim();
            if (!string.IsNullOrEmpty(rest))
                output.Add(rest);
        }
    }

    /* Returns the first alphabetic character starting at startIndex, skipping opening quotes/brackets */
    private static char? FindFirstLetter(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsLetter(c))
                return c;
            if (char.IsDigit(c))
                return null;
        }
        return null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?<term>(?<global>[。！？；।؟؛]+)|(?<strong>[!?]+)(?=[""'”’»）)\]』」〉》]*\s)|(?<period>\.+)(?=[""'”’»）)\]』」〉》]*\s))(?<close>[""'”’»）)\]』」〉》]*)\s*")]
    private static partial Regex CandidateBoundaryRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}])\p{L}\.$")]
    private static partial Regex SingleInitialOrAcronymRegex();

    [GeneratedRegex(@"\b\p{Lu}\p{Ll}{1,2}\.$")]
    private static partial Regex TitleSignatureShortRegex();

    [GeneratedRegex(@"\b\p{Lu}\p{Ll}{3}\.$")]
    private static partial Regex TitleSignatureLongRegex();
}
