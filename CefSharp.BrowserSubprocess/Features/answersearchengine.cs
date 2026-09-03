using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CefSharp.BrowserSubprocess.Features
{
    /// <summary>
    /// AnswerSearchEngine v2 — optimized with entry caching and line-based parsing.
    ///
    /// Key improvements over v1:
    /// - Parses files ONCE into memory cache (re-parses only when file timestamps change)
    /// - Unified line-based parser handles mixed-format files (Q:/A:, ##, numbered, --- all in one file)
    /// - No LCS algorithm (was O(n×m) per entry — catastrophically slow on large files)
    /// - Pre-computes lowercase + keyword tokens per entry for instant search
    /// - Thread-safe cache loading
    ///
    /// Scoring: Exact (1.0) > Contains (0.9) > Keyword Jaccard (0.0–0.8)
    /// </summary>
    public class AnswerSearchEngine
    {
        private readonly string botFolderPath;
        private readonly List<CachedEntry> cachedEntries = new List<CachedEntry>();
        private readonly Dictionary<string, DateTime> fileTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> cachedLines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly object cacheLock = new object();
        private volatile bool cacheLoaded = false;

        // Pre-compiled regexes for line-based parsing (avoid recompilation per line)
        private static readonly Regex RxQTag = new Regex(@"^(?:Q|Question)\s*[:.]\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxATag = new Regex(@"^(?:A|Answer)\s*[:.]\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxHeading = new Regex(@"^#{1,3}\s+(.+)", RegexOptions.Compiled);
        private static readonly Regex RxNumbered = new Regex(@"^\d+[.)]\s+(.+)", RegexOptions.Compiled);
        private static readonly Regex RxSepDash = new Regex(@"^-{3,}\s*$", RegexOptions.Compiled);
        private static readonly Regex RxSepEquals = new Regex(@"^={3,}\s*$", RegexOptions.Compiled);
        private static readonly Regex RxTokenize = new Regex(@"[a-z0-9]+", RegexOptions.Compiled);
        private static readonly Regex RxCleanNum = new Regex(@"^[\d]+[.)]\s*", RegexOptions.Compiled);
        private static readonly Regex RxCleanQ = new Regex(@"^(Q|Question)[:.]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxWhitespace = new Regex(@"\s+", RegexOptions.Compiled);

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "what", "who", "when", "where",
            "why", "how", "define", "explain", "describe", "of", "in", "on", "at", "to",
            "for", "and", "or", "not", "this", "that", "with", "from", "by", "as",
            "do", "does", "did", "will", "would", "can", "could", "should", "shall",
            "has", "have", "had", "been", "being", "which", "there", "their", "its"
        };

        public string BotFolderPath { get { return botFolderPath; } }
        public int CachedEntryCount { get { return cachedEntries.Count; } }

        public event Action<string> LogEvent;

        public AnswerSearchEngine(string botFolder)
        {
            botFolderPath = botFolder;
        }

        /// <summary>
        /// Preloads all BOT files into the entry cache. Call at startup for instant first search.
        /// </summary>
        public void PreloadCache()
        {
            lock (cacheLock)
            {
                RefreshCache();
            }
        }

        /// <summary>
        /// Forces a full cache rebuild (e.g., when BOT folder contents change).
        /// </summary>
        public void InvalidateCache()
        {
            lock (cacheLock)
            {
                cachedEntries.Clear();
                fileTimestamps.Clear();
                cacheLoaded = false;
            }
        }

        /// <summary>
        /// Refreshes the cache — re-parses only files that are new or modified.
        /// Must be called inside cacheLock.
        /// </summary>
        private void RefreshCache()
        {
            if (string.IsNullOrEmpty(botFolderPath) || !Directory.Exists(botFolderPath))
                return;

            var files = GetTextFiles();
            var currentFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

            // Remove entries for deleted files
            var deletedFiles = fileTimestamps.Keys
                .Where(f => !currentFiles.Contains(f))
                .ToList();
            foreach (var f in deletedFiles)
            {
                cachedEntries.RemoveAll(e => string.Equals(e.SourceFile, f, StringComparison.OrdinalIgnoreCase));
                cachedLines.Remove(f);
                fileTimestamps.Remove(f);
            }

            // Add/update entries for new or modified files
            foreach (var file in files)
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    DateTime cached;
                    if (fileTimestamps.TryGetValue(file, out cached) && cached == lastWrite)
                        continue; // File unchanged — skip

                    // Remove old entries for this file
                    cachedEntries.RemoveAll(e => string.Equals(e.SourceFile, file, StringComparison.OrdinalIgnoreCase));

                    // Parse and cache Q/A entries
                    var content = File.ReadAllText(file, DetectEncoding(file));
                    var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    cachedLines[file] = lines; // Cache raw lines for fallback search

                    var entries = ParseFileUnified(content, file);

                    foreach (var entry in entries)
                    {
                        var lower = entry.Question.ToLowerInvariant();
                        cachedEntries.Add(new CachedEntry
                        {
                            Question = entry.Question,
                            Answer = entry.Answer,
                            SourceFile = file,
                            QuestionLower = lower,
                            QuestionTokens = Tokenize(lower)
                        });
                    }

                    fileTimestamps[file] = lastWrite;
                }
                catch (Exception ex)
                {
                    Log("Cache error for " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }

            cacheLoaded = true;
            Log("Cache ready: " + cachedEntries.Count + " Q/A entries from " + fileTimestamps.Count + " files");
        }

        /// <summary>
        /// Searches cached entries for the best answer matching the question.
        /// Thread-safe. Returns null if no match above threshold.
        /// </summary>
        public SearchResult Search(string questionText)
        {
            if (string.IsNullOrWhiteSpace(questionText))
                return null;

            // Ensure cache is loaded (thread-safe double-check)
            if (!cacheLoaded)
            {
                lock (cacheLock)
                {
                    if (!cacheLoaded) RefreshCache();
                }
            }

            var cleanQ = CleanQuestion(questionText);
            var cleanQLower = cleanQ.ToLowerInvariant();
            var queryTokens = Tokenize(cleanQLower);

            if (queryTokens.Count == 0 && cleanQLower.Length < 3)
                return null;

            SearchResult best = null;

            // Read snapshot of cached entries (safe — list is only modified inside cacheLock)
            var entries = cachedEntries;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                float score = ScoreMatch(entry.QuestionLower, cleanQLower, entry.QuestionTokens, queryTokens);

                if (score > 0.3f && (best == null || score > best.Score))
                {
                    best = new SearchResult
                    {
                        Question = entry.Question,
                        Answer = entry.Answer,
                        SourceFile = entry.SourceFile,
                        Score = score
                    };

                    // Perfect match — no need to keep looking
                    if (score >= 0.99f) break;
                }
            }

            // Fallback: search raw file lines when parsed Q/A entries don't have a strong match.
            // This catches questions without format markers (no Q:, ##, numbered prefix).
            if (best == null || best.Score < 0.8f)
            {
                var queryWordList = TokenizeToList(cleanQLower);
                SearchResult lineBest;
                lock (cacheLock)
                {
                    lineBest = SearchByLineContent(cleanQLower, queryTokens, queryWordList);
                }
                if (lineBest != null && (best == null || lineBest.Score > best.Score))
                    best = lineBest;
            }

            return best;
        }

        /// <summary>
        /// Fallback: searches raw file lines directly when parsed Q/A entries miss the correct match.
        /// Finds lines with best keyword overlap, then extracts following lines as the answer.
        /// This catches questions that don't have format markers (Q:, ##, numbered, etc.).
        /// </summary>
        private SearchResult SearchByLineContent(string queryLower, HashSet<string> queryTokens, List<string> queryWordList)
        {
            if (queryTokens.Count == 0) return null;

            SearchResult best = null;
            int minTokensRequired = Math.Max(2, (int)(queryTokens.Count * 0.6)); // At least 60% keyword overlap

            foreach (var kvp in cachedLines)
            {
                var lines = kvp.Value;
                var file = kvp.Key;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line == null || line.Length < 15) continue;

                    // Quick: exact substring match on the full query
                    if (line.IndexOf(queryLower, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var answer = ExtractAnswerFromLines(lines, i);
                        if (!string.IsNullOrWhiteSpace(answer))
                        {
                            return new SearchResult
                            {
                                Question = line.Trim(),
                                Answer = answer,
                                SourceFile = file,
                                Score = 0.97f  // Near-perfect: exact substring found in a line
                            };
                        }
                    }

                    // Keyword overlap: count how many query tokens appear in the line
                    var lineLower = line.ToLowerInvariant();
                    int matched = 0;
                    foreach (var token in queryTokens)
                    {
                        if (lineLower.Contains(token))
                            matched++;
                    }

                    if (matched >= minTokensRequired)
                    {
                        // Score by keyword coverage + word sequence bonus
                        float coverage = (float)matched / queryTokens.Count;
                        var lineTokens = Tokenize(lineLower);
                        var lineWordList = TokenizeToList(lineLower);
                        float seqScore = WordSequenceScore(lineWordList, queryWordList);
                        float score = coverage * 0.5f + seqScore * 0.4f + (coverage >= 1f ? 0.1f : 0f);

                        if (score > 0.5f && (best == null || score > best.Score))
                        {
                            var answer = ExtractAnswerFromLines(lines, i);
                            if (!string.IsNullOrWhiteSpace(answer))
                            {
                                best = new SearchResult
                                {
                                    Question = line.Trim(),
                                    Answer = answer,
                                    SourceFile = file,
                                    Score = score
                                };
                            }
                        }
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// Extracts the answer from raw file lines starting after the matched question line.
        /// Grabs up to 30 lines, stopping at the next question or separator.
        /// </summary>
        private string ExtractAnswerFromLines(string[] lines, int questionLineIndex)
        {
            var sb = new StringBuilder();
            int start = questionLineIndex + 1;
            int end = Math.Min(start + 30, lines.Length);

            for (int j = start; j < end; j++)
            {
                var trimmed = lines[j].TrimStart();

                // Stop at next question marker
                if (RxQTag.IsMatch(trimmed)) break;
                if (RxHeading.IsMatch(trimmed)) break;
                if (RxSepEquals.IsMatch(trimmed)) break;

                // Stop at a line that looks like a new question (long sentence-like line after blank)
                if (j > start + 1 && string.IsNullOrWhiteSpace(lines[j - 1]) &&
                    trimmed.Length > 30 && char.IsUpper(trimmed[0]) &&
                    !trimmed.StartsWith("//") && !trimmed.StartsWith("#") && !trimmed.StartsWith("/*"))
                {
                    // Check if it looks more like a question than code
                    if (trimmed.Contains("?") || CountWords(trimmed) >= 8)
                        break;
                }

                sb.AppendLine(lines[j]);
            }

            return sb.ToString().Trim();
        }

        private int CountWords(string s)
        {
            int count = 0;
            bool inWord = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetterOrDigit(s[i]))
                {
                    if (!inWord) { count++; inWord = true; }
                }
                else inWord = false;
            }
            return count;
        }

        #region Scoring

        /// <summary>
        /// Cleans question text: removes leading numbers, Q: prefix, collapses whitespace.
        /// </summary>
        private string CleanQuestion(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var text = raw.Trim();
            text = RxCleanNum.Replace(text, "");
            text = RxCleanQ.Replace(text, "");
            text = text.TrimEnd('?', ' ', '\t');
            text = RxWhitespace.Replace(text, " ").Trim();
            return text;
        }

        /// <summary>
        /// Scores how well a stored question matches the search query.
        /// Returns 0.0 to 1.0. Uses exact match, contains, Jaccard, AND word-sequence scoring.
        /// Word-level LCS is fast (typically < 20 words per question = 400 cells max).
        /// </summary>
        private float ScoreMatch(string storedLower, string queryLower,
                                 HashSet<string> storedTokens, HashSet<string> queryTokens)
        {
            if (string.IsNullOrEmpty(storedLower) || string.IsNullOrEmpty(queryLower))
                return 0f;

            // Exact match
            if (storedLower == queryLower) return 1.0f;

            // Contains match (one is a substring of the other)
            if (storedLower.Contains(queryLower)) return 0.95f;
            if (queryLower.Contains(storedLower)) return 0.90f;

            // Keyword Jaccard similarity
            if (storedTokens.Count == 0 || queryTokens.Count == 0) return 0f;

            int intersection = 0;
            foreach (var token in queryTokens)
            {
                if (storedTokens.Contains(token))
                    intersection++;
            }

            if (intersection == 0) return 0f;

            int union = storedTokens.Count + queryTokens.Count - intersection;
            float jaccard = (float)intersection / union;

            // Word-level sequence score (fast: typically < 20 words each)
            var storedWords = TokenizeToList(storedLower);
            var queryWords = TokenizeToList(queryLower);
            float seqScore = WordSequenceScore(storedWords, queryWords);

            // Weighted combination: sequence matters more for discriminating similar questions
            float score = jaccard * 0.45f + seqScore * 0.55f;

            // Bonus: if ALL query tokens appear in the stored question
            if (intersection == queryTokens.Count)
                score = Math.Max(score, 0.78f);

            return score;
        }

        /// <summary>
        /// Word-level Longest Common Subsequence ratio.
        /// Unlike character-level LCS (O(n*m) on full strings), this operates on
        /// tokenized word arrays (typically 5-20 words), so the DP table is tiny.
        /// "sum cube digits number" vs "sum digits" → LCS=2, ratio=0.5
        /// "sum cube digits number" vs "sum cube digits number" → LCS=4, ratio=1.0
        /// </summary>
        private float WordSequenceScore(List<string> storedWords, List<string> queryWords)
        {
            int n = storedWords.Count, m = queryWords.Count;
            if (n == 0 || m == 0) return 0f;

            // DP table: typically 15x15 = 225 cells. Instant.
            var dp = new int[n + 1, m + 1];
            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    if (string.Equals(storedWords[i - 1], queryWords[j - 1], StringComparison.OrdinalIgnoreCase))
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }

            float lcs = dp[n, m];
            return (2f * lcs) / (n + m);
        }

        /// <summary>
        /// Tokenizes text into an ordered word list (preserving sequence for LCS).
        /// Unlike Tokenize() which returns a HashSet, this returns a List.
        /// </summary>
        private List<string> TokenizeToList(string textLower)
        {
            return RxTokenize.Matches(textLower)
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(w => w.Length > 2 && !StopWords.Contains(w))
                .ToList();
        }

        private HashSet<string> Tokenize(string textLower)
        {
            var words = RxTokenize.Matches(textLower)
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(w => w.Length > 2 && !StopWords.Contains(w));

            return new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Unified Line-Based Parser

        /// <summary>
        /// Parses a file into Q/A entries using a single-pass line-based parser.
        /// Handles MIXED formats in one file: Q:/A: tags, ## headings, numbered, and --- separators.
        /// O(n) where n = number of lines. No multi-line regex on full content.
        /// </summary>
        public List<QAEntry> ParseFileUnified(string content, string sourceFile)
        {
            var entries = new List<QAEntry>();
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            string currentQ = null;
            var currentA = new StringBuilder();
            bool pendingQuestion = false; // After standalone ## — next non-empty line is the question

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // ── Priority 1: Q: / Question: tag ──
                var qTag = RxQTag.Match(trimmed);
                if (qTag.Success)
                {
                    FlushEntry(entries, ref currentQ, currentA);
                    currentQ = qTag.Groups[1].Value.Trim();
                    currentA.Clear();
                    pendingQuestion = false;
                    continue;
                }

                // ── Priority 2: A: / Answer: tag ──
                var aTag = RxATag.Match(trimmed);
                if (aTag.Success && currentQ != null)
                {
                    currentA.Clear();
                    var aText = aTag.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(aText))
                        currentA.AppendLine(aText);
                    pendingQuestion = false;
                    continue;
                }

                // ── Standalone heading: ## alone on a line (separator, question on NEXT line) ──
                if (trimmed.Length <= 3 && trimmed.Length >= 1 && trimmed.TrimEnd().All(c => c == '#'))
                {
                    FlushEntry(entries, ref currentQ, currentA);
                    pendingQuestion = true; // Next non-empty line becomes the question
                    continue;
                }

                // ── Priority 3: ## Heading with text on same line (new question) ──
                var heading = RxHeading.Match(trimmed);
                if (heading.Success)
                {
                    FlushEntry(entries, ref currentQ, currentA);
                    currentQ = heading.Groups[1].Value.Trim();
                    currentA.Clear();
                    pendingQuestion = false;
                    continue;
                }

                // ── Separator === (end of Q/A block) ──
                if (RxSepEquals.IsMatch(trimmed))
                {
                    FlushEntry(entries, ref currentQ, currentA);
                    pendingQuestion = false;
                    continue;
                }

                // ── Separator --- (entry terminator when Q+A are complete, otherwise section break) ──
                if (RxSepDash.IsMatch(trimmed))
                {
                    if (currentQ != null && currentA.Length > 0)
                    {
                        // We have both Q and A — this is an entry terminator
                        FlushEntry(entries, ref currentQ, currentA);
                    }
                    // else: just a section separator, ignore
                    continue;
                }

                // ── Numbered: 1. or 1) (new question if it looks like one) ──
                var numbered = RxNumbered.Match(trimmed);
                if (numbered.Success)
                {
                    var qText = numbered.Groups[1].Value.Trim();
                    if (qText.Contains("?") || qText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length >= 5)
                    {
                        FlushEntry(entries, ref currentQ, currentA);
                        currentQ = qText;
                        currentA.Clear();
                        pendingQuestion = false;
                        continue;
                    }
                }

                // ── Pending question from standalone ## ──
                if (pendingQuestion && !string.IsNullOrWhiteSpace(trimmed))
                {
                    // This line is the question that follows a standalone ##
                    currentQ = trimmed;
                    currentA.Clear();
                    pendingQuestion = false;
                    continue;
                }

                // ── Heuristic: plain question line (no marker, but looks like a question) ──
                // Detects lines like "Write a Java program to..." after a blank line or entry flush
                if (currentQ == null && !string.IsNullOrWhiteSpace(trimmed) &&
                    trimmed.Length > 25 && char.IsUpper(trimmed[0]) &&
                    !trimmed.StartsWith("//") && !trimmed.StartsWith("/*") &&
                    !trimmed.StartsWith("import ") && !trimmed.StartsWith("public ") &&
                    !trimmed.StartsWith("private ") && !trimmed.StartsWith("class ") &&
                    !trimmed.StartsWith("def ") && !trimmed.StartsWith("int ") &&
                    !trimmed.StartsWith("```"))
                {
                    // Check if it's sentence-like (mostly letters and spaces, >= 5 words)
                    int wordCount = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    bool hasLetterMajority = trimmed.Count(c => char.IsLetter(c) || c == ' ') > trimmed.Length * 0.7;
                    if (wordCount >= 5 && hasLetterMajority)
                    {
                        FlushEntry(entries, ref currentQ, currentA);
                        currentQ = trimmed;
                        currentA.Clear();
                        pendingQuestion = false;
                        continue;
                    }
                }

                // ── Regular content line ──
                if (currentQ != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        if (currentA.Length > 0)
                            currentA.AppendLine();
                    }
                    else
                    {
                        // Skip ``` code fence markers but keep code content
                        if (trimmed.StartsWith("```"))
                            continue;
                        currentA.AppendLine(line.TrimEnd());
                    }
                }
            }

            // Flush last entry
            FlushEntry(entries, ref currentQ, currentA);

            // If nothing was parsed, treat entire file as one entry
            if (entries.Count == 0 && !string.IsNullOrWhiteSpace(content))
            {
                entries.Add(new QAEntry
                {
                    Question = Path.GetFileNameWithoutExtension(sourceFile),
                    Answer = content.Length > 5000 ? content.Substring(0, 5000) : content.Trim()
                });
            }

            return entries;
        }

        private void FlushEntry(List<QAEntry> entries, ref string currentQ, StringBuilder currentA)
        {
            if (currentQ != null && currentA.Length > 0)
            {
                var answer = currentA.ToString().Trim();
                if (!string.IsNullOrEmpty(answer))
                {
                    entries.Add(new QAEntry { Question = currentQ, Answer = answer });
                }
            }
            currentQ = null;
            currentA.Clear();
        }

        /// <summary>
        /// Legacy method — redirects to unified parser for backward compatibility.
        /// </summary>
        public List<QAEntry> ParseFile(string content, string sourceFile)
        {
            return ParseFileUnified(content, sourceFile);
        }

        #endregion

        #region File Utilities

        private List<string> GetTextFiles()
        {
            var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".md", ".csv", ".log", ".json", ".xml", ".html", ".htm" };

            var files = new List<string>();
            try
            {
                files = Directory.GetFiles(botFolderPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => textExtensions.Contains(Path.GetExtension(f)))
                    .ToList();
            }
            catch { }
            return files;
        }

        private Encoding DetectEncoding(string filePath)
        {
            try
            {
                var bytes = new byte[4];
                using (var fs = File.OpenRead(filePath))
                {
                    int read = fs.Read(bytes, 0, 4);
                    if (read >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                        return Encoding.UTF8;
                    if (read >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                        return Encoding.Unicode;
                    if (read >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                        return Encoding.BigEndianUnicode;
                }
            }
            catch { }
            return Encoding.UTF8;
        }

        private void Log(string msg)
        {
            LogEvent?.Invoke(msg);
        }

        #endregion
    }

    /// <summary>
    /// Cached Q/A entry with pre-computed search data.
    /// </summary>
    internal class CachedEntry
    {
        public string Question;
        public string Answer;
        public string SourceFile;
        public string QuestionLower;           // Pre-computed lowercase for fast comparison
        public HashSet<string> QuestionTokens; // Pre-computed keyword tokens for Jaccard
    }

    /// <summary>
    /// A single Q/A pair extracted from a file.
    /// </summary>
    public class QAEntry
    {
        public string Question { get; set; }
        public string Answer { get; set; }
    }

    /// <summary>
    /// Result of an answer search.
    /// </summary>
    public class SearchResult
    {
        public string Question { get; set; }
        public string Answer { get; set; }
        public string SourceFile { get; set; }
        public float Score { get; set; }

        public string Preview
        {
            get
            {
                if (string.IsNullOrEmpty(Answer)) return "";
                var len = Math.Min(80, Answer.Length);
                return Answer.Substring(0, len) + (Answer.Length > len ? "..." : "");
            }
        }
    }
}
