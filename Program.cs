using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fb2CleanerApp.Enums;
using Fb2CleanerApp.Models;
using Fb2CleanerApp.Workers;

namespace Fb2CleanerApp
{
    internal class Program
    {
        // ReSharper disable once ArrangeTypeMemberModifiers
        // ReSharper disable once UnusedParameter.Local
        static async Task Main(string[] args2)
        {
            ArgumentWorker.Parse(args2);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.OutputEncoding = Encoding.UTF8;

            var throttler = new SemaphoreSlim(ArgumentWorker.Concurrency);
            var numberThrottler = new SemaphoreSlim(initialCount: 1);
            int fileNumber, fileCount;

            async ValueTask<int> GetFileNumber()
            {
                try
                {
                    await numberThrottler.WaitAsync();
                    // ReSharper disable once AccessToModifiedClosure
                    return ++fileNumber;
                }
                finally
                {
                    numberThrottler.Release();
                }
            }
            try
            {
                var path = ArgumentWorker.Folder;

                switch (ArgumentWorker.Action)
                {
                    case ActionType.Restore:
                    {
                        var files = Directory
                            .GetFiles(path, "*.*", SearchOption.AllDirectories)
                            .Where(x => string.Compare(".fb2_", Path.GetExtension(x), StringComparison.InvariantCulture) == 0)
                            .ToList();

                        async Task RestoreFile(string file)
                        {
                            var num = await GetFileNumber();
                            try
                            {
                                await throttler.WaitAsync();
                                Console.WriteLine($"[{num}/{fileCount}] Restore '{file}'");

                                Fb2Worker.RestoreFile(file);
                            }
                            finally
                            {
                                throttler.Release();
                            }
                        }

                        fileCount = files.Count;
                        fileNumber = 0;

                        var tasks = files.Select(RestoreFile).ToList();
                        await Task.WhenAll(tasks);
                        break;
                    }
                    case ActionType.Fix:
                    {
                        var files = Directory
                            .GetFiles(path, "*.*", SearchOption.AllDirectories)
                            .Where(x => string.Compare(".zip", Path.GetExtension(x), StringComparison.InvariantCulture) == 0)
                            .ToList();

                        fileNumber = 0;
                        fileCount = files.Count;

                        var count = fileCount;

                        async Task ProcessZipFile(string file)
                        {
                            var num = await GetFileNumber();
                            try
                            {
                                await throttler.WaitAsync();
                                Console.WriteLine($"[{num}/{count}] '{file}'");

                                var fb2 = new Fb2Worker(file);
                                await fb2.ExtractZipped();
                            }
                            finally
                            {
                                throttler.Release();
                            }
                        }

                        // extracts all fb2 files from the zip archives.

                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine("Processing ZIP files...");
                        Console.ForegroundColor = ConsoleColor.DarkGray;

                        var tasks = files.Select(ProcessZipFile).ToList();
                        await Task.WhenAll(tasks);

                        // now we have all fb2 files.

                        files = Directory
                            .GetFiles(path, "*.*", SearchOption.AllDirectories)
                            .Where(x => string.Compare(".fb2", Path.GetExtension(x), StringComparison.InvariantCulture) == 0)
                            .ToList();

                        fileNumber = 0;
                        fileCount = files.Count;
                        var bookSummaries = new ConcurrentBag<BookSummary>();

                        async Task ProcessFb2File(string file)
                        {
                            var num = await GetFileNumber();
                            try
                            {
                                await throttler.WaitAsync();
                                Console.WriteLine($"[{num}/{fileCount}] '{file}'");

                                // fixes some fb2 file error.

                                var errorWorker = new ErrorWorker(file);
                                await errorWorker.ReadFromFile();
                                await errorWorker.FixEmphasisTagIssue();
                                await errorWorker.FixExclamationTagIssue();
                                await errorWorker.CorrectNamespaces();
                                await errorWorker.FixHtmlEscapeCharacterIssue();
                                await errorWorker.SaveToFile();

                                // processes fb2 file.

                                var fb2 = new Fb2Worker(file);
                                bookSummaries.Add(await fb2.Parse());
                            }
                            catch (Exception x)
                            {
                                await Statistics.IncrementPersinFileErrorCount();
                                bookSummaries.Add(new BookSummary
                                {
                                    InvalidFormat = true,
                                    Origin = file,
                                    FileName = file,
                                    Exception = x
                                });
                            }
                            finally
                            {
                                throttler.Release();
                            }
                        }

                        // processes fb2 files.

                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine("Processing FB2 files...");
                        Console.ForegroundColor = ConsoleColor.DarkGray;

                        tasks = files.Select(ProcessFb2File).ToList();
                        await Task.WhenAll(tasks);

                        var filesToDelete = new List<string>();

                        // handles incomplete and duplicated books without a series.

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

                            var last = seriesGroup.List.Last();
                            foreach (var bookSummary in seriesGroup.List)
                            {
                                Console.Write("\t");
                                if (last == bookSummary)
                                {
                                    Console.ForegroundColor = ConsoleColor.Cyan;
                                    Console.Write(bookSummary.LastChapterWithNumber);

                                    Console.Write(" ");
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.WriteLine(bookSummary.Origin);
                                }
                                else
                                {
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.Write(bookSummary.LastChapterWithNumber);
                                    Console.Write(" ");
                                    Console.WriteLine(bookSummary.Origin);
                                }
                            }
                        }

                        // handles incomplete and duplicated books with a series.

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

                            var last = seriesGroup.List.Last();
                            foreach (var bookSummary in seriesGroup.List)
                            {
                                Console.Write("\t");
                                if (last == bookSummary)
                                {
                                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                                    Console.Write(bookSummary.Title);

                                    Console.Write(" ");
                                    Console.ForegroundColor = ConsoleColor.Cyan;
                                    Console.Write(bookSummary.LastChapterWithNumber);

                                    Console.Write(" ");
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.WriteLine(bookSummary.Origin);
                                }
                                else
                                {
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.Write(bookSummary.Title);
                                    Console.Write(" ");
                                    Console.Write(bookSummary.LastChapterWithNumber);
                                    Console.Write(" ");
                                    Console.WriteLine(bookSummary.Origin);
                                }
                            }
                        }

                        // delete older/duplicate books.

                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine("Removing older duplicate files...");
                        Console.ForegroundColor = ConsoleColor.DarkGray;

                        foreach (var file in filesToDelete)
                        {
                            Console.WriteLine($"Deleting '{file}'");
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            await Statistics.IncrementDeletedFilesCount();
                        }

                        // show the invalid files or files Fb2 library cannot load.

                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        foreach (var bookSummary in bookSummaries.Where(x => x.InvalidFormat))
                        {
                            Console.WriteLine($"File '{bookSummary.Origin}' parsing error: '{bookSummary.Exception.Message}'");
                        }

                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine($"\r\nFiles fixed: {Statistics.FixedCount}.  Invalid files: {Statistics.ParsingErrorCount}.  Deleted files: {Statistics.DeletedCount}.");

                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
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
