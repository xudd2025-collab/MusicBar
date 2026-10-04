using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MusicBar
{
    /// <summary>Parses standard LRC. A positive file offset means lyrics appear earlier.</summary>
    public static class LrcParser
    {
        private static readonly Regex Timestamp = new Regex(
            @"\[(?<minutes>\d{1,5}):(?<seconds>\d{1,2})(?:[\.:](?<fraction>\d{1,3}))?\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Offset = new Regex(
            @"\[offset\s*:\s*(?<value>[+-]?\d+)\s*\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex Metadata = new Regex(
            @"\[(?:ar|ti|al|by|re|ve|length|offset|au|la|id)\s*:[^\]]*\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex WordTimestamp = new Regex(
            @"<\d{1,5}:\d{1,2}(?:[\.:]\d{1,3})?>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static LyricDocument Parse(string text)
        {
            LyricDocument document = new LyricDocument();
            if (string.IsNullOrEmpty(text)) return document;
            string content = WebUtility.HtmlDecode(text).TrimStart('\uFEFF');
            content = content.Replace("\r\n", "\n").Replace('\r', '\n');
            // Read offset before timestamps; a header is allowed at the end of a file.
            double offsetSeconds = 0;
            foreach (Match offset in Offset.Matches(content))
            {
                long milliseconds;
                if (long.TryParse(offset.Groups["value"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out milliseconds))
                    offsetSeconds = milliseconds / 1000.0;
            }
            List<string> plainLines = new List<string>();
            List<IndexedLine> timed = new List<IndexedLine>();
            int order = 0;
            foreach (string original in content.Split('\n'))
            {
                MatchCollection timestamps = Timestamp.Matches(original);
                string lineText = Metadata.Replace(Timestamp.Replace(original, ""), "").Trim();
                // Enhanced LRC word marks are optional; rendering here stays line based.
                lineText = WordTimestamp.Replace(lineText, "");
                if (timestamps.Count > 0 || lineText.Length > 0) plainLines.Add(lineText);
                foreach (Match timestamp in timestamps)
                {
                    int minutes, seconds;
                    if (!int.TryParse(timestamp.Groups["minutes"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out minutes) ||
                        !int.TryParse(timestamp.Groups["seconds"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out seconds) || seconds >= 60) continue;
                    string fraction = timestamp.Groups["fraction"].Value;
                    double fractionalSeconds = 0;
                    if (fraction.Length > 0)
                        fractionalSeconds = int.Parse(fraction, CultureInfo.InvariantCulture) /
                            Math.Pow(10, fraction.Length);
                    double time = Math.Max(0, minutes * 60.0 + seconds + fractionalSeconds - offsetSeconds);
                    // Empty timed lines clear old lyrics during an instrumental section.
                    timed.Add(new IndexedLine(time, lineText, order++));
                }
            }
            timed.Sort(delegate(IndexedLine left, IndexedLine right)
            {
                int timeComparison = left.Seconds.CompareTo(right.Seconds);
                return timeComparison != 0 ? timeComparison : left.Order.CompareTo(right.Order);
            });
            foreach (IndexedLine line in timed) document.Lines.Add(new LyricLine(line.Seconds, line.Text));
            document.PlainText = string.Join(Environment.NewLine, plainLines).Trim();
            return document;
        }

        /// <summary>
        /// Keeps the raw translated LRC while preparing a translation for each
        /// original line. Old caches and local overrides can call this lazily.
        /// A nearby timestamp is accepted only within the current line's nearest
        /// temporal neighbourhood; missing lines never inherit a previous caption.
        /// </summary>
        public static void EnsureTranslation(LyricDocument document)
        {
            if (document == null) return;
            if (document.Lines == null) document.Lines = new List<LyricLine>();
            if (document.TranslationLines == null) document.TranslationLines = new List<LyricLine>();
            // Persisted mappings can come from a verified recording in another
            // catalog. Retain them when their original timestamps still match.
            if (!string.IsNullOrWhiteSpace(document.Translation) && ValidTranslationMapping(document))
            {
                var cleaned = new List<LyricLine>(document.Lines.Count);
                for (int i = 0; i < document.Lines.Count; i++)
                    cleaned.Add(new LyricLine(document.Lines[i].Seconds, WithoutRepeatedOriginal(document.Lines[i].Text, document.TranslationLines[i].Text)));
                document.TranslationLines = cleaned;
                if (string.IsNullOrWhiteSpace(document.TranslationSource)) document.TranslationSource = document.Source ?? "随歌词提供的译文";
                if (string.IsNullOrWhiteSpace(document.TranslationStatus))
                {
                    int count = 0;
                    for (int i = 0; i < document.Lines.Count; i++)
                        if (!string.IsNullOrWhiteSpace(document.Lines[i].Text) && !string.IsNullOrWhiteSpace(document.TranslationLines[i].Text)) count++;
                    document.TranslationStatus = count > 0 ? "译文已连接（" + count + " 行）" : "平台译文仅包含重复原文";
                }
                return;
            }
            List<LyricLine> aligned = new List<LyricLine>(document.Lines.Count);
            foreach (LyricLine line in document.Lines) aligned.Add(new LyricLine(line.Seconds, ""));
            if (string.IsNullOrWhiteSpace(document.Translation))
            {
                document.TranslationLines = aligned;
                if (string.IsNullOrWhiteSpace(document.TranslationStatus))
                    document.TranslationStatus = document.Source != null && document.Source.StartsWith("本地", StringComparison.Ordinal)
                        ? "本地歌词没有提供译文" : "平台未提供译文";
                return;
            }
            LyricDocument translated = Parse(document.Translation);
            if (document.Lines.Count == 0 || !translated.HasTimedLyrics)
            {
                document.TranslationLines = aligned;
                document.TranslationStatus = document.Lines.Count == 0 ? "原文没有时间轴，无法逐行同步译文" : "译文没有时间轴，无法逐行同步";
                return;
            }
            int matched = 0;
            for (int originalIndex = 0; originalIndex < document.Lines.Count; originalIndex++)
            {
                LyricLine original = document.Lines[originalIndex];
                if (string.IsNullOrWhiteSpace(original.Text)) continue;
                double lower = original.Seconds - 0.35, upper = original.Seconds + 0.35;
                bool sharedLower = false, sharedUpper = false;
                for (int previous = originalIndex - 1; previous >= 0; previous--)
                    if (document.Lines[previous].Seconds < original.Seconds)
                    {
                        double midpoint = (document.Lines[previous].Seconds + original.Seconds) / 2;
                        if (midpoint >= lower) { lower = midpoint; sharedLower = true; }
                        break;
                    }
                for (int next = originalIndex + 1; next < document.Lines.Count; next++)
                    if (document.Lines[next].Seconds > original.Seconds)
                    {
                        double midpoint = (document.Lines[next].Seconds + original.Seconds) / 2;
                        if (midpoint <= upper) { upper = midpoint; sharedUpper = true; }
                        break;
                    }
                double bestDifference = double.MaxValue, bestTime = double.NaN;
                List<string> texts = new List<string>();
                bool ambiguous = false;
                foreach (LyricLine candidate in translated.Lines)
                {
                    if (candidate.Seconds < lower - .000001) continue;
                    if (candidate.Seconds > upper + .000001) break;
                    // A caption exactly between two originals is not a unique
                    // match for either line and must not appear on both rows.
                    if ((sharedLower && Math.Abs(candidate.Seconds - lower) < .000001) ||
                        (sharedUpper && Math.Abs(candidate.Seconds - upper) < .000001)) continue;
                    double difference = Math.Abs(candidate.Seconds - original.Seconds);
                    if (difference + .000001 < bestDifference)
                    {
                        bestDifference = difference; bestTime = candidate.Seconds;
                        texts.Clear(); ambiguous = false;
                    }
                    else if (Math.Abs(difference - bestDifference) > .000001) continue;
                    else if (Math.Abs(candidate.Seconds - bestTime) > .000001) { ambiguous = true; continue; }
                    string text = WithoutRepeatedOriginal(original.Text, candidate.Text);
                    if (!string.IsNullOrWhiteSpace(text) && !texts.Contains(text)) texts.Add(text);
                }
                if (!ambiguous && texts.Count > 0)
                { aligned[originalIndex].Text = string.Join(" / ", texts); matched++; }
            }
            document.TranslationLines = aligned;
            if (string.IsNullOrWhiteSpace(document.TranslationSource)) document.TranslationSource = document.Source ?? "随歌词提供的译文";
            document.TranslationStatus = matched > 0 ? "平台译文已连接（" + matched + " 行）" : "译文时间轴与原文不匹配，暂不显示";
        }

        public static string GetTranslationForLine(LyricDocument document, int originalIndex)
        {
            if (document == null || document.Lines == null || originalIndex < 0 || originalIndex >= document.Lines.Count ||
                string.IsNullOrWhiteSpace(document.Lines[originalIndex].Text)) return "";
            if (document.TranslationLines == null || document.TranslationLines.Count != document.Lines.Count)
                EnsureTranslation(document);
            if (document.TranslationLines == null || originalIndex >= document.TranslationLines.Count) return "";
            LyricLine translated = document.TranslationLines[originalIndex];
            return translated != null && Math.Abs(translated.Seconds - document.Lines[originalIndex].Seconds) < .000001 ? WithoutRepeatedOriginal(document.Lines[originalIndex].Text, translated.Text) : "";
        }

        private static string WithoutRepeatedOriginal(string original, string translated)
        {
            if (string.IsNullOrWhiteSpace(translated)) return "";
            string source = (original ?? "").Trim(), text = translated.Trim();
            if (source.Length == 0) return text;
            if (string.Equals(source, text, StringComparison.Ordinal)) return "";
            // The provider or an older cache may contain both languages in one caption.
            // Remove only an exact copy of the original; preserve other translated text.
            if (text.StartsWith(source + " / ", StringComparison.Ordinal)) text = text.Substring(source.Length + 3);
            else if (text.EndsWith(" / " + source, StringComparison.Ordinal)) text = text.Substring(0, text.Length - source.Length - 3);
            var useful = new List<string>();
            foreach (string part in text.Split(new[] { " / " }, StringSplitOptions.None))
                if (!string.Equals(part.Trim(), source, StringComparison.Ordinal)) useful.Add(part.Trim());
            return string.Join(" / ", useful);
        }

        private static bool ValidTranslationMapping(LyricDocument document)
        {
            if (document.TranslationLines.Count != document.Lines.Count || document.Lines.Count == 0) return false;
            bool useful = false;
            for (int i = 0; i < document.Lines.Count; i++)
            {
                LyricLine translated = document.TranslationLines[i];
                if (translated == null || double.IsNaN(translated.Seconds) || double.IsInfinity(translated.Seconds) ||
                    Math.Abs(translated.Seconds - document.Lines[i].Seconds) > .000001) return false;
                if (!string.IsNullOrWhiteSpace(document.Lines[i].Text) && !string.IsNullOrWhiteSpace(translated.Text)) useful = true;
            }
            return useful;
        }

        private sealed class IndexedLine
        {
            public double Seconds;
            public string Text;
            public int Order;
            public IndexedLine(double seconds, string text, int order)
            { Seconds = seconds; Text = text; Order = order; }
        }
    }
}
