using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fb2.Document;
using Fb2.Document.Models;
using Fb2.Document.Models.Base;

namespace Fb2CleanerApp
{
    internal class BookSummary
    {
        public bool InvalidFormat { get; set; }
        public Exception Exception { get; set; }

        public string Origin { get; set; }
        public DateTime OriginCreationTime => File.GetCreationTime(Origin);

        public string FileName { get; set; }
        public bool Zipped { get; set; }
        public Fb2Document Document { get; set; }

        public string Title { get; set; }
        public string SeriesName { get; set; }
        public int SeriesNumber { get; set; }

        public List<string> Genres { get; set; } = new List<string>();
        public List<Author> Authors { get; set; } = new List<Author>();
        public List<string> AuthorNames { get; set; } = new List<string>();
        public List<string> Chapters { get; set; } = new List<string>();

        public string LastChapterWithNumber => Chapters?.Where(x => Regex.IsMatch(x, @"\d+?")).LastOrDefault() ?? string.Empty;

        public string ListOfAuthors => string.Join("; ", AuthorNames);
    }

    internal class Program
    {
        /*
            @"c:\Users\Viktor Kozyrev\Downloads\Telegram Desktop\"
            @"c:\Users\Viktor Kozyrev\Documents\Calibre Library\"
        */

        #region Static Methods

        private static string HtmlEscapesToXml(string text)
        {
            var exclude = new[] { "&amp;", "&apos;", "&quot;", "&gt;", "&lt;" };
            var matches = Regex.Matches(text, @"&[\d\w]+?;").Where(m => exclude.All(x => x != m.Value)).Reverse().ToList();
            foreach (var match in matches)
            {
                var special = match.Value;
                try
                {
                    var xmlSpecial = System.Web.HttpUtility.HtmlDecode(special);
                    text = text.Remove(match.Index, match.Length).Insert(match.Index, xmlSpecial);
                }
                catch
                {
                    // ignore
                }
            }
            return text;
        }

        private static void ParseTitleInfo(BookSummary bookSummary, List<Fb2Node> nodes)
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

        private static void ParseBodyInfo(BookSummary bookSummary, List<Fb2Node> nodes)
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

        // ReSharper disable once ArrangeTypeMemberModifiers
        // ReSharper disable once UnusedParameter.Local
        static async Task Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || args.Length > 1)
                {
                    Console.WriteLine("usage: Fb2CleanerApp <path to a directory with FB2 files>");
                    Console.Write("\r\nPress any key...");
                    Console.ReadKey();
                    return;
                }
                var path = args[0];

                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                var throttler = new SemaphoreSlim(initialCount: 120);
                var numberThrottler = new SemaphoreSlim(initialCount: 1);
                var fileNumber = 0;

                async ValueTask<int> GetFileNumber()
                {
                    try
                    {
                        await numberThrottler.WaitAsync();
                        return ++fileNumber;
                    }
                    finally
                    {
                        numberThrottler.Release();
                    }
                }

                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine($"Processing folder '{path}'");

                var files = Directory
                    .GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(x => ".fb2;.zip;".Contains(Path.GetExtension(x)));
                var enumerable = files as string[] ?? files.ToArray();
                var fileCount = enumerable.Count();
                Console.WriteLine($"{fileCount} files were found");

                var bookSummaries = new ConcurrentBag<BookSummary>();

                #region Local Functions

                async Task ProcessFb2FileBase(string file)
                {
                    var document = new Fb2Document();
                    await using var stream = File.OpenRead(file);
                    await document.LoadAsync(stream);
                    var bookSummary = new BookSummary()
                    {
                        Origin = file,
                        FileName = file,
                        Zipped = false,
                        Document = document
                    };
                    bookSummaries.Add(bookSummary);
                    ParseTitleInfo(bookSummary, bookSummary.Document.Title.Content);
                    ParseBodyInfo(bookSummary, bookSummary.Document.Bodies.SelectMany(x => x.Content).ToList());
                }
                async Task ProcessFb2File(string file, bool noThrottle = false)
                {
                    try
                    {
                        if (!noThrottle) await throttler.WaitAsync();
                        var num = await GetFileNumber();
                        Console.WriteLine($"[{num:d4}/{fileCount}] '{file}'");
                        try
                        {
                            await ProcessFb2FileBase(file);
                        }
                        catch (Exception x)
                        {
                            // we have to make an attempt to correct the problem related to the HTML-escaped symbols, like &frac14; and such.
                            Debug.WriteLine(x.Message);
                            if (x.Message.StartsWith("'xlink' is an undeclared prefix."))
                            {
                                // reading the file as text
                                var text = await File.ReadAllTextAsync(file);
                                // fix XML
                                text = text.Replace("<a xlink:href=", "<a href=");
                                // rename the old file
                                File.SetAttributes(file, FileAttributes.Normal);
                                File.Move(file, file + "_", true);
                                // save the corrected file
                                await File.WriteAllTextAsync(file, text);
                                // start over
                                await ProcessFb2File(file, true);
                            }
                            else if (x.Message.StartsWith("The 'emphasis' start tag on line"))
                            {
                                // reading the file as text
                                var text = await File.ReadAllTextAsync(file);
                                // fix XML
                                text = text
                                    .Replace("<p><emphasis><emphasis></emphasis></p>", "<p><emphasis></emphasis></p>")
                                    .Replace("<p><emphasis></emphasis></emphasis></p>", "<p><emphasis></emphasis></p>");
                                // rename the old file
                                File.SetAttributes(file, FileAttributes.Normal);
                                File.Move(file, file + "_", true);
                                // save the corrected file
                                await File.WriteAllTextAsync(file, text);
                                // start over
                                await ProcessFb2File(file, true);
                            }
                            else
                            {
                                try
                                {
                                    // reading the file as text
                                    var text = await File.ReadAllTextAsync(file);
                                    // fix XML
                                    text = HtmlEscapesToXml(text);
                                    // rename the old file
                                    File.SetAttributes(file, FileAttributes.Normal);
                                    File.Move(file, file + "_", true);
                                    // save the corrected file
                                    await File.WriteAllTextAsync(file, text);
                                    // process the new file
                                    await ProcessFb2FileBase(file);
                                }
                                catch (Exception y)
                                {
                                    var bookSummary = new BookSummary()
                                    {
                                        Origin = file,
                                        FileName = file,
                                        Zipped = false,
                                        InvalidFormat = true,
                                        Exception = y
                                    };
                                    bookSummaries.Add(bookSummary);
                                }
                            }
                        }
                    }
                    finally
                    {
                        throttler.Release();
                    }
                }
                async Task ProcessZipFile(string file)
                {
                    try
                    {
                        await throttler.WaitAsync();
                        var num = await GetFileNumber();
                        Console.WriteLine($"[{num:d4}/{fileCount}] '{file}'");
                        using var archive = ZipFile.Open(file, ZipArchiveMode.Read, Encoding.UTF8);
                        foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".fb2")))
                        {
                            var document = new Fb2Document();
                            using var reader = new StreamReader(entry.Open());
                            document.Load(await reader.ReadToEndAsync());
                            var bookSummary = new BookSummary()
                            {
                                Origin = file,
                                FileName = file,
                                Zipped = true,
                                Document = document
                            };
                            bookSummaries.Add(bookSummary);
                            ParseTitleInfo(bookSummary, bookSummary.Document.Title.Content);
                            ParseBodyInfo(bookSummary, bookSummary.Document.Bodies.SelectMany(x => x.Content).ToList());
                        }
                    }
                    finally
                    {
                        throttler.Release();
                    }
                }

                #endregion

                // parsing the FB2 documents in parallel.

                var tasks = enumerable
                    .Select(file => Path.GetExtension(file) == ".zip" ? ProcessZipFile(file) : ProcessFb2File(file))
                    .ToList();
                await Task.WhenAll(tasks);

                var filesToDelete = new List<string>();

                // handle incomplete and duplicated books without a series.

                var seriesGroupsNoSeries =
                    (from bs in bookSummaries
                     where string.IsNullOrEmpty(bs.SeriesName) && !bs.InvalidFormat && bs.SeriesNumber == 0
                     group bs by new { bs.Title, bs.ListOfAuthors } into g
                     orderby g.Key.Title, g.Key.ListOfAuthors
                     select new
                     {
                         g.Key.Title,
                         g.Key.ListOfAuthors,
                         Count = g.Count(),
                         List = g.OrderBy(x => x.OriginCreationTime).ToList()
                     })
                    .Where(x => x.Count > 1 && (!string.IsNullOrEmpty(x.Title) || !string.IsNullOrEmpty(x.ListOfAuthors)))
                    .ToList();

                foreach (var seriesGroup in seriesGroupsNoSeries)
                {
                    filesToDelete
                        .AddRange(
                            seriesGroup
                                .List
                                .Take(seriesGroup.List.Count - 1)
                                .Select(x => x.Origin));
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.Write(seriesGroup.Title);
                    Console.Write(" ");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(seriesGroup.ListOfAuthors);

                    foreach (var bookSummary in seriesGroup.List)
                    {
                        Console.Write("\t");
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write(bookSummary.LastChapterWithNumber);

                        Console.Write(" ");
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine(bookSummary.Origin);
                    }
                }

                // handle incomplete and duplicated books with a series.

                var seriesGroups =
                    (from bs in bookSummaries
                     where !string.IsNullOrEmpty(bs.SeriesName) && !bs.InvalidFormat && bs.SeriesNumber != 0
                     group bs by new { bs.SeriesName, bs.SeriesNumber, bs.ListOfAuthors, bs.Title } into g
                     orderby g.Key.SeriesName, g.Key.SeriesNumber, g.Key.ListOfAuthors, g.Key.Title
                     select new
                     {
                         g.Key.SeriesName,
                         g.Key.SeriesNumber,
                         g.Key.ListOfAuthors,
                         g.Key.Title,
                         Count = g.Count(),
                         List = g.OrderBy(x => x.OriginCreationTime).ToList()
                     })
                    .Where(x => x.Count > 1 && !string.IsNullOrEmpty(x.ListOfAuthors))
                    .ToList();

                foreach (var seriesGroup in seriesGroups)
                {
                    filesToDelete
                        .AddRange(
                            seriesGroup
                                .List
                                .Take(seriesGroup.List.Count - 1)
                                .Select(x => x.Origin));
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    if (string.IsNullOrEmpty(seriesGroup.SeriesName))
                    {
                        Console.Write("No Series");
                    }
                    else
                    {
                        Console.Write(seriesGroup.SeriesName);
                        Console.Write(" ");
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write($"{seriesGroup.SeriesNumber:d2}");
                    }
                    Console.Write(" ");
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.Write(seriesGroup.Title);
                    Console.Write(" ");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(seriesGroup.ListOfAuthors);

                    foreach (var bookSummary in seriesGroup.List)
                    {
                        Console.Write("\t");
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.Write(bookSummary.Title);

                        Console.Write(" ");
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write(bookSummary.LastChapterWithNumber);

                        Console.Write(" ");
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine(bookSummary.Origin);
                    }
                }

                // delete older/duplicate books.

                Console.ForegroundColor = ConsoleColor.DarkGray;
                foreach (var file in filesToDelete)
                {
                    Console.WriteLine($"Deleting '{file}'");
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                // show the invalid files or files Fb2 library cannot load.

                foreach (var bookSummary in bookSummaries.Where(x => x.InvalidFormat))
                {
                    Console.Write($"Invalid format '{bookSummary.Origin}' ");
                    Console.WriteLine($"Message '{bookSummary.Exception.Message}'");
                }
            }
            catch (Exception gex)
            {
                Console.WriteLine(gex.Message);
                Console.WriteLine(gex.StackTrace);
            }
            Console.Write("\r\nPress any key...");
            Console.ReadKey();
        }
    }
}
