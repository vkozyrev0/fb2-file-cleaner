using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fb2.Document;
using Fb2.Document.Models;
using Fb2.Document.Models.Base;
using Fb2CleanerApp.Models;

namespace Fb2CleanerApp.Workers
{
    public class Fb2Worker
    {
        private readonly string _file;


        public Fb2Worker(string file)
        {
            _file = file;
        }

        #region Static Methods

        private static void ParseTitleInfo(BookSummary bookSummary, IEnumerable<Fb2Node> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case BookGenre genre:
                        bookSummary.Genres.Add(genre.ToString());
                        break;
                    case Author author:
                        bookSummary.Authors.Add(author);
                        var firstName = author.Content.FirstOrDefault(x => x is FirstName)?.ToString() ?? string.Empty;
                        var lastName = author.Content.FirstOrDefault(x => x is LastName)?.ToString() ?? string.Empty;
                        var name = string.Empty;
                        if (!string.IsNullOrWhiteSpace(firstName))
                        {
                            name = firstName;
                        }

                        if (!string.IsNullOrWhiteSpace(lastName))
                        {
                            if (!string.IsNullOrEmpty(name))
                            {
                                name += " ";
                            }

                            name += lastName;
                        }

                        bookSummary.AuthorNames.Add(name);
                        break;
                    case BookTitle title:
                        bookSummary.Title = title.ToString();
                        break;
                    case SequenceInfo seq:
                        bookSummary.SeriesName = seq.Attributes["name"];
                        var number = 0;
                        if (seq.Attributes.ContainsKey("number"))
                        {
                            int.TryParse(seq.Attributes["number"], out number);
                        }

                        bookSummary.SeriesNumber = number;
                        break;
                }
            }
        }

        private static void ParseBodyInfo(BookSummary bookSummary, IEnumerable<Fb2Node> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case BodySection section:
                        ParseBodyInfo(bookSummary, section.Content);
                        break;
                    case Title title:
                        bookSummary.Chapters.Add(title.ToString().Trim('\r').Trim('\n'));
                        break;
                }
            }
        }

        #endregion

        public async Task<BookSummary> Parse()
        {
            BookSummary bookSummary;
            try
            {
                var document = new Fb2Document();
                await using var stream = File.OpenRead(_file);
                await document.LoadAsync(stream);
                bookSummary = new BookSummary
                {
                    Origin = _file,
                    FileName = _file,
                    Zipped = false,
                    Document = document
                };
                Fb2Worker.ParseTitleInfo(bookSummary, bookSummary.Document.Title.Content);
                Fb2Worker.ParseBodyInfo(bookSummary, bookSummary.Document.Bodies
                    .SelectMany(x => x.Content)
                    .ToList());
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

        public async Task<List<string>> ExtractZipped()
        {
            var files = new List<string>();
            var prefix = Path.GetFileNameWithoutExtension(_file) + "_";
            using var archive = ZipFile.Open(_file ?? string.Empty, ZipArchiveMode.Read, Encoding.UTF8);
            foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".fb2")))
            {
                var file = Path.Combine(Path.GetDirectoryName(_file) ?? string.Empty, prefix + _file);
                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync();
                if (File.Exists(file))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                await File.WriteAllTextAsync(file, text);
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