using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.IO;
using System.Xml;
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
        private static readonly Regex UntimedMetadataTimestamp = new Regex(
            @"\A\[\d{1,5}:\d{1,2}(?:[\.:]\d{1,3})?-1\]\s*", RegexOptions.CultureInvariant);
        private static readonly Regex TranslationPlaceholder = new Regex(
            @"\A[\s/／]+\z", RegexOptions.CultureInvariant);
        private static readonly Regex CreditLine = new Regex(
            @"\A(?:作词|作曲|编曲|制作人|词|曲|演唱|歌手|混音|母带|录音|和声|吉他|钢琴|贝斯|鼓|监制|出品|发行|lyrics|composer|arranger|producer|written\s+by)\s*[:：]",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        internal static bool IsNonVocalCue(string text)
        {
            string value = (text ?? "").Trim();
            if (value.Length == 0 || CreditLine.IsMatch(value)) return true;
            value = value.Trim('[', ']', '(', ')', '（', '）', '【', '】', ' ', '·', '-', '—').Trim().ToLowerInvariant();
            if (Regex.IsMatch(value, @"\A(?:(?:此|本)(?:歌曲|曲)(?:为|是)(?:没有填词的)?)?纯音乐\s*[,，。:：!！]?\s*请欣赏\s*[。.!！]?\z", RegexOptions.CultureInvariant)) return true;
            return value == "间奏" || value == "前奏" || value == "尾奏" || value == "伴奏" || value == "纯音乐" ||
                value == "instrumental" || value == "interlude" || value == "intro" || value == "outro" ||
                value == "music break" || value == "♪" || value == "♫" || value == "♬";
        }

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
                // Some platform responses mark untimed production credits with
                // a -1 suffix. Strip the marker without inventing a lyric time.
                string lineText = UntimedMetadataTimestamp.Replace(Metadata.Replace(Timestamp.Replace(original, ""), "").Trim(), "");
                string enhancedText = lineText;
                lineText = WordTimestamp.Replace(lineText, "");
                if (lineText.Length > 0 && !IsNonVocalCue(lineText)) plainLines.Add(lineText);
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
                    var indexed = new IndexedLine(time, lineText, order++);
                    if (timestamps.Count == 1) indexed.Words = EnhancedWords(enhancedText, offsetSeconds);
                    timed.Add(indexed);
                }
            }
            timed.Sort(delegate(IndexedLine left, IndexedLine right)
            {
                int timeComparison = left.Seconds.CompareTo(right.Seconds);
                return timeComparison != 0 ? timeComparison : left.Order.CompareTo(right.Order);
            });
            foreach (IndexedLine line in timed)
            {
                var lyric = new LyricLine(line.Seconds, line.Text) { Words = line.Words };
                if (lyric.Words.Count > 0 && lyric.Words[lyric.Words.Count - 1].EndSeconds > lyric.Words[lyric.Words.Count - 1].StartSeconds)
                    lyric.EndSeconds = lyric.Words[lyric.Words.Count - 1].EndSeconds;
                document.Lines.Add(lyric);
            }
            document.PlainText = string.Join(Environment.NewLine, plainLines).Trim();
            return document;
        }

        private static List<LyricWord> EnhancedWords(string raw, double offset)
        {
            var words = new List<LyricWord>();
            MatchCollection marks = WordTimestamp.Matches(raw);
            int textIndex = marks.Count == 0 ? 0 : marks[0].Index;
            for (int i = 0; i < marks.Count; i++)
            {
                Match mark = marks[i];
                int next = i + 1 < marks.Count ? marks[i + 1].Index : raw.Length;
                int length = next - mark.Index - mark.Length;
                double start = WordTime(mark.Value) - offset;
                double end = i + 1 < marks.Count ? WordTime(marks[i + 1].Value) - offset : 0;
                if (length > 0 && start >= 0 && (end == 0 || end >= start))
                    words.Add(new LyricWord { StartIndex = textIndex, Length = length, StartSeconds = start, EndSeconds = end });
                textIndex += length;
            }
            return words;
        }

        private static double WordTime(string mark)
        {
            Match time = Timestamp.Match("[" + mark.Substring(1, mark.Length - 2) + "]");
            string fraction = time.Groups["fraction"].Value;
            return double.Parse(time.Groups["minutes"].Value, CultureInfo.InvariantCulture) * 60 +
                double.Parse(time.Groups["seconds"].Value, CultureInfo.InvariantCulture) +
                (fraction.Length == 0 ? 0 : double.Parse(fraction, CultureInfo.InvariantCulture) / Math.Pow(10, fraction.Length));
        }

        public static double KaraokeProgress(LyricLine line, double position, double fallbackEnd)
        {
            if (line == null || string.IsNullOrEmpty(line.Text) || double.IsNaN(position) || double.IsInfinity(position)) return 0;
            double end = line.EndSeconds > line.Seconds ? line.EndSeconds : fallbackEnd;
            if (line.Words == null || line.Words.Count == 0)
                return end > line.Seconds ? Math.Max(0, Math.Min(1, (position - line.Seconds) / (end - line.Seconds))) : 0;
            double characters = 0;
            foreach (LyricWord word in line.Words)
            {
                if (word.Length <= 0 || word.StartIndex < 0 || word.StartIndex + word.Length > line.Text.Length) continue;
                if (position < word.StartSeconds) break;
                double wordEnd = word.EndSeconds > word.StartSeconds ? word.EndSeconds : end;
                double fraction = wordEnd > word.StartSeconds ? Math.Max(0, Math.Min(1, (position - word.StartSeconds) / (wordEnd - word.StartSeconds))) : 0;
                characters = Math.Max(characters, word.StartIndex + word.Length * fraction);
            }
            return Math.Max(0, Math.Min(1, characters / line.Text.Length));
        }

        public static int ApplyWordTiming(LyricDocument document, string raw, bool translation)
        {
            if (document == null || string.IsNullOrWhiteSpace(raw) || raw.Length > 2 * 1024 * 1024) return 0;
            if (raw.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                try
                {
                    var xml = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(new StringReader(raw), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 })) xml.Load(reader);
                    XmlElement element = xml.SelectSingleNode("//*[@LyricContent]") as XmlElement;
                    if (element == null) return 0;
                    raw = element.GetAttribute("LyricContent");
                }
                catch (XmlException) { return 0; }
            }
            LyricDocument timed = Parse(raw);
            raw = Regex.Replace(raw, @"(?=\[\d+,\d+\])", "\n");
            foreach (string row in raw.Replace("\r\n", "\n").Split('\n'))
            {
                Match header = Regex.Match(row, @"\A\[(?<start>\d+),(?<duration>\d+)\](?<body>.*)\z", RegexOptions.CultureInvariant);
                if (!header.Success) continue;
                double start, duration;
                if (!double.TryParse(header.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
                    !double.TryParse(header.Groups["duration"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out duration) || duration <= 0) continue;
                string body = header.Groups["body"].Value;
                bool yrc = Regex.IsMatch(body, @"\A\(\d+,\d+,\d+\)");
                MatchCollection pieces = Regex.Matches(body, yrc ? @"\((?<time>\d+),(?<duration>\d+),\d+\)(?<text>[^()]*)" : @"(?<text>[^()]*)\((?<time>\d+),(?<duration>\d+)\)", RegexOptions.CultureInvariant);
                var words = new List<LyricWord>();
                string text = ""; int covered = 0;
                foreach (Match piece in pieces)
                {
                    double at, length;
                    if (piece.Index != covered || !double.TryParse(piece.Groups["time"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out at) ||
                        !double.TryParse(piece.Groups["duration"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                        length <= 0 || at < start || at + length > start + duration + 350 || words.Count > 0 && at < words[words.Count - 1].StartSeconds * 1000) { words.Clear(); break; }
                    string word = piece.Groups["text"].Value;
                    if (word.Length > 0) words.Add(new LyricWord { StartIndex = text.Length, Length = word.Length, StartSeconds = at / 1000, EndSeconds = (at + length) / 1000 });
                    text += word; covered = piece.Index + piece.Length;
                }
                if (covered == body.Length && words.Count > 0)
                    timed.Lines.Add(new LyricLine(start / 1000, text) { EndSeconds = (start + duration) / 1000, Words = words });
            }
            List<LyricLine> targets = translation ? document.TranslationLines : document.Lines;
            if (targets == null) return 0;
            int applied = 0;
            foreach (LyricLine target in targets)
            {
                LyricLine chosen = null;
                foreach (LyricLine candidate in timed.Lines)
                    if (candidate.Words != null && candidate.Words.Count > 0 && Math.Abs(candidate.Seconds - target.Seconds) <= .35 && candidate.Text == target.Text)
                    { if (chosen != null) { chosen = null; break; } chosen = candidate; }
                if (chosen == null) continue;
                target.EndSeconds = chosen.EndSeconds;
                target.Words = chosen.Words;
                applied++;
            }
            return applied;
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
                {
                    LyricLine previous = document.TranslationLines[i];
                    string text = WithoutRepeatedOriginal(document.Lines[i].Text, previous.Text);
                    cleaned.Add(new LyricLine(document.Lines[i].Seconds, text) { EndSeconds = previous.EndSeconds,
                        Words = text == previous.Text ? previous.Words : new List<LyricWord>() });
                }
                document.TranslationLines = cleaned;
                ApplyWordTiming(document, string.IsNullOrEmpty(document.TranslationWordTiming) ? document.Translation : document.TranslationWordTiming, true);
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
            ApplyWordTiming(document, string.IsNullOrEmpty(document.TranslationWordTiming) ? document.Translation : document.TranslationWordTiming, true);
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
            if (string.IsNullOrWhiteSpace(translated) || TranslationPlaceholder.IsMatch(translated)) return "";
            string source = (original ?? "").Trim(), text = translated.Trim();
            if (source.Length == 0) return text;
            if (string.Equals(source, text, StringComparison.Ordinal)) return "";
            // The provider or an older cache may contain both languages in one caption.
            // Remove only an exact copy of the original; preserve other translated text.
            if (text.StartsWith(source + " / ", StringComparison.Ordinal)) text = text.Substring(source.Length + 3);
            else if (text.EndsWith(" / " + source, StringComparison.Ordinal)) text = text.Substring(0, text.Length - source.Length - 3);
            var useful = new List<string>();
            foreach (string part in text.Split(new[] { " / " }, StringSplitOptions.None))
            {
                string value = part.Trim();
                if (value.Length > 0 && !string.Equals(value, source, StringComparison.Ordinal) && !TranslationPlaceholder.IsMatch(value)) useful.Add(value);
            }
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
            public List<LyricWord> Words = new List<LyricWord>();
            public IndexedLine(double seconds, string text, int order)
            { Seconds = seconds; Text = text; Order = order; }
        }
    }
}
