using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fb2.Document;
using Fb2.Document.Models;

namespace Fb2CleanerApp.Models
{
    public class BookSummary
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
    }}
