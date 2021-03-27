using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fb2CleanerApp.Models;
using Ionic.Zip;
using SoftCircuits.HtmlMonkey;

namespace Fb2CleanerApp.Workers
{
    public class Fb2Worker
    {
        private readonly string _file;
        private Encoding _encoding;

        public Fb2Worker(string file)
        {
            _file = file;
        }

        #region Static Methods

        private void GuessEncoding()
        {
            using var fs = File.OpenRead(_file);
            var charsetDetector = new Ude.CharsetDetector();
            charsetDetector.Feed(fs);
            charsetDetector.DataEnd();
            _encoding = charsetDetector.Charset != null ? Encoding.GetEncoding(charsetDetector.Charset) : Encoding.UTF8;
        }

        private static void ParseTitleInfo(BookSummary bookSummary)
        {
            var document = bookSummary.Document;

            var genres = document
                .Find("genre")
                .Where(x => x.ParentNode != null && x.ParentNode.TagName == "title-info")
                .Select(x => x.Text)
                .ToList();
            bookSummary.Genres.AddRange(genres);

            var authors = document
                .Find("author")
                .Where(x => x.ParentNode != null && x.ParentNode.TagName == "title-info")
                .Select(x => new
                {
                    FirstName = x.Children.FirstOrDefault(y => y is HtmlElementNode { TagName: "first-name" })?.Text,
                    LastName = x.Children.FirstOrDefault(y => y is HtmlElementNode { TagName: "last-name" })?.Text,
                    MiddleName = x.Children.FirstOrDefault(y => y is HtmlElementNode { TagName: "middle-name" })?.Text,
                }).ToList();

            var sequence = document
                .Find("sequence")
                .FirstOrDefault(x => x.ParentNode != null && x.ParentNode.TagName == "title-info");

            if (sequence != null)
            {
                if (sequence.Attributes.TryGetValue("name", out var sequenceName))
                {
                    bookSummary.SeriesName = sequenceName.Value;

                    // special case of sequence name not being escaped properly

                    var nameParts = new List<string>();
                    if (string.IsNullOrEmpty(bookSummary.SeriesName))
                    {
                        nameParts.AddRange(from t in sequence.Attributes where t.Value == null select t.Name);
                        if (nameParts.Count > 0 && nameParts.Last() == "\"\"")
                        {
                            nameParts.RemoveAt(nameParts.Count - 1);
                        }

                        bookSummary.SeriesName = "\"" + string.Join(" ", nameParts) + "\"";
                    }
                }
                if (sequence.Attributes.TryGetValue("number", out var sequenceNumber))
                {
                    bookSummary.SeriesNumber = int.Parse(string.IsNullOrEmpty(sequenceNumber.Value) ? "0" : sequenceNumber.Value);
                }

            }

            bookSummary.Title = document
                .Find("title, book-title")
                .FirstOrDefault(x => x.ParentNode != null && x.ParentNode.TagName == "title-info")
                ?.Text;

            foreach (var node in authors)
            {
                var nameParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(node.FirstName))
                {
                    nameParts.Add(node.FirstName);
                }
                if (!string.IsNullOrWhiteSpace(node.MiddleName))
                {
                    nameParts.Add(node.MiddleName);
                }
                if (!string.IsNullOrWhiteSpace(node.LastName))
                {
                    nameParts.Add(node.LastName);
                }
                bookSummary.AuthorNames.Add(string.Join(" ", nameParts));
            }
        }

        private static void ParseBodyInfo(BookSummary bookSummary)
        {
            var document = bookSummary.Document;
            var titles = document
                .Find("title")
                .Where(x => x.ParentNode?.ParentNode != null && x.ParentNode != null && x.ParentNode.TagName == "section" && x.ParentNode.ParentNode.TagName == "body")
                .ToList();
            foreach (var title in titles)
            {
                if (title.Children.Count == 0)
                {
                    bookSummary.Chapters.Add(title.Text);
                }
                else
                {
                    var parts = title.Children.Select(line => line.Text).ToList();
                    parts = parts
                        .Select(x => x.Trim(' ').Trim('\r').Trim('\n'))
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToList();
                    bookSummary.Chapters.Add(string.Join(" | ", parts));
                }
            }
        }

        #endregion

        public async Task<BookSummary> Parse()
        {
            BookSummary bookSummary;
            try
            {
                GuessEncoding();
                var text = await File.ReadAllTextAsync(_file, _encoding);

                var document = HtmlDocument.FromHtml(text);
                bookSummary = new BookSummary
                {
                    Origin = _file,
                    FileName = _file,
                    Zipped = false,
                    Document = document
                };
                ParseTitleInfo(bookSummary);
                ParseBodyInfo(bookSummary);
            }
            catch (Exception x)
            {
                bookSummary = new BookSummary
                {
                    Origin = _file,
                    FileName = _file,
                    Zipped = false,
                    InvalidFormat = true,
                    Exception = x
                };
            }

            return bookSummary;
        }

        private static Encoding GuessEncoding(Stream stream)
        {
            var charsetDetector = new Ude.CharsetDetector();
            charsetDetector.Feed(stream);
            charsetDetector.DataEnd();
            return charsetDetector.Charset != null ? Encoding.GetEncoding(charsetDetector.Charset) : Encoding.UTF8;
        }

        public async Task<List<string>> ExtractZipped()
        {
            var files = new List<string>();
            var prefix = Path.GetFileNameWithoutExtension(_file) + "_";
            using var archive = ZipFile.Read(_file ?? string.Empty, new ReadOptions
            {
                Encoding = Encoding.GetEncoding(ArgumentWorker.EncodingCodePage)
            });
            archive.AlternateEncoding = Encoding.UTF8;
            archive.AlternateEncodingUsage = ZipOption.AsNecessary;
            var entries = archive.Entries
                .Where(x => string.Compare(Path.GetExtension(x.FileName), ".fb2", StringComparison.InvariantCulture) ==
                0).ToList();
            foreach (var entry in entries)
            {
                var file = Path.Combine(Path.GetDirectoryName(_file) ?? string.Empty, prefix + entry.FileName);
                Encoding encoding;
                await using (var encodingStream = entry.OpenReader())
                {
                    encoding = GuessEncoding(encodingStream);
                }
                await using var stream = entry.OpenReader();
                using var reader = new StreamReader(stream, encoding);
                var text = await reader.ReadToEndAsync();
                if (File.Exists(file))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                await File.WriteAllTextAsync(file, text, encoding);
                files.Add(file);
            }

            return files;
        }
        public static void RestoreFile(string file)
        {
            var restoredFile = file.Substring(0, file.Length - 1); // removes _
            if (File.Exists(restoredFile))
            {
                File.SetAttributes(restoredFile, FileAttributes.Normal);
            }
            File.Move(file, restoredFile, true);
        }
    }
}