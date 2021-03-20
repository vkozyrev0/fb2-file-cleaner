using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Fb2CleanerApp.Models;

namespace Fb2CleanerApp.Workers
{
    public class ErrorWorker
    {
        private readonly string _file;
        private string _text;
        private bool _dirty;
        private Encoding _encoding;

        public ErrorWorker(string file)
        {
            _file = file;
        }
        private static readonly HashSet<string> Exclude = new() { "&amp;", "&apos;", "&quot;", "&gt;", "&lt;" };
        private static readonly Regex ExcludeSearchPattern = new(@"&[\d\w]+?;", RegexOptions.Compiled);

        private static string HtmlEscapesToXml(string text, IEnumerable<Match> matches)
        {
            foreach (var match in matches)
            {
                var special = match.Value;
                var xmlSpecial = System.Web.HttpUtility.HtmlDecode(special);
                text = text
                    .Remove(match.Index, match.Length)
                    .Insert(match.Index, xmlSpecial);
            }
            return text;
        }

        private void GuessEncoding()
        {
            using var fs = File.OpenRead(_file);
            var charsetDetector = new Ude.CharsetDetector();
            charsetDetector.Feed(fs);
            charsetDetector.DataEnd();
            _encoding = charsetDetector.Charset != null ? Encoding.GetEncoding(charsetDetector.Charset) : Encoding.UTF8;
        }

        public async Task CorrectNamespaces()
        {
            const string xlinkNamespace = @"xmlns:xlink=""http://www.w3.org/1999/xlink""";
            const string xlink = "<a xlink:href=";

            if (!_text.Contains(xlink)) return;

            var match = Regex.Match(_text, "\\<FictionBook\\s([\\w\\W]+?)\\>");
            if (!match.Success) return;

            var attributes = Regex.Split(match.Groups[1].Value, "\\s+");

            if (attributes.Contains(xlinkNamespace)) return;

            var position = match.Groups[1].Index + match.Groups[1].Length;
            _text = _text.Insert(position, " " + xlinkNamespace);
            _dirty = true;
            await Statistics.IncrementFixedFilesCount();
        }

        public async Task ReadFromFile()
        {
            GuessEncoding();
            _text = await File.ReadAllTextAsync(_file, _encoding);
            _dirty = false;
        }

        public async Task SaveToFile()
        {
            if (_dirty)
            {
                // rename the old file
                File.SetAttributes(_file, FileAttributes.Normal);
                File.Move(_file, _file + "_", true);

                // save the corrected file
                await File.WriteAllTextAsync(_file, _text, _encoding);
                _dirty = false;
            }
        }

        public async Task FixEmphasisTagIssue()
        {
            const string badStart = "<p><emphasis><emphasis></emphasis></p>";
            const string badEnd = "<p><emphasis></emphasis></emphasis></p>";
            const string goodStart = "<p><emphasis></emphasis></p>";
            const string goodEnd = "<p><emphasis></emphasis></p>";

            if (!_text.Contains(badStart) && !_text.Contains(badEnd)) return;
            _text = _text
                .Replace(badStart, goodStart)
                .Replace(badEnd, goodEnd);
            _dirty = true;
            await Statistics.IncrementFixedFilesCount();
        }
        public async Task FixHtmlEscapeCharacterIssue()
        {
            var matches = ExcludeSearchPattern
                .Matches(_text)
                .Where(m => !Exclude.Contains(m.Value))
                .Reverse()
                .ToList();
            if (matches.Count == 0) return;
            _text = HtmlEscapesToXml(_text, matches);
            _dirty = true;
            await Statistics.IncrementFixedFilesCount();
        }
    }
}
